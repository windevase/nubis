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
/// 업로드: 탐색기에서 끌어다 놓기(폴더 행 위에 놓으면 그 폴더로) / 버튼,
/// 다운로드: 탐색기로 끌어내기 / 버튼(폴더 선택), 파일 더블클릭: 임시 폴더로 받아 열기.
/// </summary>
public partial class SftpPanel : UserControl
{
    private readonly Func<(HostEntry Host, string User, string Password)?> _getAuth;
    private readonly Func<Task<string?>>? _getTerminalCwd;
    private SftpClient? _client;
    private string _cwd = "";
    private string _home = "/";
    private CancellationTokenSource? _cts;
    private bool _busy;

    public event Action? CloseRequested;

    /// <param name="getAuth">터미널이 로그인에 성공한 호스트·계정 (아직이면 null)</param>
    /// <param name="getTerminalCwd">터미널의 현재 폴더 (모르면 null)</param>
    public SftpPanel(Func<(HostEntry, string, string)?> getAuth, Func<Task<string?>>? getTerminalCwd = null)
    {
        InitializeComponent();
        _getAuth = getAuth;
        _getTerminalCwd = getTerminalCwd;
    }

    /// <summary>원격 홈 폴더 (연결 전이면 "/")</summary>
    public string Home => _home;

    /// <summary>터미널이 cd 한 폴더로 따라가기 (전송 중이거나 이미 그 폴더면 무시)</summary>
    public async Task FollowAsync(string path)
    {
        if (_busy || _cwd.Length == 0 || path == _cwd) return;
        await NavigateAsync(path);
    }

    private async void OnTerminalCwd(object sender, RoutedEventArgs e)
    {
        var p = _getTerminalCwd == null ? null : await _getTerminalCwd();
        if (string.IsNullOrEmpty(p))
        {
            ShowStatus("터미널 현재 폴더를 알 수 없습니다. 프롬프트가 user@host:/경로$ 형식이 아니고, 셸이 현재 폴더를 알려주지 않습니다(OSC 7).");
            return;
        }
        await NavigateAsync(p);
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
        else await EditFileAsync(item);
    }

    private async void OnListKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when FileList.SelectedItem is SftpItem item:
                if (item.IsDir) await NavigateAsync(item.FullName); else await EditFileAsync(item);
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
            DropHintText.Text = $"{DropTarget(e)} 에 업로드";
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
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = $"{_cwd} 에 업로드할 파일" };
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
            EndTransfer($"업로드 완료: {files.Count}개 ({HumanSize(total)}) → {target}");
        }
        catch (OperationCanceledException) { EndTransfer("업로드를 취소했습니다."); }
        catch (Exception ex) { EndTransfer($"업로드 실패: {ex.Message}"); }
        await NavigateAsync(_cwd);
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
            var ct = BeginTransfer("다운로드 목록 확인 중...", 0);

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

            EndTransfer($"다운로드 완료: {files.Count}개 ({HumanSize(total)}) → {destLabel}");
            return files.Select(f => f.Local).ToList();
        }
        catch (OperationCanceledException) { EndTransfer("다운로드를 취소했습니다."); }
        catch (Exception ex) { EndTransfer($"다운로드 실패: {ex.Message}"); }
        return null;
    }

    // ===================== 탐색기로 끌어내기 (drag-out) =====================
    // 끄는 동안에는 받지 않고, 마우스를 놓아 대상이 파일을 요청하는 순간(GetData) 임시 폴더로 받아 그 경로를 넘긴다.
    // 탐색기는 같은 드라이브면 임시 폴더에서 옮기고, 다른 드라이브면 복사한다.

    private readonly string _panelId = Guid.NewGuid().ToString("N");
    private Point? _dragStart;
    private SftpItem? _pressedItem;
    private bool _deferSelect;

    private static string DragTempRoot => Path.Combine(Path.GetTempPath(), "Nubis", "sftp-drag");

    private void OnListMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = null;
        _deferSelect = false;
        _pressedItem = (e.OriginalSource as FrameworkElement)?.DataContext as SftpItem;
        if (_pressedItem == null || e.ClickCount != 1) return;
        _dragStart = e.GetPosition(FileList);
        // 여러 개 선택한 상태에서 선택된 항목을 눌러 끌기 시작하면 선택이 풀리지 않게, 선택 변경은 버튼을 뗄 때로 미룬다
        if (FileList.SelectedItems.Contains(_pressedItem) && FileList.SelectedItems.Count > 1 && Keyboard.Modifiers == ModifierKeys.None)
        {
            _deferSelect = true;
            e.Handled = true;
        }
    }

    private void OnListMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_deferSelect && _pressedItem != null)
        {
            FileList.SelectedItems.Clear();
            FileList.SelectedItem = _pressedItem;
        }
        _deferSelect = false;
        _dragStart = null;
    }

    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(FileList) - start;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragStart = null;
        _deferSelect = false;
        var items = Selected();
        if (items.Count == 0 || _busy || _cwd.Length == 0) return;

        CleanupDragTemp();
        var dir = Path.Combine(DragTempRoot, Guid.NewGuid().ToString("N")[..12]);
        var data = new SftpDragData(this, items, DragTargets(items.Select(i => i.Name), dir), dir);
        try { DragDrop.DoDragDrop(FileList, data, DragDropEffects.Copy | DragDropEffects.Move); }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    /// <summary>끌어낼 항목들의 임시 폴더 안 로컬 경로 (Windows 이름으로 바꾼 뒤 겹치면 "이름 (1).확장자")</summary>
    public static List<string> DragTargets(IEnumerable<string> remoteNames, string dir)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var name in remoteNames)
        {
            var safe = SafeLocalName(name);
            var candidate = safe;
            for (var i = 1; !used.Add(candidate); i++)
                candidate = $"{Path.GetFileNameWithoutExtension(safe)} ({i}){Path.GetExtension(safe)}";
            result.Add(Path.Combine(dir, candidate));
        }
        return result;
    }

    /// <summary>
    /// 놓는 순간 호출: 임시 폴더로 받는 동안 메시지를 돌려 진행률 표시·취소 버튼이 동작하게 한다.
    /// 성공하면 true (모든 대상 경로가 만들어짐).
    /// </summary>
    internal bool DownloadForDrag(List<SftpItem> items, List<string> targets, string dir)
    {
        if (_busy) return false;
        Directory.CreateDirectory(dir);
        var task = DownloadCoreAsync(items.Zip(targets, (i, t) => (i, t)).ToList(), dir, "끌어다 놓은 위치");
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => Dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        return task.Result != null;
    }

    /// <summary>하루 지난 끌어내기 임시 폴더 정리 (다른 드라이브로 복사된 경우 남는다)</summary>
    private static void CleanupDragTemp()
    {
        try
        {
            if (!Directory.Exists(DragTempRoot)) return;
            foreach (var d in Directory.GetDirectories(DragTempRoot))
                if (Directory.GetCreationTimeUtc(d) < DateTime.UtcNow.AddDays(-1))
                    try { Directory.Delete(d, true); } catch { }
        }
        catch { }
    }

    /// <summary>
    /// 지연 렌더링 데이터 개체. 끄는 동안(마우스 버튼이 눌린 동안)은 받을 예정 경로만 돌려주고
    /// (탐색기가 같은 드라이브인지 판단할 때 경로만 본다), 버튼을 놓은 뒤 요청이 오면 그때 실제로 받는다.
    /// </summary>
    private sealed class SftpDragData : IDataObject
    {
        /// <summary>끌어낸 패널 식별 (같은 패널에 다시 놓기 방지)</summary>
        public const string SourceFormat = "Nubis.SftpDragSource";

        private readonly SftpPanel _panel;
        private readonly List<SftpItem> _items;
        private readonly List<string> _targets;
        private readonly string _dir;
        private readonly Dictionary<string, object> _extra = new(StringComparer.OrdinalIgnoreCase);
        private string[]? _result;
        private bool _done, _downloading;

        public SftpDragData(SftpPanel panel, List<SftpItem> items, List<string> targets, string dir)
        {
            _panel = panel; _items = items; _targets = targets; _dir = dir;
        }

        private static bool Is(string format, string name) => string.Equals(format, name, StringComparison.OrdinalIgnoreCase);

        private string[]? Files()
        {
            if (_done) return _result;
            if (_downloading) return null;
            if (System.Windows.Forms.Control.MouseButtons != System.Windows.Forms.MouseButtons.None) return _targets.ToArray();
            _downloading = true;
            try { _result = _panel.DownloadForDrag(_items, _targets, _dir) ? _targets.ToArray() : null; }
            finally { _downloading = false; _done = true; }
            return _result;
        }

        public object? GetData(string format, bool autoConvert)
        {
            if (Is(format, DataFormats.FileDrop)) return Files();
            if (Is(format, SourceFormat)) return _panel._panelId;
            return _extra.TryGetValue(format, out var v) ? v : null;
        }
        public object? GetData(string format) => GetData(format, true);
        public object? GetData(Type format) => GetData(format.FullName ?? "", true);

        public bool GetDataPresent(string format, bool autoConvert) =>
            (Is(format, DataFormats.FileDrop) && !(_done && _result == null)) || Is(format, SourceFormat) || _extra.ContainsKey(format);
        public bool GetDataPresent(string format) => GetDataPresent(format, true);
        public bool GetDataPresent(Type format) => GetDataPresent(format.FullName ?? "", true);

        public string[] GetFormats(bool autoConvert) => new[] { DataFormats.FileDrop, SourceFormat }.Concat(_extra.Keys).ToArray();
        public string[] GetFormats() => GetFormats(true);

        // 탐색기가 "Preferred DropEffect" 등을 써 넣는다
        public void SetData(string format, object data, bool autoConvert) => _extra[format] = data;
        public void SetData(string format, object data) => _extra[format] = data;
        public void SetData(Type format, object data) => _extra[format.FullName ?? format.Name] = data;
        public void SetData(object data) => _extra[data.GetType().FullName ?? "object"] = data;
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

    // ===================== 편집 (저장하면 서버에 자동 업로드) =====================
    // 임시 폴더로 받아 편집기로 열고, 파일이 저장될 때마다(내용이 바뀌었을 때만) 같은 원격 경로로 다시 올린다.
    // 편집하는 동안 서버 쪽 파일이 바뀌었으면(수정 시각·크기) 덮어쓰기 전에 묻는다.

    private sealed class EditSession : IDisposable
    {
        public required string Remote { get; init; }
        public required string Local { get; init; }
        public DateTime RemoteMtime { get; set; }
        public long RemoteSize { get; set; }
        public string Hash { get; set; } = "";
        public bool Uploading { get; set; }
        public FileSystemWatcher? Watcher { get; set; }
        public DispatcherTimer? Debounce { get; set; }
        public void Dispose()
        {
            Debounce?.Stop();
            if (Watcher != null) { Watcher.EnableRaisingEvents = false; Watcher.Dispose(); }
        }
    }

    private readonly Dictionary<string, EditSession> _edits = new(StringComparer.Ordinal);
    private static string EditTempRoot => Path.Combine(Path.GetTempPath(), "Nubis", "sftp-edit");

    /// <summary>편집 중인 파일 수</summary>
    public int EditingCount => _edits.Count;

    private async Task EditFileAsync(SftpItem item)
    {
        // 이미 편집 중이면 받은 파일을 그대로 다시 연다 (편집기에서 고친 내용을 덮어쓰지 않게)
        if (_edits.TryGetValue(item.FullName, out var existing) && File.Exists(existing.Local)) { OpenInEditor(existing.Local); return; }
        if (_busy) return;
        if (item.Size > 50L * 1024 * 1024 &&
            Dialog.Show(Window.GetWindow(this)!, $"{HumanSize(item.Size)} 파일입니다. 받아서 편집할까요?", "편집",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            var c = await ClientAsync();
            CleanupEditTemp();
            var host = SafeLocalName(_getAuth()?.Host.DisplayName ?? "sftp");
            var dir = Path.Combine(EditTempRoot, host, Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);
            var local = Path.Combine(dir, SafeLocalName(item.Name));

            var ct = BeginTransfer($"편집용으로 받는 중: {item.Name}", item.Size);
            var progress = new Progress<Renci.SshNet.DownloadFileProgressReport>(r => SetProgress((long)r.TotalBytesDownloaded, item.Size));
            await using (var fs = File.Create(local))
                await c.DownloadFileAsync(item.FullName, fs, progress, ct);
            var attr = await Task.Run(() => c.GetAttributes(item.FullName), ct);

            var s = new EditSession { Remote = item.FullName, Local = local, RemoteMtime = attr.LastWriteTimeUtc, RemoteSize = attr.Size, Hash = HashFile(local) };
            Watch(s);
            _edits[item.FullName] = s;
            EndTransfer($"편집 중: {item.Name} — 저장하면 서버에 자동으로 올립니다");
            OpenInEditor(local);
        }
        catch (OperationCanceledException) { EndTransfer("편집 열기를 취소했습니다."); }
        catch (Exception ex) { EndTransfer($"편집 열기 실패: {ex.Message}"); }
    }

    private void Watch(EditSession s)
    {
        var debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        debounce.Tick += async (_, _) => { debounce.Stop(); await SyncEditAsync(s); };
        // 편집기에 따라 직접 쓰거나(VS Code·메모장) 임시 파일로 쓴 뒤 이름을 바꾼다 → 폴더 전체를 보고 이름으로 거른다
        var name = Path.GetFileName(s.Local);
        var w = new FileSystemWatcher(Path.GetDirectoryName(s.Local)!)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
        };
        void Poke(string? changed)
        {
            if (!string.Equals(changed, name, StringComparison.OrdinalIgnoreCase)) return;
            Dispatcher.BeginInvoke(() => { debounce.Stop(); debounce.Start(); });
        }
        w.Changed += (_, e) => Poke(e.Name);
        w.Created += (_, e) => Poke(e.Name);
        w.Renamed += (_, e) => Poke(e.Name);
        w.EnableRaisingEvents = true;
        s.Watcher = w;
        s.Debounce = debounce;
    }

    private async Task SyncEditAsync(EditSession s)
    {
        if (!_edits.ContainsValue(s) || !File.Exists(s.Local)) return;
        if (s.Uploading) { s.Debounce?.Start(); return; }
        byte[] data;
        try
        {
            using var fs = new FileStream(s.Local, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var ms = new MemoryStream();
            fs.CopyTo(ms);
            data = ms.ToArray();
        }
        catch (IOException) { s.Debounce?.Start(); return; }   // 편집기가 아직 쓰는 중 → 잠시 뒤 다시
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));
        if (hash == s.Hash) return;   // 내용이 같으면(저장만 누름) 올리지 않음

        var name = RemoteName(s.Remote);
        s.Uploading = true;
        try
        {
            var c = await ClientAsync();
            var attr = await Task.Run(() => c.Exists(s.Remote) ? c.GetAttributes(s.Remote) : null);
            if (attr != null && (attr.LastWriteTimeUtc != s.RemoteMtime || attr.Size != s.RemoteSize) &&
                Dialog.Show(Window.GetWindow(this)!,
                    $"편집을 시작한 뒤 서버의 파일이 바뀌었습니다.\n\n{s.Remote}\n서버: {attr.LastWriteTime:yyyy-MM-dd HH:mm:ss} · {HumanSize(attr.Size)}\n\n" +
                    "내가 편집한 내용으로 덮어쓸까요? (아니요를 누르면 이번 저장은 올리지 않습니다)",
                    "편집 충돌", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                s.Hash = hash;   // 같은 내용으로 다시 묻지 않음
                ShowStatus($"올리지 않음: {name} (서버 파일이 바뀌어 있음)");
                return;
            }
            using (var up = new MemoryStream(data))
                await c.UploadFileAsync(up, s.Remote, true, new Progress<Renci.SshNet.UploadFileProgressReport>(_ => { }), CancellationToken.None);
            var after = await Task.Run(() => c.GetAttributes(s.Remote));
            s.RemoteMtime = after.LastWriteTimeUtc;
            s.RemoteSize = after.Size;
            s.Hash = hash;
            ShowStatus($"저장됨 → 서버에 올림: {name}  ({DateTime.Now:HH:mm:ss}, {HumanSize(data.Length)})");
            if (ParentPath(s.Remote) == _cwd && !_busy) await NavigateAsync(_cwd);   // 목록의 크기·시각 갱신
        }
        catch (Exception ex) { ShowStatus($"자동 업로드 실패: {name} — {ex.Message} (다시 저장하면 재시도)"); }
        finally { s.Uploading = false; }
    }

    private static string RemoteName(string remote)
    {
        var p = remote.TrimEnd('/');
        return p[(p.LastIndexOf('/') + 1)..];
    }

    private static string HashFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs));
    }

    /// <summary>설정의 편집기로 연다 (비우면 Windows 기본 프로그램, 연결된 프로그램이 없으면 메모장)</summary>
    private void OpenInEditor(string local)
    {
        var editor = SettingsStore.Current.SftpEditor.Trim().Trim('"');
        try
        {
            if (editor.Length > 0) Process.Start(new ProcessStartInfo(editor, $"\"{local}\"") { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo(local) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{local}\"") { UseShellExecute = true }); }
            catch (Exception ex) { ShowStatus($"편집기를 열지 못했습니다: {ex.Message}"); }
        }
    }

    /// <summary>VS Code 실행 파일 (설치돼 있지 않으면 null)</summary>
    public static string? FindVsCode()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "Code.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "Code.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>하루 지난 편집 임시 폴더 정리 (지금 편집 중인 것은 남김)</summary>
    private void CleanupEditTemp()
    {
        try
        {
            if (!Directory.Exists(EditTempRoot)) return;
            var inUse = _edits.Values.Select(e => Path.GetDirectoryName(e.Local)!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var hostDir in Directory.GetDirectories(EditTempRoot))
            foreach (var d in Directory.GetDirectories(hostDir))
                if (!inUse.Contains(d) && Directory.GetLastWriteTimeUtc(d) < DateTime.UtcNow.AddDays(-1))
                    try { Directory.Delete(d, true); } catch { }
        }
        catch { }
    }

    /// <summary>전송 중이 아닐 때 아래 상태 줄에 잠깐 알림</summary>
    private async void ShowStatus(string text)
    {
        if (_busy) return;
        TransferBar.Visibility = Visibility.Visible;
        CancelButton.Visibility = Visibility.Collapsed;
        TransferProgress.IsIndeterminate = false;
        TransferProgress.Value = 1;
        TransferText.Text = text;
        TransferText.ToolTip = text;
        await Task.Delay(7000);
        if (!_busy && TransferText.Text == text) TransferBar.Visibility = Visibility.Collapsed;
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
        foreach (var ed in _edits.Values) ed.Dispose();
        _edits.Clear();
        _cts?.Cancel();
        var c = _client;
        _client = null;
        Task.Run(() => { try { c?.Dispose(); } catch { } });
    }
}
