using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Nubis;

/// <summary>
/// SSH 로컬 포트 포워딩 규칙 (ssh -L 로컬포트:대상호스트:대상포트). 호스트에 저장되는 값 — hosts.json 은 파일 전체가 DPAPI 암호화.
/// 로컬 쪽은 늘 127.0.0.1 에만 연다 (다른 PC 가 이 터널을 쓰지 못하게)
/// </summary>
public sealed class PortForwardRule
{
    /// <summary>이 PC 에서 여는 포트 (127.0.0.1)</summary>
    public int LocalPort { get; set; }
    /// <summary>SSH 서버에서 본 대상 호스트 (기본 localhost = SSH 서버 자신)</summary>
    public string TargetHost { get; set; } = PortForwards.DefaultTargetHost;
    public int TargetPort { get; set; }
    /// <summary>표시용 이름 (선택)</summary>
    public string? Name { get; set; }
    /// <summary>탭이 연결되면 자동으로 열지</summary>
    public bool Enabled { get; set; } = true;

    public PortForwardRule Clone() => (PortForwardRule)MemberwiseClone();

    /// <summary>같은 규칙인지 비교하는 키 (이름·켜기 여부 제외)</summary>
    [JsonIgnore] public string Key => $"{LocalPort}|{TargetHost.ToLowerInvariant()}|{TargetPort}";
}

/// <summary>포워딩 규칙 검증·표시 (화면과 무관한 순수 함수 — PortForwardTests)</summary>
public static class PortForwards
{
    /// <summary>로컬 쪽 바인드 주소 — 이 PC 만. 0.0.0.0 으로 열지 않는다</summary>
    public const string BindAddress = "127.0.0.1";
    public const string DefaultTargetHost = "localhost";
    public const int MaxRules = 32;
    public const int MaxNameLength = 40;
    public const int MaxHostLength = 253;

    private static readonly Regex HostName = new(@"^[A-Za-z0-9_](?:[A-Za-z0-9._-]*[A-Za-z0-9_])?$", RegexOptions.CultureInvariant);

    /// <summary>포트 문자열 → 숫자. 잘못됐으면 오류 문구</summary>
    public static string? ParsePort(string? text, out int port, bool local)
    {
        port = 0;
        var t = (text ?? "").Trim();
        if (t.Length == 0) return local ? L.T("로컬 포트를 입력하세요.") : L.T("대상 포트를 입력하세요.");
        if (!int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out port)) port = -1;
        return CheckPort(port, local);
    }

    public static string? CheckPort(int port, bool local) =>
        port is >= 1 and <= 65535 ? null
            : local ? L.T("로컬 포트는 1 ~ 65535 사이 숫자여야 합니다.") : L.T("대상 포트는 1 ~ 65535 사이 숫자여야 합니다.");

    /// <summary>대상 호스트: 호스트명(영문·숫자·. - _) 또는 IP(IPv6 는 [ ] 없이 또는 감싸서). 공백·@·/·제어 문자 등은 거부</summary>
    public static string? CheckTargetHost(string? host)
    {
        if (host != null && McpHelpers.HasInvisibleChars(host)) return L.T("대상 호스트에는 호스트명 또는 IP 만 넣으세요 (포트는 따로).");
        var h = NormalizeHost(host);
        if (h.Length == 0) return L.T("대상 호스트를 입력하세요.");
        if (h.Length > MaxHostLength) return L.F("대상 호스트는 {0}자 이하여야 합니다.", MaxHostLength);
        // "[::1]:80"·"fe80::1%eth0" 처럼 포트·범위가 붙은 값은 IPAddress.TryParse 가 받아 주므로 먼저 거른다
        if (h.IndexOfAny(new[] { '[', ']', '%', '/', ' ' }) >= 0) return L.T("대상 호스트에는 호스트명 또는 IP 만 넣으세요 (포트는 따로).");
        if (h.Contains(':')) return IPAddress.TryParse(h, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? null : L.T("대상 호스트에는 호스트명 또는 IP 만 넣으세요 (포트는 따로).");
        if (!HostName.IsMatch(h) || h.Contains("..")) return L.T("대상 호스트에는 호스트명 또는 IP 만 넣으세요 (포트는 따로).");
        return null;
    }

    /// <summary>앞뒤 공백·IPv6 의 [ ] 를 걷어 낸 대상 호스트</summary>
    public static string NormalizeHost(string? host)
    {
        var h = (host ?? "").Trim();
        if (h.Length > 2 && h[0] == '[' && h[^1] == ']') h = h[1..^1];
        return h;
    }

    public static string? CheckName(string? name)
    {
        var n = (name ?? "").Trim();
        if (n.Length > MaxNameLength) return L.F("이름은 {0}자 이하여야 합니다.", MaxNameLength);
        if (McpHelpers.HasInvisibleChars(n)) return L.T("이름에 보이지 않는 제어 문자·양방향 문자가 있습니다.");
        return null;
    }

    /// <summary>입력 칸 값으로 규칙 만들기. 대상 호스트를 비우면 localhost. 잘못됐으면 (null, 오류)</summary>
    public static (PortForwardRule? Rule, string? Error) Parse(string? localPort, string? targetHost, string? targetPort, string? name)
    {
        if (ParsePort(localPort, out var lp, local: true) is { } e1) return (null, e1);
        var host = string.IsNullOrWhiteSpace(targetHost) ? DefaultTargetHost : NormalizeHost(targetHost);
        if (CheckTargetHost(host) is { } e2) return (null, e2);
        if (ParsePort(targetPort, out var tp, local: false) is { } e3) return (null, e3);
        if (CheckName(name) is { } e4) return (null, e4);
        var n = (name ?? "").Trim();
        return (new PortForwardRule { LocalPort = lp, TargetHost = host, TargetPort = tp, Name = n.Length > 0 ? n : null }, null);
    }

    /// <summary>규칙 하나 검사 (저장된 값·MCP 인자)</summary>
    public static string? Check(PortForwardRule r) =>
        CheckPort(r.LocalPort, true) ?? CheckTargetHost(r.TargetHost) ?? CheckPort(r.TargetPort, false) ?? CheckName(r.Name);

    /// <summary>
    /// 목록 검사: 규칙마다 Check + 개수 제한 + 켜진 규칙끼리 로컬 포트 중복 (꺼 둔 규칙은 같은 포트를 써도 된다 — 번갈아 켜는 용도)
    /// </summary>
    public static string? CheckList(IReadOnlyList<PortForwardRule> rules)
    {
        if (rules.Count > MaxRules) return L.F("포트 포워딩은 호스트마다 {0}개까지입니다.", MaxRules);
        foreach (var r in rules)
            if (Check(r) is { } e) return $"{Describe(r)}: {e}";
        if (DuplicateLocalPort(rules.Where(r => r.Enabled).Select(r => r.LocalPort)) is { } dup)
            return L.F("로컬 포트 {0} 을(를) 켜 둔 규칙이 둘 이상입니다.", dup);
        return null;
    }

    /// <summary>처음으로 두 번 나온 포트 (없으면 null)</summary>
    public static int? DuplicateLocalPort(IEnumerable<int> ports)
    {
        var seen = new HashSet<int>();
        foreach (var p in ports)
            if (!seen.Add(p)) return p;
        return null;
    }

    /// <summary>로컬 쪽 주소 "127.0.0.1:15432"</summary>
    public static string LocalAddress(int port) => $"{BindAddress}:{port.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>대상 쪽 주소 "db:5432" (IPv6 는 [ ] 로 감쌈)</summary>
    public static string TargetAddress(string host, int port) =>
        (host.Contains(':') ? $"[{host}]" : host) + ":" + port.ToString(CultureInfo.InvariantCulture);

    /// <summary>"127.0.0.1:15432 → db:5432" (화면·감사 로그·MCP 결과에 같은 모양)</summary>
    public static string Describe(PortForwardRule r) => Arrow(LocalAddress(r.LocalPort), TargetAddress(r.TargetHost, r.TargetPort));

    /// <summary>포워딩 한 줄 모양 — 쿠버네티스 포워딩 등 다른 종류도 같은 모양을 쓰게 따로 둔다</summary>
    public static string Arrow(string local, string remote) => $"{local} → {remote}";

    private static readonly HashSet<int> HttpPorts = new() { 80, 3000, 5000, 5601, 8000, 8080, 8081, 8088, 8888, 9000, 9090, 15672 };
    private static readonly HashSet<int> HttpsPorts = new() { 443, 6443, 8443, 9443, 10443 };

    /// <summary>
    /// 브라우저로 열 수 있어 보이는 포워딩이면 "http"/"https" (이름에 https/http/web 이 있거나 대상 포트가 흔한 웹 포트). 아니면 null
    /// </summary>
    public static string? GuessScheme(PortForwardRule r)
    {
        var n = r.Name ?? "";
        if (n.Contains("https", StringComparison.OrdinalIgnoreCase)) return "https";
        if (n.Contains("http", StringComparison.OrdinalIgnoreCase) || n.Contains("web", StringComparison.OrdinalIgnoreCase)) return "http";
        if (HttpsPorts.Contains(r.TargetPort) || HttpsPorts.Contains(r.LocalPort)) return "https";
        if (HttpPorts.Contains(r.TargetPort) || HttpPorts.Contains(r.LocalPort)) return "http";
        return null;
    }

    /// <summary>브라우저 주소 (GuessScheme 이 null 이면 null)</summary>
    public static string? BrowserUrl(PortForwardRule r) =>
        GuessScheme(r) is { } s ? $"{s}://{LocalAddress(r.LocalPort)}/" : null;

    /// <summary>목록 깊은 복사 (null·빈 목록은 null)</summary>
    public static List<PortForwardRule>? CloneList(IEnumerable<PortForwardRule>? rules) =>
        rules?.Select(r => r.Clone()).ToList() is { Count: > 0 } l ? l : null;

    /// <summary>두 목록이 같은지 (순서·이름·켜기 포함) — 편집 패널의 바뀜 확인</summary>
    public static string Snapshot(IEnumerable<PortForwardRule>? rules) =>
        string.Join(";", (rules ?? Enumerable.Empty<PortForwardRule>()).Select(r => $"{r.Key}|{r.Name}|{r.Enabled}"));
}
