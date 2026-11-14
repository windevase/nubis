using System;
using System.Linq;
using System.Text;
using Renci.SshNet;

namespace Nubis;

/// <summary>
/// 다른 사용자(보통 root) 권한의 SFTP — 터미널이 sudo su - 로 전환했을 때 SFTP 패널이 따라가는 데 쓴다.
/// 터미널의 SSH 연결 위에서 sudo 로 sftp-server 를 실행해 SFTP 를 연다 (SSH.NET 포크의 SftpServerCommand, 다시 인증하지 않음).
///  1) sudo -n 으로 비밀번호 없이 되는지 본다 (NOPASSWD).
///  2) 비밀번호가 필요하면 로그인 비밀번호 → (틀리면·없으면) 사용자 입력으로 sudo -k -S 를 확인한다.
///     확인은 입력을 닫은 채로 해서, 틀려도 1번만 실패한다 (계정 잠금·sudo 실패 누적 방지). 틀리면 다시 보내지 않는다.
///  3) 확인한 비밀번호를 sftp-server 앞에 한 줄 보내 연다. -k 로 sudo 캐시를 무시해 항상 비밀번호를 읽게 한다
///     (캐시가 있으면 sudo 가 묻지 않아 비밀번호 줄이 SFTP 데이터로 들어가 깨진다).
/// sudo 권한 없이 su - (root 비밀번호)로 전환한 경우는 지원하지 않는다 — su 는 비밀번호에 tty 가 필요해 SFTP 데이터가 깨진다.
/// </summary>
public static class SudoSftp
{
    /// <summary>sftp-server 위치 찾기: sshd_config 의 Subsystem 경로 → 배포판별 기본 경로</summary>
    public const string FindServerScript =
        "p=$(awk '$1==\"Subsystem\" && $2==\"sftp\" {print $3; exit}' /etc/ssh/sshd_config 2>/dev/null); " +
        "for c in \"$p\" /usr/lib/openssh/sftp-server /usr/libexec/openssh/sftp-server /usr/lib/ssh/sftp-server /usr/libexec/sftp-server /usr/lib/sftp-server; do " +
        "[ -n \"$c\" ] && [ -x \"$c\" ] && { echo \"$c\"; exit 0; }; done; exit 1";

    /// <summary>sh 작은따옴표 인용</summary>
    public static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>사용자 이름으로 쓸 수 있는 값인가 (명령에 들어가므로 엄격하게)</summary>
    public static bool IsValidUser(string user) =>
        user.Length is > 0 and <= 32 && System.Text.RegularExpressions.Regex.IsMatch(user, "^[a-z_][a-z0-9_.-]*\\$?$");

    // 확인도 실제로 쓸 명령(sftp-server)으로 한다 — sudoers 가 sftp-server 에만 NOPASSWD 를 준 경우 "true" 로 물으면
    // 비밀번호가 필요하다고 나와, 비밀번호 줄을 보냈다가 sftp-server 가 그 줄을 SFTP 데이터로 받아 연결이 깨진다.
    // sftp-server 는 입력이 EOF 면 바로 0 으로 끝난다 → 끝 코드 0 = 그 명령을 그 방식으로 실행할 수 있음
    public static string ProbeCommand(string user, string server) => $"sudo -n -u {Quote(user)} {Quote(server)} </dev/null";
    /// <summary>표준 입력: 비밀번호 한 줄 + EOF — 맞으면 sftp-server 가 EOF 를 읽고 0 으로 끝난다</summary>
    public static string CheckPasswordCommand(string user, string server) => $"sudo -k -S -p '' -u {Quote(user)} {Quote(server)}";
    public static string HomeCommand(string user) => $"getent passwd {Quote(user)} | cut -d: -f6";

    public static string ServerCommand(string server, string user, bool withPassword) =>
        withPassword ? $"sudo -k -S -p '' -u {Quote(user)} {Quote(server)}" : $"sudo -n -u {Quote(user)} {Quote(server)}";

    /// <summary>sudo -n 실패가 "비밀번호가 필요함" 때문인가 (그 외는 비밀번호로도 안 됨)</summary>
    public static bool NeedsPassword(string stderr) =>
        stderr.Contains("password is required", StringComparison.OrdinalIgnoreCase) ||
        stderr.Contains("비밀번호가 필요", StringComparison.Ordinal) || stderr.Contains("암호가 필요", StringComparison.Ordinal);

    /// <summary>sudo 비밀번호가 틀려서 실패했나 (그 외 사유는 다른 비밀번호로 다시 해도 소용없음)</summary>
    public static bool IsWrongPassword(string stderr) =>
        stderr.Contains("incorrect password", StringComparison.OrdinalIgnoreCase) ||
        stderr.Contains("try again", StringComparison.OrdinalIgnoreCase) ||
        stderr.Contains("no password was provided", StringComparison.OrdinalIgnoreCase);

    /// <summary>sudo 오류(stderr)를 사용자에게 보일 원인으로</summary>
    public static string Explain(string stderr)
    {
        var s = stderr.Trim();
        if (s.Contains("not in the sudoers", StringComparison.OrdinalIgnoreCase) || s.Contains("may not run sudo", StringComparison.OrdinalIgnoreCase))
            return L.T("이 계정은 sudo 권한이 없습니다. (su 로 전환한 경우 SFTP 는 따라갈 수 없습니다)");
        if (s.Contains("not allowed to execute", StringComparison.OrdinalIgnoreCase))
            return L.T("sudo 설정이 sftp-server 실행을 허용하지 않습니다.");
        if (s.Contains("must have a tty", StringComparison.OrdinalIgnoreCase) || s.Contains("requiretty", StringComparison.OrdinalIgnoreCase))
            return L.T("서버의 sudo 설정(requiretty)이 터미널 없는 실행을 막고 있습니다.");
        if (IsWrongPassword(s)) return L.T("sudo 비밀번호가 틀렸습니다.");
        if (s.Contains("unknown user", StringComparison.OrdinalIgnoreCase))
            return L.T("서버에 없는 사용자입니다.");
        var first = s.Split('\n')[0].Trim();
        return first.Length > 0 ? L.F("sudo 실패: {0}", first) : L.T("sudo 실패");
    }

    /// <summary>실패 사유 (화면에 보임)</summary>
    public sealed class SudoException(string message) : Exception(message);

    /// <summary>
    /// 사용자 user 권한의 SFTP 를 연다. 터미널 연결(shell) 위에서만 — 공유할 수 없으면 SudoException.
    /// askPassword: sudo 비밀번호 입력 (취소하면 null). 확인된 비밀번호는 cachedPassword 로 돌려준다(재연결용).
    /// </summary>
    public static (SftpClient Client, string Home, string? Password) Open(
        SshClient? shell, string user, string loginPassword, string? cachedPassword, Func<string, string?> askPassword)
    {
        if (!IsValidUser(user)) throw new SudoException(L.F("사용자 이름을 알 수 없습니다: {0}", user));
        if (!SharedSftpClient.Supported || shell is not { IsConnected: true })
            throw new SudoException(L.T("터미널 연결을 공유할 수 없어 다른 사용자 권한으로 열 수 없습니다."));

        var server = Run(shell, FindServerScript).Output.Trim();
        if (server.Length == 0) throw new SudoException(L.T("서버에서 sftp-server 를 찾지 못했습니다."));
        var home = Run(shell, HomeCommand(user)).Output.Trim();
        if (!home.StartsWith('/')) home = user == "root" ? "/root" : "/home/" + user;

        // 1) 비밀번호 없이
        var probe = Run(shell, ProbeCommand(user, server));
        if (probe.Exit == 0) return (OpenServer(shell, ServerCommand(server, user, false), null), home, null);
        if (!NeedsPassword(probe.Error)) throw new SudoException(Explain(probe.Error));

        // 2) 비밀번호 확인: 저장해 둔 것 → 로그인 비밀번호 → 사용자 입력 (각각 한 번씩만)
        string? password = null;
        string? lastError = null;
        foreach (var candidate in new[] { cachedPassword, loginPassword }.Distinct())
        {
            if (string.IsNullOrEmpty(candidate)) continue;
            var r = CheckPassword(shell, user, server, candidate);
            if (r.Exit == 0) { password = candidate; break; }
            lastError = r.Error;
            if (!IsWrongPassword(r.Error)) throw new SudoException(Explain(r.Error));
        }
        if (password == null)
        {
            var typed = askPassword(lastError == null ? "" : L.T("sudo 비밀번호가 틀렸습니다."))
                ?? throw new SudoException(L.T("sudo 비밀번호 입력이 취소되었습니다."));
            var r = CheckPassword(shell, user, server, typed);
            if (r.Exit != 0) throw new SudoException(Explain(r.Error));
            password = typed;
        }

        // 3) 확인한 비밀번호를 앞에 보내고 sftp-server 실행
        var preamble = Encoding.UTF8.GetBytes(password + "\n");
        return (OpenServer(shell, ServerCommand(server, user, true), preamble), home, password);
    }

    private static SftpClient OpenServer(SshClient shell, string command, byte[]? preamble)
    {
        try
        {
            return SharedSftpClient.TryOpen(shell, command, preamble)
                ?? throw new SudoException(L.T("터미널 연결이 끊겼습니다."));
        }
        catch (SharedSftpClient.ServerCommandException ex)
        {
            throw new SudoException(ex.ServerError is { Length: > 0 } err ? Explain(err) : L.F("sudo 실패: {0}", ex.Message));
        }
    }

    private static (int Exit, string Output, string Error) Run(SshClient shell, string command)
    {
        using var cmd = shell.CreateCommand(command);
        cmd.CommandTimeout = TimeSpan.FromSeconds(15);
        cmd.Execute();
        return (cmd.ExitStatus ?? -1, cmd.Result ?? "", cmd.Error ?? "");
    }

    /// <summary>비밀번호 한 줄을 넣고 입력을 닫는다 — 틀리면 sudo 가 다음 줄 대신 EOF 를 읽고 바로 끝난다(실패 1회)</summary>
    private static (int Exit, string Output, string Error) CheckPassword(SshClient shell, string user, string server, string password)
    {
        using var cmd = shell.CreateCommand(CheckPasswordCommand(user, server));
        cmd.CommandTimeout = TimeSpan.FromSeconds(30);
        var ar = cmd.BeginExecute();
        using (var input = cmd.CreateInputStream())
        {
            var bytes = Encoding.UTF8.GetBytes(password + "\n");
            input.Write(bytes, 0, bytes.Length);
        }
        cmd.EndExecute(ar);
        return (cmd.ExitStatus ?? -1, cmd.Result ?? "", cmd.Error ?? "");
    }
}
