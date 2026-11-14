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
    public static JumpTunnel Open(HostEntry jump, string targetHost, int targetPort, AuthPrompter? prompter, Func<HostEntry, HostKeyEventArgs, string?> verifyHostKey)
    {
        if (string.IsNullOrWhiteSpace(jump.Username))
            throw new InvalidOperationException($"점프 서버 '{jump.DisplayName}' 에 저장된 사용자명이 없습니다 (호스트·Vault 에 계정을 저장하세요).");
        var client = new SshClient(SshAuth.Build(jump, jump.Username, jump.Password, prompter: prompter)) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
        string? keyError = null;
        client.HostKeyReceived += (_, e) => e.CanTrust = (keyError = verifyHostKey(jump, e)) == null;
        prompter?.Attach(client);
        try { client.Connect(); }
        catch (Exception ex)
        {
            client.Dispose();
            var reason = keyError ?? ex.Message;
            if (ex is SshAuthenticationException) throw new SshAuthenticationException($"점프 서버 '{jump.DisplayName}' 인증 실패 — {reason}");
            throw new InvalidOperationException($"점프 서버 '{jump.DisplayName}' ({jump.Address}:{jump.Port}) 연결 실패 — {reason}", ex);
        }
        finally { prompter?.Detach(client); }

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
