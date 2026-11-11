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
    public string ToolTipText => $"{FullName}\n{Perms}  {(IsDir ? L.T("폴더") : SftpPanel.HumanSize(Size))}";
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
            ShowStatus(L.T("터미널 현재 폴더를 알 수 없습니다. 프롬프트가 user@host:/경로$ 형식이 아니고, 셸이 현재 폴더를 알려주지 않습니다(OSC 7)."));
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
        var auth = _getAuth() ?? throw new InvalidOperationException(L.T("터미널 로그인이 끝난 뒤 사용할 수 있습니다.\n잠시 후 새로고침을 누르세요."));
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
            ShowMessage(items.Count == 0 ? L.T("빈 폴더입니다.\n파일을 끌어다 놓으면 업로드합니다.") : null);
        }
        catch (Exception ex)
        {
            PathBox.Text = _cwd;
            ShowMessage(L.F("열 수 없습니다: {0}", ex.Message));
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
        var name = PromptDialog.Ask(Window.GetWindow(this), L.T("새 폴더"), L.F("{0} 에 만들 폴더 이름", _cwd));
        if (string.IsNullOrEmpty(name)) return;
        try
        {
            var c = await ClientAsync();
            await Task.Run(() => c.CreateDirectory(Combine(_cwd, name)));
            await NavigateAsync(_cwd);
        }
        catch (Exception ex) { Dialog.Show(Window.GetWindow(this)!, ex.Message, L.T("새 폴더")); }
    }

    private async void OnRename(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not SftpItem item) return;
        var name = PromptDialog.Ask(Window.GetWindow(this), L.T("이름 변경"), item.FullName, item.Name);
        if (string.IsNullOrEmpty(name) || name == item.Name) return;
        try
        {
            var c = await ClientAsync();
            await Task.Run(() => c.RenameFile(item.FullName, Combine(ParentPath(item.FullName), name)));
            await NavigateAsync(_cwd);
        }
        catch (Exception ex) { Dialog.Show(Window.GetWindow(this)!, ex.Message, L.T("이름 변경")); }
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        var items = Selected();
        if (items.Count == 0 || _busy) return;
        var dirs = items.Count(i => i.IsDir);
        var msg = L.F("{0}개 항목을 서버에서 삭제할까요?", items.Count) + (dirs > 0 ? L.F("\n(폴더 {0}개는 안의 내용까지 모두 삭제)", dirs) : "") +
                  "\n\n" + string.Join("\n", items.Take(10).Select(i => i.FullName)) + (items.Count > 10 ? "\n..." : "");
        if (Dialog.Show(Window.GetWindow(this)!, msg, L.T("삭제 확인"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            var c = await ClientAsync();
            var ct = BeginTransfer(L.T("삭제 중..."), 0);
            await Task.Run(() => { foreach (var i in items) DeleteRecursive(c, i.FullName, i.IsDir && !i.IsLink, ct); }, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Dialog.Show(Window.GetWindow(this)!, ex.Message, L.T("삭제")); }
        finally { EndTransfer(null); }
        await NavigateAsync(_cwd);
    }

    /// <summary>항목 하나씩 지우면서 취소 버튼을 확인한다 (이미 지운 것은 되돌릴 수 없음)</summary>
    private static void DeleteRecursive(SftpClient c, string path, bool isDir, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!isDir) { c.DeleteFile(path); return; }
        foreach (var f in c.ListDirectory(path).Where(f => f.Name is not "." and not ".."))
            DeleteRecursive(c, f.FullName, f.IsDirectory && !f.IsSymbolicLink, ct);
        c.DeleteDirectory(path);
    }

    private void OnCopyPath(object sender, RoutedEventArgs e)
    {
        var items = Selected();
        try { Clipboard.SetText(items.Count > 0 ? string.Join("\n", items.Select(i => i.FullName)) : _cwd); } catch { }
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
