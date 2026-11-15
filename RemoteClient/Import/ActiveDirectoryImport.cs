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

/// <summary>Active Directory 가져오기 옵션</summary>
/// <param name="Domain">도메인 DNS 이름 (corp.example.com)</param>
/// <param name="Server">도메인 컨트롤러 주소 (없으면 도메인 이름 = DNS 가 DC 로 풀어 줌). "host:port" 로 포트를 고정할 수 있다</param>
/// <param name="Username">"user@domain" · "DOMAIN\user" · "user" (UseCurrentUser 면 무시)</param>
/// <param name="Password">비밀번호 — 어디에도 기록하지 않는다</param>
/// <param name="UseCurrentUser">현재 Windows 사용자로 연결 (Negotiate)</param>
/// <param name="SearchBase">검색 기준 DN (없으면 도메인 루트)</param>
/// <param name="ServersOnly">Windows Server 와 Windows 가 아닌 OS(리눅스 등)만</param>
/// <param name="SkipDisabled">사용 안 함 계정 제외</param>
/// <param name="ActiveWithinDays">최근 N일 안에 로그온한 컴퓨터만 (lastLogonTimestamp — 복제 지연으로 최대 14일 오차)</param>
/// <param name="ResolveIp">DNS 이름을 IP 로 바꿔 등록</param>
public sealed record AdImportOptions(string Domain, string? Server, string? Username, string? Password, bool UseCurrentUser,
    string? SearchBase, bool ServersOnly, bool SkipDisabled, int? ActiveWithinDays, bool ResolveIp);

/// <summary>
/// Active Directory(사내 AD · Microsoft Entra Domain Services)의 컴퓨터 개체를 가져온다.
/// 연결: LDAPS 636 을 먼저 시도하고(인증서는 시스템 기본 검증 — 끄지 않는다), 636 에 닿지 않거나 TLS 가 실패하면
/// LDAP 389 + Negotiate 서명·암호화로 연결한다. 평문 389 로 단순 바인드(비밀번호 그대로 전송)는 절대 하지 않는다.
/// 인증 실패는 다른 포트로 다시 시도하지 않는다 (계정 잠금 방지).
/// 사용자에게 보이는 오류는 InvalidOperationException(메시지는 번역됨)으로 던진다. 비밀번호는 메시지 · 경고 어디에도 넣지 않는다.
/// </summary>
public static class ActiveDirectoryImport
{
    private const int PageSize = 500;
    private const int DnsParallel = 16;
    private static readonly TimeSpan TcpProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LdapTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DnsTimeout = TimeSpan.FromSeconds(5);

    private static readonly string[] Attributes =
        { "name", "dNSHostName", "operatingSystem", "operatingSystemVersion", "description", "distinguishedName" };

    // LDAP 결과 코드: 서버에 닿지 않음 / 연결 오류 (인증 전 단계 — 다른 포트로 넘어가도 안전)
    private const int LdapServerDown = 81;
    private const int LdapConnectError = 91;
    private const int LdapInvalidCredentials = 49;

    /// <summary>이 PC 가 도메인에 가입돼 있으면 그 도메인 이름 (아니면 null) — 입력란 기본값용</summary>
    public static string? CurrentDomain()
    {
        try
        {
            var d = IPGlobalProperties.GetIPGlobalProperties().DomainName;
            if (!string.IsNullOrWhiteSpace(d)) return d.Trim();
        }
        catch (NetworkInformationException) { }
        catch (PlatformNotSupportedException) { }

        // 도메인 계정으로 로그인했으면 NetBIOS 도메인 이름 (로컬 계정이면 컴퓨터 이름, Entra 가입이면 "AzureAD")
        var ud = Environment.UserDomainName;
        if (!string.IsNullOrWhiteSpace(ud)
            && !string.Equals(ud, Environment.MachineName, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(ud, "AzureAD", StringComparison.OrdinalIgnoreCase))
            return ud;
        return null;
    }

    /// <summary>AD 에서 컴퓨터 개체를 읽어 ImportedHost 목록으로</summary>
    public static async Task<ImportResult> FetchAsync(AdImportOptions o, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(o);
        var domain = (o.Domain ?? "").Trim();
        if (domain.Length == 0) throw new InvalidOperationException(L.T("도메인을 입력하세요."));
        if (!o.UseCurrentUser)
        {
            if (string.IsNullOrWhiteSpace(o.Username)) throw new InvalidOperationException(L.T("사용자 이름을 입력하세요."));
            // 빈 비밀번호의 단순 바인드는 AD 가 '인증 없는 익명 바인드'로 성공 처리하므로 미리 막는다
            if (string.IsNullOrEmpty(o.Password)) throw new InvalidOperationException(L.T("비밀번호를 입력하세요."));
        }

        var (host, fixedPort) = SplitServer(string.IsNullOrWhiteSpace(o.Server) ? domain : o.Server.Trim());
        var baseDn = string.IsNullOrWhiteSpace(o.SearchBase) ? DomainToBaseDn(domain) : o.SearchBase.Trim();
        long? since = o.ActiveWithinDays is int days && days > 0 ? DateTime.UtcNow.AddDays(-days).ToFileTimeUtc() : null;
        var filter = BuildFilter(o.ServersOnly, o.SkipDisabled, since);

        var result = ImportResult.Empty();
        List<Dictionary<string, string?>> entries;
        using (var conn = await ConnectAsync(host, fixedPort, domain, o, result.Warnings, ct).ConfigureAwait(false))
        {
            try
            {
                entries = await Task.Run(() => Search(conn, baseDn, filter, ct), ct).ConfigureAwait(false);
            }
            catch (DirectoryOperationException ex) when (ex.Response?.ResultCode == ResultCode.NoSuchObject)
            {
                throw new InvalidOperationException(L.F("검색 기준 DN 을 찾을 수 없습니다: {0}", baseDn), ex);
            }
            catch (DirectoryOperationException ex)
            {
                throw new InvalidOperationException(L.F("AD 검색에 실패했습니다: {0}", ex.Message), ex);
            }
            catch (LdapException ex)
            {
                throw new InvalidOperationException(L.F("AD 검색에 실패했습니다: {0}", ex.Message), ex);
            }
        }

        var skipped = 0;
        foreach (var e in entries)
        {
            var h = MapComputer(e);
            if (h == null) skipped++;
            else result.Hosts.Add(h);
        }
        if (skipped > 0) result.Warnings.Add(L.F("이름이 없는 항목 {0}개를 건너뛰었습니다.", skipped));

        if (o.ResolveIp && result.Hosts.Count > 0)
        {
            var failed = await ResolveAsync(result.Hosts, ct).ConfigureAwait(false);
            if (failed > 0) result.Warnings.Add(L.F("{0}대는 IP 를 찾지 못해 DNS 이름을 그대로 두었습니다.", failed));
        }

        result.Hosts.Sort((a, b) =>
        {
            var g = string.Compare(a.Group ?? "", b.Group ?? "", StringComparison.CurrentCultureIgnoreCase);
            return g != 0 ? g : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
        });
        if (result.Hosts.Count == 0) result.Warnings.Add(L.T("조건에 맞는 컴퓨터가 없습니다."));
        return result;
    }

    // ───────────── 순수 함수 (테스트 대상) ─────────────

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

    private static bool IsSslPort(int port) => port is 636 or 3269;

    private static bool IsConnectError(LdapException ex) => ex.ErrorCode is LdapServerDown or LdapConnectError;

    /// <summary>
    /// 연결 · 바인드. 시도 순서: (포트 고정이면 그 포트만) 636 LDAPS → 389 서명·암호화.
    /// 다음 포트로 넘어가는 경우는 TCP 연결 실패 · TLS 실패(서버 다운 코드)뿐 — 인증 실패는 바로 오류
    /// </summary>
    private static async Task<LdapConnection> ConnectAsync(string host, int? fixedPort, string domain, AdImportOptions o,
        List<string> warnings, CancellationToken ct)
    {
        var ports = fixedPort is int fp ? new[] { fp } : new[] { 636, 389 };
        var tlsFailed = false;
        string? tlsFailedEndpoint = null;

        foreach (var port in ports)
        {
            ct.ThrowIfCancellationRequested();
            var ssl = IsSslPort(port);
            if (!await CanConnectAsync(host, port, ct).ConfigureAwait(false)) continue;

            var conn = Create(host, port, ssl, domain, o);
            LdapException? err;
            try
            {
                err = await Task.Run(() =>
                {
                    try { conn.Bind(); return (LdapException?)null; }
                    catch (LdapException ex) { return ex; }
                }, ct).ConfigureAwait(false);
            }
            catch
            {
                conn.Dispose();
                throw;
            }

            if (err == null)
            {
                if (tlsFailed) warnings.Add(L.T("LDAPS(636) 인증서를 확인하지 못해 LDAP 389(서명 · 암호화)로 연결했습니다."));
                return conn;
            }
            conn.Dispose();

            if (IsConnectError(err))
            {
                // TCP 는 열렸는데 서버 다운 = 대부분 TLS 핸드셰이크(인증서 신뢰 · 이름 불일치) 실패. 비밀번호는 아직 보내지 않았다
                if (ssl) { tlsFailed = true; tlsFailedEndpoint = host + ":" + port.ToString(CultureInfo.InvariantCulture); }
                continue;
            }
            if (err.ErrorCode == LdapInvalidCredentials)
                throw new InvalidOperationException(L.T("AD 로그인에 실패했습니다. 사용자 이름과 비밀번호를 확인하세요. (계정 잠금을 막기 위해 다시 시도하지 않았습니다)"), err);
            throw new InvalidOperationException(L.F("AD 연결에 실패했습니다: {0}", err.Message), err);
        }

        if (tlsFailed)
            throw new InvalidOperationException(L.F("{0} 의 LDAPS 인증서를 신뢰할 수 없습니다. 도메인 컨트롤러(Entra Domain Services 는 보안 LDAP)의 인증서를 이 PC 가 신뢰하도록 등록하고, 서버 주소를 인증서 이름과 같게 입력하세요.", tlsFailedEndpoint));
        if (fixedPort is int only)
            throw new InvalidOperationException(L.F("AD 서버 {0} 에 연결할 수 없습니다 (포트 {1}).", host, only));
        throw new InvalidOperationException(L.F("{0} 에 연결할 수 없습니다 (LDAPS 636 · LDAP 389). 도메인 컨트롤러 주소와 방화벽 · VPN 연결을 확인하세요.", host));
    }

    private static LdapConnection Create(string host, int port, bool ssl, string domain, AdImportOptions o)
    {
        var id = new LdapDirectoryIdentifier(host, port, false, false);
        LdapConnection conn;
        if (o.UseCurrentUser)
        {
            // 자격 증명 없이 Negotiate = 현재 Windows 사용자 (Kerberos / NTLM)
            conn = new LdapConnection(id) { AuthType = AuthType.Negotiate };
        }
        else if (ssl)
        {
            // TLS(인증서 검증 완료) 안에서만 단순 바인드 — Entra DS 보안 LDAP 처럼 Kerberos 가 닿지 않는 곳에서도 동작.
            // 사용자 이름은 UPN 또는 DOMAIN\user 그대로 보낸다 (AD 는 둘 다 받는다)
            var user = o.Username!.Trim();
            if (!user.Contains('@') && !user.Contains('\\')) user = user + "@" + domain;
            conn = new LdapConnection(id, new NetworkCredential(user, o.Password), AuthType.Basic);
        }
        else
        {
            // 389 는 Negotiate(비밀번호를 보내지 않음) + 서명 · 암호화만
            conn = new LdapConnection(id, MakeNegotiateCredential(o.Username!.Trim(), o.Password, domain), AuthType.Negotiate);
        }

        conn.Timeout = LdapTimeout;
        conn.AutoBind = false;
        var so = conn.SessionOptions;
        so.ProtocolVersion = 3;
        so.ReferralChasing = ReferralChasingOptions.None;
        if (ssl) so.SecureSocketLayer = true;   // 인증서 검증 콜백을 두지 않는다 = 시스템 기본 검증
        else { so.Signing = true; so.Sealing = true; }
        return conn;
    }

    private static NetworkCredential MakeNegotiateCredential(string user, string? password, string domain)
    {
        var bs = user.IndexOf('\\');
        if (bs > 0) return new NetworkCredential(user.Substring(bs + 1), password, user.Substring(0, bs));
        if (user.Contains('@')) return new NetworkCredential(user, password);   // UPN 은 도메인 없이
        return new NetworkCredential(user, password, domain);
    }

    private static async Task<bool> CanConnectAsync(string host, int port, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TcpProbeTimeout);
        using var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        catch (SocketException) { return false; }
    }

    private static List<Dictionary<string, string?>> Search(LdapConnection conn, string baseDn, string filter, CancellationToken ct)
    {
        var list = new List<Dictionary<string, string?>>();
        var req = new SearchRequest(baseDn, filter, SearchScope.Subtree, Attributes);
        var page = new PageResultRequestControl(PageSize);
        req.Controls.Add(page);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var resp = (SearchResponse)conn.SendRequest(req, LdapTimeout);
            foreach (SearchResultEntry e in resp.Entries) list.Add(ReadEntry(e));

            PageResultResponseControl? next = null;
            foreach (var c in resp.Controls)
                if (c is PageResultResponseControl p) next = p;
            if (next == null || next.Cookie == null || next.Cookie.Length == 0) break;
            page.Cookie = next.Cookie;
        }
        return list;
    }

    private static Dictionary<string, string?> ReadEntry(SearchResultEntry e)
    {
        var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (string an in e.Attributes.AttributeNames)
        {
            var attr = e.Attributes[an];
            if (attr == null || attr.Count == 0) continue;
            var values = attr.GetValues(typeof(string));
            if (values.Length > 0) d[an] = values[0] as string;
        }
        if (!d.TryGetValue("distinguishedName", out var dn) || string.IsNullOrEmpty(dn)) d["distinguishedName"] = e.DistinguishedName;
        return d;
    }

    /// <summary>DNS 이름 → IP (IPv4 우선, 동시 16개, 개별 시간 제한). 실패한 대수를 돌려준다 (주소는 DNS 이름 그대로)</summary>
    private static async Task<int> ResolveAsync(List<ImportedHost> hosts, CancellationToken ct)
    {
        var failed = 0;
        var options = new ParallelOptions { MaxDegreeOfParallelism = DnsParallel, CancellationToken = ct };
        await Parallel.ForEachAsync(Enumerable.Range(0, hosts.Count), options, async (i, token) =>
        {
            var h = hosts[i];
            if (IPAddress.TryParse(h.Address, out _)) return;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(DnsTimeout);
            try
            {
                var addrs = await Dns.GetHostAddressesAsync(h.Address, cts.Token).ConfigureAwait(false);
                var ip = addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                         ?? addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal);
                if (ip != null) hosts[i] = h with { Address = ip.ToString() };   // 서로 다른 인덱스만 쓰므로 안전
                else Interlocked.Increment(ref failed);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { Interlocked.Increment(ref failed); }
            catch (SocketException) { Interlocked.Increment(ref failed); }
            catch (ArgumentException) { Interlocked.Increment(ref failed); }
        }).ConfigureAwait(false);
        return failed;
    }
}
