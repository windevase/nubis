using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Nubis;

/// <summary>서버가 묻는 인증 질문 (keyboard-interactive 의 OTP·비밀번호 변경 등) 한 묶음</summary>
public sealed record AuthPromptRequest(string Title, string Instruction, IReadOnlyList<AuthPromptField> Fields)
{
    /// <summary>입력 창이 채운다: 질문별 "이 답 기억하기" 체크 (CanRemember 인 질문만)</summary>
    public bool[] Remember { get; } = new bool[Fields.Count];
}

/// <param name="Secret">입력값을 가린다 (서버가 화면 표시(echo)를 끈 질문)</param>
/// <param name="CanRemember">"이 답 기억하기" 를 보여준다 (화면에 보이는 질문만 — OTP 코드처럼 가려지는 값은 기억하지 않음)</param>
public sealed record AuthPromptField(string Label, bool Secret, bool CanRemember = false);

/// <summary>
/// 대화형 연결(SSH 탭·SFTP 패널)에서 서버의 인증 질문에 사용자가 답하게 한다.
/// ask 는 SSH.NET 의 백그라운드 스레드에서 호출되므로 창은 호출하는 쪽이 UI 스레드로 넘겨 띄운다. null 이면 취소.
/// 이 객체가 없는 비대화형 연결(run_command·SFTP 도구)은 비밀번호로 답할 수 없는 질문을 받으면 바로 실패한다.
/// </summary>
/// <param name="remembered">호스트에 기억해 둔 답 (질문 → 답). 화면에 보이는 질문에만 쓴다</param>
/// <param name="remember">"이 답 기억하기" 를 고른 답 저장 (null 이면 체크박스를 보이지 않음)</param>
public sealed class AuthPrompter(Func<AuthPromptRequest, string[]?> ask, Action<string>? banner = null,
    IReadOnlyDictionary<string, string>? remembered = null, Action<string, string>? remember = null)
{
    /// <summary>인증 단계 대기 시간 — 사용자가 OTP 등을 입력하는 동안 연결 타임아웃(15초)에 걸리지 않게</summary>
    public static readonly TimeSpan AuthTimeout = TimeSpan.FromMinutes(3);

    private TimeSpan? _origTimeout;

    internal string[]? Ask(AuthPromptRequest r) => ask(r);
    internal Action<string>? Banner => banner;
    internal string? Remembered(string prompt) =>
        !SkipRemembered && remembered != null && remembered.TryGetValue(prompt, out var a) ? a : null;
    internal Action<string, string>? RememberAnswer => remember;

    /// <summary>
    /// 이번 시도에서 서버의 호스트키를 받았거나 인증 질문을 받음 — 이 뒤의 끊김·시간 초과(Push·OTP 대기 초과, LoginGraceTime,
    /// MaxAuthTries)는 네트워크 오류로 보지 않는다. 공인 IP 재시도·자동 재연결이 비밀번호와 새 Push 를 다시 보내 계정이 잠기지 않게.
    /// </summary>
    public bool ReachedAuth { get; internal set; }

    /// <summary>인증을 실제로 시작함 (keyboard-interactive 질문을 받았거나 password 방식으로 비밀번호를 보냄)</summary>
    public bool AuthStarted { get; internal set; }

    /// <summary>이번 시도에서 호스트에 기억해 둔 답을 썼음 — 실패했으면 다음 시도는 기억한 답 없이 묻는다</summary>
    public bool UsedRemembered { get; internal set; }

    /// <summary>기억해 둔 답을 쓰지 않고 사용자에게 묻는다 (직전 시도가 기억한 답으로 실패한 경우 탭이 켠다)</summary>
    public bool SkipRemembered { get; set; }

    internal void MarkAuthStarted() => AuthStarted = ReachedAuth = true;

    /// <summary>서버 요구로 비밀번호를 바꿨으면 새 비밀번호 (연결 후 저장값 갱신용)</summary>
    public string? NewPassword { get; internal set; }

    /// <summary>
    /// 마지막으로 받은 인증 질문에 저장된 비밀번호로만 답했음 — 이 상태로 인증이 실패했으면
    /// 다음 질문(OTP 등)까지 가지 못하고 저장된 비밀번호가 거부된 것이다.
    /// </summary>
    public bool LastAnsweredWithSavedPassword { get; internal set; }

    /// <summary>
    /// 사용자가 인증 입력 창(OTP·비밀번호 변경)을 취소했음 — 인증 실패가 아니므로 계정을 다시 묻지 않는다.
    /// SSH.NET 이 예외 종류를 바꿔 전달하더라도 알 수 있게 예외(AuthPromptCancelledException)와 별도로 둔다.
    /// </summary>
    public bool Cancelled { get; internal set; }

    private EventHandler<HostKeyEventArgs>? _hostKeyHandler;

    /// <summary>
    /// Connect 전에 호출. 키 교환이 끝나 호스트키를 받은 뒤(= TCP 연결·키 교환 타임아웃은 그대로 둔 채)
    /// 인증 단계의 대기 시간만 늘린다. SSH.NET 은 인증 응답을 ConnectionInfo.Timeout 만큼만 기다린다.
    /// </summary>
    public void Attach(BaseClient client)
    {
        var ci = client.ConnectionInfo;
        _origTimeout = ci.Timeout;
        if (_hostKeyHandler != null) client.HostKeyReceived -= _hostKeyHandler;
        _hostKeyHandler = (_, _) =>
        {
            ReachedAuth = true;
            if (ci.Timeout < AuthTimeout) ci.Timeout = AuthTimeout;
        };
        client.HostKeyReceived += _hostKeyHandler;
    }

    /// <summary>
    /// Connect 뒤(성공·실패 모두) 호출 — 채널 열기 등 이후 동작의 타임아웃을 원래대로.
    /// 호스트키 이벤트 처리기도 뗀다 — 연결 뒤 키 재교환(rekey) 때 다시 3분으로 늘리지 않게.
    /// </summary>
    public void Detach(BaseClient client)
    {
        if (_hostKeyHandler != null)
        {
            client.HostKeyReceived -= _hostKeyHandler;
            _hostKeyHandler = null;
        }
        if (_origTimeout is { } t) client.ConnectionInfo.Timeout = t;
    }
}

/// <summary>사용자가 서버의 인증 질문(OTP·비밀번호 변경) 입력 창을 취소함 — 인증 실패와 구분한다</summary>
public sealed class AuthPromptCancelledException(string message) : SshAuthenticationException(message);

/// <summary>
/// 로컬 SSH 키를 읽지 못함 (키 암호가 틀림·파일 없음·지원하지 않는 형식) — 다시 시도해도 같으므로 자동 재연결하지 않는다.
/// 기존 호출하는 쪽(InvalidOperationException·Exception 을 잡는 곳)이 그대로 동작하도록 InvalidOperationException 을 상속.
/// </summary>
public sealed class SshKeyLoadException(string message, Exception inner) : InvalidOperationException(message, inner);

/// <summary>연결 실패 분류 (UI·SSH.NET 연결과 무관, 테스트 대상)</summary>
public static class SshFailure
{
    /// <summary>네트워크 계층 실패로 보이는가 (연결 거부·시간 초과·끊김)</summary>
    public static bool IsNetwork(Exception ex) =>
        ex is System.Net.Sockets.SocketException or SshOperationTimeoutException or SshConnectionException or TimeoutException ||
        ex.InnerException is System.Net.Sockets.SocketException;

    /// <summary>
    /// 다시 시도해도 같거나, 다시 시도하면 위험한 실패 (자동 재연결·공인 IP 재시도 하지 않음):
    /// 인증 실패·입력 취소·로컬 키 오류, 그리고 비밀번호·keyboard-interactive 답을 실제로 보낸 뒤(authStarted)의 모든 실패 —
    /// 그 뒤의 시간 초과·끊김을 다시 시도하면 비밀번호와 새 Push·OTP 요청이 또 나가 계정이 잠길 수 있다.
    /// 공개키만 써 본 시도(재부팅 중 pam_nologin, 인증 중 VPN 순단 등)의 끊김·시간 초과는 네트워크 실패로 보고 다시 시도해도 된다
    /// (인증 실패 SshAuthenticationException 은 여전히 다시 시도하지 않는다).
    /// </summary>
    public static bool IsPermanent(Exception ex, bool authStarted) =>
        authStarted || ex is SshAuthenticationException or SshKeyLoadException;

    /// <summary>사설 IP 실패 뒤 공인 IP 로 한 번 더 시도해도 되는가 (비밀번호·OTP 를 보내기 전의 네트워크 실패만)</summary>
    public static bool CanTryPublicIp(Exception ex, bool authStarted) => !IsPermanent(ex, authStarted) && IsNetwork(ex);

    /// <summary>
    /// 점프 서버 연결 실패의 종류 — 인증을 시작한 뒤(비밀번호·OTP 답을 보냄, 비대화형은 호스트키를 받은 뒤)의 실패는 네트워크 실패로 보지 않는다
    /// </summary>
    public static JumpFailure JumpKind(Exception ex, bool keyRejected, bool authStarted) =>
        keyRejected ? JumpFailure.HostKey
        : ex is SshAuthenticationException ? JumpFailure.Auth
        : ex is SshKeyLoadException ? JumpFailure.Config
        : authStarted ? JumpFailure.AuthPhase
        : JumpFailure.Network;
}

public static class SshAuth
{
    /// <summary>
    /// 키 / 비밀번호 / keyboard-interactive 인증을 담은 ConnectionInfo.
    /// prompter 가 있으면(대화형) 비밀번호로 답할 수 없는 질문(OTP 등)을 사용자에게 묻고,
    /// 비밀번호가 없어도 keyboard-interactive 를 시도한다 (키 + OTP 를 요구하는 서버).
    /// </summary>
    /// <param name="tunnel">점프 서버 터널을 거치면 그 로컬 포트로 접속 (호스트키는 호출하는 쪽이 대상 서버 기준으로 확인)</param>
    public static ConnectionInfo Build(HostEntry host, string user, string password, int timeoutSec = 15, AuthPrompter? prompter = null, JumpTunnel? tunnel = null)
    {
        // 순서가 시도 순서다 (SSH.NET 은 서버가 허용한 것 중 이 목록 순서대로 시도). OpenSSH 와 같이 키 → keyboard-interactive → password.
        // password 를 먼저 하면, 2단계 인증(PAM 의 OTP·Push 등)을 keyboard-interactive 로만 받는 서버에서 password 방식이
        // 다음 질문에 답하지 못해 실패하고, 그 실패 때문에 이어지는 keyboard-interactive 까지 거부된다 (OpenSSH 는 되는데 Nubis 만 실패).
        var methods = new List<AuthenticationMethod>();
        if (LoadKey(host, password) is { } key) methods.Add(new PrivateKeyAuthenticationMethod(user, key));
        else
        {
            // 키를 지정하지 않았으면 OpenSSH 처럼 ssh-agent 의 키 → ~/.ssh 기본 키 순으로
            var sources = new List<IPrivateKeySource>();
            if (SshAgent.KeySource() is { } agent) sources.Add(agent);
            sources.AddRange(LoadDefaultKeys());
            if (sources.Count > 0) methods.Add(new PrivateKeyAuthenticationMethod(user, sources.ToArray()));
        }
        var state = new KbdAuth.State();
        if (!string.IsNullOrEmpty(password) || prompter != null)
        {
            var kbd = new KeyboardInteractiveAuthenticationMethod(user);
            kbd.AuthenticationPrompt += (_, e) => Answer(e, password, state, prompter);
            methods.Add(kbd);
        }
        if (!string.IsNullOrEmpty(password))
        {
            var pw = new PasswordAuthenticationMethod(user, password);
            pw.PasswordExpired += (_, e) => e.NewPassword = System.Text.Encoding.UTF8.GetBytes(AskNewPassword(prompter));
            methods.Add(new PasswordUnlessSent(pw, state, prompter));
        }
        if (methods.Count == 0) throw new InvalidOperationException("인증 정보(비밀번호 또는 키)가 없습니다.");
        var ci = new ConnectionInfo(tunnel?.LocalHost ?? host.Address, tunnel?.LocalPort ?? host.Port, user, methods.ToArray()) { Timeout = TimeSpan.FromSeconds(timeoutSec) };
        if (prompter?.Banner is { } banner)
            ci.AuthenticationBanner += (_, e) => { if (!string.IsNullOrWhiteSpace(e.BannerMessage)) banner(e.BannerMessage); };
        return ci;
    }

    /// <summary>
    /// password 방식: 같은 연결에서 keyboard-interactive 로 이미 이 비밀번호를 보냈다가 실패했으면 다시 보내지 않고 멈춘다.
    /// 그대로 두면 2단계 인증 서버에서 Push 시간 초과·OTP 오류 뒤 같은 비밀번호가 password 방식으로 한 번 더 나가
    /// (그 서버에서는 성공할 수 없음) 실패 횟수만 두 배로 쌓여 계정이 빨리 잠긴다.
    /// keyboard-interactive 를 허용하지 않는 서버(password 만)에서는 그대로 password 방식으로 보낸다.
    /// </summary>
    private sealed class PasswordUnlessSent(PasswordAuthenticationMethod inner, KbdAuth.State state, AuthPrompter? prompter) : AuthenticationMethod(inner.Username)
    {
        public override string Name => inner.Name;

        public override AuthenticationResult Authenticate(Session session)
        {
            if (state.PasswordUsed)
                throw new SshAuthenticationException("Permission denied (keyboard-interactive).");
            prompter?.MarkAuthStarted();   // 비밀번호를 보낸다 — 이 뒤의 실패는 다시 시도하지 않는다
            var result = inner.Authenticate(session);
            AllowedAuthentications = inner.AllowedAuthentications;
            return result;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>keyboard-interactive 질문 응답: 로그인 비밀번호 질문은 저장된 비밀번호로(한 번만), 나머지는 사용자에게</summary>
    private static void Answer(AuthenticationPromptEventArgs e, string password, KbdAuth.State state, AuthPrompter? prompter)
    {
        prompter?.MarkAuthStarted();
        var prompts = e.Prompts.OrderBy(p => p.Id).ToList();
        var auto = KbdAuth.AutoFill(prompts.Select(p => p.Request).ToList(), password, state);
        for (var i = 0; i < prompts.Count; i++)
            if (auto[i] != null) prompts[i].Response = auto[i];

        // 호스트에 기억해 둔 답 ("Choose [1] Push, or [2] TOTP" 같은 고정 선택). KbdAuth.CanRemember 참고 —
        // 예전에 저장된 OTP 같은 값은 쓰지 않는다
        var memo = new string?[prompts.Count];
        if (prompter != null)
            for (var i = 0; i < prompts.Count; i++)
                if (auto[i] == null && prompter.Remembered(prompts[i].Request.Trim()) is { } a && KbdAuth.CanRemember(prompts[i].Request.Trim(), prompts[i].IsEchoed, a))
                {
                    prompts[i].Response = memo[i] = a;
                    prompter.UsedRemembered = true;
                }
        var rest = prompts.Where((_, i) => auto[i] == null && memo[i] == null).ToList();

        if (prompter != null)
        {
            // OpenSSH 처럼 서버의 질문을 터미널에 보여준다 — 어느 단계에서 실패했는지 알 수 있게 (비밀번호·가려진 값은 쓰지 않음)
            prompter.LastAnsweredWithSavedPassword = rest.Count == 0 && auto.Any(x => x != null) && memo.All(x => x == null);
            if (prompter.Banner is { } show)
            {
                var lines = new List<string>();
                if (!string.IsNullOrWhiteSpace(e.Instruction)) lines.Add(e.Instruction.Trim());
                for (var i = 0; i < prompts.Count; i++)
                    lines.Add(prompts[i].Request.Trim() + "  " + (auto[i] != null ? L.T("(비밀번호로 답함)")
                        : memo[i] != null ? (memo[i]!.Length == 0 ? L.T("(기억한 답: 빈 값)") : L.F("(기억한 답: {0})", memo[i]!))
                        : L.T("(입력 창)")));
                if (lines.Count > 0) show(string.Join("\n", lines));
            }
        }
        if (rest.Count == 0) return;

        if (prompter == null)
            throw new SshAuthenticationException(KbdAuth.NonInteractiveError(rest[0].Request, state.PasswordUsed));

        var canRemember = prompter.RememberAnswer != null;
        var req = new AuthPromptRequest(L.T("서버가 추가 인증을 요구합니다"), e.Instruction?.Trim() ?? "",
            rest.Select(p => new AuthPromptField(p.Request.Trim(), !p.IsEchoed, canRemember)).ToList());
        var answers = prompter.Ask(req);
        if (answers == null)
        {
            prompter.Cancelled = true;
            throw new AuthPromptCancelledException(L.T("인증 입력이 취소되었습니다."));
        }
        var answered = new List<(string Prompt, string Answer)>();
        for (var i = 0; i < rest.Count; i++)
        {
            var answer = i < answers.Length ? answers[i] : "";
            rest[i].Response = answer;
            answered.Add((rest[i].Request.Trim(), answer));
            if (req.Remember[i] && KbdAuth.CanRemember(rest[i].Request.Trim(), rest[i].IsEchoed, answer))
                prompter.RememberAnswer?.Invoke(rest[i].Request.Trim(), answer);
        }
        // PAM 이 keyboard-interactive 로 비밀번호 변경을 요구함 ("New password:" → "Retype new password:") —
        // 두 값이 같으면 연결 성공 뒤 저장된 비밀번호를 새 값으로 갱신하도록 기록 (질문이 여러 번에 나뉘어 와도 State 가 이어 준다)
        if (KbdAuth.TrackNewPassword(state, answered) is { } newPassword) prompter.NewPassword = newPassword;
    }

    /// <summary>서버가 비밀번호 만료를 알림 (SSH_MSG_USERAUTH_PASSWD_CHANGEREQ) — 대화형이면 새 비밀번호를 받는다</summary>
    private static string AskNewPassword(AuthPrompter? prompter)
    {
        if (prompter == null)
            throw new SshAuthenticationException("비밀번호가 만료되어 서버가 변경을 요구합니다 — 비대화형 연결에서는 변경할 수 없습니다. 앱에서 직접 접속해 변경하세요.");
        var instruction = L.T("비밀번호가 만료되어 서버가 변경을 요구합니다.");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var a = prompter.Ask(new AuthPromptRequest(L.T("비밀번호 변경"), instruction,
                [new AuthPromptField(L.T("새 비밀번호"), true), new AuthPromptField(L.T("새 비밀번호 확인"), true)]));
            if (a == null)
            {
                prompter.Cancelled = true;
                throw new AuthPromptCancelledException(L.T("비밀번호 변경이 취소되었습니다."));
            }
            if (a.Length >= 2 && a[0].Length > 0 && a[0] == a[1])
            {
                prompter.NewPassword = a[0];
                return a[0];
            }
            instruction = L.T("두 비밀번호가 같지 않습니다. 다시 입력하세요.");
        }
        throw new SshAuthenticationException(L.T("비밀번호 변경이 취소되었습니다."));
    }

    /// <summary>
    /// 개인키: Vault 키 내용이 있으면 그것, 없으면 키 파일. 키에 암호가 걸려 있으면
    /// Vault 에 저장한 키 암호 → (없으면) 비밀번호 순으로 쓴다.
    /// </summary>
    private static PrivateKeyFile? LoadKey(HostEntry host, string password)
    {
        // 키 암호가 틀림·파일 없음 등은 연결 실패가 아니다 — 구분해서 자동 재연결하지 않게
        try { return LoadKeyCore(host, password); }
        catch (Exception ex) when (ex is not SshKeyLoadException)
        {
            throw new SshKeyLoadException(L.F("SSH 키를 읽지 못했습니다 — {0}", ex.Message), ex);
        }
    }

    private static PrivateKeyFile? LoadKeyCore(HostEntry host, string password)
    {
        if (!string.IsNullOrEmpty(host.KeyMaterial))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(host.KeyMaterial);
            var pass = !string.IsNullOrEmpty(host.KeyPassphrase) ? host.KeyPassphrase : password;
            try { return new PrivateKeyFile(new System.IO.MemoryStream(bytes)); }
            catch (SshPassPhraseNullOrEmptyException) { return new PrivateKeyFile(new System.IO.MemoryStream(bytes), pass); }
        }
        if (string.IsNullOrWhiteSpace(host.KeyPath)) return null;
        try { return new PrivateKeyFile(host.KeyPath); }
        catch (SshPassPhraseNullOrEmptyException) { return new PrivateKeyFile(host.KeyPath, password); }
    }

    /// <summary>기본 키 폴더 (null 이면 %USERPROFILE%\.ssh). 테스트가 빈 폴더로 바꾼다</summary>
    public static string? DefaultKeyDir { get; set; }

    /// <summary>OpenSSH 가 키를 지정하지 않았을 때 시도하는 기본 키 (이 순서)</summary>
    public static readonly string[] DefaultKeyNames = ["id_ed25519", "id_ecdsa", "id_rsa"];

    /// <summary>
    /// 호스트·Vault 에 키가 없을 때 OpenSSH 처럼 ~/.ssh 의 기본 키를 시도한다 (ssh host 는 되는데 Nubis 는 비밀번호를 묻는 차이 방지).
    /// 암호가 걸린 키·읽을 수 없는 키는 건너뛴다 (묻지 않음 — 쓰려면 호스트나 Vault 에 키와 암호를 지정).
    /// </summary>
    /// <summary>기본 키 파일이나 ssh-agent 키가 하나라도 있는가 (비밀번호가 없어도 먼저 키로 시도해 볼지)</summary>
    public static bool HasDefaultKeyFiles()
    {
        var dir = DefaultKeyDir ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        if (DefaultKeyNames.Any(n => System.IO.File.Exists(System.IO.Path.Combine(dir, n)))) return true;
        // UI 스레드에서도 불린다 — 에이전트 파이프가 없으면 묻지 않고, 있어도 짧게만 기다린다 (잠긴·확인 대기 에이전트에 멈추지 않게)
        return SshAgent.PipeExists() && SshAgent.ListKeys(SshAgent.ListTimeoutMs).Count > 0;
    }

    public static IPrivateKeySource[] LoadDefaultKeys()
    {
        var dir = DefaultKeyDir ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        var keys = new List<IPrivateKeySource>();
        foreach (var name in DefaultKeyNames)
        {
            var path = System.IO.Path.Combine(dir, name);
            if (!System.IO.File.Exists(path)) continue;
            try { keys.Add(new PrivateKeyFile(path)); }
            catch { /* 암호가 걸렸거나 지원하지 않는 형식 */ }
        }
        return keys.ToArray();
    }

    public static string HostKeyId(HostEntry h) => $"{h.Address}:{h.Port}";
    public static string Fingerprint(HostKeyEventArgs e) => $"{e.HostKeyName} SHA256:{e.FingerPrintSHA256}";

    /// <summary>
    /// 비대화형 연결용 호스트키 확인 (ssh 의 accept-new 와 같음): 처음 보는 서버는 저장 후 신뢰, 저장값과 다르면 거부.
    /// 거부 사유를 반환하고, 신뢰하면 null.
    /// </summary>
    public static string? VerifyAcceptNew(HostEntry h, HostKeyEventArgs e)
    {
        var id = HostKeyId(h);
        var fp = Fingerprint(e);
        var known = KnownHostStore.Get(id);
        if (known == null)
        {
            // OpenSSH known_hosts 참고: 같은 키면 신뢰, 다른 키·폐기된 키면 거부 (비대화형이라 묻지 않는다)
            switch (OpenSshKnownHosts.Check(h.Address, h.Port, e.HostKey))
            {
                case OpenSshKnownHosts.Verdict.Mismatch:
                    return $"호스트키가 OpenSSH known_hosts 에 저장된 값과 다릅니다 ({fp}). 앱에서 직접 접속해 키를 확인·신뢰한 뒤 다시 시도하세요.";
                case OpenSshKnownHosts.Verdict.Revoked:
                    return $"OpenSSH known_hosts 에 폐기(@revoked)된 호스트키입니다 ({fp}).";
            }
            KnownHostStore.Set(id, fp);
            return null;
        }
        if (KnownHostStore.IsRejected(known))
            return "사용자가 이 서버의 호스트키를 거부한 적이 있습니다. 앱에서 직접 접속해 키를 확인·신뢰한 뒤 다시 시도하세요.";
        return known == fp ? null : $"호스트키가 저장된 값과 다릅니다 ({known} → {fp}). Known Hosts 에서 확인 후 삭제하세요.";
    }
}

/// <summary>keyboard-interactive 응답 판단 (UI·SSH.NET 과 무관, 테스트 대상)</summary>
public static class KbdAuth
{
    public sealed class State
    {
        /// <summary>이번 연결에서 저장된 비밀번호를 이미 답했음 — 또 물으면 틀렸다는 뜻이라 같은 값을 다시 보내지 않는다(계정 잠금 방지)</summary>
        public bool PasswordUsed;
        /// <summary>keyboard-interactive 비밀번호 변경에서 "New password:" 에 답한 값 (확인 질문과 같으면 새 비밀번호로 확정)</summary>
        public string? PendingNewPassword;
    }

    private static readonly Regex PasswordWord = new(@"password|passwd|passwort|kennwort|mot de passe|contraseña|비밀\s*번호|암호|パスワード|密码|密碼",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // 새 비밀번호·확인 입력, OTP·토큰·인증 코드처럼 로그인 비밀번호가 아닌 질문
    private static readonly Regex NotLoginPassword = new(
        @"\bnew\b|re-?enter|re-?type|again|confirm|repeat|verif|\botp\b|one[- ]?time|token|\bcode\b|passcode|\bpin\b|2fa|mfa|duo|authenticator|" +
        @"새\s*(비밀|암호)|다시|확인|변경|일회용|인증\s*(번호|코드)|토큰|보안\s*코드",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// 기억한 답을 쓰거나 저장해도 되는가: 화면에 보이는 질문의 답, 또는 가려진 질문이라도 빈 답("Press enter ...")만.
    /// 가려진 질문의 실제 값(OTP 코드·비밀번호)은 기억하지 않는다.
    /// </summary>
    public static bool CanUseRemembered(bool echoed, string answer) => echoed || answer.Length == 0;

    // OTP·토큰·인증 코드·PIN·비밀번호처럼 답이 비밀인 질문 (화면에 보이게 묻더라도 — Duo 의 "Passcode or option (1-3):" 등)
    private static readonly Regex SecretPrompt = new(
        @"\botp\b|one[- ]?time|token|\bcode\b|passcode|\bpin\b|2fa|mfa|authenticator|verif|" +
        @"password|passwd|passwort|kennwort|일회용|인증\s*(번호|코드)|토큰|보안\s*코드|비밀\s*번호|암호|パスワード|密码|密碼",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex LongDigits = new(@"\d{6,}", RegexOptions.CultureInvariant);
    private static readonly Regex ShortOption = new(@"^\d{1,2}$", RegexOptions.CultureInvariant);
    // 선택지 단어 ("push"·"sms"·"phone" — Duo 의 "Passcode or option" 에 숫자 대신 넣는 말). 글자만, 8자 이하
    private static readonly Regex OptionWord = new(@"^[A-Za-z]{1,8}$", RegexOptions.CultureInvariant);
    // 선택지를 고르라는 질문 — 이런 질문에서만 선택지 단어를 기억한다 ("Enter OTP:"·"Password:" 의 글자 답은 기억하지 않음)
    private static readonly Regex OptionPrompt = new(@"\boption|\bchoose|\bchoice|\bselect|\bmethod|\[\d\]|\(\d+\s*-\s*\d+\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PasswordOnlyPrompt = new(@"password|passwd|passwort|kennwort|비밀\s*번호|암호|パスワード|密码|密碼", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// 이 질문의 답을 기억하거나, 기억한 답을 써도 되는가. CanUseRemembered 에 더해
    /// 6자리 이상 숫자(OTP 코드)는 기억하지 않고, OTP·토큰·코드·비밀번호를 묻는 질문은 짧은 선택지 번호("1" = Push 등)와
    /// 선택지를 고르라는 질문의 짧은 선택지 단어("push"·"sms" 등, 비밀번호 질문 제외)만 기억한다.
    /// </summary>
    public static bool CanRemember(string prompt, bool echoed, string answer)
    {
        if (!CanUseRemembered(echoed, answer)) return false;
        if (answer.Length == 0) return true;   // "Press enter to continue"
        if (LongDigits.IsMatch(answer)) return false;
        if (!SecretPrompt.IsMatch(prompt)) return true;
        var a = answer.Trim();
        return ShortOption.IsMatch(a) ||
               (OptionWord.IsMatch(a) && OptionPrompt.IsMatch(prompt) && !PasswordOnlyPrompt.IsMatch(prompt));
    }

    private static readonly Regex NewWord = new(@"\bnew\b|새\s*(비밀|암호)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ConfirmWord = new(@"re-?enter|re-?type|again|confirm|repeat|verify|다시|확인", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>새 비밀번호를 묻는 질문인가 ("New password:", "새 비밀번호:") — 확인 질문은 제외</summary>
    public static bool IsNewPasswordPrompt(string request) =>
        PasswordWord.IsMatch(request) && NewWord.IsMatch(request) && !ConfirmWord.IsMatch(request);

    /// <summary>새 비밀번호 확인 질문인가 ("Retype new password:", "새 비밀번호 확인:")</summary>
    public static bool IsConfirmPasswordPrompt(string request) =>
        PasswordWord.IsMatch(request) && ConfirmWord.IsMatch(request);

    /// <summary>
    /// 사용자가 답한 질문들로 keyboard-interactive 비밀번호 변경을 추적한다. 새 비밀번호와 확인 값이 같으면 그 값, 아니면 null.
    /// 질문이 한 번에 오든 여러 번에 나뉘어 오든(State 에 이어 둠) 동작한다.
    /// </summary>
    public static string? TrackNewPassword(State state, IReadOnlyList<(string Prompt, string Answer)> answered)
    {
        string? confirmed = null;
        foreach (var (prompt, answer) in answered)
        {
            if (IsConfirmPasswordPrompt(prompt))
            {
                if (state.PendingNewPassword is { Length: > 0 } pending && answer == pending) confirmed = answer;
            }
            else if (IsNewPasswordPrompt(prompt)) state.PendingNewPassword = answer;
        }
        return confirmed;
    }

    /// <summary>로그인 비밀번호를 묻는 질문인가 ("Password:", "(current) UNIX password:", "비밀번호:")</summary>
    public static bool IsPasswordPrompt(string request) =>
        PasswordWord.IsMatch(request) && !NotLoginPassword.IsMatch(request);

    /// <summary>
    /// 질문별 자동 응답. 비밀번호 질문 하나에만, 연결당 한 번만 저장된 비밀번호를 넣는다. null 이면 사용자에게 물어야 하는 질문.
    /// </summary>
    public static string?[] AutoFill(IReadOnlyList<string> requests, string password, State state)
    {
        var result = new string?[requests.Count];
        if (string.IsNullOrEmpty(password) || state.PasswordUsed) return result;
        for (var i = 0; i < requests.Count; i++)
        {
            if (!IsPasswordPrompt(requests[i])) continue;
            result[i] = password;
            state.PasswordUsed = true;
            break;
        }
        return result;
    }

    /// <summary>비대화형 연결에서 답할 수 없는 질문을 받았을 때의 오류 (Claude 가 읽음)</summary>
    public static string NonInteractiveError(string request, bool passwordUsed)
    {
        var q = request.Trim();
        if (passwordUsed && IsPasswordPrompt(q))
            return $"서버가 비밀번호를 다시 요구했습니다 (\"{q}\") — 저장된 비밀번호가 틀렸을 수 있습니다. 계정 잠금을 막기 위해 다시 보내지 않았습니다.";
        return $"서버가 추가 인증 입력을 요구합니다 (\"{q}\") — OTP 등은 비대화형 연결(run_command·SFTP 도구)에서 답할 수 없습니다. 앱에서 직접 접속하세요.";
    }

    /// <summary>
    /// 인증 실패 메시지("Permission denied (publickey,password).")에서 서버가 허용하는 방식을 읽어
    /// 사용자에게 보일 원인 안내를 만든다. 알 수 없으면 null.
    /// </summary>
    public static string? FailureHint(string message, bool hasKey, bool hasPassword)
    {
        var m = Regex.Match(message, @"\(([a-z0-9@.,\-]+)\)\s*\.?\s*$", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var allowed = m.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant()).ToHashSet();
        var pubkey = allowed.Contains("publickey");
        var pass = allowed.Contains("password") || allowed.Contains("keyboard-interactive");

        if (!pubkey && !pass)
            return L.F("서버가 이 앱이 지원하지 않는 인증 방식만 허용합니다 ({0}).", string.Join(", ", allowed));
        if (pubkey && !pass)
            return hasKey
                ? L.T("서버가 SSH 키 인증만 허용하는데 등록된 키를 받아들이지 않았습니다. 사용자명과 서버의 authorized_keys 를 확인하세요.")
                : L.T("서버가 SSH 키 인증만 허용합니다. Vault 에 SSH 키를 등록해 이 호스트에 연결하세요.");
        if (!hasPassword && !hasKey)
            return L.T("서버가 비밀번호 인증을 요구합니다. 비밀번호를 입력하세요.");
        return L.T("사용자명 또는 비밀번호가 틀렸거나 계정이 잠겼을 수 있습니다. 여러 번 실패하면 서버가 계정을 잠글 수 있으니 확인 후 다시 시도하세요.");
    }
}
