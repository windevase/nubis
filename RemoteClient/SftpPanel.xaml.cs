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
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace Nubis;

public sealed class SftpItem
{
    private static readonly Brush DirBrush = Frozen(Color.FromRgb(0xF5, 0xB7, 0x40));
    private static readonly Brush FileBrush = Frozen(Color.FromRgb(0x8A, 0x90, 0xA8));
    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    public required string Name { get; init; }
    public required string FullName { get; init; }
    public bool IsDir { get; init; }
    public bool IsLink { get; init; }
    public long Size { get; init; }
    public DateTime Modified { get; init; }
    public string Perms { get; init; } = "";

    public string Glyph => IsDir ? "" : IsLink ? "" : "";
    public Brush GlyphBrush => IsDir ? DirBrush : FileBrush;
    public string SizeText => IsDir ? "" : SftpPanel.HumanSize(Size);
    public string ModifiedText => Modified == default ? "" : Modified.ToString("yyyy-MM-dd HH:mm");
    public string ToolTipText => $"{FullName}\n{Perms}  {(IsDir ? "폴더" : SftpPanel.HumanSize(Size))}";
}

/// <summary>
/// SSH 탭 왼쪽의 SFTP 파일 패널. 터미널 세션이 로그인한 계정으로 별도 SFTP 연결을 연다.
/// 업로드: 탐색기에서 끌어다 놓기 / 버튼, 다운로드: 버튼(폴더 선택), 파일 더블클릭: 임시 폴더로 받아 열기.
/// </summary>
public partial class SftpPanel : UserControl
{
    private readonly Func<(HostEntry Host, string User, string Password)?> _getAuth;
    private SftpClient? _client;
    private string _cwd = "";
    private string _home = "/";
    private CancellationTokenSource? _cts;
    private bool _busy;

    public event Action? CloseRequested;

    /// <param name="getAuth">터미널이 로그인에 성공한 호스트·계정 (아직이면 null)</param>
    public SftpPanel(Func<(HostEntry, string, string)?> getAuth)
    {
        InitializeComponent();
        _getAuth = getAuth;
    }

    // ===================== 연결 / 탐색 =====================

    public async Task OpenAsync()
    {
        if (_cwd.Length > 0) return;
        try
        {
            await ClientAsync();
            await NavigateAsync(_home);
        }
        catch (Exception ex) { ShowMessage(ex.Message); }
    }

    private async Task<SftpClient> ClientAsync()
    {
        if (_client is { IsConnected: true }) return _client;
        var auth = _getAuth() ?? throw new InvalidOperationException("터미널 로그인이 끝난 뒤 사용할 수 있습니다.\n잠시 후 새로고침을 누르세요.");
        _client?.Dispose();
        var client = new SftpClient(SshAuth.Build(auth.Host, auth.User, auth.Password));
        string? keyError = null;
        client.HostKeyReceived += (_, e) => e.CanTrust = (keyError = SshAuth.VerifyAcceptNew(auth.Host, e)) == null;
        try { await Task.Run(client.Connect); }
        catch when (keyError != null) { client.Dispose(); throw new InvalidOperationException(keyError); }
        _home = client.WorkingDirectory;
        _client = client;
        return client;
    }

    private async Task NavigateAsync(string path)
    {
        try
        {
            var c = await ClientAsync();
            var files = await Task.Run(() => c.ListDirectory(path).Where(f => f.Name is not "." and not "..").ToList());
            var items = await Task.Run(() => files.Select(f =>
            {
                var isDir = f.IsDirectory;
                if (f.IsSymbolicLink)
                {
                    try { isDir = c.GetAttributes(f.FullName).IsDirectory; } catch { }
                }
                return new SftpItem
                {
                    Name = f.Name, FullName = f.FullName, IsDir = isDir, IsLink = f.IsSymbolicLink,
                    Size = f.Length, Modified = f.LastWriteTime, Perms = Perms(f),
                };
            }).OrderByDescending(i => i.IsDir).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList());

            _cwd = path;
            PathBox.Text = path;
            FileList.ItemsSource = items;
            ShowMessage(items.Count == 0 ? "빈 폴더입니다.\n파일을 끌어다 놓으면 업로드합니다." : null);
        }
        catch (Exception ex)
        {
            PathBox.Text = _cwd;
            ShowMessage($"열 수 없습니다: {ex.Message}");
        }
    }

    private void ShowMessage(string? text)
    {
        MessageText.Text = text ?? "";
        MessageText.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string Perms(ISftpFile f) =>
        (f.IsDirectory ? "d" : f.IsSymbolicLink ? "l" : "-") +
        (f.OwnerCanRead ? "r" : "-") + (f.OwnerCanWrite ? "w" : "-") + (f.OwnerCanExecute ? "x" : "-") +
        (f.GroupCanRead ? "r" : "-") + (f.GroupCanWrite ? "w" : "-") + (f.GroupCanExecute ? "x" : "-") +
        (f.OthersCanRead ? "r" : "-") + (f.OthersCanWrite ? "w" : "-") + (f.OthersCanExecute ? "x" : "-");

    public static string Combine(string dir, string name) => dir.EndsWith('/') ? dir + name : dir + "/" + name;

    public static string ParentPath(string path)
    {
        var p = path.TrimEnd('/');
        var i = p.LastIndexOf('/');
        return i <= 0 ? "/" : p[..i];
    }

    public static string HumanSize(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1024 * 1024 => $"{b / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{b / 1024.0 / 1024:0.#} MB",
        _ => $"{b / 1024.0 / 1024 / 1024:0.##} GB",
    };

    private List<SftpItem> Selected() => FileList.SelectedItems.Cast<SftpItem>().ToList();

    // ===================== 툴바 / 키보드 =====================

    private async void OnUp(object sender, RoutedEventArgs e) => await NavigateAsync(ParentPath(_cwd));
    private async void OnHome(object sender, RoutedEventArgs e) => await NavigateAsync(_home);
    private async void OnRefresh(object sender, RoutedEventArgs e) => await NavigateAsync(_cwd.Length > 0 ? _cwd : _home);
    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private async void OnPathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        var p = PathBox.Text.Trim();
        if (p.Length > 0) await NavigateAsync(p);
    }

    private async void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not SftpItem item) return;
        if (item.IsDir) await NavigateAsync(item.FullName);
        else await OpenFileAsync(item);
    }

    private async void OnListKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when FileList.SelectedItem is SftpItem item:
                if (item.IsDir) await NavigateAsync(item.FullName); else await OpenFileAsync(item);
                break;
            case Key.Back: await NavigateAsync(ParentPath(_cwd)); break;
            case Key.F5: await NavigateAsync(_cwd); break;
            case Key.Delete: OnDelete(sender, e); break;
            case Key.F2: OnRename(sender, e); break;
            default: return;
        }
        e.Handled = true;
    }

    private async void OnNewFolder(object sender, RoutedEventArgs e)
    {
        var name = PromptDialog.Ask(Window.GetWindow(this), "새 폴더", $"{_cwd} 에 만들 폴더 이름");
        if (string.IsNullOrEmpty(name)) return;
        try
        {
            var c = await ClientAsync();
            await Task.Run(() => c.CreateDirectory(Combine(_cwd, name)));
            await NavigateAsync(_cwd);
        }
        catch (Exception ex) { Dialog.Show(Window.GetWindow(this)!, ex.Message, "새 폴더"); }
    }

    private async void OnRename(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not SftpItem item) return;
        var name = PromptDialog.Ask(Window.GetWindow(this), "이름 변경", item.FullName, item.Name);
        if (string.IsNullOrEmpty(name) || name == item.Name) return;
        try
        {
            var c = await ClientAsync();
            await Task.Run(() => c.RenameFile(item.FullName, Combine(ParentPath(item.FullName), name)));
            await NavigateAsync(_cwd);
        }
        catch (Exception ex) { Dialog.Show(Window.GetWindow(this)!, ex.Message, "이름 변경"); }
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        var items = Selected();
        if (items.Count == 0 || _busy) return;
        var dirs = items.Count(i => i.IsDir);
        var msg = $"{items.Count}개 항목을 서버에서 삭제할까요?" + (dirs > 0 ? $"\n(폴더 {dirs}개는 안의 내용까지 모두 삭제)" : "") +
                  "\n\n" + string.Join("\n", items.Take(10).Select(i => i.FullName)) + (items.Count > 10 ? "\n..." : "");
        if (Dialog.Show(Window.GetWindow(this)!, msg, "삭제 확인", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            var c = await ClientAsync();
            BeginTransfer("삭제 중...", 0);
            await Task.Run(() => { foreach (var i in items) DeleteRecursive(c, i.FullName, i.IsDir && !i.IsLink); });
        }
        catch (Exception ex) { Dialog.Show(Window.GetWindow(this)!, ex.Message, "삭제"); }
        finally { EndTransfer(null); }
        await NavigateAsync(_cwd);
    }

    private static void DeleteRecursive(SftpClient c, string path, bool isDir)
    {
        if (!isDir) { c.DeleteFile(path); return; }
        foreach (var f in c.ListDirectory(path).Where(f => f.Name is not "." and not ".."))
            DeleteRecursive(c, f.FullName, f.IsDirectory && !f.IsSymbolicLink);
        c.DeleteDirectory(path);
    }

    private void OnCopyPath(object sender, RoutedEventArgs e)
    {
        var items = Selected();
        try { Clipboard.SetText(items.Count > 0 ? string.Join("\n", items.Select(i => i.FullName)) : _cwd); } catch { }
    }

    // ===================== 업로드 =====================

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && !_busy ? DragDropEffects.Copy : DragDropEffects.None;
        if (e.Effects != DragDropEffects.None)
        {
            DropHintText.Text = $"{_cwd} 에 업로드";
            DropHint.Visibility = Visibility.Visible;
        }
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e) => DropHint.Visibility = Visibility.Collapsed;

    private async void OnDrop(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0) await UploadAsync(paths);
    }

    private async void OnUploadClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = $"{_cwd} 에 업로드할 파일" };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) await UploadAsync(dlg.FileNames);
    }

    private async Task UploadAsync(string[] localPaths)
    {
        if (_busy || _cwd.Length == 0) return;
        var target = _cwd;
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
                    $"이미 있는 항목 {existing.Count}개를 덮어쓸까요?\n\n" + string.Join("\n", existing.Take(10)),
                    "업로드", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            var total = files.Sum(f => f.Size);
            long done = 0;
            var ct = BeginTransfer($"업로드 준비 ({files.Count}개, {HumanSize(total)})", total);
            await Task.Run(() => { foreach (var d in dirs) if (!c.Exists(d)) c.CreateDirectory(d); }, ct);
            foreach (var (local, remote, size) in files)
            {
                TransferText.Text = $"업로드 {Path.GetFileName(local)}  ({HumanSize(done)} / {HumanSize(total)})";
                var baseDone = done;
                var progress = new Progress<Renci.SshNet.UploadFileProgressReport>(r => SetProgress(baseDone + (long)r.TotalBytesUploaded, total));
                await using (var fs = File.OpenRead(local))
                    await c.UploadFileAsync(fs, remote, true, progress, ct);
                done += size;
                SetProgress(done, total);
            }
            EndTransfer($"업로드 완료: {files.Count}개 ({HumanSize(total)})");
        }
        catch (OperationCanceledException) { EndTransfer("업로드를 취소했습니다."); }
        catch (Exception ex) { EndTransfer($"업로드 실패: {ex.Message}"); }
        await NavigateAsync(target);
    }

    // ===================== 다운로드 =====================

    private async void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        var items = Selected();
        if (items.Count == 0 || _busy) return;
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "다운로드할 폴더 선택", InitialDirectory = downloads };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        await DownloadAsync(items, dlg.FolderName, openWhenDone: false);
    }

    private async void OnOpenItem(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not SftpItem item) return;
        if (item.IsDir) await NavigateAsync(item.FullName);
        else await OpenFileAsync(item);
    }

    /// <summary>임시 폴더로 받아 기본 프로그램으로 열기 (로그·설정 파일 확인용)</summary>
    private async Task OpenFileAsync(SftpItem item)
    {
        if (item.Size > 200L * 1024 * 1024 &&
            Dialog.Show(Window.GetWindow(this)!, $"{HumanSize(item.Size)} 파일입니다. 받아서 열까요?", "열기",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var host = _getAuth()?.Host.Address ?? "sftp";
        var dir = Path.Combine(Path.GetTempPath(), "Nubis", "sftp", host, Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        await DownloadAsync(new List<SftpItem> { item }, dir, openWhenDone: true);
    }

    private async Task DownloadAsync(List<SftpItem> items, string localDir, bool openWhenDone)
    {
        if (_busy) return;
        try
        {
            var c = await ClientAsync();
            var ct = BeginTransfer("다운로드 목록 확인 중...", 0);

            var files = new List<(string Remote, string Local, long Size)>();
            var dirs = new List<string>();
            await Task.Run(() =>
            {
                foreach (var i in items) Plan(c, i.FullName, i.IsDir, UniquePath(Path.Combine(localDir, SafeLocalName(i.Name)), i.IsDir), files, dirs);
            }, ct);

            // 원격 이름에서 만든 경로가 선택한 폴더 밖으로 나가지 않는지 확인
            var root = Path.GetFullPath(localDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (files.Any(f => !Path.GetFullPath(f.Local).StartsWith(root, StringComparison.OrdinalIgnoreCase)) ||
                dirs.Any(d => !Path.GetFullPath(d).StartsWith(root, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("허용되지 않는 파일 이름이 있어 다운로드를 중단했습니다.");

            var total = files.Sum(f => f.Size);
            long done = 0;
            foreach (var d in dirs) Directory.CreateDirectory(d);
            foreach (var (remote, local, size) in files)
            {
                TransferText.Text = $"다운로드 {Path.GetFileName(local)}  ({HumanSize(done)} / {HumanSize(total)})";
                var baseDone = done;
                var progress = new Progress<Renci.SshNet.DownloadFileProgressReport>(r => SetProgress(baseDone + (long)r.TotalBytesDownloaded, total));
                await using (var fs = File.Create(local))
                    await c.DownloadFileAsync(remote, fs, progress, ct);
                done += size;
                SetProgress(done, total);
            }

            if (openWhenDone && files.Count == 1)
            {
                EndTransfer(null);
                Process.Start(new ProcessStartInfo(files[0].Local) { UseShellExecute = true });
            }
            else
            {
                EndTransfer($"다운로드 완료: {files.Count}개 ({HumanSize(total)}) → {localDir}");
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{localDir}\"") { UseShellExecute = true });
            }
        }
        catch (OperationCanceledException) { EndTransfer("다운로드를 취소했습니다."); }
        catch (Exception ex) { EndTransfer($"다운로드 실패: {ex.Message}"); }
    }

    private static void Plan(SftpClient c, string remote, bool isDir, string local,
        List<(string, string, long)> files, List<string> dirs)
    {
        if (!isDir)
        {
            files.Add((remote, local, c.GetAttributes(remote).Size));
            return;
        }
        dirs.Add(local);
        foreach (var f in c.ListDirectory(remote).Where(f => f.Name is not "." and not ".."))
            Plan(c, f.FullName, f.IsDirectory && !f.IsSymbolicLink, Path.Combine(local, SafeLocalName(f.Name)), files, dirs);
    }

    /// <summary>
    /// 원격 이름을 Windows 파일 이름으로: 경로 구분자·예약 문자(\ / : * ? " &lt; &gt; |)·제어 문자를 _ 로,
    /// "." / ".." 와 끝의 점·공백 제거, 예약 장치 이름(CON, NUL 등) 회피. 콜론은 NTFS 대체 스트림이 되는 것을 막기 위해 치환.
    /// </summary>
    public static string SafeLocalName(string name)
    {
        var chars = name.Select(ch => ch < 32 || "\\/:*?\"<>|".Contains(ch) ? '_' : ch).ToArray();
        var s = new string(chars).TrimEnd('.', ' ');
        if (s.Length == 0 || s is "." or "..") s = "_";
        var stem = s.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3])))
            s = "_" + s;
        return s;
    }

    /// <summary>같은 이름이 이미 있으면 "이름 (1).확장자" 형태로 (기존 로컬 파일 덮어쓰기 방지)</summary>
    public static string UniquePath(string path, bool isDir)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = isDir ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
        var ext = isDir ? "" : Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    // ===================== 전송 상태 표시 =====================

    private CancellationToken BeginTransfer(string text, long total)
    {
        _busy = true;
        _cts = new CancellationTokenSource();
        TransferBar.Visibility = Visibility.Visible;
        CancelButton.Visibility = Visibility.Visible;
        TransferText.Text = text;
        TransferProgress.IsIndeterminate = total == 0;
        TransferProgress.Value = 0;
        return _cts.Token;
    }

    private void SetProgress(long done, long total)
    {
        TransferProgress.IsIndeterminate = false;
        TransferProgress.Value = total > 0 ? Math.Min(1.0, (double)done / total) : 1;
    }

    private async void EndTransfer(string? result)
    {
        _busy = false;
        _cts?.Dispose();
        _cts = null;
        CancelButton.Visibility = Visibility.Collapsed;
        TransferProgress.IsIndeterminate = false;
        if (result == null) { TransferBar.Visibility = Visibility.Collapsed; return; }
        TransferText.Text = result;
        TransferProgress.Value = 1;
        await Task.Delay(6000);
        if (!_busy) TransferBar.Visibility = Visibility.Collapsed;
    }

    private void OnCancelTransfer(object sender, RoutedEventArgs e) => _cts?.Cancel();

    public void Close()
    {
        _cts?.Cancel();
        var c = _client;
        _client = null;
        Task.Run(() => { try { c?.Dispose(); } catch { } });
    }
}
