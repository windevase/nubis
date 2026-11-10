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
        else DropHint.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e) => DropHint.Visibility = Visibility.Collapsed;

    private async void OnDrop(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
        if (_busy || _cwd.Length == 0 || IsOwnDrag(e.Data)) return;
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
        try
        {
            var c = await ClientAsync();

            // 전송 계획: (로컬 파일, 원격 경로, 크기) + 만들 폴더
            var files = new List<(string Local, string Remote, long Size)>();
            var dirs = new List<string>();
            foreach (var p in localPaths)
            {
                var name = Path.GetFileName(p.TrimEnd('\\'));
                if (Directory.Exists(p))
                {
                    var root = Combine(target, name);
                    dirs.Add(root);
                    foreach (var d in Directory.EnumerateDirectories(p, "*", SearchOption.AllDirectories))
                        dirs.Add(Combine(root, Path.GetRelativePath(p, d).Replace('\\', '/')));
                    foreach (var f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                        files.Add((f, Combine(root, Path.GetRelativePath(p, f).Replace('\\', '/')), new FileInfo(f).Length));
                }
                else if (File.Exists(p)) files.Add((p, Combine(target, name), new FileInfo(p).Length));
            }

            var existing = await Task.Run(() => localPaths.Select(p => Combine(target, Path.GetFileName(p.TrimEnd('\\')))).Where(c.Exists).ToList());
            if (existing.Count > 0 && Dialog.Show(Window.GetWindow(this)!,
                    L.F("이미 있는 항목 {0}개를 덮어쓸까요?\n\n", existing.Count) + string.Join("\n", existing.Take(10)),
                    L.T("업로드"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            var total = files.Sum(f => f.Size);
            long done = 0;
            var ct = BeginTransfer(L.F("업로드 준비 ({0}개, {1})", files.Count, HumanSize(total)), total);
            await Task.Run(() => { foreach (var d in dirs) if (!c.Exists(d)) c.CreateDirectory(d); }, ct);
            foreach (var (local, remote, size) in files)
            {
                TransferText.Text = L.F("업로드 {0}  ({1} / {2})", Path.GetFileName(local), HumanSize(done), HumanSize(total));
                var baseDone = done;
                var progress = new Progress<Renci.SshNet.UploadFileProgressReport>(r => SetProgress(baseDone + (long)r.TotalBytesUploaded, total));
                await using (var fs = File.OpenRead(local))
                    await c.UploadFileAsync(fs, remote, true, progress, ct);
                done += size;
                SetProgress(done, total);
            }
            EndTransfer(L.F("업로드 완료: {0}개 ({1}) → {2}", files.Count, HumanSize(total), target));
        }
        catch (OperationCanceledException) { EndTransfer(L.T("업로드를 취소했습니다.")); }
        catch (Exception ex) { EndTransfer(L.F("업로드 실패: {0}", ex.Message)); }
        await NavigateAsync(_cwd);
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
        else
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
                await using (var fs = File.Create(local))
                    await c.DownloadFileAsync(remote, fs, progress, ct);
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
}
