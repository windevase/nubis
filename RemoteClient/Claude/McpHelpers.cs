using System;
using System.IO;
using System.Linq;
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
    /// send_to_terminal 을 승인 없이 입력해도 되는가. 모두 만족할 때만:
    ///  - 사용자가 '읽기 전용 명령은 승인 없이 실행'(AutoApproveReadOnly) 을 켰다
    ///  - 화면에 셸 프롬프트가 보인다 — sudo 비밀번호 질문·vim·mysql 안이면 같은 글자도 다른 뜻이 된다
    ///  - 한 줄이고 Enter 로 실행한다 (여러 줄·Enter 없는 입력은 프롬프트 상태를 바꿀 수 있음)
    ///  - ReadOnlyCommands 가 읽기 전용으로 본다 (허용 목록 방식, 애매하면 아님)
    /// </summary>
    public static bool CanAutoApproveTerminalInput(bool settingOn, string text, bool pressEnter, bool atPrompt, Func<string, bool> isReadOnly) =>
        settingOn && atPrompt && pressEnter && text.Trim().Length > 0 && text.IndexOfAny(['\r', '\n']) < 0 && isReadOnly(text);

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
    public static string? BlockedUploadReason(string localPath, string dataDir)
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
