using System;
using System.Reflection;
using Renci.SshNet;

namespace Nubis;

/// <summary>
/// 터미널의 SSH 연결 위에 SFTP 채널만 하나 더 여는 클라이언트 (OpenSSH ControlMaster 와 같은 방식).
/// 인증을 다시 하지 않으므로 OTP 를 요구하는 서버에서도 SFTP 를 열 때 다시 묻지 않는다.
///
/// SSH.NET 은 세션 공유 API 를 공개하지 않아, BaseClient 의 internal 속성 Session 에 터미널 세션을 리플렉션으로 넣는다.
/// 그러면 Connect 가 새 연결을 만들지 않고 기존 세션에서 SFTP 서브시스템만 연다.
/// 이 클라이언트를 닫을 때는 SFTP 채널만 닫고 세션은 놓아준다 — 세션은 터미널의 것이다.
/// SSH.NET 이 바뀌어 속성을 찾지 못하면 <see cref="Supported"/> 가 false 이고 호출하는 쪽은 별도 연결로 넘어간다.
/// </summary>
public sealed class SharedSftpClient : SftpClient
{
    private static readonly PropertyInfo? SessionProperty =
        typeof(BaseClient).GetProperty("Session", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    /// <summary>세션 공유가 가능한 SSH.NET 버전인가 (업데이트로 내부 구조가 바뀌었는지 테스트가 확인)</summary>
    public static bool Supported => SessionProperty is { GetMethod: not null, SetMethod: not null };

    private SharedSftpClient(ConnectionInfo info) : base(info) { }

    /// <summary>서버 명령(sudo sftp-server 등)으로 SFTP 를 열지 못함. ServerError 는 명령이 표준 오류로 남긴 글</summary>
    public sealed class ServerCommandException(string message, string? serverError, Exception inner) : Exception(message, inner)
    {
        public string? ServerError { get; } = serverError;
    }

    /// <summary>
    /// 터미널 연결에서 SFTP 채널을 연다. 공유할 수 없으면(지원 안 됨·터미널 연결 끊김) null.
    /// 채널 열기가 실패하면(서버의 세션 수 제한 등) 예외 — 터미널 연결은 그대로 둔다.
    /// </summary>
    /// <param name="serverCommand">"sftp" 서브시스템 대신 실행할 명령 (SSH.NET 포크의 SftpServerCommand, 예: sudo sftp-server)</param>
    /// <param name="preamble">명령이 시작된 뒤 SFTP 보다 먼저 보낼 바이트 (sudo -S 비밀번호 줄)</param>
    public static SharedSftpClient? TryOpen(SshClient? shell, string? serverCommand = null, byte[]? preamble = null)
    {
        if (!Supported || shell is not { IsConnected: true }) return null;
        var session = SessionProperty!.GetValue(shell);
        if (session == null) return null;
        // ConnectionInfo 는 터미널 것을 그대로 쓴다 (공개 생성자는 소유하지 않으므로 닫을 때 해제하지 않음)
        var client = new SharedSftpClient(shell.ConnectionInfo) { SftpServerCommand = serverCommand, SftpServerPreamble = preamble };
        SessionProperty.SetValue(client, session);
        try
        {
            client.Connect();
            return client;
        }
        catch (Exception ex)
        {
            var serverError = client.SftpServerError;
            client.Release();
            client.Dispose();
            if (serverCommand != null) throw new ServerCommandException(ex.Message, serverError, ex);
            throw;
        }
    }

    protected override void OnConnected()
    {
        try { base.OnConnected(); }
        catch
        {
            // SFTP 채널을 못 열면 BaseClient.Connect 가 세션을 해제하려 한다 — 터미널 세션이므로 먼저 놓아준다
            Release();
            throw;
        }
    }

    protected override void OnDisconnecting()
    {
        // SFTP 채널을 닫은 뒤 세션을 놓아준다 → 이어지는 BaseClient 의 세션 해제가 아무것도 하지 않음 (Dispose 도 이 경로)
        try { base.OnDisconnecting(); }
        finally { Release(); }
    }

    private void Release() => SessionProperty?.SetValue(this, null);
}
