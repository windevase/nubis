using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Nubis;

/// <summary>리소스 패널의 종류</summary>
public enum K8sKind { Pods, Deployments, Services, Nodes }

/// <summary>리소스 패널의 행 하나 (kubectl get -o json 을 줄인 것)</summary>
public sealed record K8sItem(K8sKind Kind, string Name, string Namespace, string Ready, string Status, string Restarts, string Age,
    string Extra, IReadOnlyList<string> Containers, bool Healthy)
{
    /// <summary>kubectl 에 넘길 "종류/이름" (describe · YAML 등)</summary>
    public string Ref => Kind switch
    {
        K8sKind.Pods => "pod/" + Name,
        K8sKind.Deployments => "deployment/" + Name,
        K8sKind.Services => "service/" + Name,
        _ => "node/" + Name,
    };

    public string ToolTipText => Namespace.Length > 0 ? $"{Namespace}/{Name}\n{Status}  {Extra}" : $"{Name}\n{Status}  {Extra}";
}

/// <summary>kubectl JSON 출력 → 패널 행 (화면 무관, K8sResourcesTests)</summary>
public static class K8sResources
{
    public static string KindArg(K8sKind kind) => kind switch
    {
        K8sKind.Pods => "pods",
        K8sKind.Deployments => "deployments",
        K8sKind.Services => "services",
        _ => "nodes",
    };

    public static bool Namespaced(K8sKind kind) => kind != K8sKind.Nodes;

    /// <summary>목록 kubectl 인자 (ns = null 이면 모든 네임스페이스)</summary>
    public static List<string> ListArgs(K8sKind kind, string? ns)
    {
        var a = new List<string> { "get", KindArg(kind), "-o", "json", "--request-timeout=20s" };
        if (Namespaced(kind)) { if (ns == null) a.Add("--all-namespaces"); else { a.Add("-n"); a.Add(ns); } }
        return a;
    }

    public static List<string> ParseNamespaces(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Items(doc.RootElement).Select(i => Str(i, "metadata", "name")).Where(n => n.Length > 0)
            .OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    public static List<K8sItem> Parse(K8sKind kind, string json, DateTime nowUtc)
    {
        using var doc = JsonDocument.Parse(json);
        var list = Items(doc.RootElement).Select(i => kind switch
        {
            K8sKind.Pods => Pod(i, nowUtc),
            K8sKind.Deployments => Deployment(i, nowUtc),
            K8sKind.Services => Service(i, nowUtc),
            _ => Node(i, nowUtc),
        }).ToList();
        return list.OrderBy(x => x.Namespace, StringComparer.Ordinal).ThenBy(x => x.Name, StringComparer.Ordinal).ToList();
    }

    private static K8sItem Pod(JsonElement p, DateTime now)
    {
        var containers = Arr(p, "spec", "containers").Select(c => Str(c, "name")).Where(n => n.Length > 0).ToList();
        var (status, ready, total, restarts) = PodStatus(p);
        var healthy = status is "Running" or "Succeeded" or "Completed" && (status != "Running" || ready == total);
        return new K8sItem(K8sKind.Pods, Str(p, "metadata", "name"), Str(p, "metadata", "namespace"), $"{ready}/{total}", status,
            restarts.ToString(CultureInfo.InvariantCulture), Age(p, now), Str(p, "spec", "nodeName"), containers, healthy);
    }

    /// <summary>
    /// kubectl get pods 의 STATUS · READY · RESTARTS 열과 같은 규칙 (kubernetes/kubectl printers.go printPod, 1.27+):
    /// phase → status.reason → 스케줄 대기(SchedulingGated) → 초기화 컨테이너 → 컨테이너 대기·종료 사유 → 삭제 중(Terminating).
    /// 네이티브 사이드카(restartPolicy: Always 인 initContainer)는 READY 분모·재시작 수에 들어가고, 시작했으며 Ready 면 ready 로 센다.
    /// 종료(Completed)한 컨테이너가 있어도 실행 중이며 Ready 인 컨테이너가 있으면 Ready 조건에 따라 Running / NotReady
    /// </summary>
    internal static (string Status, int Ready, int Total, int Restarts) PodStatus(JsonElement p)
    {
        var phase = Str(p, "status", "phase");
        var reason = phase;
        if (Str(p, "status", "reason") is { Length: > 0 } podReason) reason = podReason;
        var conditions = Arr(p, "status", "conditions").ToList();
        if (conditions.Any(c => Str(c, "type") == "PodScheduled" && Str(c, "reason") == "SchedulingGated")) reason = "SchedulingGated";

        var specCount = Arr(p, "spec", "containers").Count();
        var statuses = Arr(p, "status", "containerStatuses").ToList();
        var total = specCount > 0 ? specCount : statuses.Count;   // spec 이 없는 출력이면 상태 수로
        var ready = 0;
        var restarts = 0;
        var sidecarRestarts = 0;

        // 초기화 컨테이너 (Init:1/2 · Init:CrashLoopBackOff ...). 계속 도는 사이드카(restartPolicy Always)는 시작했으면 건너뛴다
        var initSpecs = Arr(p, "spec", "initContainers").ToList();
        var sidecars = initSpecs.Where(c => Str(c, "restartPolicy") == "Always").Select(c => Str(c, "name")).ToHashSet(StringComparer.Ordinal);
        total += initSpecs.Count(c => Str(c, "restartPolicy") == "Always");
        var initializing = false;
        var i = 0;
        foreach (var s in Arr(p, "status", "initContainerStatuses"))
        {
            var idx = i++;
            var count = Int(s, "restartCount") ?? 0;
            var isSidecar = sidecars.Contains(Str(s, "name"));
            restarts += count;
            if (isSidecar) sidecarRestarts += count;
            var st = s.TryGetProperty("state", out var v) ? v : default;
            var terminated = st.ValueKind == JsonValueKind.Object && st.TryGetProperty("terminated", out var t) ? t : default;
            var waiting = st.ValueKind == JsonValueKind.Object && st.TryGetProperty("waiting", out var w) ? w : default;
            if (terminated.ValueKind == JsonValueKind.Object && Int(terminated, "exitCode") is 0) continue;
            if (isSidecar && IsTrue(s, "started"))
            {
                if (IsTrue(s, "ready")) ready++;
                continue;
            }
            if (terminated.ValueKind == JsonValueKind.Object)
                reason = Str(terminated, "reason") is { Length: > 0 } tr ? "Init:" + tr
                    : Int(terminated, "signal") is { } sig and not 0 ? $"Init:Signal:{sig}"
                    : $"Init:ExitCode:{Int(terminated, "exitCode") ?? 0}";
            else if (waiting.ValueKind == JsonValueKind.Object && Str(waiting, "reason") is { Length: > 0 } wr && wr != "PodInitializing")
                reason = "Init:" + wr;
            else
                reason = $"Init:{idx}/{initSpecs.Count}";
            initializing = true;
            break;
        }

        // Initialized 조건이 True 면(초기화가 끝난 뒤 사이드카가 다시 시작하는 중 등) 일반 컨테이너 사유로 덮는다
        if (!initializing || conditions.Any(c => Str(c, "type") == "Initialized" && Str(c, "status") == "True"))
        {
            restarts = sidecarRestarts;
            var hasRunning = false;
            // kubectl 처럼 뒤에서 앞으로 — 앞쪽 컨테이너의 사유가 마지막에 남는다
            for (var k = statuses.Count - 1; k >= 0; k--)
            {
                var s = statuses[k];
                restarts += Int(s, "restartCount") ?? 0;
                var st = s.TryGetProperty("state", out var v) ? v : default;
                var obj = st.ValueKind == JsonValueKind.Object;
                if (obj && st.TryGetProperty("waiting", out var w) && Str(w, "reason") is { Length: > 0 } wr) reason = wr;
                else if (obj && st.TryGetProperty("terminated", out var t) && Str(t, "reason") is { Length: > 0 } tr) reason = tr;
                else if (obj && st.TryGetProperty("terminated", out var t2))
                    reason = Int(t2, "signal") is { } sig and not 0 ? $"Signal:{sig}" : $"ExitCode:{Int(t2, "exitCode") ?? 0}";
                else if (obj && st.TryGetProperty("running", out _) && IsTrue(s, "ready"))
                {
                    hasRunning = true;
                    ready++;
                }
            }
            if (reason == "Completed" && hasRunning)
                reason = conditions.Any(c => Str(c, "type") == "Ready" && Str(c, "status") == "True") ? "Running" : "NotReady";
        }

        // 삭제 중: 노드 연결이 끊긴 파드는 Unknown, 이미 끝난(Succeeded/Failed) 파드는 원래 상태를 둔다
        if (p.TryGetProperty("metadata", out var m) && m.TryGetProperty("deletionTimestamp", out var del) && del.ValueKind == JsonValueKind.String)
        {
            if (Str(p, "status", "reason") == "NodeLost") reason = "Unknown";
            else if (phase is not ("Succeeded" or "Failed")) reason = "Terminating";
        }
        return (reason.Length > 0 ? reason : "Unknown", ready, total, restarts);
    }

    private static bool IsTrue(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>두 행의 보이는 내용이 같은가 (자동 새로고침에서 바뀐 행만 고치려고)</summary>
    public static bool SameItem(K8sItem a, K8sItem b) =>
        a.Kind == b.Kind && a.Name == b.Name && a.Namespace == b.Namespace && a.Ready == b.Ready && a.Status == b.Status
        && a.Restarts == b.Restarts && a.Age == b.Age && a.Extra == b.Extra && a.Healthy == b.Healthy
        && a.Containers.SequenceEqual(b.Containers, StringComparer.Ordinal);

    /// <summary>
    /// 다음 자동 새로고침까지 기다릴 시간: 기본 5초, kubectl 이 오래 걸린 큰 목록이면 걸린 시간의 3배 (최대 60초)
    /// — 큰 클러스터에서 끝나자마자 다시 받느라 CPU 를 계속 쓰지 않게
    /// </summary>
    public static TimeSpan NextRefreshInterval(TimeSpan elapsed)
    {
        var baseInterval = TimeSpan.FromSeconds(5);
        var scaled = TimeSpan.FromTicks(elapsed.Ticks * 3);
        if (scaled <= baseInterval) return baseInterval;
        return scaled >= TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : scaled;
    }

    /// <summary>kubectl 실패를 상태 줄 한 줄로 (시간 초과·빈 메시지에도 문구가 있게)</summary>
    public static string ErrorText(Kubectl.Result r)
    {
        if (r.TimedOut) return L.T("응답이 없습니다 (시간 초과).");
        var line = FirstLine(r.Error.Length > 0 ? r.Error : r.Output);
        return line.Length > 0 ? line : L.F("kubectl 이 실패했습니다 (종료 코드 {0}).", r.ExitCode);
    }

    /// <summary>첫 번째 내용 있는 줄 (300자까지)</summary>
    public static string FirstLine(string s)
    {
        var line = s.Trim().Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
        return line.Length > 300 ? line[..300] + "…" : line;
    }

    private static K8sItem Deployment(JsonElement d, DateTime now)
    {
        var want = Int(d, "spec", "replicas") ?? 1;
        var ready = Int(d, "status", "readyReplicas") ?? 0;
        var avail = Int(d, "status", "availableReplicas") ?? 0;
        var containers = Arr(d, "spec", "template", "spec", "containers").Select(c => Str(c, "image")).Where(n => n.Length > 0).ToList();
        return new K8sItem(K8sKind.Deployments, Str(d, "metadata", "name"), Str(d, "metadata", "namespace"), $"{ready}/{want}",
            ready >= want ? "Available" : "Progressing", avail.ToString(CultureInfo.InvariantCulture), Age(d, now),
            string.Join(", ", containers), Array.Empty<string>(), ready >= want);
    }

    private static K8sItem Service(JsonElement s, DateTime now)
    {
        var type = Str(s, "spec", "type");
        var ports = string.Join(",", Arr(s, "spec", "ports").Select(p =>
        {
            var port = Int(p, "port")?.ToString(CultureInfo.InvariantCulture) ?? "";
            var node = Int(p, "nodePort") is { } np ? ":" + np.ToString(CultureInfo.InvariantCulture) : "";
            return $"{port}{node}/{Str(p, "protocol")}";
        }));
        var external = Arr(s, "status", "loadBalancer", "ingress").Select(i => Str(i, "ip") is { Length: > 0 } ip ? ip : Str(i, "hostname"))
            .FirstOrDefault(x => x.Length > 0) ?? "";
        return new K8sItem(K8sKind.Services, Str(s, "metadata", "name"), Str(s, "metadata", "namespace"), type, Str(s, "spec", "clusterIP"),
            ports, Age(s, now), external, Array.Empty<string>(), true);
    }

    private static K8sItem Node(JsonElement n, DateTime now)
    {
        var ready = Arr(n, "status", "conditions").FirstOrDefault(c => Str(c, "type") == "Ready");
        var isReady = ready.ValueKind == JsonValueKind.Object && Str(ready, "status") == "True";
        var roles = n.TryGetProperty("metadata", out var m) && m.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Object
            ? string.Join(",", labels.EnumerateObject().Select(l => l.Name).Where(l => l.StartsWith("node-role.kubernetes.io/", StringComparison.Ordinal))
                .Select(l => l["node-role.kubernetes.io/".Length..]))
            : "";
        var status = (isReady ? "Ready" : "NotReady") + (n.TryGetProperty("spec", out var sp) && sp.TryGetProperty("unschedulable", out var u) && u.ValueKind == JsonValueKind.True ? ",SchedulingDisabled" : "");
        return new K8sItem(K8sKind.Nodes, Str(n, "metadata", "name"), "", roles.Length > 0 ? roles : "<none>", status,
            Str(n, "status", "nodeInfo", "kubeletVersion"), Age(n, now), Str(n, "status", "nodeInfo", "osImage"), Array.Empty<string>(), isReady);
    }

    /// <summary>kubectl 과 같은 짧은 나이 (45s · 12m · 5h · 3d · 2y)</summary>
    internal static string Age(JsonElement e, DateTime now)
    {
        if (!DateTime.TryParse(Str(e, "metadata", "creationTimestamp"), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)) return "";
        var d = now - t;
        if (d.TotalSeconds < 0) return "0s";
        if (d.TotalMinutes < 2) return $"{(int)d.TotalSeconds}s";
        if (d.TotalHours < 2) return $"{(int)d.TotalMinutes}m";
        if (d.TotalDays < 2) return $"{(int)d.TotalHours}h";
        if (d.TotalDays < 730) return $"{(int)d.TotalDays}d";
        return $"{(int)(d.TotalDays / 365)}y";
    }

    // ----- JSON 도우미 -----

    private static IEnumerable<JsonElement> Items(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().ToList()
            : Enumerable.Empty<JsonElement>();

    private static bool TryPath(JsonElement e, string[] path, out JsonElement v)
    {
        v = e;
        foreach (var p in path)
        {
            if (v.ValueKind != JsonValueKind.Object || !v.TryGetProperty(p, out v)) return false;
        }
        return true;
    }

    private static string Str(JsonElement e, params string[] path) =>
        TryPath(e, path, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int? Int(JsonElement e, params string[] path) =>
        TryPath(e, path, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private static IEnumerable<JsonElement> Arr(JsonElement e, params string[] path) =>
        TryPath(e, path, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().ToList() : Enumerable.Empty<JsonElement>();
}
