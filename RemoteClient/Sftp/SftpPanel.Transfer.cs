using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace Nubis;

public partial class SftpPanel
{
    // ===================== 업로드 =====================

    /// <summary>놓을 위치: 폴더 행 위면 그 폴더, 아니면 현재 폴더</summary>
    private string DropTarget(DragEventArgs e) =>
        (e.OriginalSource as FrameworkElement)?.DataContext is SftpItem { IsDir: true } dir ? dir.FullName : _cwd;

    /// <summary>이 패널에서 끌어낸 항목을 같은 패널에 다시 놓는 경우 (받았다가 그대로 다시 올리게 되므로 막는다)</summary>
    private bool IsOwnDrag(IDataObject data) =>
        data.GetDataPresent(SftpDragData.SourceFormat) && data.GetData(SftpDragData.SourceFormat) as string == _panelId;

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var ok = !_busy && _cwd.Length > 0 && e.Data.GetDataPresent(DataFormats.FileDrop) && !IsOwnDrag(e.Data);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        if (ok)
        {
            DropHintText.Text = L.F("{0} 에 업로드", DropTarget(e));
            DropHint.Visibility = Visibility.Visible;
        }
        else if (_busy && e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            DropHintText.Text = L.T("다른 전송이 진행 중입니다. 끝난 뒤 다시 놓아 주세요.");
            DropHint.Visibility = Visibility.Visible;
        }
        else DropHint.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e) => DropHint.Visibility = Visibility.Collapsed;

    private async void OnDrop(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
        if (_busy) { ShowBusyHint(); return; }
        if (_cwd.Length == 0 || IsOwnDrag(e.Data)) return;
        var target = DropTarget(e);
        // 다른 SFTP 패널에서 끌어온 경우 여기서 GetData 가 원본 서버에서 임시 폴더로 받아 온다 (서버 간 복사)
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0) await UploadAsync(paths, target);
    }

    private async void OnUploadClick(object sender, RoutedEventArgs e)
    {
        if (_busy) { ShowBusyHint(); return; }   // 파일을 다 고른 뒤에 말없이 무시되지 않게
        if (_cwd.Length == 0) return;   // 아직 목록을 못 열었음 — 파일을 다 고른 뒤 말없이 무시되지 않게 창을 띄우지 않는다
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = L.F("{0} 에 업로드할 파일", _cwd) };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        if (_busy) { ShowBusyHint(); return; }   // 고르는 사이 다른 전송이 시작됨
        await UploadAsync(dlg.FileNames, _cwd);
    }

    private async Task UploadAsync(string[] localPaths, string target)
    {
        if (_busy || target.Length == 0) return;
        // 준비(폴더 훑기·서버에 같은 이름이 있는지 확인)도 오래 걸릴 수 있어 처음부터 전송 중으로 표시한다 —
        // 화면이 멈춘 것처럼 보이지 않게, 그 사이 두 번째 업로드·사용자 전환이 끼어들지 않게
        var ct = BeginTransfer(L.T("업로드 목록 확인 중..."), 0);
        var tseq = _statusSeq;   // 늦게 도착한 진행 보고가 끝난 뒤의 결과 문구를 덮지 않게
        try
        {
            var c = await ClientAsync();

            // 전송 계획: (로컬 파일, 원격 경로, 크기) + 만들 폴더
            var (files, dirs, unreadable) = await Task.Run(() => PlanUpload(localPaths, target, ct), ct);
            var skipNote = unreadable > 0 ? L.F("(읽을 수 없는 폴더·파일·링크 폴더 {0}개는 건너뜀)", unreadable) : null;

            // 이미 있는 파일 (폴더 안의 파일까지): 덮어쓰기 / 건너뛰기 / 취소.
            // 파일이 많으면 파일마다 묻지 않고 대상 폴더 목록을 한 번씩 읽어 판단한다 (수천 개면 왕복만 수 분 걸렸다)
            var names = files.Count >= RemoteNameCache.BatchThreshold ? new RemoteNameCache(p => ListNames(c, p)) : null;
            var existing = await Task.Run(() =>
            {
                var set = new HashSet<string>(StringComparer.Ordinal);
                foreach (var f in files)
                {
                    ct.ThrowIfCancellationRequested();
                    if (names?.Exists(f.Remote) ?? c.Exists(f.Remote)) set.Add(f.Remote);
                }
                return set;
            }, ct);
            if (existing.Count > 0)
            {
                var choice = Dialog.Choose(Window.GetWindow(this), L.F("서버에 같은 이름의 파일이 {0}개 있습니다.", existing.Count), L.T("업로드"),
                    L.T("덮어쓰기"), L.T("건너뛰기"), L.T("취소"), MessageBoxImage.Question,
                    string.Join("\n", existing.Take(15)) + (existing.Count > 15 ? "\n..." : ""));
                if (choice is -1 or 2) { EndTransfer(null); return; }
                if (choice == 1) files = files.Where(f => !existing.Contains(f.Remote)).ToList();
            }
            if (files.Count == 0 && dirs.Count == 0) { EndTransfer(skipNote); return; }

            var total = files.Sum(f => f.Size);
            long done = 0;
            TransferText.Text = L.F("업로드 준비 ({0}개, {1})", files.Count, HumanSize(total));
            TransferProgress.IsIndeterminate = total == 0;
            TransferProgress.Value = 0;
            await Task.Run(() =>
            {
                foreach (var d in dirs)
                {
                    ct.ThrowIfCancellationRequested();
                    if (names?.Exists(d) ?? c.Exists(d)) continue;
                    c.CreateDirectory(d);
                    names?.Created(d);
                }
            }, ct);
            var meter = new TransferMeter(total);
            for (var n = 0; n < files.Count; n++)
            {
                var (local, remote, size) = files[n];
                var label = L.F("업로드 {0}  [{1}/{2}]", Path.GetFileName(local), n + 1, files.Count);
                TransferText.Text = $"{label}  ({meter.Detail(done)})";
                var baseDone = done;
                // 화면 갱신은 0.25초에 한 번만 UI 스레드로 (보고마다 올리면 빠른 전송에서 UI 스레드가 바빠진다)
                var progress = ThrottledProgress<Renci.SshNet.UploadFileProgressReport>.OnDispatcher(Dispatcher, r =>
                {
                    if (tseq != _statusSeq) return;   // 전송이 이미 끝남
                    var now = baseDone + (long)r.TotalBytesUploaded;
                    SetProgress(now, total);
                    TransferText.Text = $"{label}  ({meter.Detail(now)})";
                });
                // 목록으로 없다고 확인한 파일은 업로드 전에 다시 묻지 않는다 (새 폴더에 올리는 수천 개 파일)
                await UploadOneAsync(c, () => File.OpenRead(local), remote, progress, ct, knownMissing: names?.Exists(remote) == false);
                done += size;
                SetProgress(done, total);
            }
            EndTransfer(L.F("업로드 완료: {0}개 ({1}) → {2}", files.Count, HumanSize(total), target) + (skipNote != null ? " " + skipNote : ""), ToastKind.Success);
        }
        catch (OperationCanceledException) { EndTransfer(L.T("업로드를 취소했습니다.")); }
        catch (Exception ex) { EndTransfer(L.F("업로드 실패: {0}", ex.Message), ToastKind.Error); }
        await NavigateAsync(_cwd);
    }

    /// <summary>
    /// 업로드 계획: 올릴 파일(로컬, 원격 경로, 크기)과 만들 폴더. 폴더는 하위까지 훑는다 (백그라운드 스레드에서).
    /// 폴더 안에서 읽을 수 없는 하위 폴더·파일과 링크(정션·심볼릭 링크) 폴더는 건너뛰고 개수를 Skipped 로 — 하나 때문에 전체가 실패하지 않게
    /// </summary>
    private static (List<(string Local, string Remote, long Size)> Files, List<string> Dirs, int Skipped) PlanUpload(string[] localPaths, string target, CancellationToken ct)
    {
        var files = new List<(string Local, string Remote, long Size)>();
        var dirs = new List<string>();
        var skipped = 0;
        foreach (var p in localPaths)
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(p.TrimEnd('\\', '/'));
            // 드라이브 루트(C:\)는 이름이 비어 대상 폴더에 드라이브 전체가 풀려 올라간다 — 받지 않는다
            if (string.IsNullOrEmpty(name))
                throw new InvalidOperationException(L.F("드라이브 전체는 업로드할 수 없습니다: {0}", p));
            if (Directory.Exists(p))
            {
                var root = Combine(target, name);
                dirs.Add(root);
                var scan = ScanLocalFolder(p, ct);
                dirs.AddRange(scan.Dirs.Select(d => Combine(root, d)));
                files.AddRange(scan.Files.Select(f => (f.Local, Combine(root, f.Rel), f.Size)));
                skipped += scan.Skipped;
            }
            else if (File.Exists(p)) files.Add((p, Combine(target, name), new FileInfo(p).Length));
        }
        return (files, dirs, skipped);
    }

    /// <summary>
    /// 로컬 폴더를 하위까지 훑는다. Dirs·Files 의 Rel 은 root 기준 "/" 구분 상대 경로 (폴더는 부모가 먼저).
    /// 숨김·시스템 파일도 포함한다. 열 수 없는 하위 폴더(권한 없음)·크기를 못 읽는 파일·링크 폴더(정션·심볼릭 링크 — 순환·밖으로 나감)는
    /// 건너뛰고 Skipped 로 센다. root 자체를 못 열면 예외 (사용자가 고른 폴더라 조용히 비우지 않는다)
    /// </summary>
    public static (List<string> Dirs, List<(string Local, string Rel, long Size)> Files, int Skipped) ScanLocalFolder(string root, CancellationToken ct = default)
    {
        var dirs = new List<string>();
        var files = new List<(string Local, string Rel, long Size)>();
        var skipped = 0;
        var opts = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0, ReturnSpecialDirectories = false };
        string Rel(string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            List<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", opts).ToList(); }
            catch (Exception ex) when (dir != root && ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                skipped++;   // 열 수 없는 하위 폴더 — 원격에 빈 폴더도 만들지 않는다
                continue;
            }
            if (dir != root) dirs.Add(Rel(dir));
            var subdirs = new List<string>();
            foreach (var e in entries)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (e is DirectoryInfo d)
                    {
                        // 정션·심볼릭 링크 폴더는 따라가지 않는다 (OneDrive 클라우드 파일 같은 다른 재분석 지점은 LinkTarget 이 null 이라 그대로 훑는다)
                        if ((d.Attributes & FileAttributes.ReparsePoint) != 0 && d.LinkTarget != null) { skipped++; continue; }
                        subdirs.Add(d.FullName);
                    }
                    else if (e is FileInfo f) files.Add((f.FullName, Rel(f.FullName), f.Length));
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) { skipped++; }
            }
            // 이름순으로 훑게 (스택이라 거꾸로 넣는다)
            subdirs.Sort(StringComparer.OrdinalIgnoreCase);
            for (var i = subdirs.Count - 1; i >= 0; i--) stack.Push(subdirs[i]);
        }
        return (dirs, files, skipped);
    }

    // ===================== 다운로드 =====================

    private async void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        var items = Selected();
        if (items.Count == 0) return;
        if (_busy) { ShowBusyHint(); return; }
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = L.T("다운로드할 폴더 선택"), InitialDirectory = downloads };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        await DownloadAsync(items, dlg.FolderName, openWhenDone: false);
    }

    private async void OnOpenItem(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not SftpItem item) return;
        if (item.IsDir) await NavigateAsync(item.FullName);
        else await EditFileAsync(item);
    }

    private async void OnOpenCopy(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not SftpItem { IsDir: false } item) return;
        if (item.IsSpecial) { ShowStatus(L.F("장치·FIFO·소켓 같은 특수 파일은 열 수 없습니다: {0}", item.Name)); return; }
        await OpenFileAsync(item);
    }

    /// <summary>임시 폴더로 받아 기본 프로그램으로 열기 (로그·설정 파일 확인용)</summary>
    private async Task OpenFileAsync(SftpItem item)
    {
        if (item.Size > 200L * 1024 * 1024 &&
            Dialog.Show(Window.GetWindow(this)!, L.F("{0} 파일입니다. 받아서 열까요?", HumanSize(item.Size)), L.T("열기"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (_busy) { ShowBusyHint(); return; }
        string dir;
        try
        {
            CleanupOpenTemp();
            // 주소는 경로에 그대로 쓰지 않는다 (IPv6 의 ':' 등)
            var host = SafeLocalName(_getAuth()?.Host.Address ?? "sftp");
            dir = Path.Combine(OpenTempRoot, host, Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex) { ShowStatus(L.F("다운로드 실패: {0}", ex.Message), kind: ToastKind.Error); return; }
        TrackTempDir(dir);   // 패널이 닫힐 때 지운다
        await DownloadAsync(new List<SftpItem> { item }, dir, openWhenDone: true);
    }

    private async Task DownloadAsync(List<SftpItem> items, string localDir, bool openWhenDone)
    {
        if (_busy) return;
        // 선택한 항목끼리(대소문자만 다른 이름·a:b 와 a_b) 또는 이미 있는 파일과 겹치면 "이름 (1)" — 조용히 덮어쓰지 않게
        var targets = UniqueTargets(items.Select(i => (i.Name, i.IsDir)), localDir, p => File.Exists(p) || Directory.Exists(p));
        var plan = items.Zip(targets, (i, t) => (i, t)).ToList();
        var files = await DownloadCoreAsync(plan, localDir, localDir);
        // 받는 사이 탭이 닫혔으면 열지 않는다 (Close 가 임시 폴더를 지우는 중 — 닫은 탭의 파일이 뒤늦게 열리지 않게)
        if (files == null || _closed) return;
        try
        {
            if (openWhenDone && files.Count == 1)
            {
                // "읽기 전용 사본" — 편집기에서 고쳐 저장해도 서버에 올라가지 않으므로 저장부터 막아 착각하지 않게
                try { File.SetAttributes(files[0], File.GetAttributes(files[0]) | FileAttributes.ReadOnly); } catch { }
                OpenDownloaded(files[0]);
            }
            else if (SettingsStore.Current.SftpOpenFolderAfterDownload)
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{localDir}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { ShowStatus(ex.Message, kind: ToastKind.Error); }
    }

    /// <summary>
    /// (원격 항목, 받을 로컬 경로) 목록을 받는다. 폴더는 하위까지. 성공하면 받은 로컬 파일 목록, 실패·취소면 null.
    /// 모든 로컬 경로는 <paramref name="localDir"/> 안이어야 한다.
    /// </summary>
    private async Task<List<string>?> DownloadCoreAsync(List<(SftpItem Item, string Local)> plan, string localDir, string destLabel)
    {
        if (_busy) return null;
        // 연결을 기다리기 전에 전송 중으로 표시 — 그 사이 다른 전송이 끼어들어 서로의 상태(_busy·_cts)를 지우지 않게
        var ct = BeginTransfer(L.T("다운로드 목록 확인 중..."), 0);
        var tseq = _statusSeq;   // 늦게 도착한 진행 보고가 끝난 뒤의 결과 문구를 덮지 않게
        try
        {
            var c = await ClientAsync();

            var files = new List<(string Remote, string Local, long Size)>();
            var dirs = new List<string>();
            var skipped = new List<string>();
            await Task.Run(() =>
            {
                // 직접 고른 파일의 속성 확인은 병렬로 — 하나씩 물으면 수천 개를 골랐을 때(Ctrl+A) 받기 전 왕복만 수 분 걸렸다.
                // 못 읽은 것은 Plan 이 다시 물어 원래 오류(없음·권한)를 보인다
                var known = new System.Collections.Concurrent.ConcurrentDictionary<string, SftpFileAttributes>(StringComparer.Ordinal);
                var top = plan.Where(p => !p.Item.IsDir).Select(p => p.Item.FullName).ToList();
                if (top.Count > 1)
                    Parallel.ForEach(top, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, f =>
                    {
                        try { known[f] = c.GetAttributes(f); } catch { }
                    });
                foreach (var (i, local) in plan)
                    Plan(c, i.FullName, i.IsDir, local, files, dirs, skipped, ct, known.GetValueOrDefault(i.FullName));

                // 원격 이름에서 만든 경로가 선택한 폴더 밖으로 나가지 않는지 확인
                var root = Path.GetFullPath(localDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (files.Any(f => !Path.GetFullPath(f.Local).StartsWith(root, StringComparison.OrdinalIgnoreCase)) ||
                    dirs.Any(d => !Path.GetFullPath(d).StartsWith(root, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException(L.T("허용되지 않는 파일 이름이 있어 다운로드를 중단했습니다."));
                // 폴더 만들기도 여기서 — 하위 폴더가 수천 개면 UI 스레드가 멈췄다
                foreach (var d in dirs)
                {
                    ct.ThrowIfCancellationRequested();
                    Directory.CreateDirectory(d);
                }
            }, ct);

            var total = files.Sum(f => f.Size);
            long done = 0;
            var meter = new TransferMeter(total);
            for (var n = 0; n < files.Count; n++)
            {
                var (remote, local, size) = files[n];
                var label = L.F("다운로드 {0}  [{1}/{2}]", Path.GetFileName(local), n + 1, files.Count);
                TransferText.Text = $"{label}  ({meter.Detail(done)})";
                var baseDone = done;
                var progress = ThrottledProgress<Renci.SshNet.DownloadFileProgressReport>.OnDispatcher(Dispatcher, r =>
                {
                    if (tseq != _statusSeq) return;   // 전송이 이미 끝남
                    var now = baseDone + (long)r.TotalBytesDownloaded;
                    SetProgress(now, total);
                    TransferText.Text = $"{label}  ({meter.Detail(now)})";
                });
                // 받는 중에는 .part 로 쓰고 다 받은 뒤 이름을 바꾼다 (취소·실패 때 반쯤 받은 파일이 남지 않게).
                // .part 이름은 겹치지 않게 — 같은 폴더의 "x.part" 파일을 "x" 받는 중에 덮어쓰지 않게
                var part = $"{local}.{Guid.NewGuid().ToString("N")[..8]}.part";
                try
                {
                    await using (var fs = File.Create(part))
                        await c.DownloadFileAsync(remote, fs, progress, ct);
                    File.Move(part, local, overwrite: true);
                    // 다른 서버에서 받은 파일 — "인터넷에서 받음" 표시로 실행 파일은 SmartScreen, 문서는 보호된 보기가 적용되게 (탐색기로 끌어낸 파일 포함)
                    MarkFromInternet(local);
                }
                catch
                {
                    try { File.Delete(part); } catch { }
                    throw;
                }
                done += size;
                SetProgress(done, total);
            }

            EndTransfer(L.F("다운로드 완료: {0}개 ({1}) → {2}", files.Count, HumanSize(total), destLabel) +
                        (skipped.Count > 0 ? " " + L.F("(폴더 링크·끊긴 링크·특수 파일 {0}개는 건너뜀)", skipped.Count) : ""), ToastKind.Success);
            return files.Select(f => f.Local).ToList();
        }
        catch (OperationCanceledException) { EndTransfer(L.T("다운로드를 취소했습니다.")); }
        catch (Exception ex) { EndTransfer(L.F("다운로드 실패: {0}", ex.Message), ToastKind.Error); }
        return null;
    }

    /// <summary>안내를 띄우기 전 문구 — 안내가 떠 있는 동안 또 누르면 안내 문구를 "원래 문구"로 잡아 그대로 남는 일이 없게</summary>
    private string? _busyHintBefore;
    private int _busyHintSeq;

    private async void ShowBusyHint()
    {
        var hint = L.T("다른 전송이 진행 중입니다. 끝난 뒤 다시 시도하세요.");
        TransferBar.Visibility = Visibility.Visible;
        if (TransferText.Text != hint) _busyHintBefore = TransferText.Text;
        TransferText.Text = hint;
        var seq = ++_busyHintSeq;
        await Task.Delay(2500);
        if (seq != _busyHintSeq) return;   // 더 나중에 누른 안내가 되돌린다
        if (_busy && TransferText.Text == hint && _busyHintBefore != null) TransferText.Text = _busyHintBefore;
        _busyHintBefore = null;
    }
}
