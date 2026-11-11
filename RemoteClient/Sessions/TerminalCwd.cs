using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Nubis;

/// <summary>
/// 셸이 OSC 7 을 보내지 않을 때 화면에서 터미널 현재 폴더를 알아내기 (SFTP 패널 따라가기용).
///  - "user@host:/경로$" (Debian·Ubuntu 기본): 프롬프트에서 바로 읽는다.
///  - "[user@host 폴더]$" (RHEL·Amazon Linux·CentOS 기본): 폴더 이름만 보이므로 직전 줄의 cd 명령으로
///    경로를 계산하고, 계산한 경로의 마지막 이름이 새 프롬프트의 폴더 이름과 같을 때만 믿는다.
/// 알 수 없으면 null (따라가지 않음).
/// </summary>
public static class TerminalCwd
{
    private static readonly Regex BracketPrompt =
        new(@"^(?:\([^)]*\)\s*)?\[[^@\s\]]+@[^\s\]]+ (?<d>[^\]]+)\][$#](?<cmd>.*)$", RegexOptions.Compiled);
    private static readonly Regex FirstArg = new(@"^\s*(?:""(?<a>[^""]*)""|'(?<a>[^']*)'|(?<a>[^\s;&|]+))", RegexOptions.Compiled);

    /// <param name="screen">커서 줄까지의 마지막 몇 줄 (XtermView.ReadBufferAsync)</param>
    /// <param name="home">원격 홈 폴더</param>
    /// <param name="current">직전에 알던 터미널 현재 폴더 (모르면 null)</param>
    public static string? Resolve(string screen, string home, string? current)
    {
        if (XtermView.PromptPath(screen, home) is { } p) return p;

        var lines = screen.Replace("\r", "").Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0) return null;
        var last = BracketPrompt.Match(lines[^1]);
        if (!last.Success || last.Groups["cmd"].Value.Trim().Length > 0) return null;   // 명령을 입력 중
        var name = last.Groups["d"].Value;
        if (name == "~") return home;
        if (name == "/") return "/";

        // 새 프롬프트 바로 위쪽에서 cd 를 실행한 프롬프트 줄 찾기
        for (var i = lines.Count - 2; i >= 0; i--)
        {
            var m = BracketPrompt.Match(lines[i]);
            if (!m.Success) continue;
            var cmd = m.Groups["cmd"].Value.Trim();
            if (current == null) return null;
            string? target = null;
            if (cmd == "cd") target = home;
            else if (cmd.StartsWith("cd ") || cmd.StartsWith("cd\t"))
            {
                var a = FirstArg.Match(cmd[2..]);
                if (a.Success) target = Combine(current, a.Groups["a"].Value, home);
            }
            else if (Leaf(current) == name) return current;   // cd 가 아닌 명령: 폴더 그대로
            return target != null && Leaf(target) == name ? target : null;
        }
        return null;
    }

    /// <summary>cd 인자를 현재 폴더 기준 절대 경로로 (~, ., .. 처리). "-" 처럼 알 수 없으면 null</summary>
    public static string? Combine(string current, string arg, string home)
    {
        if (arg.Length == 0 || arg == "~") return home;
        if (arg == "-" || arg.StartsWith('$') || arg.StartsWith("~") && !arg.StartsWith("~/")) return null;
        var path = arg.StartsWith("~/") ? home.TrimEnd('/') + arg[1..]
                 : arg.StartsWith('/') ? arg
                 : current.TrimEnd('/') + "/" + arg;
        var parts = new List<string>();
        foreach (var s in path.Split('/'))
        {
            if (s.Length == 0 || s == ".") continue;
            if (s == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(s);
        }
        return "/" + string.Join("/", parts);
    }

    private static string Leaf(string path) => path == "/" ? "/" : path.TrimEnd('/').Split('/')[^1];
}
