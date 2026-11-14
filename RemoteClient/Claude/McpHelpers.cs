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

    /// <summary>
    /// send_to_terminal 을 승인 없이 입력해도 되는가. 모두 만족할 때만:
    ///  - 사용자가 '읽기 전용 명령은 승인 없이 실행'(AutoApproveReadOnly) 을 켰다
    ///  - 화면에 셸 프롬프트가 보인다 — sudo 비밀번호 질문·vim·mysql 안이면 같은 글자도 다른 뜻이 된다
    ///  - 한 줄이고 Enter 로 실행한다 (여러 줄·Enter 없는 입력은 프롬프트 상태를 바꿀 수 있음)
    ///  - ReadOnlyCommands 가 읽기 전용으로 본다 (허용 목록 방식, 애매하면 아님)
    ///  - 로컬 터미널(이 PC 의 PowerShell)이 아니다 — gc ~\.ssh\id_rsa 같은 읽기 명령으로 이 PC 의 비밀 파일을 화면에 띄워
    ///    read_terminal 로 읽으면 ClaudeGuard 의 파일 읽기 차단을 우회하게 된다
    /// </summary>
    public static bool CanAutoApproveTerminalInput(bool settingOn, string text, bool pressEnter, bool atPrompt, Func<string, bool> isReadOnly,
        bool localTerminal = false) =>
        settingOn && !localTerminal && atPrompt && pressEnter && text.Trim().Length > 0 && text.IndexOfAny(['\r', '\n']) < 0 && isReadOnly(text);

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
        string full;
        try { full = Path.GetFullPath(localPath); }
        catch { return "잘못된 경로입니다."; }
        static bool Under(string path, string dir)
        {
            var d = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return path.StartsWith(d, StringComparison.OrdinalIgnoreCase) || path.Equals(d.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        if (Under(full, dataDir)) return "Nubis 데이터 폴더(계정·Vault·설정)의 파일은 업로드할 수 없습니다.";
        // 계정별 프로필을 쓰면 dataDir 은 users\<계정> 이고, 공통 폴더(login.json, 다른 계정의 users\**)는 따로 막는다
        if (rootDir != null && Under(full, rootDir)) return "Nubis 데이터 폴더(로그인 설정·계정별 프로필)의 파일은 업로드할 수 없습니다.";
        var ssh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        if (Under(full, ssh)) return "~/.ssh 폴더(개인키)의 파일은 업로드할 수 없습니다.";
        var name = Path.GetFileName(full).ToLowerInvariant();
        if (name is "id_rsa" or "id_ed25519" or "id_ecdsa" or "id_dsa" || name.EndsWith(".ppk") || name.EndsWith(".pem"))
            return "개인키로 보이는 파일은 업로드할 수 없습니다.";
        return null;
    }

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
