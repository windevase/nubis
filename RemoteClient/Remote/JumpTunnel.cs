using System;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Nubis;

/// <summary>
/// 점프 서버(ProxyJump)를 거치는 터널. 점프 서버에 SSH 로 붙어 대상 서버로 가는 로컬 포트 포워딩(127.0.0.1:임의 포트)을 연다.
/// 대상 서버에는 그 로컬 포트로 접속하되, 호스트키는 대상 서버 주소 기준으로 확인한다 (호출하는 쪽).
/// 점프 서버도 Nubis 호스트라 Vault 계정·OTP 입력 창·에이전트·기본 키가 그대로 쓰인다.
/// </summary>
public sealed class JumpTunnel : IDisposable
{
    private readonly SshClient _client;
    private readonly ForwardedPortLocal _forward;

    public string LocalHost => "127.0.0.1";
    public int LocalPort => (int)_forward.BoundPort;
    /// <summary>점프 서버 이름 (안내·오류용)</summary>
    public string JumpName { get; }

    private JumpTunnel(SshClient client, ForwardedPortLocal forward, string name)
    {
        _client = client;
        _forward = forward;
        JumpName = name;
    }

    /// <param name="jump">점프 서버의 접속용 사본 (계정 채워진 것)</param>
    /// <param name="prompter">대화형이면 점프 서버의 인증 질문(OTP 등)을 묻는다. null 이면 비대화형</param>
    /// <param name="verifyHostKey">점프 서버 호스트키 확인 — 거부 사유, 신뢰하면 null</param>
    /// <exception cref="JumpTunnelException">점프 서버 계정 없음·호스트키 거부·인증 실패·연결 실패 (대상 서버의 실패와 구분)</exception>
    /// <exception cref="AuthPromptCancelledException">점프 서버의 인증 질문(OTP 등) 입력을 사용자가 취소함</exception>
    public static JumpTunnel Open(HostEntry jump, string targetHost, int targetPort, AuthPrompter? prompter, Func<HostEntry, HostKeyEventArgs, string?> verifyHostKey)
    {
        // 점프 서버로 지정한 호스트가 지워졌다 — 점프 없이 직접 연결하지 않는다
        if (jump.IsMissingJump)
            throw new JumpTunnelException(L.T("점프 서버로 지정한 호스트가 목록에 없습니다 (삭제됨). 호스트 편집에서 점프 서버를 다시 고르세요."), JumpFailure.Config);
        if (string.IsNullOrWhiteSpace(jump.Username))
            throw new JumpTunnelException(L.F("점프 서버 '{0}' 에 저장된 사용자명이 없습니다 (호스트·Vault 에 계정을 저장하세요).", jump.DisplayName), JumpFailure.Config);
        ConnectionInfo info;
        try { info = SshAuth.Build(jump, jump.Username, jump.Password, prompter: prompter); }
        catch (SshKeyLoadException ex)
        {
            // 점프 서버의 키를 읽지 못함 (키 암호 틀림 등) — 설정 문제라 다시 시도하지 않는다
            throw new JumpTunnelException(L.F("점프 서버 '{0}' — {1}", jump.DisplayName, ex.Message), JumpFailure.Config, ex);
        }
        var client = new SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
        string? keyError = null;
        var keyReceived = false;
        client.HostKeyReceived += (_, e) =>
        {
            keyReceived = true;
            e.CanTrust = (keyError = verifyHostKey(jump, e)) == null;
        };
        prompter?.Attach(client);
        try { client.Connect(); }
        catch (Exception ex)
        {
            // Detach 는 Dispose 전에 — 닫힌 클라이언트의 ConnectionInfo 에 접근하면 ObjectDisposedException 이 나서 진짜 사유를 덮는다
            prompter?.Detach(client);
            client.Dispose();
            // 사용자가 입력 창을 취소함 — 실패가 아니다 (호출하는 쪽이 "취소" 로 처리)
            if (ex is AuthPromptCancelledException || prompter?.Cancelled == true)
                throw ex as AuthPromptCancelledException ?? new AuthPromptCancelledException(ex.Message);
            var reason = keyError ?? ex.Message;
            // 비밀번호·OTP 답을 보낸 뒤의 끊김·시간 초과(OTP·Push 대기 초과, MaxAuthTries 등)는 네트워크 실패가 아니다 —
            // 다시 시도하면 점프 서버에 비밀번호·새 Push 가 또 나가 계정이 잠길 수 있다.
            // 키를 신뢰했지만 인증을 시작하기 전(호스트키 확인 창을 오래 띄워 키 교환 시간 초과, 공개키만 시도 중 끊김)은 네트워크 실패로 본다.
            // 비대화형(prompter 없음)은 인증 시작 여부를 알 수 없으므로 호스트키를 받은 뒤면 인증 단계로 본다 (예전 방식)
            var authStarted = prompter != null ? prompter.AuthStarted : keyReceived;
            var kind = SshFailure.JumpKind(ex, keyError != null, authStarted);
            throw kind switch
            {
                JumpFailure.HostKey => new JumpTunnelException(L.F("점프 서버 '{0}' ({1}) 호스트키 확인 실패 — {2}", jump.DisplayName, $"{jump.Address}:{jump.Port}", reason), kind, ex),
                JumpFailure.Auth => new JumpTunnelException(L.F("점프 서버 '{0}' 인증 실패 — {1}", jump.DisplayName, reason), kind, ex),
                JumpFailure.AuthPhase => new JumpTunnelException(L.F("점프 서버 '{0}' 인증 중 연결이 끊기거나 시간이 초과되었습니다 — {1}", jump.DisplayName, reason), kind, ex),
                _ => new JumpTunnelException(L.F("점프 서버 '{0}' ({1}) 연결 실패 — {2}", jump.DisplayName, $"{jump.Address}:{jump.Port}", reason), kind, ex),
            };
        }
        prompter?.Detach(client);

        try
        {
            var forward = new ForwardedPortLocal("127.0.0.1", 0, targetHost, (uint)targetPort);
            client.AddForwardedPort(forward);
            forward.Start();
            return new JumpTunnel(client, forward, jump.DisplayName);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public bool IsAlive => _client.IsConnected && _forward.IsStarted;

    public void Dispose()
    {
        try { _forward.Stop(); } catch { }
        try { _client.Dispose(); } catch { }
    }
}

/// <summary>점프 서버 단계의 실패 종류</summary>
public enum JumpFailure
{
    /// <summary>점프 서버에 계정이 없음 등 설정 문제</summary>
    Config,
    /// <summary>점프 서버 호스트키가 저장값과 다르거나 거부됨</summary>
    HostKey,
    /// <summary>점프 서버 인증 실패</summary>
    Auth,
    /// <summary>점프 서버에 연결하지 못함 (네트워크, 인증을 시작하기 전 — 호스트키 확인 창 대기 중 키 교환 시간 초과 포함)</summary>
    Network,
    /// <summary>인증을 시작한 뒤(비밀번호·OTP 답을 보냄) 끊김·시간 초과 — 다시 시도하면 비밀번호·Push 가 또 나가므로 자동으로 다시 시도하지 않는다</summary>
    AuthPhase,
}

/// <summary>
/// 점프 서버 단계에서 실패 — 대상 서버의 인증 실패로 오해하지 않게(대상 계정을 다시 묻지 않게) 따로 구분한다.
/// 기존 호출하는 쪽(InvalidOperationException 을 잡는 SFTP 도구 등)이 그대로 동작하도록 InvalidOperationException 을 상속.
/// </summary>
public sealed class JumpTunnelException(string message, JumpFailure kind, Exception? inner = null) : InvalidOperationException(message, inner)
{
    public JumpFailure Kind { get; } = kind;
    public bool IsAuth => Kind == JumpFailure.Auth;
    /// <summary>다시 시도해도 같은 결과이거나 다시 시도하면 위험한 실패 (자동 재연결하지 않음): 계정 없음·호스트키·인증·인증 단계 끊김</summary>
    public bool IsPermanent => Kind != JumpFailure.Network;
}
