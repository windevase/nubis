using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Nubis;

/// <summary>Ansible 인벤토리: 호스트 패턴(범위) 펼치기 · ssh 인자에서 점프 서버 찾기 (AnsibleInventoryImport 의 일부)</summary>
public static partial class AnsibleInventoryImport
{
    // ───────────────────────── 호스트 패턴 ─────────────────────────

    /// <summary>"web[01:03].example.com:2222" → 이름 목록 + 포트. 범위 안의 콜론은 포트로 보지 않는다</summary>
    internal static (List<string> Names, int? Port, bool Truncated) ExpandHostPattern(string pattern, int limit)
    {
        int? port = null;
        var body = pattern;
        // 범위 괄호를 지운 뒤 콜론이 정확히 하나이고 뒤가 숫자면 name:port (IPv6 주소는 콜론이 여러 개라 제외)
        var masked = RangePattern.Replace(body, m => new string('x', m.Length));
        int colon = masked.LastIndexOf(':');
        if (colon > 0 && masked.IndexOf(':') == colon && colon < masked.Length - 1 && masked.Substring(colon + 1).All(char.IsAsciiDigit))
        {
            if (int.TryParse(body.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var p)) port = p;
            body = body.Substring(0, colon);
        }
        var names = new List<string>();
        bool truncated = Expand(body, names, limit);
        return (names, port, truncated);
    }

    /// <summary>범위를 재귀로 펼친다. limit 개를 넘으면 멈추고 true</summary>
    private static bool Expand(string pattern, List<string> output, int limit)
    {
        var m = RangePattern.Match(pattern);
        if (!m.Success) { if (output.Count >= limit) return true; output.Add(pattern); return false; }

        var head = pattern.Substring(0, m.Index);
        var tail = pattern.Substring(m.Index + m.Length);
        var start = m.Groups[1].Value;
        var end = m.Groups[2].Value;
        int step = 1;
        if (m.Groups[3].Success && (!int.TryParse(m.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out step) || step <= 0)) step = 1;

        if (start.All(char.IsAsciiDigit) && end.All(char.IsAsciiDigit))
        {
            if (!long.TryParse(start, NumberStyles.None, CultureInfo.InvariantCulture, out var s)
                || !long.TryParse(end, NumberStyles.None, CultureInfo.InvariantCulture, out var e))
                return ExpandLiteral(pattern, output, limit);
            // 시작값에 앞자리 0 이 있으면 그 자릿수로 맞춘다 (Ansible 과 같음)
            var fmt = start.Length > 1 && start[0] == '0' ? "D" + start.Length.ToString(CultureInfo.InvariantCulture) : "D";
            for (long i = s; i <= e; i += step)
                if (Expand(head + i.ToString(fmt, CultureInfo.InvariantCulture) + tail, output, limit)) return true;
            return false;
        }
        if (start.Length == 1 && end.Length == 1 && char.IsAsciiLetter(start[0]) && char.IsAsciiLetter(end[0]))
        {
            for (int c = start[0]; c <= end[0]; c += step)
                if (Expand(head + (char)c + tail, output, limit)) return true;
            return false;
        }
        return ExpandLiteral(pattern, output, limit);
    }

    /// <summary>범위로 해석할 수 없는 괄호는 그대로 둔다</summary>
    private static bool ExpandLiteral(string pattern, List<string> output, int limit)
    {
        if (output.Count >= limit) return true;
        output.Add(pattern);
        return false;
    }

    // ───────────────────────── 셸 인자 · ProxyJump ─────────────────────────

    /// <summary>Python shlex 와 비슷한 분리: 따옴표 · 백슬래시 처리, comments 면 토큰 시작의 # 부터 무시</summary>
    internal static List<string> ShellSplit(string s, bool comments)
    {
        var result = new List<string>();
        var cur = new StringBuilder();
        bool inToken = false;
        char quote = '\0';
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (quote == '\'')
            {
                if (c == '\'') quote = '\0'; else cur.Append(c);
                continue;
            }
            if (quote == '"')
            {
                if (c == '"') quote = '\0';
                else if (c == '\\' && i + 1 < s.Length && (s[i + 1] is '"' or '\\')) cur.Append(s[++i]);
                else cur.Append(c);
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                if (inToken) { result.Add(cur.ToString()); cur.Clear(); inToken = false; }
                continue;
            }
            if (comments && c == '#' && !inToken) break;
            inToken = true;
            if (c is '\'' or '"') quote = c;
            else if (c == '\\' && i + 1 < s.Length) cur.Append(s[++i]);
            else cur.Append(c);
        }
        if (inToken) result.Add(cur.ToString());
        return result;
    }

    /// <summary>ssh 인자에서 점프 서버를 찾는다: -J x · -o ProxyJump=x · -o ProxyCommand="ssh -W %h:%p user@bastion"</summary>
    internal static (string? Jump, bool MultiHop) FindJumpHost(string? args)
    {
        if (string.IsNullOrWhiteSpace(args)) return (null, false);
        var tokens = ShellSplit(args, comments: false);
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            string? opt = null, jump = null;
            if (t == "-o" && i + 1 < tokens.Count) opt = tokens[++i];
            else if (t.StartsWith("-o", StringComparison.Ordinal) && t.Length > 2) opt = t.Substring(2);
            else if (t == "-J" && i + 1 < tokens.Count) jump = tokens[++i];
            else if (t.StartsWith("-J", StringComparison.Ordinal) && t.Length > 2) jump = t.Substring(2);

            if (opt != null)
            {
                int sep = opt.IndexOfAny(new[] { '=', ' ', '\t' });
                if (sep <= 0) continue;
                var key = opt.Substring(0, sep).Trim();
                var value = opt.Substring(sep + 1).Trim().TrimStart('=').Trim();
                if (key.Equals("ProxyJump", StringComparison.OrdinalIgnoreCase)) jump = value;
                else if (key.Equals("ProxyCommand", StringComparison.OrdinalIgnoreCase))
                {
                    var pc = FromProxyCommand(value);
                    if (pc != null) return (pc, false);
                }
            }
            if (string.IsNullOrWhiteSpace(jump) || jump.Equals("none", StringComparison.OrdinalIgnoreCase)) continue;

            var hops = jump.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (hops.Length == 0) continue;
            // Nubis 의 점프 서버는 한 단계뿐 — ssh_config 가져오기와 같이 첫 번째(이 PC 에서 바로 닿는) 점프 서버를 쓴다
            var first = hops[0];
            if (first.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)) first = first.Substring(6);
            return (first.TrimEnd('/'), hops.Length > 1);
        }
        return (null, false);
    }

    /// <summary>"ssh -W %h:%p -q -p 2222 user@bastion" → "user@bastion:2222". ssh 가 아니면 null</summary>
    private static string? FromProxyCommand(string command)
    {
        var tokens = ShellSplit(command, comments: false);
        if (tokens.Count == 0) return null;
        var exe = Path.GetFileName(tokens[0].Replace('\\', '/'));
        if (!exe.Equals("ssh", StringComparison.OrdinalIgnoreCase) && !exe.Equals("ssh.exe", StringComparison.OrdinalIgnoreCase)) return null;
        const string valueFlags = "BbcDEeFIiJLlmOoPpQRSWw";
        string? user = null, port = null, dest = null;
        for (int i = 1; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Length >= 2 && t[0] == '-')
            {
                char f = t[1];
                if (valueFlags.IndexOf(f) < 0) continue; // -q · -A · -T 등 값 없는 플래그
                string? v = t.Length > 2 ? t.Substring(2) : (i + 1 < tokens.Count ? tokens[++i] : null);
                if (f == 'p') port = v;
                else if (f == 'l') user = v;
                continue;
            }
            dest = t;
            break; // 첫 위치 인자가 대상, 그 뒤는 원격 명령
        }
        if (string.IsNullOrEmpty(dest)) return null;
        if (dest.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)) dest = dest.Substring(6).TrimEnd('/');
        if (user != null && !dest.Contains('@')) dest = user + "@" + dest;
        if (port != null && port != "22") dest += ":" + port;
        return dest;
    }
}
