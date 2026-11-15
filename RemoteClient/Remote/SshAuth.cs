using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Security;

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
    /// 사용자가 입력 창에서 서버의 질문(OTP·인증 코드 등)에 직접 답했음 — 이 뒤의 인증 실패는 비밀번호보다 입력한 값(OTP 만료·오타)일 가능성이 크다.
    /// KbdAuth.FailureHint 의 answeredPrompt 로 넘겨 "비밀번호가 틀렸다" 로만 안내하지 않게 한다.
    /// </summary>
    public bool AnsweredByUser { get; internal set; }

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

/// <summary>
/// ssh-agent 가 서명하지 못함 (하드웨어 키(FIDO) 터치 대기 초과·확인 거부·에이전트 응답 없음).
/// 에이전트의 TimeoutException 이 그대로 나가면 네트워크 실패로 분류돼 공인 IP 재시도·자동 재연결로 터치 요청이 반복된다 —
/// 인증 실패(SshAuthenticationException)로 바꿔 다시 시도하지 않게 한다.
/// </summary>
public sealed class SshAgentSignException(string message, Exception inner) : SshAuthenticationException(message, inner);

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
        // ssh-rsa(SHA-1) 서명을 낼지는 서버 배너(ServerVersion)를 보고 정한다 — 배너는 인증 전에 받으므로 키 목록을 인증할 때 고른다
        ConnectionInfo? ciRef = null;
        string? ServerVersion() => ciRef?.ServerVersion;
        if (LoadKey(host, password) is { } key)
        {
            // 지정한 키: SHA-2 서명 먼저, ssh-rsa(SHA-1)는 옛 서버용으로 맨 뒤 (SSH.NET 기본 순서는 ssh-rsa 가 처음).
            // rsa-sha2 를 모르는 옛 OpenSSH(7.2 미만)면 ssh-rsa 를 먼저 (모르는 알고리즘 시도로 실패 횟수를 쓰지 않게)
            methods.Add(new PrivateKeyAuthenticationMethod(user, new AlgorithmSource(key.HostKeyAlgorithms.ToList(), specifiedKey: true, ServerVersion)));
        }
        else
        {
            // 키를 지정하지 않았으면 OpenSSH 처럼 ssh-agent 의 키 → ~/.ssh 기본 키 순으로.
            // 같은 키(에이전트에 올린 id_rsa 등)를 두 번 내밀지 않고, rsa-sha2 를 아는 OpenSSH(7.2+)에는 ssh-rsa(SHA-1)를 내밀지 않아 공개키 시도 횟수를 줄인다 —
            // 서버의 MaxAuthTries(기본 6)를 공개키 시도로 다 써 버리면 비밀번호·OTP 까지 가지 못하고 끊긴다.
            // 옛 OpenSSH·다른 SSH 서버(네트워크 장비 등)는 ssh-rsa 만 받을 수 있어 남겨 둔다 (2차 점검에서 빼 버려 기본 id_rsa 로 붙던 옛 서버가 안 되던 문제)
            var algs = new List<HostAlgorithm>();
            if (SshAgent.KeySource() is { } agent) algs.AddRange(agent.HostKeyAlgorithms);
            foreach (var k in LoadDefaultKeys()) algs.AddRange(k.HostKeyAlgorithms);
            if (PickAlgorithms(algs, Sha1Rsa.Last).Count > 0)
                methods.Add(new PrivateKeyAuthenticationMethod(user, new AlgorithmSource(algs, specifiedKey: false, ServerVersion)));
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
        ciRef = ci;
        if (prompter?.Banner is { } banner)
            ci.AuthenticationBanner += (_, e) => { if (!string.IsNullOrWhiteSpace(e.BannerMessage)) banner(e.BannerMessage); };
        return ci;
    }

    /// <summary>
    /// 공개키 인증에 내밀 서명 알고리즘 목록 — 읽을 때(= SSH.NET 이 인증할 때, 서버 배너를 받은 뒤) 서버에 맞춰 PickAlgorithms 로 정리한다.
    /// 배너를 아직 모르면 ssh-rsa 를 맨 뒤에 둔다 (옛 서버 호환)
    /// </summary>
    private sealed class AlgorithmSource(IReadOnlyList<HostAlgorithm> algs, bool specifiedKey, Func<string?> serverVersion) : IPrivateKeySource
    {
        public IReadOnlyCollection<HostAlgorithm> HostKeyAlgorithms => PickAlgorithms(algs, Sha1RsaFor(serverVersion(), specifiedKey));
    }

    /// <summary>ssh-rsa(SHA-1) 서명을 어떻게 낼지</summary>
    public enum Sha1Rsa
    {
        /// <summary>내밀지 않음 (rsa-sha2 를 아는 서버)</summary>
        Drop,
        /// <summary>SHA-2 서명을 다 시도한 뒤 맨 뒤에 (서버를 모를 때)</summary>
        Last,
        /// <summary>SHA-2 보다 먼저 (rsa-sha2 를 모르는 옛 서버)</summary>
        First,
    }

    private static readonly Regex OpenSshVersion = new(@"OpenSSH_(?:for_Windows_)?(\d+)\.(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// 서버 배너(예: "SSH-2.0-OpenSSH_9.6p1", "SSH-2.0-OpenSSH_for_Windows_9.5")로 ssh-rsa 정책을 정한다.
    /// OpenSSH 7.2 이상은 rsa-sha2-256/512 공개키 인증을 받으므로 ssh-rsa 가 필요 없고(8.8 부터는 기본으로 거부), 7.2 미만은 ssh-rsa 만 안다.
    /// 다른 SSH 서버(dropbear·네트워크 장비 등)·배너를 모를 때는 SHA-2 를 먼저 시도하고 ssh-rsa 를 맨 뒤에.
    /// 지정한 키(specifiedKey)는 rsa-sha2 를 아는 서버에도 맨 뒤에 남긴다 (사용자가 고른 키라 시도 하나를 더 써도 된다 — 기존 동작)
    /// </summary>
    public static Sha1Rsa Sha1RsaFor(string? serverVersion, bool specifiedKey)
    {
        var m = serverVersion == null ? Match.Empty : OpenSshVersion.Match(serverVersion);
        if (!m.Success) return Sha1Rsa.Last;
        var major = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var minor = int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        var knowsSha2 = major > 7 || (major == 7 && minor >= 2);
        return !knowsSha2 ? Sha1Rsa.First : specifiedKey ? Sha1Rsa.Last : Sha1Rsa.Drop;
    }

    /// <summary>예전 호출 형태: allowSha1Rsa 면 ssh-rsa 를 맨 뒤로, 아니면 뺀다</summary>
    public static List<HostAlgorithm> PickAlgorithms(IEnumerable<HostAlgorithm> algs, bool allowSha1Rsa) =>
        PickAlgorithms(algs, allowSha1Rsa ? Sha1Rsa.Last : Sha1Rsa.Drop);

    /// <summary>
    /// 공개키 인증에서 시도할 (서명 알고리즘, 공개키) 목록 정리 — 순서 유지, 같은 알고리즘·같은 공개키 blob 은 한 번만
    /// (ssh-agent 와 ~/.ssh 에 같은 키가 있을 때). ssh-rsa·ssh-rsa 인증서(SHA-1 서명)는 policy 에 따라 빼거나 맨 뒤·맨 앞으로.
    /// SSH.NET 은 알고리즘마다 서버에 한 번씩 물어 실패 횟수가 쌓이므로(MaxAuthTries) 중복을 줄여야 비밀번호까지 간다.
    /// </summary>
    public static List<HostAlgorithm> PickAlgorithms(IEnumerable<HostAlgorithm> algs, Sha1Rsa policy)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<HostAlgorithm>();
        var sha1 = new List<HostAlgorithm>();
        foreach (var a in algs)
        {
            byte[] data;
            try { data = a.Data; }
            catch { continue; }
            if (!seen.Add(a.Name + " " + Convert.ToBase64String(data))) continue;
            if (a.Name is "ssh-rsa" or "ssh-rsa-cert-v01@openssh.com")
            {
                if (policy != Sha1Rsa.Drop) sha1.Add(a);
                continue;
            }
            result.Add(a);
        }
        if (policy == Sha1Rsa.First) result.InsertRange(0, sha1);
        else result.AddRange(sha1);
        return result;
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
        prompter.AnsweredByUser = true;
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
            instruction = a.Length >= 2 && a[0].Length == 0
                ? L.T("새 비밀번호를 입력하세요.")
                : L.T("두 비밀번호가 같지 않습니다. 다시 입력하세요.");
        }
        // 입력이 세 번 맞지 않음 — 서버가 비밀번호를 거부한 것이 아니므로 인증 실패(계정 다시 묻기)가 아니라 취소로 처리한다
        prompter.Cancelled = true;
        throw new AuthPromptCancelledException(L.T("새 비밀번호가 확인 값과 세 번 일치하지 않아 비밀번호 변경을 취소했습니다."));
    }

    /// <summary>
    /// 개인키: Vault 키 내용이 있으면 그것, 없으면 키 파일. 키에 암호가 걸려 있으면
    /// Vault 에 저장한 키 암호 → (없으면) 비밀번호 순으로 쓴다.
    /// </summary>
    private static PrivateKeyFile? LoadKey(HostEntry host, string password)
    {
        // 키 암호가 틀림·파일 없음 등은 연결 실패가 아니다 — 구분해서 자동 재연결하지 않게
        try { return LoadKeyCore(host, password); }
        catch (SshPassPhraseNullOrEmptyException ex)
        {
            throw new SshKeyLoadException(L.T("SSH 키에 암호가 걸려 있는데 키 암호(또는 비밀번호)가 저장되어 있지 않습니다."), ex);
        }
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
            return OpenKey(bytes, !string.IsNullOrEmpty(host.KeyPassphrase) ? host.KeyPassphrase : password);
        }
        if (string.IsNullOrWhiteSpace(host.KeyPath)) return null;
        return OpenKey(System.IO.File.ReadAllBytes(host.KeyPath), password);
    }

    /// <summary>
    /// 키 열기: 먼저 암호 없이, 암호가 걸렸으면 pass 로. SSH.NET 은 OpenSSH·PKCS#1·PuTTY 형식에서만 "암호 필요"
    /// (SshPassPhraseNullOrEmptyException)를 알려 주고, 암호 걸린 PKCS#8("BEGIN ENCRYPTED PRIVATE KEY")은 다른 예외를 내서
    /// Vault 에 키 암호까지 저장한 PKCS#8 키가 "키를 읽지 못했습니다" 로 실패하던 문제 — 그 형식은 처음부터 암호로 연다.
    /// </summary>
    private static PrivateKeyFile OpenKey(byte[] bytes, string? pass)
    {
        var encryptedPkcs8 = System.Text.Encoding.ASCII.GetString(bytes).Contains("-----BEGIN ENCRYPTED PRIVATE KEY-----", StringComparison.Ordinal);
        if (encryptedPkcs8)
        {
            if (string.IsNullOrEmpty(pass)) throw new SshPassPhraseNullOrEmptyException("Private key is encrypted but passphrase is empty.");
            return new PrivateKeyFile(new System.IO.MemoryStream(bytes), pass);
        }
        try { return new PrivateKeyFile(new System.IO.MemoryStream(bytes)); }
        catch (SshPassPhraseNullOrEmptyException) when (!string.IsNullOrEmpty(pass))
        {
            return new PrivateKeyFile(new System.IO.MemoryStream(bytes), pass);
        }
    }

    /// <summary>기본 키 폴더 (null 이면 %USERPROFILE%\.ssh). 테스트가 빈 폴더로 바꾼다</summary>
    public static string? DefaultKeyDir { get; set; }

    /// <summary>OpenSSH 가 키를 지정하지 않았을 때 시도하는 기본 키 (이 순서)</summary>
    public static readonly string[] DefaultKeyNames = ["id_ed25519", "id_ecdsa", "id_rsa"];

    /// <summary>기본 키 파일이나 ssh-agent 키가 하나라도 있는가 (비밀번호가 없어도 먼저 키로 시도해 볼지)</summary>
    public static bool HasDefaultKeyFiles()
    {
        var dir = DefaultKeyDir ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        if (DefaultKeyNames.Any(n => System.IO.File.Exists(System.IO.Path.Combine(dir, n)))) return true;
        // UI 스레드에서도 불린다 — 에이전트 파이프가 없으면 묻지 않고, 있어도 짧게만 기다린다 (잠긴·확인 대기 에이전트에 멈추지 않게)
        return SshAgent.PipeExists() && SshAgent.ListKeys(SshAgent.ListTimeoutMs).Count > 0;
    }

    /// <summary>
    /// 호스트·Vault 에 키가 없을 때 OpenSSH 처럼 ~/.ssh 의 기본 키를 시도한다 (ssh host 는 되는데 Nubis 는 비밀번호를 묻는 차이 방지).
    /// 암호가 걸린 키·읽을 수 없는 키는 건너뛴다 (묻지 않음 — 쓰려면 호스트나 Vault 에 키와 암호를 지정).
    /// </summary>
    public static IPrivateKeySource[] LoadDefaultKeys() => LoadDefaultKeys(null);

    /// <param name="encrypted">암호가 걸려 건너뛴 기본 키 파일 이름(id_ed25519 등)을 담는다 — 실패 안내용</param>
    public static IPrivateKeySource[] LoadDefaultKeys(ICollection<string>? encrypted)
    {
        var dir = DefaultKeyDir ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        var keys = new List<IPrivateKeySource>();
        foreach (var name in DefaultKeyNames)
        {
            var path = System.IO.Path.Combine(dir, name);
            if (!System.IO.File.Exists(path)) continue;
            try
            {
                var bytes = System.IO.File.ReadAllBytes(path);
                // 암호 걸린 PKCS#8 은 SSH.NET 이 "암호 필요" 예외를 내지 않으므로 먼저 본다 (OpenKey 와 같은 판단)
                if (System.Text.Encoding.ASCII.GetString(bytes).Contains("-----BEGIN ENCRYPTED PRIVATE KEY-----", StringComparison.Ordinal))
                {
                    encrypted?.Add(name);
                    continue;
                }
                keys.Add(new PrivateKeyFile(new System.IO.MemoryStream(bytes)));
            }
            catch (SshPassPhraseNullOrEmptyException) { encrypted?.Add(name); }
            catch { /* 읽을 수 없거나 지원하지 않는 형식 */ }
        }
        return keys.ToArray();
    }

    /// <summary>
    /// 암호가 걸려 자동으로 시도하지 못한 기본 키(~/.ssh/id_*) 중 ssh-agent 에도 올라가 있지 않은 것의 이름.
    /// 키 인증이 실패했을 때 "기본 키가 있는데 왜 안 쓰나" 를 안내하는 데 쓴다 (SkippedKeysHint). 에이전트 확인은 짧게만 기다린다.
    /// </summary>
    public static List<string> SkippedEncryptedDefaultKeys()
    {
        var names = new List<string>();
        LoadDefaultKeys(names);
        if (names.Count == 0) return names;
        var dir = DefaultKeyDir ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        var agentBlobs = SshAgent.PipeExists()
            ? SshAgent.ListKeys(SshAgent.ListTimeoutMs).Select(k => Convert.ToBase64String(k.Blob)).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        if (agentBlobs.Count == 0) return names;
        // 같은 이름의 .pub 공개키가 에이전트에 있으면 에이전트로 이미 시도한 키 — 안내에서 뺀다
        return names.Where(n =>
        {
            try
            {
                var parts = System.IO.File.ReadAllText(System.IO.Path.Combine(dir, n + ".pub")).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                return !(parts.Length >= 2 && agentBlobs.Contains(parts[1]));
            }
            catch { return true; }   // .pub 가 없거나 읽지 못함 — 알 수 없으니 안내한다
        }).ToList();
    }

    /// <summary>건너뛴 암호 걸린 기본 키 안내 (없으면 null)</summary>
    public static string? SkippedKeysHint(IReadOnlyCollection<string> names) =>
        names.Count == 0 ? null
        : L.F("~/.ssh 의 {0} 은(는) 암호가 걸려 있어 자동으로 시도하지 않았습니다. 쓰려면 ssh-agent 에 올리거나(ssh-add) 호스트·Vault 에 키와 키 암호를 등록하세요.",
            string.Join(", ", names));

    public static string HostKeyId(HostEntry h) => $"{h.Address}:{h.Port}";
    /// <summary>
    /// 저장·비교용 호스트키 지문 "키형식 SHA256:...". 형식은 협상된 서명 알고리즘(HostKeyName = rsa-sha2-512 등)이 아니라 키 형식(ssh-rsa)으로 —
    /// 같은 RSA 키라도 서버·SSH.NET 버전에 따라 rsa-sha2-512 / rsa-sha2-256 / ssh-rsa 로 달라져 "키가 바뀌었다" 로 보이던 문제.
    /// </summary>
    public static string Fingerprint(HostKeyEventArgs e) => $"{KnownHostStore.KeyType(e.HostKeyName)} SHA256:{e.FingerPrintSHA256}";

    /// <summary>같은 서버의 다른 주소에 저장된 호스트키와 비교한 결과</summary>
    public enum SiblingVerdict
    {
        /// <summary>다른 주소에 저장된 같은 형식의 키가 없음</summary>
        Unknown,
        /// <summary>다른 주소에 같은 키가 신뢰돼 있음</summary>
        Match,
        /// <summary>다른 주소에 같은 형식의 다른 키가 신뢰돼 있음 (중간자 공격일 수 있음)</summary>
        Mismatch,
        /// <summary>사용자가 다른 주소에서 이 키를 거부함</summary>
        Rejected,
    }

    /// <summary>
    /// 호스트의 다른 주소(기본 주소·사설 IP·공인 IP)에 저장된 호스트키와 비교한다. 호스트키는 "주소:포트" 별로 저장되므로
    /// 사설 IP 로 신뢰한 서버에 공인 IP 로 처음 접속하면(VPN 이 꺼져 공인 IP 재시도 등) 다른 키가 와도 처음 보는 서버로 자동 신뢰되던 문제를 막는다.
    /// 우선순위: Rejected > Mismatch > Match > Unknown. 형식이 다른 키(ed25519 와 rsa)는 비교하지 않는다.
    /// </summary>
    public static (SiblingVerdict Verdict, string? Id, string? Known) CheckSiblingKeys(HostEntry h, string fp)
    {
        var verdict = SiblingVerdict.Unknown;
        string? hitId = null, hitKnown = null;
        var addrs = new List<string>();
        foreach (var a in new[] { h.Address, h.PrivateIp, h.PublicIp })
            if (!string.IsNullOrWhiteSpace(a) && !string.Equals(a, h.Address, StringComparison.OrdinalIgnoreCase) &&
                !addrs.Contains(a, StringComparer.OrdinalIgnoreCase))
                addrs.Add(a);
        var type = FingerprintType(fp);
        foreach (var a in addrs)
        {
            var id = $"{a}:{h.Port}";
            var known = KnownHostStore.Get(id);
            if (known == null || known == KnownHostStore.UnavailableMark) continue;
            SiblingVerdict v;
            if (KnownHostStore.IsRejected(known))
            {
                if (known != KnownHostStore.RejectedMark(fp)) continue;   // 다른 키를 거부한 기록은 신뢰한 키가 아니다
                v = SiblingVerdict.Rejected;
            }
            else if (FingerprintType(known) != type) continue;
            else v = known == fp ? SiblingVerdict.Match : SiblingVerdict.Mismatch;
            if (v > verdict) (verdict, hitId, hitKnown) = (v, id, known);
        }
        return (verdict, hitId, hitKnown);
    }

    /// <summary>지문 "형식 SHA256:..." 의 형식 부분</summary>
    private static string FingerprintType(string fp)
    {
        var sp = fp.IndexOf(' ');
        return sp > 0 ? fp[..sp] : "";
    }

    /// <summary>
    /// 비대화형 연결용 호스트키 확인 (ssh 의 accept-new 와 같음): 처음 보는 서버는 저장 후 신뢰, 저장값과 다르면 거부.
    /// 처음 보는 주소라도 같은 서버의 다른 주소에 저장된 키와 다르면(또는 거기서 거부한 키면) 거부한다.
    /// 거부 사유를 반환하고, 신뢰하면 null. (SFTP 패널 화면에도 보이므로 번역한다)
    /// </summary>
    public static string? VerifyAcceptNew(HostEntry h, HostKeyEventArgs e)
    {
        var id = HostKeyId(h);
        var fp = Fingerprint(e);
        var known = KnownHostStore.Get(id);
        // OpenSSH known_hosts 참고: 폐기(@revoked)된 키는 Nubis 에 저장된 키와 같아도 항상 거부
        var openSsh = OpenSshKnownHosts.Check(h.Address, h.Port, e.HostKey);
        if (openSsh == OpenSshKnownHosts.Verdict.Revoked)
            return L.F("OpenSSH known_hosts 에 폐기(@revoked)된 호스트키입니다 ({0}).", fp);
        if (known == null)
        {
            // 처음 보는 서버: 같은 키면 신뢰, 다른 키면 거부 (비대화형이라 묻지 않는다)
            if (openSsh == OpenSshKnownHosts.Verdict.Mismatch)
                return L.F("호스트키가 OpenSSH known_hosts 에 저장된 값과 다릅니다 ({0}). 앱에서 직접 접속해 키를 확인·신뢰한 뒤 다시 시도하세요.", fp);
            var (sibling, siblingId, siblingKnown) = CheckSiblingKeys(h, fp);
            if (sibling == SiblingVerdict.Rejected)
                return L.F("같은 서버의 다른 주소({0})에서 사용자가 거부한 호스트키입니다. 앱에서 직접 접속해 키를 확인·신뢰한 뒤 다시 시도하세요.", siblingId!);
            if (sibling == SiblingVerdict.Mismatch)
                return L.F("호스트키가 같은 서버의 다른 주소({0})에 저장된 값과 다릅니다 ({1} → {2}). 중간자 공격일 수 있어 자동으로 신뢰하지 않았습니다. 앱에서 직접 접속해 키를 확인·신뢰한 뒤 다시 시도하세요.",
                    siblingId!, siblingKnown!, fp);
            KnownHostStore.Set(id, fp);
            return null;
        }
        if (KnownHostStore.IsRejected(known))
            return L.T("사용자가 이 서버의 호스트키를 거부한 적이 있습니다. 앱에서 직접 접속해 키를 확인·신뢰한 뒤 다시 시도하세요.");
        return known == fp ? null : L.F("호스트키가 저장된 값과 다릅니다 ({0} → {1}). Known Hosts 에서 확인 후 삭제하세요.", known, fp);
    }
}

/// <summary>
/// 흔한 SSH.NET·소켓 예외 → 사용자에게 보일 한 줄 설명 (화면 표시용, 번역됨). 호출하는 쪽이 "연결 실패: " 등을 앞에 붙인다.
/// 이 앱이 만든 예외(키 읽기·입력 취소·에이전트 서명)는 이미 설명 문구라 그대로. 모르는 예외는 원래 메시지 첫 줄.
/// </summary>
public static class SshErrors
{
    public static string Describe(Exception ex)
    {
        // AggregateException·TargetInvocationException 등 감싼 예외는 안쪽을 본다
        while (ex is AggregateException { InnerException: { } a }) ex = a;
        while (ex is System.Reflection.TargetInvocationException { InnerException: { } t }) ex = t;
        var first = FirstLine(ex.Message);
        switch (ex)
        {
            case SshKeyLoadException or AuthPromptCancelledException or SshAgentSignException:
                return first;
            case SshPassPhraseNullOrEmptyException:
                return L.T("SSH 키에 암호가 걸려 있는데 키 암호(또는 비밀번호)가 저장되어 있지 않습니다.");
            case SshAuthenticationException:
                return L.F("인증에 실패했습니다 ({0}).", first);
            case SftpPermissionDeniedException:
                return L.T("원격 경로에 권한이 없습니다.");
            case SftpPathNotFoundException:
                return L.T("원격 경로가 없습니다.");
            case SshOperationTimeoutException or TimeoutException:
                return L.T("서버 응답 시간이 초과되었습니다.");
            case SshConnectionException ce:
                return ce.DisconnectReason switch
                {
                    Renci.SshNet.Messages.Transport.DisconnectReason.KeyExchangeFailed =>
                        L.F("키 교환에 실패했습니다 — 호스트키를 신뢰하지 않았거나 서버와 맞는 암호화 방식이 없습니다 ({0}).", first),
                    Renci.SshNet.Messages.Transport.DisconnectReason.HostKeyNotVerifiable =>
                        L.T("호스트키를 확인할 수 없어 연결을 끊었습니다."),
                    Renci.SshNet.Messages.Transport.DisconnectReason.TooManyConnections =>
                        L.T("서버의 동시 접속 수가 가득 찼습니다. 잠시 뒤 다시 시도하세요."),
                    Renci.SshNet.Messages.Transport.DisconnectReason.ProtocolVersionNotSupported =>
                        L.T("SSH 서버가 아니거나 지원하지 않는 SSH 버전입니다."),
                    Renci.SshNet.Messages.Transport.DisconnectReason.ConnectionLost =>
                        L.T("서버와의 연결이 끊겼습니다."),
                    _ => ex.InnerException is System.Net.Sockets.SocketException se ? Socket(se) : L.F("서버가 연결을 끊었습니다 ({0}).", first),
                };
            case System.Net.Sockets.SocketException se:
                return Socket(se);
        }
        if (ex.InnerException is System.Net.Sockets.SocketException inner) return Socket(inner);
        return first;
    }

    private static string Socket(System.Net.Sockets.SocketException se) => se.SocketErrorCode switch
    {
        System.Net.Sockets.SocketError.ConnectionRefused => L.T("서버가 연결을 거부했습니다 (포트가 닫혔거나 SSH 서비스가 꺼져 있음)."),
        System.Net.Sockets.SocketError.TimedOut => L.T("연결 시간이 초과되었습니다 (주소·방화벽·보안 그룹·VPN 을 확인하세요)."),
        System.Net.Sockets.SocketError.HostNotFound or System.Net.Sockets.SocketError.NoData or System.Net.Sockets.SocketError.TryAgain =>
            L.T("서버 주소를 찾을 수 없습니다 (DNS 이름을 확인하세요)."),
        System.Net.Sockets.SocketError.NetworkUnreachable or System.Net.Sockets.SocketError.HostUnreachable or System.Net.Sockets.SocketError.NetworkDown =>
            L.T("서버에 닿을 수 없습니다 (네트워크·VPN·라우팅을 확인하세요)."),
        System.Net.Sockets.SocketError.ConnectionReset or System.Net.Sockets.SocketError.ConnectionAborted =>
            L.T("서버와의 연결이 끊겼습니다."),
        _ => FirstLine(se.Message),
    };

    private static string FirstLine(string s) => s.Split('\n')[0].TrimEnd('\r');
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
        return $"서버가 추가 인증 입력을 요구합니다 (\"{q}\") — OTP 등은 비대화형 연결(run_command·SFTP 도구)에서 답할 수 없습니다. 앱에서 이 서버의 SSH 탭을 열어(open_session) 인증을 마치면, run_command 는 그 탭의 연결로 다시 인증 없이 실행합니다.";
    }

    /// <summary>
    /// 인증 실패 메시지("Permission denied (publickey,password).")에서 서버가 허용하는 방식을 읽어
    /// 사용자에게 보일 원인 안내를 만든다. 알 수 없으면 null.
    /// </summary>
    /// <param name="answeredPrompt">사용자가 입력 창에서 서버 질문(OTP 등)에 답했음 (AuthPrompter.AnsweredByUser) — 비밀번호보다 입력값을 먼저 의심하게 안내</param>
    public static string? FailureHint(string message, bool hasKey, bool hasPassword, bool answeredPrompt = false)
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
        if (answeredPrompt && allowed.Contains("keyboard-interactive"))
            return L.T("서버가 입력한 인증 값(OTP·인증 코드 등)을 받아들이지 않았습니다. 코드가 만료되지 않았는지, 비밀번호가 맞는지 확인하세요. 여러 번 실패하면 계정이 잠길 수 있습니다.");
        if (!hasPassword && !hasKey)
            return L.T("서버가 비밀번호 인증을 요구합니다. 비밀번호를 입력하세요.");
        return L.T("사용자명 또는 비밀번호가 틀렸거나 계정이 잠겼을 수 있습니다. 여러 번 실패하면 서버가 계정을 잠글 수 있으니 확인 후 다시 시도하세요.");
    }
}
