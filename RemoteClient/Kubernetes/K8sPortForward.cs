using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Nubis;

/// <summary>
/// kubectl port-forward 의 인자 조립 · 포트 고르기 · 출력 해석 (화면 무관, K8sPortForwardTests).
/// 로컬 쪽은 항상 127.0.0.1 에만 연다 (--address 127.0.0.1) — 같은 네트워크의 다른 PC 가 클러스터 안으로 들어오지 못하게
/// </summary>
public static class K8sPortForward
{
    public const string BindAddress = "127.0.0.1";

    /// <summary>포트 포워딩을 열 수 있는 리소스 (파드 · 디플로이먼트 · 서비스, 네임스페이스가 있는 것)</summary>
    public static bool CanForward(K8sItem i) =>
        i.Kind is K8sKind.Pods or K8sKind.Deployments or K8sKind.Services && i.Namespace.Length > 0 && i.Name.Length > 0;

    /// <summary>kubectl port-forward 대상 (pod/x · deploy/x · svc/x — 목록 행에도 이 모양으로 보인다)</summary>
    public static string TargetRef(K8sKind kind, string name) => kind switch
    {
        K8sKind.Pods => "pod/" + name,
        K8sKind.Deployments => "deploy/" + name,
        K8sKind.Services => "svc/" + name,
        _ => throw new ArgumentException("port-forward: pod · deployment · service only"),
    };

    /// <summary>
    /// "pod/x" · "po/x" · "pods/x" · "svc/x" · "service/x" · "deploy/x" · "deployment/x" → (종류, 이름). 종류가 없으면 파드로 본다.
    /// 모르는 종류이거나 이름이 쿠버네티스 이름이 아니면 null
    /// </summary>
    public static (K8sKind Kind, string Name)? ParseTarget(string? spec)
    {
        var s = (spec ?? "").Trim();
        var slash = s.IndexOf('/');
        var kindText = slash < 0 ? "pod" : s[..slash].ToLowerInvariant();
        var name = slash < 0 ? s : s[(slash + 1)..];
        K8sKind? kind = kindText switch
        {
            "pod" or "pods" or "po" => K8sKind.Pods,
            "svc" or "service" or "services" => K8sKind.Services,
            "deploy" or "deployment" or "deployments" => K8sKind.Deployments,
            _ => null,
        };
        return kind is { } k && IsK8sName(name) ? (k, name) : null;
    }

    /// <summary>쿠버네티스 이름 (네임스페이스 · 파드 · 서비스) — kubectl 인자로 그대로 넘기므로 다른 문자는 받지 않는다</summary>
    public static bool IsK8sName(string s) => Regex.IsMatch(s, "^[a-z0-9]([-a-z0-9.]{0,251}[a-z0-9])?$");

    public static bool ValidPort(int p) => p is >= 1 and <= 65535;

    /// <summary>
    /// kubectl 인자 (--context 는 실행할 때 앞에 붙는다): port-forward --address 127.0.0.1 -n ns 대상 로컬:원격.
    /// 이름·포트가 규칙에 맞지 않으면 ArgumentException
    /// </summary>
    public static List<string> Args(string ns, K8sKind kind, string name, int local, int remote)
    {
        if (!IsK8sName(ns) || !IsK8sName(name)) throw new ArgumentException("namespace · name");
        if (!ValidPort(local) || !ValidPort(remote)) throw new ArgumentException("port");
        return new List<string>
        {
            "port-forward", "--address", BindAddress, "-n", ns, TargetRef(kind, name),
            local.ToString(CultureInfo.InvariantCulture) + ":" + remote.ToString(CultureInfo.InvariantCulture),
        };
    }

    /// <summary>목록 행 문구: "localhost:8080 → svc/web:80" (SSH 포트 포워딩 목록과 같은 모양)</summary>
    public static string Label(int local, string target, int remote) =>
        string.Create(CultureInfo.InvariantCulture, $"localhost:{local} → {target}:{remote}");

    /// <summary>복사·브라우저용 주소 (127.0.0.1 에만 열리므로 localhost 대신 — ::1 로 먼저 가는 클라이언트가 있다)</summary>
    public static string LocalAddress(int local) => string.Create(CultureInfo.InvariantCulture, $"{BindAddress}:{local}");

    /// <summary>포트 문자열 (앞뒤 공백 허용) → 1~65535, 아니면 null</summary>
    public static int? ParsePort(string? s) =>
        int.TryParse((s ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var p) && ValidPort(p) ? p : null;

    /// <summary>
    /// 로컬 포트 제안: 원격과 같은 번호 → 쓰이면 그 뒤로 100개 → 그래도 없으면 10000 위에서 원격 번호 끝자리를 맞춘 것부터 → 없으면 null.
    /// 1024 미만도 Windows 는 관리자 권한 없이 열 수 있으므로 같은 번호를 먼저 본다
    /// </summary>
    public static int? SuggestLocalPort(int remote, Func<int, bool> isFree)
    {
        if (!ValidPort(remote)) return null;
        foreach (var p in Candidates(remote))
            if (isFree(p)) return p;
        return null;
    }

    private static IEnumerable<int> Candidates(int remote)
    {
        yield return remote;
        for (var p = remote + 1; p <= Math.Min(65535, remote + 100); p++) yield return p;
        // 10000 + (remote % 10000) 부터 1만씩 (80 → 10080 · 20080 …)
        for (var p = 10000 + remote % 10000; p <= 65535; p += 10000) if (p != remote) yield return p;
    }

    /// <summary>"Forwarding from 127.0.0.1:8080 -> 80" → (8080, 80). 다른 줄이면 null</summary>
    public static (int Local, int Remote)? ParseForwarding(string line)
    {
        var m = Regex.Match(line, @"^\s*Forwarding from (?:127\.0\.0\.1|\[::1\]|localhost):(\d{1,5}) -> (\d{1,5})\s*$");
        if (!m.Success) return null;
        var l = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var r = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        return ValidPort(l) && ValidPort(r) ? (l, r) : null;
    }

    /// <summary>오류로 보여 줄 줄인가 (연결마다 찍히는 "Handling connection for 8080" 등은 아니다)</summary>
    public static bool IsNoise(string line)
    {
        var t = line.Trim();
        return t.Length == 0 || t.StartsWith("Handling connection for ", StringComparison.Ordinal) || ParseForwarding(t) != null;
    }

    /// <summary>
    /// kubectl 오류 줄을 짧게: klog 머리("E1119 10:00:00.123 1234 portforward.go:413] ")를 떼고 300자까지
    /// </summary>
    public static string CleanError(string line)
    {
        var t = Regex.Replace(line.Trim(), @"^[EWIF]\d{4} \d{2}:\d{2}:\d{2}\.\d+\s+\d+\s+\S+:\d+\]\s*", "");
        if (t.StartsWith("error: ", StringComparison.Ordinal)) t = t[7..];
        return t.Length > 300 ? t[..300] + "…" : t;
    }

    /// <summary>
    /// 브라우저로 열 만한 포트면 "http" / "https", 아니면 null. 포트 이름·appProtocol 을 먼저 보고(https · http · web),
    /// 없으면 흔한 웹 포트 번호로 짐작한다 (데이터베이스 등 다른 포트에는 버튼을 보이지 않는다)
    /// </summary>
    public static string? WebScheme(int remote, K8sPort? port)
    {
        var hint = ((port?.AppProtocol ?? "") + " " + (port?.Name ?? "")).ToLowerInvariant();
        if (Regex.IsMatch(hint, @"(^|[^a-z])(https|tls)([^a-z]|$)") || hint.Contains("kubernetes.io/wss")) return "https";
        if (Regex.IsMatch(hint, @"(^|[^a-z])(http|http2|h2c|web|ui|dashboard|kubernetes\.io/h2c)([^a-z0-9]|$)")) return "http";
        if (hint.Trim().Length > 0 && Regex.IsMatch(hint, @"(grpc|mysql|postgres|redis|mongo|amqp|dns|kafka)")) return null;
        return remote switch
        {
            443 or 8443 => "https",
            80 or 3000 or 4200 or 5000 or 5601 or 8000 or 8080 or 8081 or 8088 or 8888 or 9000 or 9090 => "http",
            _ => null,
        };
    }

    /// <summary>브라우저 주소 (스킴이 없으면 null)</summary>
    public static string? BrowserUrl(int local, string? scheme) =>
        scheme is "http" or "https" ? string.Create(CultureInfo.InvariantCulture, $"{scheme}://{BindAddress}:{local}/") : null;

    /// <summary>포트 목록 한 줄: "80/TCP  http  → 8080" · "8080/TCP  web  (app)"</summary>
    public static string PortText(K8sKind kind, K8sPort p)
    {
        var parts = new List<string> { string.Create(CultureInfo.InvariantCulture, $"{p.Port}/{p.Protocol}") };
        if (p.Name.Length > 0) parts.Add(p.Name);
        if (p.Detail.Length > 0) parts.Add(kind == K8sKind.Services ? "→ " + p.Detail : "(" + p.Detail + ")");
        return string.Join("  ", parts);
    }

    /// <summary>port-forward 는 TCP 만 된다 — UDP · SCTP 포트는 목록에서 뺀다</summary>
    public static List<K8sPort> TcpPorts(IEnumerable<K8sPort> ports) =>
        ports.Where(p => string.Equals(p.Protocol, "TCP", StringComparison.OrdinalIgnoreCase) || p.Protocol.Length == 0).ToList();

    /// <summary>
    /// 이 PC 에서 로컬 포트가 비었는지: 어느 주소로든 듣고 있는 소켓이 없고(0.0.0.0 으로 듣는 프로그램이 있으면
    /// 127.0.0.1 바인드가 성공해도 둘이 엉킨다) 127.0.0.1 에 실제로 바인드할 수 있을 때만
    /// </summary>
    public static bool IsLocalPortFree(int port)
    {
        if (!ValidPort(port)) return false;
        try
        {
            if (IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port)) return false;
        }
        catch { }
        try
        {
            var l = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
            l.Start();
            l.Stop();
            return true;
        }
        catch (SocketException) { return false; }
    }
}
