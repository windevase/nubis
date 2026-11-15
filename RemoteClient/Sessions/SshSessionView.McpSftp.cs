using System;
using Renci.SshNet;

namespace Nubis;

public sealed partial class SshSessionView
{
    /// <summary>
    /// MCP SFTP 도구용 (UI 스레드에서 호출): 이 탭의 로그인된 SSH 연결 위에 SFTP 채널을 여는 함수.
    /// 다시 인증하지 않으므로 OTP 서버에서도 묻지 않는다. 탭의 SFTP 패널이 sudo 로 다른 사용자가 돼 있으면(RunAs) 같은 권한으로 연다.
    /// 연결된 상태가 아니면 null. Open 은 백그라운드 스레드에서 부르고, 돌려받은 클라이언트는 부른 쪽이 Dispose 한다
    /// (터미널 세션은 닫지 않고 SFTP 채널만 닫힌다). 비밀번호는 함수 안에만 들어 있고 밖으로 내보내지 않는다
    /// </summary>
    internal (string User, string? RunAs, Func<SftpClient> Open)? McpSftpAccess()
    {
        if (SharedConnection is not { } conn) return null;
        var shell = conn.Client;
        var runAs = _sftp?.RunAs;
        if (runAs == null)
            return (conn.User, null, () => SharedSftpClient.TryOpen(shell)
                ?? throw new InvalidOperationException("탭의 SSH 연결을 공유할 수 없습니다 (연결이 끊겼거나 이 SSH.NET 버전이 지원하지 않음). session_id 없이 다시 시도하세요."));
        var login = _authPassword ?? "";
        var cached = _sftp!.CachedSudoPassword;
        // 비밀번호 입력 창은 띄우지 않는다 (MCP 경로) — 패널이 이미 확인한 비밀번호(또는 NOPASSWD)로만 연다
        return (conn.User, runAs, () => SudoSftp.Open(shell, runAs, login, cached, _ => null).Client);
    }
}
