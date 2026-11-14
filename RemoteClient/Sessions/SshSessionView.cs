using System;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Nubis;

/// <summary>SSH 세션 탭: XtermView(xterm.js) 화면 ⇄ SSH.NET ShellStream</summary>
public sealed class SshSessionView : UserControl, ISessionView
{
    private readonly HostEntry _host;
    private readonly XtermView _term = new();
    private readonly Decoder _utf8 = new UTF8Encoding(false).GetDecoder();
    private readonly object _decodeLock = new();

    private SshClient? _client;
    private ShellStream? _shell;
    private bool _closed, _connecting;
    private volatile int _gen;
    private SessionStatus _status = SessionStatus.Connecting;

    public event Action<SessionStatus>? StatusChanged;
    public XtermView Terminal => _term;
    public SessionStatus Status => _status;

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

    /// <param name="saveCredentials">접속 때 입력한 계정을 저장하는 콜백 (저장 가능한 호스트일 때만)</param>
    public SshSessionView(HostEntry host, Action<string, string>? saveCredentials = null)
    {
        _host = host;
        _saveCredentials = saveCredentials;

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
        _term.Ready += () => { try { _shell?.ChangeWindowSize(_term.Cols, _term.Rows, 0, 0); } catch { } };
        _term.Resized += (c, r) => { try { _shell?.ChangeWindowSize(c, r, 0, 0); } catch { } };
        _term.Input += OnInput;
        _term.CwdChanged += cwd =>
        {
            _lastCwd = cwd;
            if (SettingsStore.Current.SftpFollowTerminal && _sftp is { IsVisible: true }) _ = _sftp.FollowAsync(cwd);
        };
        _term.PromptScreen += OnPromptScreen;
    }

    /// <summary>패널을 연 뒤, 패널이 열리기 전에 알아낸 터미널 현재 폴더가 있으면 그리로</summary>
    private async System.Threading.Tasks.Task OpenSftpAsync(SftpPanel panel)
    {
        await panel.OpenAsync();
        if (!SettingsStore.Current.SftpFollowTerminal) return;
        // 터미널이 이미 다른 사용자(sudo su -)로 전환된 뒤에 패널을 연 경우
        if (FollowPromptUser(await _term.ReadBufferAsync(8))) return;
        if ((_lastCwd ?? _promptCwd ?? await TerminalCwdAsync()) is { } cwd) await panel.FollowAsync(cwd);
    }

    /// <summary>터미널 현재 폴더: OSC 7 로 받은 값, 없으면 화면 프롬프트에서 읽기</summary>
    private async System.Threading.Tasks.Task<string?> TerminalCwdAsync()
    {
        if (_lastCwd != null) return _lastCwd;
        if (_sftp is not { IsOpen: true }) return _promptCwd;   // 홈 폴더를 아직 모름
        var screen = await _term.ReadBufferAsync(8);
        return TerminalCwd.Resolve(screen, _sftp.Home, _promptCwd ?? _sftp.Home) ?? _promptCwd;
    }

    /// <summary>Enter 친 뒤: OSC 7 을 보내지 않는 셸(대부분의 서버 기본 bash)이면 새 프롬프트가 그려지길 기다린다</summary>
    private void OnCommandEntered()
    {
        // OSC 7 로 폴더를 받는 셸이어도 프롬프트는 본다 — sudo su - 같은 사용자 전환은 프롬프트로만 알 수 있다
        if (!SettingsStore.Current.SftpFollowTerminal || _sftp is not { IsVisible: true, IsOpen: true }) return;
        _watchingPrompt = true;
        _term.WatchPrompt();
    }

    /// <summary>
    /// 프롬프트의 사용자가 바뀌었으면(sudo su - → root, exit → 로그인 계정) SFTP 도 그 사용자 권한으로 바꾼다.
    /// 바꾸는 중이면 true — 폴더 따라가기는 전환이 끝난 뒤 새 홈 기준으로 한다.
    /// </summary>
    private bool FollowPromptUser(string screen)
    {
        if (_sftp == null || _authUser == null || TerminalCwd.PromptUser(screen) is not { } user) return false;
        var want = user == _authUser ? null : user;
        if (want == _sftp.RunAs || _sftp.RunAsFailed(want) || _switchingUser) return false;
        _ = SwitchSftpUserAsync(want, screen);
        return true;
    }

    private bool _switchingUser;

    private async System.Threading.Tasks.Task SwitchSftpUserAsync(string? user, string screen)
    {
        _switchingUser = true;
        try
        {
            // 새 셸 — 앞 셸이 OSC 7 로 알려준 폴더는 더 이상 맞지 않는다
            _lastCwd = null;
            _promptCwd = null;
            _lastPromptScreen = null;
            if (_sftp == null || !await _sftp.RunAsAsync(user)) return;
            // 홈이 바뀌었으니(~ = /root 등) 새 홈 기준으로 프롬프트의 폴더를 다시 계산
            if (TerminalCwd.Resolve(screen, _sftp.Home, _sftp.Home) is { } cwd)
            {
                _promptCwd = cwd;
                await _sftp.FollowAsync(cwd);
            }
            else await _sftp.FollowAsync(_sftp.Home);
        }
        finally { _switchingUser = false; }
    }

    /// <summary>
    /// 출력이 그려질 때마다 (첫 Enter 줄 ~ 커서 줄). 새 프롬프트가 보이면 그 폴더로 따라간다.
    /// 감시는 마지막 Enter 뒤 일정 시간 이어진다 — 앞 명령의 프롬프트가 오기 전에 다음 cd 를 친 경우도 마지막 프롬프트까지 따라간다.
    /// </summary>
    private void OnPromptScreen(string screen)
    {
        if (!_watchingPrompt || _sftp == null) return;
        // 화면이 그대로인데 다시 오는 경우(벨 등) — 같은 cd 를 이미 옮긴 폴더 기준으로 한 번 더 계산하지 않게
        if (screen == _lastPromptScreen) return;
        _lastPromptScreen = screen;
        if (FollowPromptUser(screen)) return;
        if (_lastCwd != null) return;   // 셸이 OSC 7 로 폴더를 알려줌 — 프롬프트로 계산하지 않는다
        if (TerminalCwd.Resolve(screen, _sftp.Home, _promptCwd ?? _sftp.Home) is not { } cwd) return;   // 아직 명령 실행 중
        if (cwd == _promptCwd) return;   // 폴더 그대로 (패널에서 직접 옮겨 둔 위치는 건드리지 않음)
        _promptCwd = cwd;
        if (_lastCwd == null && SettingsStore.Current.SftpFollowTerminal && _sftp.IsVisible) _ = _sftp.FollowAsync(cwd);
    }

    private FrameworkElement BuildToolStrip()
    {
        var res = Application.Current.Resources;
        var sftpButton = new Button
        {
            Style = (Style)res["IconButton"],
            Content = "",
            FontSize = 15,
            Width = 30,
            Height = 30,
            Margin = new Thickness(0, 6, 0, 0),
            ToolTip = L.T("SFTP 파일 (업로드·다운로드)"),
        };
        sftpButton.Click += (_, _) => ToggleSftp();
        var panel = new StackPanel();
        panel.Children.Add(sftpButton);
        return new Border
        {
            Width = 36,
            Background = (System.Windows.Media.Brush)res["SidebarBg"],
            BorderBrush = (System.Windows.Media.Brush)res["Line"],
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = panel,
        };
    }

    public void ToggleSftp(bool? open = null)
    {
        var isOpen = _sftpColumn.Width.Value > 0;
        var want = open ?? !isOpen;
        if (want == isOpen) return;
        if (want)
        {
            if (_sftp == null)
            {
                _sftp = new SftpPanel(() => _authUser != null ? (_host, _authUser, _authPassword ?? "") : null, TerminalCwdAsync, () => _client);
                _sftp.CloseRequested += () => ToggleSftp(false);
                Grid.SetColumn(_sftp, 1);
                ((Grid)Content).Children.Add(_sftp);
            }
            _sftp.Visibility = Visibility.Visible;
            _sftpColumn.MinWidth = 260;
            _sftpColumn.Width = new GridLength(_sftpWidth);
            _splitter.Visibility = Visibility.Visible;
            _ = OpenSftpAsync(_sftp);
        }
        else
        {
            _sftpWidth = _sftpColumn.ActualWidth > 0 ? _sftpColumn.ActualWidth : _sftpWidth;
            _sftpColumn.MinWidth = 0;
            _sftpColumn.Width = new GridLength(0);
            _splitter.Visibility = Visibility.Collapsed;
            if (_sftp != null) _sftp.Visibility = Visibility.Collapsed;
            _term.FocusTerminal();
        }
    }

    private void OnInput(string d)
    {
        if (_shell != null && _status == SessionStatus.Connected)
        {
            SendRaw(d);
            if (d.Contains('\r')) OnCommandEntered();
        }
        else if (_status == SessionStatus.Disconnected && !_connecting && d.Contains('\r')) _ = ConnectAsync();
    }

    private void SendRaw(string d)
    {
        var bytes = Encoding.UTF8.GetBytes(d);
        try { _shell!.Write(bytes, 0, bytes.Length); _shell.Flush(); } catch { }
    }

    private void SetStatus(SessionStatus s)
    {
        _status = s;
        Dispatcher.BeginInvoke(() => StatusChanged?.Invoke(s));
    }

    // ---------- SSH ----------
    private async Task ConnectAsync()
    {
        if (_connecting || _closed) return;
        _connecting = true;
        SetStatus(SessionStatus.Connecting);
        var gen = ++_gen;   // 이전 연결의 Closed/Error 이벤트는 무시
        DisposeSsh();

        // 다시 연결할 때는 직전에 로그인에 성공한 계정을 그대로 (접속 때 입력만 하고 저장하지 않은 경우에도 다시 묻지 않게)
        var user = _authUser ?? _host.Username;
        var password = _authUser != null ? _authPassword ?? "" : _host.Password;
        var save = false;

        // 사용자명/인증 수단이 없거나, 직전 시도에서 인증이 거부됐으면 물어본다
        if (_askCredentials || string.IsNullOrWhiteSpace(user) || (string.IsNullOrEmpty(password) && !_host.HasKey && !SshAuth.HasDefaultKeyFiles()))
        {
            var cred = CredentialDialog.Ask(Window.GetWindow(this), $"{_host.DisplayName} ({_host.Address})", user, _saveCredentials != null);
            if (cred == null)
            {
                _term.Info(L.T("인증 정보 입력이 취소되었습니다. Enter 키를 누르면 다시 시도합니다."), "33");
                _connecting = false;
                SetStatus(SessionStatus.Disconnected);
                return;
            }
            user = cred.Value.User;
            password = cred.Value.Password;
            save = cred.Value.Save;
        }

        _term.Info($"Connecting to {user}@{_host.Address}:{_host.Port} ...");
        var (cols, rows) = (_term.Cols, _term.Rows);
        var target = $"{user}@{_host.DisplayName} ({_host.Address})";
        // 서버의 인증 질문(OTP·비밀번호 변경 등)은 사용자에게 묻고, 로그인 전 안내문(배너)·질문 진행은 터미널에 보여준다
        var prompter = new AuthPrompter(
            req => Dispatcher.Invoke(() => AuthPromptDialog.Ask(Window.GetWindow(this), target, req)),
            banner => _term.Info(banner.TrimEnd().Replace("\r\n", "\n").Replace("\n", "\r\n"), "36"));

        try
        {
            _hostKeyAccepted = false;
            _hostKeyRejected = false;
            await Task.Run(() =>
            {
                var client = new SshClient(SshAuth.Build(_host, user, password, prompter: prompter)) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
                ShellStream? shell = null;
                try
                {
                    client.HostKeyReceived += OnHostKey;
                    client.ErrorOccurred += (_, e) => { if (gen == _gen) OnDisconnected(e.Exception.Message); };
                    prompter.Attach(client);
                    try { client.Connect(); }
                    finally { prompter.Detach(client); }

                    shell = client.CreateShellStream("xterm-256color", cols, rows, 0, 0, 65536);
                    shell.DataReceived += OnData;
                    shell.Closed += (_, _) => { if (gen == _gen) OnDisconnected(null); };
                }
                catch
                {
                    // 연결은 됐는데 셸 생성에 실패한 경우에도 연결을 남기지 않는다
                    try { shell?.Dispose(); } catch { }
                    try { client.Dispose(); } catch { }
                    throw;
                }

                // 연결하는 동안 탭이 닫혔거나 새 연결 시도가 시작됐으면 이 연결은 버린다 (누수 방지)
                if (_closed || gen != _gen)
                {
                    try { shell.Dispose(); } catch { }
                    try { client.Dispose(); } catch { }
                    throw new OperationCanceledException();
                }
                _client = client;
                _shell = shell;
            });

            // 연결 직후 서버가 셸을 바로 닫은 경우(nologin, 세션 수 제한 등) 이미 Disconnected 로 바뀌어 있음
            if (_status == SessionStatus.Disconnected) return;
            SetStatus(SessionStatus.Connected);
            _askCredentials = false;
            _autoAttempts = 0;
            _promptCwd = null;   // 새 셸은 홈 폴더에서 시작
            _lastPromptScreen = null;
            _sftp?.ResetRunAs();   // 새 셸은 로그인 계정으로 시작
            if (prompter.NewPassword is { } newPassword)
            {
                // 서버 요구로 비밀번호를 바꿈 — 이 호스트에 저장된 비밀번호를 쓰던 경우면 새 값으로 저장
                if (_saveCredentials != null && password == _host.Password && !string.IsNullOrEmpty(_host.Password)) save = true;
                password = newPassword;
                _term.Info(L.T("비밀번호를 변경했습니다."), "32");
            }
            _authUser = user;
            _authPassword = password;
            // 로그인에 성공한 계정만 저장 (이번 세션에도 반영해 재연결 시 다시 묻지 않음)
            if (save)
            {
                _host.Username = user;
                _host.Password = password;
                Dispatcher.Invoke(() => _saveCredentials?.Invoke(user, password));
            }
            Dispatcher.Invoke(() =>
            {
                // 처음 로그인했을 때 SFTP 패널 자동 열기 (사용자가 닫은 뒤 재연결할 때는 다시 열지 않음)
                if (!_sftpAutoTried && SettingsStore.Current.SftpAutoOpen) { _sftpAutoTried = true; ToggleSftp(true); }
                if (IsVisible) _term.FocusTerminal();
            });
        }
        catch (OperationCanceledException) when (_closed || gen != _gen)
        {
            // 닫힌 탭 / 이전 시도 — 조용히 종료
        }
        catch (SshAuthenticationException ex)
        {
            // 저장된 비밀번호가 바뀐 경우 등: 다음 시도에서 계정을 다시 묻는다
            _askCredentials = true;
            _term.Info(L.F("인증 실패: {0}", ex.Message), "31");
            if (prompter.LastAnsweredWithSavedPassword)
                _term.Info(L.T("저장된 비밀번호로 답했는데 서버가 거부했습니다. 이 호스트(또는 연결된 Vault 계정)의 비밀번호가 맞는지 확인하세요. 계속 실패하면 계정이 잠길 수 있습니다."), "33");
            else if (KbdAuth.FailureHint(ex.Message, _host.HasKey, !string.IsNullOrEmpty(password)) is { } hint) _term.Info(hint, "33");
            _term.Info(L.T("Enter 키를 누르면 계정을 다시 입력합니다."));
            DisposeSsh();
            SetStatus(SessionStatus.Disconnected);
        }
        catch (Exception) when (_hostKeyRejected)
        {
            // 사용자가 호스트키를 거부함 — 네트워크 오류가 아니다. 공인 IP 재시도·자동 재연결을 하지 않는다
            // (공인 IP 는 known_hosts 의 다른 항목이라 확인 없이 새 키로 신뢰돼 경고를 우회하게 된다)
            _term.Info(L.T("호스트키를 신뢰하지 않아 연결하지 않았습니다. Enter 키를 누르면 다시 시도합니다."), "31");
            DisposeSsh();
            SetStatus(SessionStatus.Disconnected);
        }
        catch (Exception ex) when (_hostKeyAccepted && !_retriedAfterHostKey)
        {
            // 호스트키 확인 창을 읽는 동안 키 교환 시간이 초과될 수 있음 — 키는 저장됐으니 한 번 자동 재시도
            _retriedAfterHostKey = true;
            _connecting = false;
            DisposeSsh();
            _term.Info(L.F("({0}) 호스트키를 저장했으니 다시 연결합니다...", ex.Message.Split('\n')[0]));
            _ = ConnectAsync();
            return;
        }
        catch (Exception ex) when (IsNetworkError(ex) && !_triedPublic && _host.HasPublicIp && _host.Address != _host.PublicIp)
        {
            // 사설 IP 로 연결이 안 되면(VPN 꺼짐 등) 공인 IP 로 한 번 더 — 인증 실패는 여기로 오지 않는다
            _triedPublic = true;
            _connecting = false;
            DisposeSsh();
            _term.Info(L.F("연결 실패: {0}", ex.Message.Split('\n')[0]), "31");
            _term.Info(L.F("공인 IP {0} 로 다시 시도합니다...", _host.PublicIp));
            _host.Address = _host.PublicIp!;
            _ = ConnectAsync();
            return;
        }
        catch (Exception ex)
        {
            _term.Info(L.F("연결 실패: {0}", ex.Message), "31");
            DisposeSsh();
            SetStatus(SessionStatus.Disconnected);
            if (!ScheduleAutoReconnect()) _term.Info(L.T("Enter 키를 누르면 다시 연결합니다. (탭 우클릭 → 다시 연결)"));
        }
        finally
        {
            _connecting = false;
        }
    }

    private bool _askCredentials, _hostKeyAccepted, _hostKeyRejected, _retriedAfterHostKey, _triedPublic;
    private int _autoAttempts;
    private const int MaxAutoAttempts = 5;

    private static bool IsNetworkError(Exception ex) =>
        ex is System.Net.Sockets.SocketException or SshOperationTimeoutException or SshConnectionException or TimeoutException ||
        ex.InnerException is System.Net.Sockets.SocketException;

    /// <summary>설정 "끊기면 자동으로 다시 연결" 이 켜져 있으면 2·4·8·16·32초 뒤 다시 연결. 예약했으면 true</summary>
    private bool ScheduleAutoReconnect()
    {
        if (_closed || !SettingsStore.Current.SshAutoReconnect || _askCredentials || _autoAttempts >= MaxAutoAttempts) return false;
        var delay = TimeSpan.FromSeconds(2 << _autoAttempts);
        _autoAttempts++;
        _term.Info(L.F("{0}초 뒤 자동으로 다시 연결합니다 ({1}/{2})... Enter 를 누르면 바로 연결", (int)delay.TotalSeconds, _autoAttempts, MaxAutoAttempts), "33");
        var gen = _gen;
        _ = Task.Delay(delay).ContinueWith(_ => Dispatcher.BeginInvoke(() =>
        {
            if (!_closed && gen == _gen && _status == SessionStatus.Disconnected && !_connecting) _ = ConnectAsync();
        }));
        return true;
    }

    /// <summary>탭 우클릭 "다시 연결"</summary>
    public void Reconnect()
    {
        if (_closed || _connecting) return;
        _autoAttempts = 0;
        _ = ConnectAsync();
    }

    private void OnHostKey(object? sender, HostKeyEventArgs e)
    {
        var key = SshAuth.HostKeyId(_host);
        var fp = SshAuth.Fingerprint(e);
        var known = KnownHostStore.Get(key);
        if (known == fp) { e.CanTrust = true; return; }

        // Nubis 가 처음 보는 서버: OpenSSH 의 known_hosts 를 참고 — ssh 로 이미 신뢰한 키면 묻지 않고, 다른 키면 바뀐 키처럼 경고
        var openSsh = known == null ? OpenSshKnownHosts.Check(_host.Address, _host.Port, e.HostKey) : OpenSshKnownHosts.Verdict.Unknown;
        if (openSsh == OpenSshKnownHosts.Verdict.Match)
        {
            KnownHostStore.Set(key, fp);
            _term.Info(L.F("OpenSSH known_hosts 에서 신뢰한 호스트키: {0} {1}", key, fp));
            e.CanTrust = true;
            return;
        }
        if (openSsh == OpenSshKnownHosts.Verdict.Revoked)
        {
            _hostKeyRejected = true;
            _term.Info(L.F("OpenSSH known_hosts 에 폐기(@revoked)된 호스트키라 연결하지 않습니다: {0}", fp), "31");
            e.CanTrust = false;
            return;
        }

        // 처음 보는 서버: 설정이 꺼져 있으면(기본) 확인 없이 저장하고 접속 (ssh 의 StrictHostKeyChecking=accept-new)
        // 저장된 키와 다른 경우(중간자 공격 가능)와 사용자가 전에 거부한 키는 아래에서 항상 묻는다
        if (known == null && openSsh == OpenSshKnownHosts.Verdict.Unknown && !SettingsStore.Current.SshHostKeyPrompt)
        {
            KnownHostStore.Set(key, fp);
            _term.Info(L.F("새 호스트키 저장: {0} {1}", key, fp));
            e.CanTrust = true;
            return;
        }
        // 사용자가 전에 거부한 키는 "아직 신뢰 안 함" 으로 보고 다시 묻는다
        if (known != null && KnownHostStore.IsRejected(known)) known = null;

        var (title, msg, detail, icon) = openSsh == OpenSshKnownHosts.Verdict.Mismatch
            ? (L.T("호스트키가 바뀌었습니다"),
               L.T("OpenSSH(known_hosts)에 저장된 이 서버의 키와 다릅니다. 서버를 다시 설치했거나 IP 가 다른 서버로 바뀐 경우가 아니라면 중간자 공격일 수 있습니다.\n그래도 이 키를 신뢰하고 접속할까요?"),
               $"{key}\n{fp}", MessageBoxImage.Warning)
            : known == null
            ? (L.T("처음 접속하는 서버입니다"),
               L.F("{0} 서버의 호스트키를 신뢰하고 저장할까요?\n다음 접속부터는 묻지 않습니다.", _host.DisplayName),
               $"{key}\n{fp}", MessageBoxImage.Question)
            : (L.T("호스트키가 바뀌었습니다"),
               L.T("저장된 값과 다릅니다. 서버를 다시 설치했거나 IP 가 다른 서버로 바뀐 경우가 아니라면 중간자 공격일 수 있습니다.\n그래도 새 키로 교체하고 접속할까요?"),
               L.F("{0}\n저장된 값: {1}\n현재 값:   {2}", key, known, fp), MessageBoxImage.Warning);

        var ok = Dispatcher.Invoke(() =>
            Dialog.Show(Window.GetWindow(this), msg, title, MessageBoxButton.YesNo, icon, detail) == MessageBoxResult.Yes);
        if (ok) { KnownHostStore.Set(key, fp); _hostKeyAccepted = true; }
        else
        {
            _hostKeyRejected = true;
            // 거부한 키는 기록해 둔다 — Claude 일괄 실행·SFTP 의 "처음 보는 키 자동 신뢰" 가 이 키를 받아들이지 않도록
            if (KnownHostStore.Get(key) == null) KnownHostStore.Set(key, KnownHostStore.RejectedMark(fp));
        }
        e.CanTrust = ok;
    }

    private void OnData(object? sender, ShellDataEventArgs e)
    {
        string text;
        lock (_decodeLock)
        {
            var chars = new char[_utf8.GetCharCount(e.Data, 0, e.Data.Length)];
            _utf8.GetChars(e.Data, 0, e.Data.Length, chars, 0);
            text = new string(chars);
        }
        _term.Write(text);
    }

    private void OnDisconnected(string? reason)
    {
        if (_status == SessionStatus.Disconnected || _closed) return;
        SetStatus(SessionStatus.Disconnected);
        _term.Write("\r\n");
        if (reason != null) _term.Info(L.F("오류: {0}", reason), "31");
        _term.Info(L.T("연결이 종료되었습니다. Enter 키를 누르면 다시 연결합니다. (탭 우클릭 → 다시 연결)"), "33");
        // 자동 재연결은 오류로 끊긴 경우만. 셸이 정상 종료(exit·logout, reason == null)했으면 다시 붙지 않는다
        if (reason != null) Dispatcher.BeginInvoke(() => ScheduleAutoReconnect());
    }

    private void DisposeSsh()
    {
        var shell = _shell; var client = _client;
        _shell = null; _client = null;
        Task.Run(() =>
        {
            try { shell?.Dispose(); } catch { }
            try { if (client?.IsConnected == true) client.Disconnect(); } catch { }
            try { client?.Dispose(); } catch { }
        });
    }

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
        DisposeSsh();
        _sftp?.Close();
        _term.Close();
    }
}
