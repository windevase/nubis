namespace Nubis;

public partial class SftpPanel
{
    /// <summary>
    /// MCP SFTP 도구가 이 탭의 sudo 전환(RunAs)을 따라 열 때 다시 쓸 확인된 sudo 비밀번호 (UI 스레드에서만 읽는다).
    /// 앱 안에서 SudoSftp.Open 에만 넘기고, MCP 결과·감사 로그에는 절대 넣지 않는다
    /// </summary>
    internal string? CachedSudoPassword => _sudoPassword;
}
