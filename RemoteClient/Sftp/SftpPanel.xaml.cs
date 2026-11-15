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
    /// <summary>
    /// 붓(정적 필드)을 UI 스레드에서 먼저 만든다 — 항목은 백그라운드에서 만들므로, 그쪽에서 처음 만들면 SolidColorBrush 가
    /// 스레드 풀 스레드에 Dispatcher(숨은 메시지 창 포함)를 새로 만들어 남긴다
    /// </summary>
    internal static void EnsureStatics()
    {
        _ = DirBrush;
        _ = FileBrush;
    }

    public required string Name { get; init; }
    public required string FullName { get; init; }
    public bool IsDir { get; init; }
    public bool IsLink { get; init; }
    public long Size { get; init; }
    public DateTime Modified { get; init; }
    public string Perms { get; init; } = "";
    /// <summary>소켓·FIFO·장치 파일 (링크면 가리키는 대상 기준) — 열기·편집·다운로드하지 않는다 (/dev/zero 처럼 끝없이 읽히거나 멈춤)</summary>
    public bool IsSpecial { get; init; }

    public string Glyph => IsDir ? "" : IsLink ? "" : "";
    public Brush GlyphBrush => IsDir ? DirBrush : FileBrush;
    public string SizeText => IsDir ? "" : SftpPanel.HumanSize(Size);
    public string ModifiedText => Modified == default ? "" : Modified.ToString("yyyy-MM-dd HH:mm");
    public string ToolTipText => $"{FullName}\n{Perms}  {(IsDir ? L.T("폴더") : SftpPanel.HumanSize(Size))}";
}

/// <summary>
/// SSH 탭 왼쪽의 SFTP 파일 패널. 터미널의 SSH 연결 위에 SFTP 채널을 열고(다시 인증하지 않음),
/// 그럴 수 없으면 터미널이 로그인한 계정으로 별도 SFTP 연결을 연다.
/// 업로드: 탐색기에서 끌어다 놓기(폴더 행 위에 놓으면 그 폴더로) / 버튼,
/// 다운로드: 탐색기로 끌어내기 / 버튼(폴더 선택), 파일 더블클릭: 임시 폴더로 받아 열기.
/// </summary>
public partial class SftpPanel : UserControl
{
    private readonly Func<(HostEntry Host, string User, string Password)?> _getAuth;
    private readonly Func<Task<string?>>? _getTerminalCwd;
    private readonly Func<SshClient?>? _getShell;
    private SftpClient? _client;
    private string _cwd = "";
    private string _home = "/";
    private CancellationTokenSource? _cts;
    private bool _busy;

    public event Action? CloseRequested;

    /// <param name="getAuth">터미널이 로그인에 성공한 호스트·계정 (아직이면 null)</param>
    /// <param name="getTerminalCwd">터미널의 현재 폴더 (모르면 null)</param>
    /// <param name="getShell">터미널의 SSH 연결 — 이 위에 SFTP 채널을 연다</param>
    public SftpPanel(Func<(HostEntry, string, string)?> getAuth, Func<Task<string?>>? getTerminalCwd = null, Func<SshClient?>? getShell = null)
    {
        InitializeComponent();
        SftpItem.EnsureStatics();
        _getAuth = getAuth;
        _getTerminalCwd = getTerminalCwd;
        _getShell = getShell;
    }

    /// <summary>원격 홈 폴더 (연결 전이면 "/")</summary>
    public string Home => _home;
    /// <summary>연결해서 목록을 한 번 이상 열었음 (Home 이 실제 값)</summary>
    public bool IsOpen => _cwd.Length > 0;

    /// <summary>터미널이 cd 한 폴더로 따라가기 (전송 중이거나 이미 그 폴더로 가는 중이면 무시)</summary>
    public async Task FollowAsync(string path)
    {
        if (_cwd.Length == 0) return;
        if (path == (_navTarget ?? _cwd)) { _pendingFollow = null; return; }
        // 전송 중에는 바로 가지 않고 기억해 뒀다가 전송이 끝나면 간다 (EndTransfer) — 전송 중 cd 가 무시돼 패널이 옛 폴더에 남지 않게
        if (_busy) { _pendingFollow = path; return; }
        await NavigateAsync(path);
    }

    /// <summary>전송 중에 터미널이 이동한 폴더 — 전송이 끝나면 그리로 간다 (사용자가 직접 이동하면 버린다)</summary>
    private string? _pendingFollow;

    private async void OnTerminalCwd(object sender, RoutedEventArgs e)
    {
        var p = _getTerminalCwd == null ? null : await _getTerminalCwd();
        if (string.IsNullOrEmpty(p))
        {
            ShowStatus(L.T("터미널 현재 폴더를 알 수 없습니다. 프롬프트에서 경로를 읽을 수 없고, 셸이 현재 폴더를 알려주지 않습니다(OSC 7)."));
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

    /// <summary>터미널이 다시 연결된 뒤: 열려 있으면 지금 폴더 목록을 새 연결로 다시 읽고, 아직 못 열었으면 연다</summary>
    public Task RefreshAsync() => _cwd.Length > 0 ? NavigateAsync(_cwd) : OpenAsync();

    // SFTP 연결 열기는 한 번에 하나만 — 동시에 부른 쪽마다 연결을 열면 sudo 비밀번호 창이 두 번 뜨고 채널이 샌다.
    // 목록 읽기(NavigateAsync)도 같은 잠금 안에서 해, 사용자 전환(RunAsAsync)이 쓰는 중인 연결을 닫지 않게 한다
    private readonly SemaphoreSlim _clientLock = new(1, 1);
    /// <summary>패널이 닫힘 (탭 닫힘) — 이후 연 연결은 바로 닫는다</summary>
    private bool _closed;
    /// <summary>별도 연결의 인증이 거부·취소됨 — 터미널이 다시 연결될 때(ResetRunAs)까지 다시 시도하지 않는다 (OTP 창 반복·계정 잠금 방지)</summary>
    private string? _separateAuthFailed;

    private async Task<SftpClient> ClientAsync()
    {
        if (_client is { IsConnected: true } ready) return ready;
        await _clientLock.WaitAsync();
        try { return await ClientCoreAsync(); }
        finally { _clientLock.Release(); }
    }

    /// <summary>연결을 연다 — _clientLock 을 잡은 상태에서만 호출</summary>
    private async Task<SftpClient> ClientCoreAsync()
    {
        if (_closed) throw new OperationCanceledException();
        if (_client is { IsConnected: true } ready) return ready;   // 기다리는 동안 다른 쪽이 열었음
        var auth = _getAuth() ?? throw new InvalidOperationException(L.T("터미널 로그인이 끝난 뒤 사용할 수 있습니다.\n잠시 후 새로고침을 누르세요."));
        // 터미널이 끊겨 있거나 다시 연결하는 중이면 기다린다 — 그 사이 새로고침·편집 저장이 별도 로그인(OTP 창)을 띄우지 않게
        if (_getShell != null && _getShell() is not { IsConnected: true })
            throw new InvalidOperationException(L.T("터미널 연결을 기다리는 중입니다. 터미널이 다시 연결된 뒤 새로고침을 누르세요."));
        var old = _client;   // 터미널이 재연결돼 이미 끊긴 채널일 수 있다
        _client = null;
        DisposeLater(old);

        var client = await OpenClientAsync(auth);
        if (_closed)
        {
            // 연결하는 동안 패널이 닫힘 — Close 가 놓친 연결이므로 여기서 닫는다
            DisposeLater(client);
            throw new OperationCanceledException();
        }
        _client = client;
        return client;
    }

    /// <summary>SFTP 요청 하나의 응답 대기 한도 — 터미널 공유·sudo·별도 연결 모두 같은 값</summary>
    private static readonly TimeSpan SftpOperationTimeout = TimeSpan.FromSeconds(60);

    private async Task<SftpClient> OpenClientAsync((HostEntry Host, string User, string Password) auth)
    {
        // 0) 터미널이 다른 사용자로 전환돼 있으면 그 권한으로 (sudo, SftpPanel.RunAs.cs)
        if (await OpenRunAsAsync(auth) is { } runAs)
        {
            runAs.OperationTimeout = SftpOperationTimeout;   // 멈춘 채널에서 무한정 기다리지 않게 (별도 연결과 같은 값)
            return runAs;
        }

        // 1) 터미널 연결 위에 SFTP 채널 — 인증을 다시 하지 않는다 (OTP 서버에서 두 번 묻지 않게)
        try
        {
            var shell = _getShell?.Invoke();
            if (await Task.Run(() => SharedSftpClient.TryOpen(shell)) is { } shared)
            {
                shared.OperationTimeout = SftpOperationTimeout;
                _home = shared.WorkingDirectory;
                return shared;
            }
        }
        catch (Exception ex)
        {
            // 서버의 세션 수 제한(MaxSessions) 등 — 별도 연결로 넘어간다
            Debug.WriteLine($"SFTP 채널을 터미널 연결에서 열지 못해 별도 연결로 넘어감: {ex.Message}");
        }

        // 2) 별도 연결 — 서버가 OTP 등을 요구하면 다시 묻는다.
        //    점프 서버를 거치는 호스트는 터미널 연결 공유로만 (점프 서버를 건너뛰고 직접 붙지 않게)
        if (auth.Host.Jump != null)
            throw new InvalidOperationException(L.T("점프 서버를 거치는 호스트는 터미널 연결 위에서만 SFTP 를 열 수 있습니다. 터미널이 연결된 뒤 새로고침을 누르세요."));
        // 앞서 인증이 거부·취소됐으면 다시 묻지 않는다 (인증 실패는 재시도하지 않음)
        if (_separateAuthFailed != null)
            throw new InvalidOperationException(L.F("별도 SFTP 연결의 인증에 실패했습니다: {0}\n터미널이 다시 연결될 때까지 다시 시도하지 않습니다.", _separateAuthFailed));
        var target = $"SFTP · {auth.User}@{auth.Host.DisplayName} ({auth.Host.Address})";
        var prompter = new AuthPrompter(req => Dispatcher.Invoke(() => AuthPromptDialog.Ask(Window.GetWindow(this), target, req)));
        var client = new SftpClient(SshAuth.Build(auth.Host, auth.User, auth.Password, prompter: prompter))
        {
            // 끊긴 연결을 알아채고, 응답 없는 서버에서 무한정 멈추지 않게
            KeepAliveInterval = TimeSpan.FromSeconds(30),
            OperationTimeout = SftpOperationTimeout,
        };
        string? keyError = null;
        client.HostKeyReceived += (_, e) => e.CanTrust = (keyError = SshAuth.VerifyAcceptNew(auth.Host, e)) == null;
        prompter.Attach(client);
        try
        {
            // Detach 는 Dispose 보다 먼저 — 닫힌 클라이언트의 ConnectionInfo 를 건드리면 ObjectDisposedException 이 진짜 오류(호스트 키·인증)를 가린다
            try { await Task.Run(client.Connect); }
            finally { try { prompter.Detach(client); } catch { } }
        }
        catch when (keyError != null) { client.Dispose(); throw new InvalidOperationException(keyError); }
        catch (Exception ex) when (ex is Renci.SshNet.Common.SshAuthenticationException || prompter.Cancelled)
        {
            client.Dispose();
            _separateAuthFailed = ex.Message;
            throw new InvalidOperationException(L.F("별도 SFTP 연결의 인증에 실패했습니다: {0}\n터미널이 다시 연결될 때까지 다시 시도하지 않습니다.", ex.Message), ex);
        }
        catch { client.Dispose(); throw; }
        _home = client.WorkingDirectory;
        return client;
    }

    /// <summary>연결 닫기는 UI 스레드를 막지 않게 백그라운드에서</summary>
    private static void DisposeLater(SftpClient? c)
    {
        if (c == null) return;
        Task.Run(() => { try { c.Dispose(); } catch { } });
    }

    // 이동 요청 번호 — 빠르게 연달아 이동하면(터미널에서 cd 를 이어서 친 경우) 마지막 요청의 목록만 화면에 반영
    private int _navSeq;
    private string? _navTarget;

    private async Task NavigateAsync(string path)
    {
        var seq = ++_navSeq;
        _navTarget = path;
        _pendingFollow = null;   // 직접 이동했거나 따라가는 중 — 전송 중 기억해 둔 터미널 폴더는 버린다
        try
        {
            List<ISftpFile> files = [];
            var linkTarget = new System.Collections.Concurrent.ConcurrentDictionary<string, SftpFileAttributes>();
            // 목록을 읽는 동안 연결을 잡아 둔다 — 사용자 전환(RunAsAsync)이 쓰는 중인 연결을 닫지 않게
            await _clientLock.WaitAsync();
            try
            {
                if (seq != _navSeq) return;   // 기다리는 동안 더 나중 이동이 들어옴
                var c = await ClientCoreAsync();
                files = await Task.Run(() => c.ListDirectory(path).Where(f => f.Name is not "." and not "..").ToList());
                if (seq != _navSeq) return;
                // 심볼릭 링크가 폴더인지는 링크마다 서버에 물어야 한다 — 차례로 물으면 /etc·/usr/lib 처럼 링크가 많은 폴더가 느려 병렬로
                await Task.Run(() => Parallel.ForEach(files.Where(f => f.IsSymbolicLink), new ParallelOptions { MaxDegreeOfParallelism = 8 }, f =>
                {
                    try { linkTarget[f.FullName] = c.GetAttributes(f.FullName); } catch { }
                }));
            }
            finally { _clientLock.Release(); }
            if (seq != _navSeq || _closed) return;
            // 항목 만들기는 백그라운드에서 — 수만 개짜리 폴더(/usr/lib 등)에서 UI 스레드를 잡지 않게 (SftpItem 의 붓은 고정(Freeze)돼 스레드와 무관)
            var items = await Task.Run(() => files.Select(f =>
            {
                var t = f.IsSymbolicLink && linkTarget.TryGetValue(f.FullName, out var a) ? a : null;
                return new SftpItem
                {
                    Name = f.Name, FullName = f.FullName, IsLink = f.IsSymbolicLink,
                    IsDir = t?.IsDirectory ?? f.IsDirectory,
                    IsSpecial = t != null
                        ? IsSpecialFile(t.IsSocket, t.IsNamedPipe, t.IsCharacterDevice, t.IsBlockDevice)
                        : !f.IsSymbolicLink && IsSpecialFile(f.IsSocket, f.IsNamedPipe, f.IsCharacterDevice, f.IsBlockDevice),
                    Size = f.Length, Modified = f.LastWriteTime, Perms = Perms(f),
                };
            }).ToList());
            if (seq != _navSeq || _closed) return;   // 만드는 동안 더 나중 이동이 들어왔거나 탭이 닫힘

            _navTarget = null;
            var sameFolder = path == _cwd;
            _cwd = path;
            PathBox.Text = path;
            SetItems(items, keepSelection: sameFolder);   // 정렬·숨김 파일 표시 (SftpPanel.List.cs)
        }
        catch (Exception ex)
        {
            if (seq != _navSeq) return;   // 더 나중 이동이 진행 중
            _navTarget = null;
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
        // ~ · ~/x · 상대 경로(현재 폴더 기준) · . / .. 를 절대 경로로 바꿔서 간다
        var p = PathBox.Text.Trim();
        if (p.Length > 0) await NavigateAsync(ResolvePathInput(p, _cwd, _home));
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
            case Key.H when Keyboard.Modifiers == ModifierKeys.Control: ToggleHidden(); break;
            default: return;
        }
        e.Handled = true;
    }

    // ===================== 전송 상태 표시 =====================

    /// <summary>상태 줄 표시 번호 — 몇 초 뒤 숨길 때 그 사이 더 새 결과·알림이 떴으면 숨기지 않는다</summary>
    private int _statusSeq;

    private CancellationToken BeginTransfer(string text, long total)
    {
        _busy = true;
        _statusSeq++;
        _cts = new CancellationTokenSource();
        TransferBar.Visibility = Visibility.Visible;
        CancelButton.Visibility = Visibility.Visible;
        TransferText.Text = text;
        TransferText.ToolTip = null;
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
        // 전송 중 sudo 전환에 실패하는 등 미뤄 둔 알림 (SftpPanel.RunAs.cs)
        result = AppendNotice(result, _pendingNotice);
        _pendingNotice = null;
        // 전송 중에 터미널이 이동한 폴더로 — 전송 뒤 새로고침(NavigateAsync(_cwd))보다 나중에 돌아 마지막 이동이 이기게
        if (_pendingFollow is { } follow)
        {
            _pendingFollow = null;
            _ = Dispatcher.InvokeAsync(async () => { if (!_busy && !_closed) await NavigateAsync(follow); }, DispatcherPriority.Background);
        }
        var seq = ++_statusSeq;
        if (result == null) { TransferBar.Visibility = Visibility.Collapsed; return; }
        TransferText.Text = result;
        TransferText.ToolTip = result;
        TransferProgress.Value = 1;
        await Task.Delay(6000);
        if (!_busy && seq == _statusSeq) TransferBar.Visibility = Visibility.Collapsed;
    }

    private void OnCancelTransfer(object sender, RoutedEventArgs e) => _cts?.Cancel();

    /// <summary>
    /// 탭이 닫힘. 돌려주는 Task 는 편집 자동 업로드(진행 중 + 저장했지만 아직 안 올린 것)를 마치고 SFTP 연결을 닫은 뒤 끝난다 —
    /// 터미널 연결 위의 SFTP 채널이면 부르는 쪽은 이 Task 가 끝난 뒤에 SSH 연결을 닫아야 마지막 업로드가 잘리지 않는다.
    /// </summary>
    public Task Close()
    {
        // 두 번 불려도(탭 닫기 + 창 닫기 등) 같은 정리를 기다리게 — 두 번째가 바로 끝나 SSH 연결을 먼저 닫지 않게
        if (_closeTask != null) return _closeTask;
        _closed = true;   // 지금 연결 중인 ClientCoreAsync 는 연결이 끝나면 스스로 닫는다
        // 여기서 예외가 나가면 부르는 쪽(CloseSession)의 SSH 정리가 통째로 빠진다 — 동기 부분은 무엇도 던지지 않게
        List<EditSession> sessions = [];
        var uploading = false;
        List<string> temps = [];
        Window? owner = null;
        SftpClient? c = null;
        try
        {
            sessions = _edits.Values.ToList();
            uploading = sessions.Any(ed => ed.Uploading);
            foreach (var ed in sessions)
                try { ed.Dispose(); } catch { }   // 감시·디바운스 중지 (대기 중이던 저장은 아래에서 마지막으로 올린다)
            _edits.Clear();
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
            c = _client;
            _client = null;
            // 편집용 임시 폴더는 지우지 않는다 — 메모장·VS Code 는 파일을 잠그지 않아, 편집기에 열린 채 저장하지 않은 내용이
            // 파일을 잃는다. 하루 지난 것은 CleanupEditTemp 가 정리한다. 열기·끌어내기용 임시 폴더만 지운다
            temps = TakePanelTempDirs().Where(d => !IsEditTempDir(d)).ToList();
            owner = Window.GetWindow(this) ?? Application.Current?.MainWindow;
            // 목록(폴더가 크면 수만 개)·전송 대기 상태를 놓는다 — 닫는 동안(최대 CloseBudget) 또는 외부(탐색기의 끌기 개체 등)가
            // 패널을 잠시 더 붙잡아도 항목 목록까지 남지 않게
            _pendingFollow = null;
            _allItems = [];
            FileList.ItemsSource = null;
        }
        catch (Exception ex) { Debug.WriteLine($"SFTP 패널 닫기 준비 실패: {ex.Message}"); }

        var dispatcher = Dispatcher;
        // 편집 자동 업로드는 _clientLock 을 잡고 한다 — 올리는 중이면 끝난 뒤에 연결을 닫는다 (서버 파일이 반쯤 쓰인 채 남지 않게).
        // 저장했지만 디바운스(0.8초)를 기다리던 편집도 연결을 닫기 전에 올린다.
        // 잠금 대기 + 마지막 업로드 전체를 CloseBudget(2분) 안에 끝낸다 — 부르는 쪽은 이 Task 뒤에 SSH 연결을 닫으므로
        // 멈춘 채널 때문에 닫은 탭의 연결이 끝없이 남지 않게. 시간 안에 못 올린 것은 로컬 파일 위치와 함께 알린다
        _closeTask = Task.Run(async () =>
        {
            var failures = new List<string>();
            var locked = false;
            using var budget = new CancellationTokenSource(CloseBudget);
            try
            {
                if (uploading || sessions.Count > 0)
                {
                    try { locked = await _clientLock.WaitAsync(Timeout.InfiniteTimeSpan, budget.Token); }
                    catch (OperationCanceledException) { }
                }
                foreach (var s in sessions)
                    if (await FlushEditOnCloseAsync(locked ? c : null, s, locked, budget.Token) is { } err) failures.Add(err);
            }
            catch (Exception ex) { Debug.WriteLine($"SFTP 패널 닫기 중 편집 업로드 실패: {ex.Message}"); }
            finally
            {
                try { c?.Dispose(); } catch { }
                if (locked) try { _clientLock.Release(); } catch { }
            }
            try { DeleteTempDirs(temps); } catch { }
            if (failures.Count > 0)
            {
                try
                {
                    _ = dispatcher.InvokeAsync(() =>
                    {
                        try
                        {
                            // 탭이 있던 창이 이미 닫혔으면 주 창을 주인으로 (닫힌 창을 주인으로 쓰면 예외)
                            var o = owner is { IsLoaded: true } ? owner : Application.Current?.MainWindow;
                            Dialog.Show(o, L.T("탭을 닫으면서 저장한 편집 내용을 서버에 올리지 못했습니다. 편집한 파일은 이 PC 에 남아 있습니다."),
                                L.T("편집 업로드"), MessageBoxButton.OK, MessageBoxImage.Warning, string.Join("\n\n", failures));
                        }
                        catch (Exception ex) { Debug.WriteLine($"편집 업로드 실패 알림을 띄우지 못함: {ex.Message}"); }
                    });
                }
                catch { /* 앱 종료 중 — Dispatcher 가 이미 멈춤 */ }
            }
        });
        return _closeTask;
    }

    private Task? _closeTask;

    /// <summary>탭을 닫을 때 연결 잠금 대기 + 마지막 편집 업로드에 쓰는 전체 시간 (SshSessionView 는 이 안에 SSH 연결을 닫는다)</summary>
    public static readonly TimeSpan CloseBudget = TimeSpan.FromMinutes(2);

    /// <summary>
    /// 탭을 닫기 전에 사용자에게 물을 문구 — 편집 중인 파일이 없으면 null.
    /// (탭을 닫으면 저장해 둔 내용은 마지막으로 올리지만, 편집기에서 아직 저장하지 않은 내용은 더 이상 올라가지 않는다)
    /// </summary>
    public string? CloseConfirmMessage() =>
        _closed || _edits.Count == 0 ? null
        : L.F("SFTP 패널에서 편집 중인 파일이 {0}개 있습니다.\n탭을 닫으면 저장해 둔 내용은 서버에 올리지만, 그 뒤 편집기에서 저장하는 내용은 더 이상 올라가지 않습니다.\n\n탭을 닫을까요?", _edits.Count)
          + "\n\n" + string.Join("\n", _edits.Keys.Take(10)) + (_edits.Count > 10 ? "\n..." : "");
}
