using System;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Nubis;

/// <summary>SSH 세션 탭: XtermView(xterm.js) 화면 ⇄ SSH.NET ShellStream</summary>
public sealed partial class SshSessionView : UserControl, ISessionView
{
    private readonly HostEntry _host;
    private readonly XtermView _term = new();
    private readonly Decoder _utf8 = new UTF8Encoding(false).GetDecoder();
    private readonly object _decodeLock = new();
    private char[] _decodeBuf = Array.Empty<char>();

    private SshClient? _client;
    private ShellStream? _shell;
    // _closed 는 연결 스레드(Task.Run)에서도 읽는다
    private volatile bool _closed;
    private bool _connecting;
    private volatile int _gen;
    /// <summary>_client/_shell/_tunnel 교체와 "닫힘·세대 확인 후 대입"을 한 덩어리로 (연결 중 탭 닫기 경쟁 → 연결 누수 방지)</summary>
    private readonly object _sshLock = new();
    /// <summary>상태 전환(연결됨 ↔ 끊김)을 한 덩어리로 — 셸 Closed 와 ErrorOccurred 가 다른 스레드에서 동시에 와도 한 번만 처리</summary>
    private readonly object _statusLock = new();
    /// <summary>키 입력을 순서대로 보내는 백그라운드 작업 줄 (서버가 읽지 않아 SSH 창이 가득 차도 UI 스레드가 멈추지 않게)</summary>
    private System.Threading.Tasks.Task _sendChain = System.Threading.Tasks.Task.CompletedTask;
    private readonly object _sendLock = new();
    /// <summary>이 연결에서 처음 받은 OSC 7 의 호스트 — 터미널에서 ssh 로 들어간 다른 서버의 OSC 7 은 무시한다</summary>
    private string? _osc7Host;
    private SessionStatus _status = SessionStatus.Connecting;

    public event Action<SessionStatus>? StatusChanged;
    public XtermView Terminal => _term;
    public SessionStatus Status => _status;

    /// <summary>
    /// 이 탭의 로그인된 SSH 연결 (연결됨 상태일 때만) — MCP run_command 가 그 위에 명령 채널만 열어 다시 인증하지 않고 실행한다
    /// (OTP 서버에서도 Push 없이). 연결은 터미널의 것이라 쓰는 쪽에서 닫으면 안 된다. User 는 이 탭이 로그인한 계정
    /// </summary>
    public (SshClient Client, string User)? SharedConnection =>
        _status == SessionStatus.Connected && !_closed && _client is { IsConnected: true } c && _authUser != null ? (c, _authUser) : null;

    private readonly Action<string, string>? _saveCredentials;
    private bool _started;

    // 로그인에 성공한 계정 (SFTP 패널이 같은 계정으로 연결)
    private string? _authUser, _authPassword;

    // 레이아웃: [도구 막대] [SFTP 패널] [분할선] [터미널]
    private readonly ColumnDefinition _sftpColumn = new() { Width = new GridLength(0) };
    private readonly GridSplitter _splitter;
    private SftpPanel? _sftp;
    private double _sftpWidth = 400;
    private bool _sftpAutoTried;
    /// <summary>셸이 OSC 7 로 마지막에 알려준 현재 폴더</summary>
    private string? _lastCwd;
    /// <summary>OSC 7 이 없을 때 화면 프롬프트로 알아낸 현재 폴더 (TerminalCwd.Resolve)</summary>
    private string? _promptCwd;
    /// <summary>Enter 뒤 새 프롬프트를 기다리는 중 (XtermView.WatchPrompt)</summary>
    private bool _watchingPrompt;
    private string? _lastPromptScreen;

    private readonly SessionSource? _source;

    /// <param name="saveCredentials">접속 때 입력한 계정을 저장하는 콜백 (저장 가능한 호스트일 때만)</param>
    /// <param name="source">저장된 호스트의 최신 계정·출처 (다시 연결할 때 반영, 목록에 있는 호스트만)</param>
    public SshSessionView(HostEntry host, Action<string, string>? saveCredentials = null, SessionSource? source = null)
    {
        _host = host;
        _originalAddress = host.Address;
        _saveCredentials = saveCredentials;
        _source = source;
        InitForwards();

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(_sftpColumn);
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var strip = BuildToolStrip();
        Grid.SetColumn(strip, 0);
        _splitter = new GridSplitter
        {
            Width = 4, Background = (System.Windows.Media.Brush)Application.Current.Resources["Line"],
            HorizontalAlignment = HorizontalAlignment.Center, ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            Visibility = Visibility.Collapsed,
        };
        Grid.SetColumn(_splitter, 2);
        Grid.SetColumn(_term, 3);
        grid.Children.Add(strip);
        grid.Children.Add(_splitter);
        grid.Children.Add(_term);
        Content = grid;
        // 터미널 화면 준비(탭이 처음 보일 때)를 기다리지 않고 바로 연결. 출력은 XtermView 가 모아 둔다
        Loaded += (_, _) => { if (!_started) { _started = true; _ = ConnectAsync(); } };
        _term.Ready += () => SendResize(_term.Cols, _term.Rows);
        _term.Resized += SendResize;
        _term.Input += OnInput;
        _term.CwdChanged += (cwd, host) =>
        {
            // 터미널 안에서 ssh 로 다른 서버에 들어가면 그 서버의 OSC 7 이 온다 — 이 서버의 경로가 아니므로 따라가지 않는다
            if (host.Length > 0)
            {
                _osc7Host ??= host;
                if (!string.Equals(host, _osc7Host, StringComparison.OrdinalIgnoreCase)) return;
            }
            _lastCwd = cwd;
            if (SettingsStore.Current.SftpFollowTerminal && _sftp is { IsVisible: true }) _ = _sftp.FollowAsync(cwd);
        };
        _term.PromptScreen += OnPromptScreen;
        // 끊기면 터미널 아래쪽에 "다시 연결" 버튼 (상태 알림은 UI 스레드로 오지만 순서가 섞일 수 있어 지금 상태로 판단)
        StatusChanged += _ =>
        {
            if (_closed) return;
            if (_status == SessionStatus.Disconnected) _term.ShowReconnect(L.T("다시 연결"));
            else _term.HideReconnect();
        };
        _term.ReconnectRequested += () => { if (_status == SessionStatus.Disconnected) Reconnect(); };
    }

    private void OnInput(string d)
    {
        if (_shell != null && _status == SessionStatus.Connected)
        {
            SendRaw(d);
            if (d.Contains('\r')) OnCommandEntered();
        }
        else if (_status == SessionStatus.Disconnected && !_connecting && d.Contains('\r'))
            // 키 입력은 WebView2 의 WebMessageReceived 처리 중에 온다 — 계정 입력 창(모달)을 그 안에서 띄우지 않게 한 박자 늦춘다
            Dispatcher.BeginInvoke(() => { if (!_closed && _status == SessionStatus.Disconnected && !_connecting) _ = ConnectAsync(); });
    }

    /// <summary>
    /// 터미널 크기 변경을 서버에 알린다. 채널 요청 전송은 소켓이 막히면 기다릴 수 있어 UI 스레드에서 하지 않고,
    /// 키 입력과 같은 작업 줄에서 순서대로 보낸다 (마지막 크기가 마지막에 도착하게)
    /// </summary>
    private void SendResize(uint cols, uint rows)
    {
        var shell = _shell;
        if (shell == null || cols == 0 || rows == 0) return;
        lock (_sendLock)
            _sendChain = _sendChain.ContinueWith(_ =>
            {
                try { shell.ChangeWindowSize(cols, rows, 0, 0); } catch { }
            }, System.Threading.Tasks.TaskScheduler.Default);
    }

    /// <summary>
    /// 셸에 입력 보내기. Write+Flush 는 서버가 읽지 않으면(SSH 채널 창이 가득 참) 막힐 수 있어 UI 스레드에서 하지 않고,
    /// 한 줄로 이어진 백그라운드 작업에서 들어온 순서대로 보낸다
    /// </summary>
    private void SendRaw(string d)
    {
        var shell = _shell;
        if (shell == null) return;
        var bytes = Encoding.UTF8.GetBytes(d);
        lock (_sendLock)
            _sendChain = _sendChain.ContinueWith(_ =>
            {
                try { shell.Write(bytes, 0, bytes.Length); shell.Flush(); } catch { }
            }, System.Threading.Tasks.TaskScheduler.Default);
    }

    private void SetStatus(SessionStatus s)
    {
        lock (_statusLock) _status = s;
        Dispatcher.BeginInvoke(() => StatusChanged?.Invoke(s));
    }

    /// <summary>연결 완료 → 연결됨. 그사이 셸이 이미 닫혀 끊김으로 바뀌었으면 false (확인과 전환 사이에 끊김이 끼어들지 않게)</summary>
    private bool TrySetConnected()
    {
        lock (_statusLock)
        {
            if (_status == SessionStatus.Disconnected || _closed) return false;
            _status = SessionStatus.Connected;
        }
        Dispatcher.BeginInvoke(() => StatusChanged?.Invoke(SessionStatus.Connected));
        return true;
    }

    /// <summary>
    /// 셸 출력 읽기 스레드. 셸이 닫히면(채널 종료·연결 끊김·DisposeSsh) Read 가 남은 데이터를 다 돌려준 뒤 0 을 돌려줘 끝난다.
    /// 돌려주는 핸들은 읽기가 끝나면 켜진다 (Closed 처리가 마지막 출력 뒤에 오게)
    /// </summary>
    private System.Threading.ManualResetEventSlim StartReadLoop(ShellStream shell, int gen)
    {
        var drained = new System.Threading.ManualResetEventSlim(false);
        new System.Threading.Thread(() =>
        {
            var buf = new byte[32768];
            try
            {
                int n;
                // 세대 확인 — 버린(다시 연결 전) 셸에서 늦게 온 출력이 새 화면에 섞이지 않게
                while ((n = shell.Read(buf, 0, buf.Length)) > 0 && gen == _gen)
                    OnData(buf, n);
            }
            catch { /* 셸 닫힘 */ }
            finally { drained.Set(); }
        }) { IsBackground = true, Name = "ssh-read" }.Start();
        return drained;
    }

    private void OnData(byte[] data, int count)
    {
        string text;
        lock (_decodeLock)
        {
            // 글자 버퍼는 재사용한다 — 읽기마다(최대 32KB) char[] 를 새로 만들고 GetCharCount 로 한 번 더 훑지 않게.
            // UTF-8 은 바이트 수 + 디코더에 남은 바이트(최대 3) 보다 글자(UTF-16)가 많아질 수 없다
            var need = count + 4;
            if (_decodeBuf.Length < need) _decodeBuf = new char[Math.Max(need, 32768 + 4)];
            var n = _utf8.GetChars(data, 0, count, _decodeBuf, 0);
            if (n == 0) return;
            text = new string(_decodeBuf, 0, n);
        }
        _term.Write(text);
    }

    /// <remarks>
    /// 셸만 닫힌 경우(exit·logout) SSH 연결(_client)은 여기서 닫지 않는다 — 다시 연결(ConnectAsync → DisposeSsh)이나 탭 닫기 때 정리된다.
    /// 그동안 SFTP 패널은 같은 연결로 계속 쓸 수 있다. 오류로 끊긴 경우 연결은 이미 죽어 있다.
    /// </remarks>
    private void OnDisconnected(string? reason)
    {
        // Closed·ErrorOccurred 가 서로 다른 스레드에서 거의 동시에 올 수 있다 — 확인과 전환을 한 번에 해 안내·자동 재연결이 두 번 나가지 않게
        lock (_statusLock)
        {
            if (_status == SessionStatus.Disconnected || _closed) return;
            _status = SessionStatus.Disconnected;
        }
        Dispatcher.BeginInvoke(() => StatusChanged?.Invoke(SessionStatus.Disconnected));
        _forwards.Detach();   // 셸만 닫혀(exit) 연결이 남아 있어도 포워딩은 닫는다 — 다시 연결되면 다시 연다
        _term.Write("\r\n");
        if (reason != null) _term.Info(L.F("오류: {0}", reason), "31");
        _term.Info(L.T("연결이 종료되었습니다. Enter 키를 누르면 다시 연결합니다. (탭 우클릭 → 다시 연결)"), "33");
        // 자동 재연결은 오류로 끊긴 경우만. 셸이 정상 종료(exit·logout, reason == null)했으면 다시 붙지 않는다
        if (reason != null) Dispatcher.BeginInvoke(() => ScheduleAutoReconnect());
    }

    private void DisposeSsh()
    {
        ShellStream? shell; SshClient? client; JumpTunnel? tunnel;
        lock (_sshLock)
        {
            shell = _shell; client = _client; tunnel = _tunnel;
            _shell = null; _client = null; _tunnel = null;
        }
        _forwards.Detach();   // 리스너는 바로 닫는다 (연결을 닫기 전에)
        Task.Run(() =>
        {
            try { shell?.Dispose(); } catch { }
            try { if (client?.IsConnected == true) client.Disconnect(); } catch { }
            try { client?.Dispose(); } catch { }
            tunnel?.Dispose();   // 점프 서버 연결은 대상 서버 연결을 닫은 뒤에
        });
    }

    /// <summary>점프 서버 터널 (ProxyJump 를 쓸 때만)</summary>
    private JumpTunnel? _tunnel;

    // ---------- ISessionView ----------
    public void FocusSession() => _term.FocusTerminal();

    public async Task<string?> ReadTerminalAsync(int lines) => await _term.ReadBufferAsync(lines);
    public async Task<string?> GetSelectionAsync() => await _term.GetSelectionAsync();

    /// <summary>대화형 셸에 텍스트 입력 (Claude send_to_terminal 용)</summary>
    public bool SendText(string text)
    {
        if (_shell == null || _status != SessionStatus.Connected) return false;
        SendRaw(text);
        return true;
    }

    public void CloseSession()
    {
        _closed = true;
        // 포워딩은 바로 닫는다 — 아래 SFTP 정리(최대 2분)를 기다리는 동안 이 PC 의 포트가 열려 있지 않게
        _forwards.Detach();
        // 셸(터미널 채널)은 바로 닫는다 — 실행 중이던 명령(tail -f 등)이 아래 SFTP 정리(최대 2분)를 기다리며 서버에 남지 않게.
        // SFTP 는 같은 연결의 다른 채널이라 영향이 없다
        ShellStream? shell;
        lock (_sshLock)
        {
            shell = _shell;
            _shell = null;
        }
        if (shell != null) _ = Task.Run(() => { try { shell.Dispose(); } catch { } });
        // SFTP 패널은 이 SSH 연결 위의 채널을 쓴다 — 편집 파일 자동 업로드(마지막 저장분 포함)가 끝난 뒤에 SSH 를 닫는다
        // (Close 의 Task 는 최대 2분 안에 끝난다. DisposeSsh 는 잠금 + 백그라운드 해제라 어느 스레드에서 불러도 된다)
        var sftpClosed = _sftp?.Close() ?? Task.CompletedTask;
        _term.Close();
        _ = sftpClosed.ContinueWith(_ => DisposeSsh(), TaskScheduler.Default);
    }
}
