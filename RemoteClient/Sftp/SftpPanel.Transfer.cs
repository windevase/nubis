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
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = L.F("{0} 에 업로드할 파일", _cwd) };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) await UploadAsync(dlg.FileNames, _cwd);
    }

    private async Task UploadAsync(string[] localPaths, string target)
    {
        if (_busy || target.Length == 0) return;
        // 준비(폴더 훑기·서버에 같은 이름이 있는지 확인)도 오래 걸릴 수 있어 처음부터 전송 중으로 표시한다 —
        // 화면이 멈춘 것처럼 보이지 않게, 그 사이 두 번째 업로드·사용자 전환이 끼어들지 않게
        var ct = BeginTransfer(L.T("업로드 목록 확인 중..."), 0);
        try
        {
            var c = await ClientAsync();

            // 전송 계획: (로컬 파일, 원격 경로, 크기) + 만들 폴더
            var (files, dirs) = await Task.Run(() => PlanUpload(localPaths, target, ct), ct);

            // 이미 있는 파일 (폴더 안의 파일까지): 덮어쓰기 / 건너뛰기 / 취소
            var existing = await Task.Run(() =>
            {
                var set = new HashSet<string>(StringComparer.Ordinal);
                foreach (var f in files)
                {
                    ct.ThrowIfCancellationRequested();
                    if (c.Exists(f.Remote)) set.Add(f.Remote);
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
            if (files.Count == 0 && dirs.Count == 0) { EndTransfer(null); return; }

            var total = files.Sum(f => f.Size);
            long done = 0;
            TransferText.Text = L.F("업로드 준비 ({0}개, {1})", files.Count, HumanSize(total));
            TransferProgress.IsIndeterminate = total == 0;
            TransferProgress.Value = 0;
            await Task.Run(() => { foreach (var d in dirs) if (!c.Exists(d)) c.CreateDirectory(d); }, ct);
            foreach (var (local, remote, size) in files)
            {
                TransferText.Text = L.F("업로드 {0}  ({1} / {2})", Path.GetFileName(local), HumanSize(done), HumanSize(total));
                var baseDone = done;
                var progress = new Progress<Renci.SshNet.UploadFileProgressReport>(r => SetProgress(baseDone + (long)r.TotalBytesUploaded, total));
                await UploadOneAsync(c, local, remote, progress, ct);
                done += size;
                SetProgress(done, total);
            }
            EndTransfer(L.F("업로드 완료: {0}개 ({1}) → {2}", files.Count, HumanSize(total), target));
        }
        catch (OperationCanceledException) { EndTransfer(L.T("업로드를 취소했습니다.")); }
        catch (Exception ex) { EndTransfer(L.F("업로드 실패: {0}", ex.Message)); }
        await NavigateAsync(_cwd);
    }

    /// <summary>업로드 계획: 올릴 파일(로컬, 원격 경로, 크기)과 만들 폴더. 폴더는 하위까지 훑는다 (백그라운드 스레드에서)</summary>
    private static (List<(string Local, string Remote, long Size)> Files, List<string> Dirs) PlanUpload(string[] localPaths, string target, CancellationToken ct)
    {
        var files = new List<(string Local, string Remote, long Size)>();
        var dirs = new List<string>();
        foreach (var p in localPaths)
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(p.TrimEnd('\\'));
            if (Directory.Exists(p))
            {
                var root = Combine(target, name);
                dirs.Add(root);
                foreach (var d in Directory.EnumerateDirectories(p, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    dirs.Add(Combine(root, Path.GetRelativePath(p, d).Replace('\\', '/')));
                }
                foreach (var f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    files.Add((f, Combine(root, Path.GetRelativePath(p, f).Replace('\\', '/')), new FileInfo(f).Length));
                }
            }
            else if (File.Exists(p)) files.Add((p, Combine(target, name), new FileInfo(p).Length));
        }
        return (files, dirs);
    }

    // ===================== 다운로드 =====================

    private async void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        var items = Selected();
        if (items.Count == 0 || _busy) return;
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
        if (FileList.SelectedItem is SftpItem { IsDir: false } item) await OpenFileAsync(item);
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
        catch (Exception ex) { ShowStatus(L.F("다운로드 실패: {0}", ex.Message)); return; }
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
        if (files == null) return;
        try
        {
            if (openWhenDone && files.Count == 1)
                OpenDownloaded(files[0]);
            else if (SettingsStore.Current.SftpOpenFolderAfterDownload)
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{localDir}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { ShowStatus(ex.Message); }
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
        try
        {
            var c = await ClientAsync();

            var files = new List<(string Remote, string Local, long Size)>();
            var dirs = new List<string>();
            var skipped = new List<string>();
            await Task.Run(() =>
            {
                foreach (var (i, local) in plan) Plan(c, i.FullName, i.IsDir, local, files, dirs, skipped, ct);
            }, ct);

            // 원격 이름에서 만든 경로가 선택한 폴더 밖으로 나가지 않는지 확인
            var root = Path.GetFullPath(localDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (files.Any(f => !Path.GetFullPath(f.Local).StartsWith(root, StringComparison.OrdinalIgnoreCase)) ||
                dirs.Any(d => !Path.GetFullPath(d).StartsWith(root, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(L.T("허용되지 않는 파일 이름이 있어 다운로드를 중단했습니다."));

            var total = files.Sum(f => f.Size);
            long done = 0;
            foreach (var d in dirs) Directory.CreateDirectory(d);
            foreach (var (remote, local, size) in files)
            {
                TransferText.Text = L.F("다운로드 {0}  ({1} / {2})", Path.GetFileName(local), HumanSize(done), HumanSize(total));
                var baseDone = done;
                var progress = new Progress<Renci.SshNet.DownloadFileProgressReport>(r => SetProgress(baseDone + (long)r.TotalBytesDownloaded, total));
                // 받는 중에는 .part 로 쓰고 다 받은 뒤 이름을 바꾼다 (취소·실패 때 반쯤 받은 파일이 남지 않게).
                // .part 이름은 겹치지 않게 — 같은 폴더의 "x.part" 파일을 "x" 받는 중에 덮어쓰지 않게
                var part = $"{local}.{Guid.NewGuid().ToString("N")[..8]}.part";
                try
                {
                    await using (var fs = File.Create(part))
                        await c.DownloadFileAsync(remote, fs, progress, ct);
                    File.Move(part, local, overwrite: true);
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
                        (skipped.Count > 0 ? " " + L.F("(폴더 링크·끊긴 링크·특수 파일 {0}개는 건너뜀)", skipped.Count) : ""));
            return files.Select(f => f.Local).ToList();
        }
        catch (OperationCanceledException) { EndTransfer(L.T("다운로드를 취소했습니다.")); }
        catch (Exception ex) { EndTransfer(L.F("다운로드 실패: {0}", ex.Message)); }
        return null;
    }

    private async void ShowBusyHint()
    {
        TransferBar.Visibility = Visibility.Visible;
        var before = TransferText.Text;
        TransferText.Text = L.T("다른 전송이 진행 중입니다. 끝난 뒤 다시 시도하세요.");
        await Task.Delay(2500);
        if (_busy && TransferText.Text == L.T("다른 전송이 진행 중입니다. 끝난 뒤 다시 시도하세요.")) TransferText.Text = before;
    }

    /// <summary>
    /// 파일 하나 올리기. 취소·실패 때 서버에 반쯤 쓴 파일이 남지 않게:
    /// - 새 파일: 그대로 올리고, 중간에 멈추면 지운다
    /// - 이미 있는 내 파일: 임시 이름으로 올린 뒤 원래 파일의 권한·그룹을 옮기고 이름을 바꿔 교체한다. 멈추면 원래 파일은 그대로
    /// 임시 파일 방식이 안 되는 경우는 원래 파일에 바로 덮어쓴다 (예전 방식):
    /// - 심볼릭 링크 (이름을 바꾸면 링크가 일반 파일로 바뀜 → 링크가 가리키는 파일에 써야 함)
    /// - 파일은 쓸 수 있지만 폴더에 새 파일을 못 만드는 곳 (예: /etc/app/config.yml), 서버가 EXCL 만들기를 지원하지 않음
    /// - 내 소유가 아닌 파일 (그룹 쓰기 권한으로 고치는 www-data 파일 등 — 바꿔치기하면 소유자가 바뀌고 하드링크·ACL 이 끊김)
    /// - 이름 바꾸기 실패 — 원본을 먼저 지우지 않는다
    /// </summary>
    private static Task UploadOneAsync(SftpClient c, string local, string remote, IProgress<Renci.SshNet.UploadFileProgressReport> progress, CancellationToken ct) =>
        UploadOneAsync(c, () => File.OpenRead(local), remote, progress, ct);

    /// <param name="open">올릴 내용 (부를 때마다 처음부터 읽는 새 스트림 — 교체 실패 때 다시 올린다)</param>
    private static async Task UploadOneAsync(SftpClient c, Func<Stream> open, string remote, IProgress<Renci.SshNet.UploadFileProgressReport> progress, CancellationToken ct)
    {
        var old = await Task.Run(() => c.Exists(remote) ? c.GetAttributes(remote) : null, ct);
        if (old == null)
        {
            try { await UploadToAsync(c, open, remote, progress, ct); }
            catch { await Task.Run(() => { try { c.DeleteFile(remote); } catch { } }); throw; }
            return;
        }
        if (old.IsSymbolicLink) { await UploadToAsync(c, open, remote, progress, ct); return; }

        // 임시 이름은 예측할 수 없게, 이미 있으면 실패하게(심어 둔 링크를 따라가지 않음), 내용을 쓰기 전에 0600 으로
        var temp = UploadTempName(remote);
        var created = false;
        try
        {
            try
            {
                await Task.Run(() => CreatePrivate(c, temp), ct);
                created = true;
            }
            catch (SftpException)
            {
                // 폴더에 임시 파일을 만들 수 없음(권한 없음) · 서버가 "없을 때만 만들기"(EXCL)를 지원하지 않음(SSH_FX_FAILURE·OP_UNSUPPORTED 등)
                // → 아래에서 원래 파일에 바로 덮어쓴다. 만들기가 실패했으므로 그 이름의 파일은 지우지 않는다 (남의 것일 수 있음)
            }
            // 내 파일이 아니면(예: www-data 소유 664 파일을 그룹 쓰기로 편집) 바꿔치기하면 소유자가 나로 바뀌고
            // 하드링크·ACL 이 끊긴다 → 빈 임시 파일만 지우고 원래 파일에 바로 덮어쓴다 (예전 방식)
            if (created && !await Task.Run(() => TryMatchOwner(c, temp, old), ct))
            {
                created = false;
                await Task.Run(() => { try { c.DeleteFile(temp); } catch { } });
            }
            if (created) await UploadToAsync(c, open, temp, progress, ct);
        }
        catch (OperationCanceledException)
        {
            if (created) await Task.Run(() => { try { c.DeleteFile(temp); } catch { } });
            throw;
        }
        catch
        {
            // 직접 만든 임시 파일만 지운다 (만들기에 실패했다면 그 이름의 파일은 남의 것)
            if (created) await Task.Run(() => { try { c.DeleteFile(temp); } catch { } });
            throw;
        }
        if (!created)
        {
            // 임시 파일을 못 만들었거나 남의 파일 → 원래 파일에 바로 덮어쓰기 (실패해도 다시 시도하지 않음)
            await UploadToAsync(c, open, remote, progress, ct);
            return;
        }

        var replaced = await Task.Run(() =>
        {
            try
            {
                var a = c.GetAttributes(temp);
                a.OwnerCanRead = old.OwnerCanRead; a.OwnerCanWrite = old.OwnerCanWrite; a.OwnerCanExecute = old.OwnerCanExecute;
                a.GroupCanRead = old.GroupCanRead; a.GroupCanWrite = old.GroupCanWrite; a.GroupCanExecute = old.GroupCanExecute;
                a.OthersCanRead = old.OthersCanRead; a.OthersCanWrite = old.OthersCanWrite; a.OthersCanExecute = old.OthersCanExecute;
                c.SetAttributes(temp, a);
            }
            catch { /* 권한을 못 옮겨도 교체는 진행 (소유자·그룹은 TryMatchOwner 에서 이미 맞춤) */ }
            try { c.RenameFile(temp, remote, true); return true; }   // posix-rename: 원자적 교체 (OpenSSH)
            catch { return false; }
        }, CancellationToken.None);
        if (replaced) return;

        // posix-rename 미지원 등으로 교체 실패: 원본은 건드리지 않았으니 원본에 바로 덮어쓰고 임시 파일을 지운다
        try { await UploadToAsync(c, open, remote, progress, ct); }
        finally { await Task.Run(() => { try { c.DeleteFile(temp); } catch { } }); }
    }

    private static async Task UploadToAsync(SftpClient c, Func<Stream> open, string path, IProgress<Renci.SshNet.UploadFileProgressReport> progress, CancellationToken ct)
    {
        await using var fs = open();
        await c.UploadFileAsync(fs, path, true, progress, ct);
    }

    /// <summary>
    /// 바꿔치기 업로드를 해도 되는가 — 방금 만든 임시 파일(소유자 = 접속한 사용자)의 소유자·그룹이 원래 파일과 같을 때만.
    /// 다르면(남의 파일, 그룹을 못 맞춤) 바꿔치기가 소유자를 바꾸고 하드링크·ACL 을 끊으므로 원래 파일에 바로 덮어쓴다
    /// </summary>
    public static bool CanSwapReplace(int oldUserId, int oldGroupId, int tempUserId, int tempGroupId) =>
        oldUserId == tempUserId && oldGroupId == tempGroupId;

    /// <summary>
    /// 빈 임시 파일의 그룹을 원래 파일에 맞춰 보고(내 파일이면 내가 속한 그룹으로는 바꿀 수 있다), 바꿔치기해도 되는지 돌려준다.
    /// 소유자는 바꾸려 하지 않는다 — 남의 파일이면 바로 덮어쓰기로 간다. 속성을 못 읽으면 false (안전하게 덮어쓰기)
    /// </summary>
    private static bool TryMatchOwner(SftpClient c, string temp, SftpFileAttributes old)
    {
        try
        {
            var a = c.GetAttributes(temp);
            if (a.UserId != old.UserId) return false;
            if (a.GroupId != old.GroupId)
            {
                try { a.GroupId = old.GroupId; c.SetAttributes(temp, a); } catch { }
                a = c.GetAttributes(temp);
            }
            return CanSwapReplace(old.UserId, old.GroupId, a.UserId, a.GroupId);
        }
        catch (SftpException) { return false; }
    }

    /// <summary>교체 업로드용 임시 이름 — 같은 폴더, 매번 다른 임의 접미사 (world-writable 폴더에 미리 심어 둔 이름을 쓰지 않게)</summary>
    public static string UploadTempName(string remote) =>
        $"{remote}.{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)).ToLowerInvariant()}.nubis-upload";

    /// <summary>
    /// 빈 파일을 "없을 때만" 만들고(O_CREAT|O_EXCL — 이미 있거나 링크면 실패) 소유자만 읽고 쓰게(0600) 한다.
    /// 내용은 그 뒤에 쓰므로 umask 권한(0644)으로 내용이 보이는 순간이 없다. 권한 변경 실패는 무시 (파일은 이미 내 것)
    /// </summary>
    private static void CreatePrivate(SftpClient c, string path)
    {
        using (c.Open(path, FileMode.CreateNew, FileAccess.Write)) { }
        try
        {
            var a = c.GetAttributes(path);
            a.OwnerCanRead = true; a.OwnerCanWrite = true; a.OwnerCanExecute = false;
            a.GroupCanRead = false; a.GroupCanWrite = false; a.GroupCanExecute = false;
            a.OthersCanRead = false; a.OthersCanWrite = false; a.OthersCanExecute = false;
            c.SetAttributes(path, a);
        }
        catch { }
    }
}
