using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Nubis;

/// <summary>표 가져오기: 칸 값 해석 — 주소 · 포트 · 프로토콜 · OS · 그룹 (TableImport 의 일부)</summary>
public static partial class TableImport
{
    // ───────── 값 해석 ─────────

    /// <summary>"[scheme://][user@]host[:port]" 분해. IPv6 는 [::1]:22 처럼 대괄호일 때만 포트를 뗀다</summary>
    private static (string Host, int? Port, string? User, Protocol? Scheme) SplitAddress(string raw)
    {
        var s = raw.Trim();
        Protocol? scheme = null;
        int sep = s.IndexOf("://", StringComparison.Ordinal);
        if (sep > 0)
        {
            scheme = s[..sep].Trim().ToLowerInvariant() switch
            {
                "ssh" or "sftp" => Nubis.Protocol.Ssh,
                "rdp" => Nubis.Protocol.Rdp,
                _ => null,
            };
            s = s[(sep + 3)..].TrimEnd('/');
        }

        string? user = null;
        int at = s.LastIndexOf('@');
        if (at >= 0)
        {
            var u = s[..at].Trim();
            user = u.Length > 0 ? u : null;
            s = s[(at + 1)..].Trim();
        }

        int? port = null;
        if (s.StartsWith('['))
        {
            int close = s.IndexOf(']');
            if (close > 0)
            {
                var rest = s[(close + 1)..].Trim();
                if (rest.StartsWith(':')) port = ParsePort(rest[1..]);
                s = s[1..close];
            }
        }
        else if (s.Count(ch => ch == ':') == 1)
        {
            int colon = s.IndexOf(':');
            var p = ParsePort(s[(colon + 1)..]);
            if (p != null)
            {
                port = p;
                s = s[..colon];
            }
        }
        return (s.Trim(), port, user, scheme);
    }

    /// <summary>1~65535. 엑셀 숫자 서식의 천 단위 쉼표("3,389")도 받는다 (쉼표 자리가 맞을 때만)</summary>
    private static int? ParsePort(string s)
    {
        s = s.Trim();
        if (s.Length is 5 or 6 && s[^4] == ',' && s.Count(ch => ch == ',') == 1) s = s.Remove(s.Length - 4, 1);
        return int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is >= 1 and <= 65535 ? p : null;
    }

    /// <summary>전각 영숫자·기호(１０．０．０．１, ：)와 전각 공백을 반각으로. 한글 등 다른 글자는 그대로</summary>
    private static string Narrow(string s)
    {
        StringBuilder? sb = null;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            char n = c is >= '！' and <= '～' ? (char)(c - 0xFEE0) : c == '　' ? ' ' : c;
            if (n != c)
            {
                sb ??= new StringBuilder(s, 0, i, s.Length);
                sb.Append(n);
            }
            else sb?.Append(c);
        }
        return sb?.ToString() ?? s;
    }

    private static bool IsIpLiteral(string host)
    {
        if (host.Length == 0) return false;
        if (Ipv4.IsMatch(host))
            return host.Split('.').All(o => int.TryParse(o, NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v <= 255);
        return host.Contains(':') && IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6;
    }

    /// <summary>주소처럼 보이는 칸: IP, 점이 있는 호스트명, localhost, 또는 user@ · :포트 · scheme:// 가 붙은 형태</summary>
    private static bool LooksLikeAddress(string cell)
    {
        var c = cell.Trim();
        if (c.Length == 0 || c.Any(char.IsWhiteSpace)) return false;
        var a = SplitAddress(c);
        if (a.Host.Length == 0 || a.Host.Any(char.IsWhiteSpace)) return false;
        if (IsIpLiteral(a.Host) || HostnameRx.IsMatch(a.Host) || a.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        // 점 없는 이름이라도 user@ · :포트 · ssh:// 처럼 접속 주소 형태가 분명하면 주소로 본다
        return (a.User != null || a.Port != null || a.Scheme != null) && Regex.IsMatch(a.Host, @"^[A-Za-z0-9_][A-Za-z0-9_.-]*$");
    }

    private static bool IsProtocolWord(string s) =>
        s.Trim().ToLowerInvariant() is "ssh" or "rdp" or "sftp";

    /// <summary>프로토콜 열 값: ssh/rdp 또는 windows/linux 같은 종류 이름</summary>
    private static Protocol? ParseProtocol(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var v = s.Trim().ToLowerInvariant();
        if (v.Contains("rdp") || v.Contains("windows") || v == "win" || v.Contains("원격 데스크톱") || v.Contains("원격데스크톱") || v == "윈도우")
            return Nubis.Protocol.Rdp;
        if (v.Contains("ssh") || v == "sftp" || v == "리눅스" || IsLinuxOs(v)) return Nubis.Protocol.Ssh;
        return null;
    }

    /// <summary>프로토콜 정보가 없을 때: 포트 3389/33890 → RDP, 22 → SSH, 그다음 OS (Windows → RDP, 리눅스 계열 → SSH)</summary>
    private static Protocol? InferProtocol(int? port, string? os)
    {
        if (port is 3389 or 33890) return Nubis.Protocol.Rdp;
        if (port == 22) return Nubis.Protocol.Ssh;
        if (string.IsNullOrWhiteSpace(os)) return null;
        var v = os.ToLowerInvariant();
        if (v.Contains("windows") || v.Contains("윈도우")) return Nubis.Protocol.Rdp;
        if (IsLinuxOs(v) || v.Contains("리눅스")) return Nubis.Protocol.Ssh;
        return null;
    }

    private static bool IsLinuxOs(string lower) => LinuxWords.Any(w => lower.Contains(w, StringComparison.Ordinal));

    /// <summary>그룹 여러 단계 (a/b, a\b) → "a / b"</summary>
    private static string? NormalizeGroup(string? g)
    {
        if (string.IsNullOrWhiteSpace(g)) return null;
        var parts = g.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                     .Select(p => OneLine(p))
                     .Where(p => !string.IsNullOrEmpty(p))
                     .ToList();
        return parts.Count == 0 ? null : string.Join(" / ", parts);
    }

    /// <summary>칸 안 줄바꿈 · 연속 공백을 한 칸으로. 빈 값은 null</summary>
    private static string? OneLine(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return Regex.Replace(s.Trim(), @"\s+", " ");
    }

    /// <summary>행 번호 목록을 "3-5, 8, 10" 처럼 줄인다 (너무 길면 앞 20구간 + …)</summary>
    internal static string FormatRows(IReadOnlyList<int> rows)
    {
        var sorted = rows.Distinct().OrderBy(x => x).ToList();
        var parts = new List<string>();
        for (int i = 0; i < sorted.Count;)
        {
            int j = i;
            while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1) j++;
            parts.Add(j == i ? sorted[i].ToString(CultureInfo.InvariantCulture)
                             : sorted[i].ToString(CultureInfo.InvariantCulture) + "-" + sorted[j].ToString(CultureInfo.InvariantCulture));
            i = j + 1;
        }
        const int max = 20;
        return parts.Count <= max ? string.Join(", ", parts) : string.Join(", ", parts.Take(max)) + ", …";
    }
}
