using System;
using System.Collections.Generic;
using System.DirectoryServices.Protocols;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nubis;

/// <summary>Active Directory 가져오기: LDAP 필터 · DN 해석 · 결과 항목 → 서버 같은 순수 함수 (ActiveDirectoryImport 의 일부)</summary>
public static partial class ActiveDirectoryImport
{
    // ───────────── 순수 함수 (테스트 대상) ─────────────

    /// <summary>검색 기준 DN 이 도메인 밖이라 referral 이 왔을 때 안내 (기준 DN 이 도메인의 DC= 와 다르면 그것도 짚어 준다)</summary>
    public static string ReferralMessage(string baseDn, string domain)
    {
        var expected = DomainToBaseDn(domain);
        // "OU=x, DC=corp, DC=com" 처럼 쉼표 뒤 공백이 있어도 같은 DN
        return baseDn.Replace(" ", "").EndsWith(expected.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)
            ? L.F("검색 기준 {0} 은(는) 이 도메인 컨트롤러가 직접 가진 범위가 아니어서 다른 서버로 안내되었습니다. 그 OU 가 속한 도메인을 도메인 칸에 입력하거나 서버를 그 도메인의 DC 로 지정하세요.", baseDn)
            : L.F("검색 기준 {0} 이(가) 도메인 {1} ({2}) 밖에 있습니다. 검색 기준을 {2} 로 끝나게 고치거나, 그 DN 이 속한 도메인을 도메인 칸에 입력하세요.", baseDn, domain, expected);
    }

    /// <summary>컴퓨터 검색 LDAP 필터</summary>
    /// <param name="lastLogonAfterFileTime">이 FILETIME(UTC) 이후 로그온한 것만 (null = 조건 없음)</param>
    public static string BuildFilter(bool serversOnly, bool skipDisabled, long? lastLogonAfterFileTime)
    {
        var sb = new StringBuilder("(&(objectCategory=computer)");
        // 서버만: Windows Server 이거나, Windows 가 아닌 OS (sssd 로 가입한 리눅스는 operatingSystem 이 비었거나 "Linux" 등)
        if (serversOnly) sb.Append("(|(operatingSystem=*Server*)(!(operatingSystem=*Windows*)))");
        // userAccountControl 비트 2 = ACCOUNTDISABLE (LDAP_MATCHING_RULE_BIT_AND)
        if (skipDisabled) sb.Append("(!(userAccountControl:1.2.840.113556.1.4.803:=2))");
        if (lastLogonAfterFileTime is long ft) sb.Append("(lastLogonTimestamp>=").Append(ft.ToString(CultureInfo.InvariantCulture)).Append(')');
        sb.Append(')');
        return sb.ToString();
    }

    /// <summary>
    /// DN 의 상위 컨테이너(OU · CN) 경로를 그룹으로: "CN=web01,OU=Web,OU=Servers,DC=corp,DC=com" → "Servers / Web".
    /// 첫 RDN(컴퓨터 자신)과 DC 는 뺀다. 컨테이너가 없으면 null
    /// </summary>
    public static string? GroupFromDn(string dn)
    {
        if (string.IsNullOrWhiteSpace(dn)) return null;
        var rdns = ParseDn(dn);
        var parts = rdns.Skip(1)
            .Where(r => r.Type.Equals("OU", StringComparison.OrdinalIgnoreCase) || r.Type.Equals("CN", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Value)
            .Where(v => v.Length > 0)
            .Reverse()
            .ToList();
        return parts.Count == 0 ? null : string.Join(" / ", parts);
    }

    /// <summary>LDAP 속성(대소문자 무시) → ImportedHost. 이름도 DNS 이름도 없으면 null</summary>
    public static ImportedHost? MapComputer(IReadOnlyDictionary<string, string?> attrs)
    {
        ArgumentNullException.ThrowIfNull(attrs);
        var name = Get(attrs, "name");
        var dns = Get(attrs, "dNSHostName");
        var os = Get(attrs, "operatingSystem");
        var ver = Get(attrs, "operatingSystemVersion");
        var desc = Get(attrs, "description");
        var dn = Get(attrs, "distinguishedName");

        if (name == null && dns != null)
        {
            var dot = dns.IndexOf('.');
            name = dot > 0 ? dns.Substring(0, dot) : dns;
        }
        if (name == null) return null;

        var isWindows = os != null && os.Contains("Windows", StringComparison.OrdinalIgnoreCase);
        string? note = desc;
        if (os == null)
        {
            var noOs = L.T("OS 정보 없음");
            note = note == null ? noOs : note + " · " + noOs;
        }

        return new ImportedHost
        {
            Name = name,
            Address = dns ?? name,
            Protocol = isWindows ? Protocol.Rdp : Protocol.Ssh,
            Os = os == null ? null : (ver == null ? os : os + " " + ver),
            Group = dn == null ? null : GroupFromDn(dn),
            Note = note,
        };
    }

    /// <summary>corp.example.com → DC=corp,DC=example,DC=com (이미 DN 형태면 그대로)</summary>
    public static string DomainToBaseDn(string domain)
    {
        var d = (domain ?? "").Trim().Trim('.');
        if (d.Contains('=')) return d;
        return string.Join(",", d.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(label => "DC=" + EscapeDnValue(label)));
    }

    // ───────────── 내부 구현 ─────────────

    private static string? Get(IReadOnlyDictionary<string, string?> attrs, string key)
    {
        if (!attrs.TryGetValue(key, out var v))
        {
            v = null;
            foreach (var kv in attrs)
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) { v = kv.Value; break; }
        }
        v = v?.Trim();
        return string.IsNullOrEmpty(v) ? null : v;
    }

    private static string EscapeDnValue(string v)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < v.Length; i++)
        {
            var c = v[i];
            if (c is ',' or '+' or '"' or '\\' or '<' or '>' or ';' or '=' || (i == 0 && (c == '#' || c == ' ')) || (i == v.Length - 1 && c == ' '))
                sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>DN 을 (타입, 값) RDN 목록으로. 이스케이프(\, · \\ · \2C 같은 16진 UTF-8 바이트)를 푼다</summary>
    private static List<(string Type, string Value)> ParseDn(string dn)
    {
        var rdns = new List<(string Type, string Value)>();
        var sb = new StringBuilder();
        var bytes = new List<byte>();

        void FlushBytes()
        {
            if (bytes.Count == 0) return;
            sb.Append(Encoding.UTF8.GetString(bytes.ToArray()));
            bytes.Clear();
        }
        void AddRdn()
        {
            FlushBytes();
            var raw = sb.ToString();
            sb.Clear();
            // 타입에는 이스케이프가 없으므로 첫 '=' 가 타입/값 구분
            var eq = raw.IndexOf('=');
            if (eq <= 0) return;
            rdns.Add((raw.Substring(0, eq).Trim(), raw.Substring(eq + 1).Trim()));
        }

        for (var i = 0; i < dn.Length; i++)
        {
            var c = dn[i];
            if (c == '\\' && i + 1 < dn.Length)
            {
                if (i + 2 < dn.Length && Uri.IsHexDigit(dn[i + 1]) && Uri.IsHexDigit(dn[i + 2]))
                {
                    bytes.Add(byte.Parse(dn.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 2;
                    continue;
                }
                FlushBytes();
                sb.Append(dn[i + 1]);
                i++;
                continue;
            }
            if (c == ',' || c == ';') { AddRdn(); continue; }
            FlushBytes();
            sb.Append(c);
        }
        AddRdn();
        return rdns;
    }

    /// <summary>"host:port" 분리 (IPv6 처럼 ':' 가 여럿이면 포트 없음으로 본다. "[v6]:port" 는 지원)</summary>
    private static (string Host, int? Port) SplitServer(string server)
    {
        var s = server.Trim();
        if (s.StartsWith('[') && s.IndexOf(']') is var close && close > 0)
        {
            var h = s.Substring(1, close - 1);
            if (close + 1 < s.Length && s[close + 1] == ':' && int.TryParse(s.AsSpan(close + 2), NumberStyles.None, CultureInfo.InvariantCulture, out var p6) && p6 is > 0 and < 65536)
                return (h, p6);
            return (h, null);
        }
        var colon = s.IndexOf(':');
        if (colon > 0 && colon == s.LastIndexOf(':')
            && int.TryParse(s.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is > 0 and < 65536)
            return (s.Substring(0, colon), p);
        return (s, null);
    }
}
