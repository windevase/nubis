using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Nubis;

/// <summary>SSH 탭: 연결·인증·실패 처리·자동 재연결</summary>
public sealed partial class SshSessionView
{
    // ---------- SSH ----------
    private async Task ConnectAsync()
    {
        if (_connecting || _closed) return;
        _connecting = true;
        SetStatus(SessionStatus.Connecting);
        var gen = ++_gen;   // 이전 연결의 Closed/Error/DataReceived 이벤트는 무시
        DisposeSsh();
        // 앞 셸 출력이 UTF-8 글자 중간에서 끊겼으면 디코더에 남은 바이트가 새 셸 첫 출력을 깨뜨린다
        lock (_decodeLock) _utf8.Reset();
        _osc7Host = null;   // 새 로그인 — 처음 오는 OSC 7 의 호스트를 다시 기억한다

        // 새 연결 시도는 항상 원래(사설) 주소부터 — 지난번에 공인 IP 로 넘어갔어도 VPN 이 돌아왔으면 사설로 붙는다.
        // 공인 IP 재시도(바로 아래 catch 가 예약)일 때만 바꿔 둔 주소를 그대로 쓴다
        if (_publicRetryPending) _publicRetryPending = false;
        else
        {
            _host.Address = _originalAddress;
            _triedPublic = false;
        }

        // 저장된 호스트·Vault 의 계정이 바뀌었으면(이 탭을 연 뒤 Vault 비밀번호를 고친 경우 등) 그 값으로.
        // 직전에 인증이 거부돼 계정을 물을 차례여도 바뀐 값을 먼저 시도한다
        var fresh = _source?.Reload();
        // 포워딩 규칙도 저장된 호스트의 최신 값으로 (이 탭에서 바꾼 켜기/끄기·임시 규칙은 유지)
        if (fresh != null) _forwards.SetSavedRules(fresh.PortForwards);
        if (fresh != null && _host.RefreshCredentialsFrom(fresh))
        {
            _authUser = null;
            _authPassword = null;
            _askCredentials = false;
            _term.Info(L.T("저장된 계정이 바뀌어 새 값으로 연결합니다."));
        }

        // 이 탭에서 점프 서버 비밀번호를 바꿨는데 저장값이 아직 옛 비밀번호면(저장 안 함 · 빠른 접속 · Vault 저장 확인 대기)
        // 이번 연결도 새 비밀번호로 — 점프 서버에 옛 비밀번호를 거듭 보내 계정이 잠기지 않게
        if (_jumpPasswordChange is { } jpc)
        {
            if (_host.Jump is { IsMissingJump: false } j && j.Id == jpc.Id && j.Username == jpc.User && j.Password == jpc.Old)
                j.Password = jpc.New;
            else _jumpPasswordChange = null;   // 저장값이 바뀜(새 값이 저장됐거나 사용자가 고침) 또는 점프 서버가 바뀜 — 저장값을 따른다
        }

        // 다시 연결할 때는 직전에 로그인에 성공한 계정을 그대로 (접속 때 입력만 하고 저장하지 않은 경우에도 다시 묻지 않게)
        var user = _authUser ?? _host.Username;
        var password = _authUser != null ? _authPassword ?? "" : _host.Password;
        var save = false;
        var credSource = _authUser != null ? L.T("직전에 로그인한 계정") : _source?.CredentialSource();

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
            credSource = L.T("입력한 계정");
        }

        // 어느 계정으로 시도하는지 보이게 (Vault · 호스트에 저장 · 입력) — 인증이 실패했을 때 무엇을 고쳐야 하는지 알 수 있게
        _term.Info($"Connecting to {user}@{_host.Address}:{_host.Port} ..." + (credSource != null ? $"  ({credSource})" : ""));
        var (cols, rows) = (_term.Cols, _term.Rows);
        var target = $"{user}@{_host.DisplayName} ({_host.Address})";
        // 서버의 인증 질문(OTP·비밀번호 변경 등)은 사용자에게 묻고, 로그인 전 안내문(배너)·질문 진행은 터미널에 보여준다
        var prompter = new AuthPrompter(
            req => Dispatcher.Invoke(() => AuthPromptDialog.Ask(Window.GetWindow(this), target, req)),
            banner => _term.Info(banner.TrimEnd().Replace("\r\n", "\n").Replace("\n", "\r\n"), "36"),
            _source?.PromptAnswers?.Invoke(),
            _source?.RememberAnswer is { } rememberAnswer ? (q, a) => Dispatcher.Invoke(() => rememberAnswer(q, a)) : null)
        {
            // 직전 시도가 기억한 답으로 실패했으면 이번에는 기억한 답을 쓰지 않고 묻는다 (틀린 기억값으로 반복 실패·잠금 방지)
            SkipRemembered = _skipRemembered,
        };

        AuthPrompter? jumpPrompter = null;
        var authenticated = false;
        // OS 로고용 배포판을 읽을 연결 · 접속한 주소 (SFTP 패널이 채널을 연 뒤에 읽는다 — 아래)
        SshClient? connected = null;
        var connectedAddress = _host.Address;
        try
        {
            _hostKeyAccepted = false;
            _hostKeyRejected = false;
            await Task.Run(() =>
            {
                // 점프 서버(ProxyJump)를 거치면 먼저 터널을 연다 — 점프 서버의 OTP 등도 입력 창으로 묻는다
                JumpTunnel? tunnel = null;
                if (_host.Jump is { } jump)
                {
                    _term.Info(L.F("점프 서버 {0} ({1}) 를 거쳐 연결합니다.", jump.DisplayName, jump.Address));
                    var jumpTarget = L.F("점프 서버 · {0}@{1} ({2})", jump.Username, jump.DisplayName, jump.Address);
                    jumpPrompter = new AuthPrompter(
                        req => Dispatcher.Invoke(() => AuthPromptDialog.Ask(Window.GetWindow(this), jumpTarget, req)),
                        banner => _term.Info(banner.TrimEnd().Replace("\r\n", "\n").Replace("\n", "\r\n"), "36"));
                    // 점프 서버 호스트키도 대상 서버와 같은 정책으로 (설정 "처음 보는 서버 확인"·바뀐 키 경고)
                    tunnel = JumpTunnel.Open(jump, _host.Address, _host.Port, jumpPrompter, VerifyJumpHostKey);
                    // 점프 서버가 비밀번호 변경을 요구해 바꿈 — 터널이 열렸으니 인증(변경)에 성공했다. 대상 서버 연결 결과를 기다리지 않고 바로 저장한다
                    // (대상 서버에서 실패해도 점프 서버 비밀번호는 이미 바뀌었다 — 다음 연결이 옛 비밀번호를 보내 계정이 잠기지 않게)
                    if (jumpPrompter.NewPassword is { } jumpNewPassword)
                        Dispatcher.Invoke(() => OnJumpPasswordChanged(jump, jumpNewPassword));
                }
                SshClient? client = null;
                ShellStream? shell = null;
                try
                {
                    // 키 읽기 실패(키 암호 틀림 등)로 Build 가 실패해도 점프 터널을 닫는다 (자동 재연결마다 터널이 새지 않게)
                    client = new SshClient(SshAuth.Build(_host, user, password, prompter: prompter, tunnel: tunnel)) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
                    client.HostKeyReceived += OnHostKey;
                    prompter.Attach(client);
                    try { client.Connect(); }
                    finally { prompter.Detach(client); }

                    shell = client.CreateShellStream("xterm-256color", cols, rows, 0, 0, 65536);
                    // 출력은 읽기 스레드가 Read 로 꺼낸다 — ShellStream 은 받은 데이터를 모두 내부 버퍼에 쌓고(DataReceived 를 써도)
                    // Read 로만 비우므로, 이벤트만 쓰면 세션 동안 받은 출력이 전부 메모리에 남는다
                    var drained = StartReadLoop(shell, gen);
                    // Closed 는 SSH.NET 이 다른 스레드에서 올린다 — 버퍼에 남은 마지막 출력(logout 등)을 다 그린 뒤 "연결 종료" 안내를 쓴다
                    shell.Closed += (_, _) =>
                    {
                        if (gen != _gen) return;
                        drained.Wait(TimeSpan.FromSeconds(2));
                        if (gen == _gen) OnDisconnected(null);
                    };
                    // 오류 이벤트는 연결·셸이 준비된 뒤에 — 연결 중의 오류는 아래 catch 가 처리한다
                    // (먼저 달면 핸드셰이크 오류가 OnDisconnected 의 자동 재연결과 catch 경로를 둘 다 타 재연결이 겹친다)
                    client.ErrorOccurred += (_, e) => { if (gen == _gen) OnDisconnected(e.Exception.Message); };
                }
                catch
                {
                    // 연결은 됐는데 셸 생성에 실패한 경우에도 연결을 남기지 않는다
                    try { shell?.Dispose(); } catch { }
                    try { client?.Dispose(); } catch { }
                    tunnel?.Dispose();
                    throw;
                }

                // 연결하는 동안 탭이 닫혔거나 새 연결 시도가 시작됐으면 이 연결은 버린다 (누수 방지).
                // 확인과 대입을 DisposeSsh 와 같은 잠금 안에서 — 그 사이에 탭이 닫혀 DisposeSsh 가 빈 값을 정리하고 끝나는 일이 없게
                bool discard;
                lock (_sshLock)
                {
                    discard = _closed || gen != _gen;
                    if (!discard)
                    {
                        _client = client;
                        _shell = shell;
                        _tunnel = tunnel;
                    }
                }
                if (discard)
                {
                    try { shell.Dispose(); } catch { }
                    try { client.Dispose(); } catch { }
                    tunnel?.Dispose();
                    throw new OperationCanceledException();
                }
                SshServerOs.Report(_host, client);
                connected = client;
                connectedAddress = _host.Address;
            });
            authenticated = true;
            _skipRemembered = false;

            // 셸은 터미널 화면이 준비되기 전의 기본 크기(120x30)로 만들어졌을 수 있다 — 그사이 Ready·Resized 로 온 크기 변경은
            // _shell 이 아직 없어 버려졌으므로, 지금 화면 크기와 다르면 서버에 다시 알린다
            if (_term.Cols != cols || _term.Rows != rows) SendResize(_term.Cols, _term.Rows);

            // 연결 직후 서버가 셸을 바로 닫은 경우(nologin, 세션 수 제한 등) 이미 Disconnected 로 바뀌어 있음
            if (!TrySetConnected()) return;
            AttachForwards(gen);
            _askCredentials = false;
            _autoAttempts = 0;
            _triedPublic = false;
            _promptCwd = null;   // 새 셸은 홈 폴더에서 시작
            _lastCwd = null;     // 앞 셸이 OSC 7 로 알려준 폴더는 새 셸과 맞지 않는다 (get_terminal_info 가 옛 값을 보고하지 않게)
            _lastPromptScreen = null;
            _loginPromptHost = null;   // 새 로그인 — 첫 프롬프트의 호스트를 다시 읽는다
            _ = CaptureLoginPromptHostAsync(gen);
            // 패널이 sudo 사용자(RunAs)였으면 그 폴더(/root 등)는 로그인 계정으로 못 열 수 있다 — 다시 연결 뒤 홈으로
            var sftpWasRunAs = _sftp?.RunAs != null;
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
                var sftpShown = _sftp != null && _sftp.Visibility == Visibility.Visible;
                // 처음 로그인했을 때 SFTP 패널 자동 열기 (사용자가 닫은 뒤 재연결할 때는 다시 열지 않음)
                if (!_sftpAutoTried && SettingsStore.Current.SftpAutoOpen)
                {
                    _sftpAutoTried = true;
                    if (!sftpShown) ToggleSftp(true);   // 새로 열면 ToggleSftp 가 목록까지 연다
                }
                // 패널이 이미 보이는 경우: 로그인 전에 열어 "로그인이 끝난 뒤…" 안내에 머문 패널은 지금 열고,
                // 다시 연결된 경우는 새 연결로 목록을 새로 고친다
                if (sftpShown && _sftp != null)
                    _sftpOpening = !_sftp.IsOpen ? OpenSftpAsync(_sftp) : sftpWasRunAs ? RefreshSftpToHomeAsync(_sftp) : _sftp.RefreshAsync();
                if (IsVisible) _term.FocusTerminal();
                if (connected != null) _ = ProbeDistroAfterSftpAsync(connected, connectedAddress, gen);
            });
        }
        catch (OperationCanceledException) when (_closed || gen != _gen)
        {
            // 닫힌 탭 / 이전 시도 — 조용히 종료
        }
        catch (Exception ex) when (ex is AuthPromptCancelledException || prompter.Cancelled || jumpPrompter?.Cancelled == true)
        {
            // 사용자가 OTP·비밀번호 변경 입력 창을 취소함 — 인증 실패가 아니므로 계정을 다시 묻지 않고, 자동 재연결도 하지 않는다
            _term.Info(L.T("인증 입력을 취소했습니다. Enter 키를 누르면 다시 연결합니다."), "33");
            DisposeSsh();
            SetStatus(SessionStatus.Disconnected);
        }
        catch (JumpTunnelException ex)
        {
            // 점프 서버 단계의 실패 — 대상 서버의 계정을 묻지 않는다(_askCredentials 그대로).
            // 인증·호스트키·설정 문제는 다시 시도해도 같으므로 자동 재연결하지 않는다 (계정 잠금·반복 경고 방지)
            _term.Info(L.F("연결 실패: {0}", ex.Message), "31");
            DisposeSsh();
            SetStatus(SessionStatus.Disconnected);
            if (ex.Kind == JumpFailure.HostKey)
                _term.Info(L.T("점프 서버의 호스트키를 신뢰할 수 없습니다. 점프 서버에 직접 접속해 키를 확인한 뒤 Enter 키를 누르면 다시 연결합니다."), "33");
            else if (ex.Kind == JumpFailure.AuthPhase)
                _term.Info(L.T("비밀번호·OTP 가 다시 전송되지 않도록 자동으로 다시 연결하지 않습니다. Enter 키를 누르면 다시 연결합니다."), "33");
            else if (ex.IsPermanent)
                _term.Info(L.T("점프 서버의 계정(호스트·Vault)을 확인한 뒤 Enter 키를 누르면 다시 연결합니다."), "33");
            else if (!ScheduleAutoReconnect())
                _term.Info(L.T("Enter 키를 누르면 다시 연결합니다. (탭 우클릭 → 다시 연결)"));
        }
        catch (SshKeyLoadException ex)
        {
            // 로컬 키를 읽지 못함 (키 암호 틀림·파일 없음) — 연결 실패가 아니므로 자동 재연결하지 않는다
            _term.Info(L.F("연결 실패: {0}", ex.Message), "31");
            _term.Info(L.T("SSH 키와 키 암호(호스트·Vault)를 확인한 뒤 Enter 키를 누르면 다시 연결합니다."), "33");
            DisposeSsh();
            SetStatus(SessionStatus.Disconnected);
        }
        catch (SshAuthenticationException ex)
        {
            // 저장된 비밀번호가 바뀐 경우 등: 다음 시도에서 계정을 다시 묻는다
            _askCredentials = true;
            _term.Info(L.F("인증 실패: {0}", ex.Message), "31");
            if (prompter.LastAnsweredWithSavedPassword)
                _term.Info(L.F("{0} 의 비밀번호를 서버가 거부했습니다. 비밀번호가 맞는지 확인하세요. 계속 실패하면 계정이 잠길 수 있습니다.", credSource ?? L.T("저장된 계정")), "33");
            else if (KbdAuth.FailureHint(ex.Message, _host.HasKey, !string.IsNullOrEmpty(password), answeredPrompt: prompter.AnsweredByUser) is { } hint) _term.Info(hint, "33");
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
        catch (Exception ex) when (_hostKeyAccepted && !_retriedAfterHostKey && !prompter.AuthStarted)
        {
            // 호스트키 확인 창을 읽는 동안 키 교환 시간이 초과될 수 있음 — 키는 저장됐으니 한 번 자동 재시도.
            // 인증을 이미 시작했으면(비밀번호·OTP 를 보냄) 재시도하지 않는다 (아래)
            _retriedAfterHostKey = true;
            _publicRetryPending = _triedPublic;   // 공인 IP 로 시도하던 중이면 그 주소로 다시
            _connecting = false;
            DisposeSsh();
            _term.Info(L.F("({0}) 호스트키를 저장했으니 다시 연결합니다...", ex.Message.Split('\n')[0]));
            _ = ConnectAsync();
            return;
        }
        catch (Exception ex) when (SshFailure.IsPermanent(ex, prompter.AuthStarted))
        {
            // 비밀번호·OTP 답을 실제로 보낸 뒤(인증 중·인증 뒤)의 끊김·시간 초과 — OTP·Push 승인 대기 초과, 서버의 LoginGraceTime·MaxAuthTries 등.
            // 네트워크 오류로 보고 공인 IP 재시도·자동 재연결을 하면 비밀번호와 새 Push·OTP 요청이 또 나가 계정이 잠길 수 있다.
            // 공개키만 써 본 시도(재부팅 중 pam_nologin, VPN 순단)는 여기로 오지 않고 아래에서 네트워크 실패로 다시 시도한다
            _term.Info(L.F("연결 실패: {0}", ex.Message.Split('\n')[0]), "31");
            if (!authenticated)
                _term.Info(L.T("인증 중 또는 인증 뒤에 연결이 끊기거나 시간이 초과되었습니다 (OTP·Push 승인 대기 시간 초과, 서버의 인증 시도 횟수 제한 등)."), "33");
            _term.Info(L.T("비밀번호·OTP 가 다시 전송되지 않도록 자동으로 다시 연결하지 않습니다. Enter 키를 누르면 다시 연결합니다."), "33");
            DisposeSsh();
            SetStatus(SessionStatus.Disconnected);
        }
        catch (Exception ex) when (SshFailure.CanTryPublicIp(ex, prompter.AuthStarted) &&!_triedPublic && _host.Jump == null && _host.HasPublicIp && _host.Address != _host.PublicIp)
        {
            // 사설 IP 로 연결이 안 되면(VPN 꺼짐 등) 공인 IP 로 한 번 더 — 인증 실패·인증 단계의 실패는 여기로 오지 않는다
            _triedPublic = true;
            _publicRetryPending = true;   // 다음 ConnectAsync 가 주소를 사설로 되돌리지 않게
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
            // 호스트키 저장 뒤 재시도·공인 IP 재시도는 catch 안에서 새 ConnectAsync 를 이미 시작했다(첫 await 까지 동기 실행 → _gen 증가).
            // 그 시도가 진행 중인데 여기서 _connecting 을 끄면 탭 메뉴 "다시 연결" 이 시도를 하나 더 겹쳐 시작한다 (OTP·비밀번호 중복 전송)
            if (gen == _gen) _connecting = false;
            // 기억한 답(Push 선택 등)을 쓴 시도가 실패함 — 이 탭의 다음 시도는 기억한 답 없이 묻는다 (성공하면 위에서 다시 끈다)
            if (!authenticated && prompter.UsedRemembered) _skipRemembered = true;
        }
    }

    /// <summary>직전 시도가 기억한 답으로 실패함 — 다음 시도는 기억한 답을 쓰지 않는다</summary>
    private bool _skipRemembered;

    /// <summary>이 탭에서 점프 서버 비밀번호를 바꿈 (점프 서버 Id · 사용자명 · 옛 · 새 비밀번호). 저장값이 옛 값이면 다음 연결에 새 값을 쓴다</summary>
    private (Guid Id, string User, string Old, string New)? _jumpPasswordChange;

    /// <summary>
    /// 점프 서버가 요구해 비밀번호를 바꿈 (UI 스레드). 저장된 비밀번호로 로그인하던 경우만 새 값으로 저장한다 —
    /// 대상 서버와 같은 기준 (점프 서버 호스트 또는 그 Vault 계정, SessionSource.SaveJumpCredentials).
    /// </summary>
    private void OnJumpPasswordChanged(HostEntry jump, string newPassword)
    {
        _term.Info(L.T("점프 서버 비밀번호를 변경했습니다."), "32");
        var old = jump.Password;
        if (string.IsNullOrEmpty(old) || old == newPassword) return;
        _jumpPasswordChange = (jump.Id, jump.Username, old, newPassword);
        jump.Password = newPassword;   // 접속용 사본 — 이 탭의 다음 연결(Reload 없는 빠른 접속 포함)도 새 값으로
        _source?.SaveJumpCredentials?.Invoke(jump.Username, newPassword);
    }

    private bool _askCredentials, _hostKeyAccepted, _hostKeyRejected, _retriedAfterHostKey, _triedPublic, _publicRetryPending;
    /// <summary>탭을 열 때의 접속 주소 (공인 IP 로 넘어갔다가도 다음 연결은 이 주소부터)</summary>
    private readonly string _originalAddress;
    private int _autoAttempts;
    private const int MaxAutoAttempts = 5;

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
}
