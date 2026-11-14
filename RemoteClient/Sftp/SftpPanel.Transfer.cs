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
        var host = _getAuth()?.Host.Address ?? "sftp";
        var dir = Path.Combine(Path.GetTempPath(), "Nubis", "sftp", host, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        await DownloadAsync(new List<SftpItem> { item }, dir, openWhenDone: true);
    }

    private async Task DownloadAsync(List<SftpItem> items, string localDir, bool openWhenDone)
    {
        if (_busy) return;
        var plan = items.Select(i => (i, UniquePath(Path.Combine(localDir, SafeLocalName(i.Name)), i.IsDir))).ToList();
        var files = await DownloadCoreAsync(plan, localDir, localDir);
        if (files == null) return;
        if (openWhenDone && files.Count == 1)
            Process.Start(new ProcessStartInfo(files[0]) { UseShellExecute = true });
        else if (SettingsStore.Current.SftpOpenFolderAfterDownload)
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{localDir}\"") { UseShellExecute = true });
    }

    /// <summary>
    /// (원격 항목, 받을 로컬 경로) 목록을 받는다. 폴더는 하위까지. 성공하면 받은 로컬 파일 목록, 실패·취소면 null.
    /// 모든 로컬 경로는 <paramref name="localDir"/> 안이어야 한다.
    /// </summary>
    private async Task<List<string>?> DownloadCoreAsync(List<(SftpItem Item, string Local)> plan, string localDir, string destLabel)
    {
        if (_busy) return null;
        try
        {
            var c = await ClientAsync();
            var ct = BeginTransfer(L.T("다운로드 목록 확인 중..."), 0);

            var files = new List<(string Remote, string Local, long Size)>();
            var dirs = new List<string>();
            await Task.Run(() =>
            {
                foreach (var (i, local) in plan) Plan(c, i.FullName, i.IsDir, local, files, dirs);
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
                // 받는 중에는 .part 로 쓰고 다 받은 뒤 이름을 바꾼다 (취소·실패 때 반쯤 받은 파일이 남지 않게)
                var part = local + ".part";
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

            EndTransfer(L.F("다운로드 완료: {0}개 ({1}) → {2}", files.Count, HumanSize(total), destLabel));
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
    /// - 이미 있는 파일: 임시 이름으로 올린 뒤 원래 파일의 권한(·소유자)을 옮기고 이름을 바꿔 교체한다. 멈추면 원래 파일은 그대로
    /// 임시 파일 방식이 안 되는 경우는 원래 파일에 바로 덮어쓴다 (예전 방식):
    /// - 심볼릭 링크 (이름을 바꾸면 링크가 일반 파일로 바뀜 → 링크가 가리키는 파일에 써야 함)
    /// - 파일은 쓸 수 있지만 폴더에 새 파일을 못 만드는 곳 (예: /etc/app/config.yml)
    /// - 이름 바꾸기 실패 — 원본을 먼저 지우지 않는다
    /// </summary>
    private static async Task UploadOneAsync(SftpClient c, string local, string remote, IProgress<Renci.SshNet.UploadFileProgressReport> progress, CancellationToken ct)
    {
        var old = await Task.Run(() => c.Exists(remote) ? c.GetAttributes(remote) : null, ct);
        if (old == null)
        {
            try { await UploadToAsync(c, local, remote, progress, ct); }
            catch { await Task.Run(() => { try { c.DeleteFile(remote); } catch { } }); throw; }
            return;
        }
        if (old.IsSymbolicLink) { await UploadToAsync(c, local, remote, progress, ct); return; }

        var temp = remote + ".nubis-upload";
        try { await UploadToAsync(c, local, temp, progress, ct); }
        catch (OperationCanceledException)
        {
            await Task.Run(() => { try { c.DeleteFile(temp); } catch { } });
            throw;
        }
        catch (Exception ex) when (ex is SftpPermissionDeniedException or SftpPathNotFoundException)
        {
            // 폴더에 임시 파일을 만들 수 없음 → 원래 파일에 바로 덮어쓰기
            await Task.Run(() => { try { c.DeleteFile(temp); } catch { } });
            await UploadToAsync(c, local, remote, progress, ct);
            return;
        }
        catch
        {
            await Task.Run(() => { try { c.DeleteFile(temp); } catch { } });
            throw;
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
            catch { /* 권한을 못 옮겨도 교체는 진행 */ }
            try
            {
                var a = c.GetAttributes(temp);
                if (a.UserId != old.UserId || a.GroupId != old.GroupId) { a.UserId = old.UserId; a.GroupId = old.GroupId; c.SetAttributes(temp, a); }
            }
            catch { /* root 가 아니면 소유자는 못 바꾼다 */ }
            try { c.RenameFile(temp, remote, true); return true; }   // posix-rename: 원자적 교체 (OpenSSH)
            catch { return false; }
        }, CancellationToken.None);
        if (replaced) return;

        // posix-rename 미지원 등으로 교체 실패: 원본은 건드리지 않았으니 원본에 바로 덮어쓰고 임시 파일을 지운다
        try { await UploadToAsync(c, local, remote, progress, ct); }
        finally { await Task.Run(() => { try { c.DeleteFile(temp); } catch { } }); }
    }

    private static async Task UploadToAsync(SftpClient c, string local, string path, IProgress<Renci.SshNet.UploadFileProgressReport> progress, CancellationToken ct)
    {
        await using var fs = File.OpenRead(local);
        await c.UploadFileAsync(fs, path, true, progress, ct);
    }
}
