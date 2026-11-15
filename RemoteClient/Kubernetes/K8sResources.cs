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
        var statuses = Arr(p, "status", "containerStatuses").ToList();
        var containers = Arr(p, "spec", "containers").Select(c => Str(c, "name")).Where(n => n.Length > 0).ToList();
        var total = containers.Count > 0 ? containers.Count : statuses.Count;
        var ready = statuses.Count(s => s.TryGetProperty("ready", out var r) && r.ValueKind == JsonValueKind.True);
        var restarts = statuses.Sum(s => s.TryGetProperty("restartCount", out var r) && r.TryGetInt32(out var n) ? n : 0);
        var status = PodStatus(p, statuses);
        var healthy = status is "Running" or "Succeeded" or "Completed" && (status != "Running" || ready == total);
        return new K8sItem(K8sKind.Pods, Str(p, "metadata", "name"), Str(p, "metadata", "namespace"), $"{ready}/{total}", status,
            restarts.ToString(CultureInfo.InvariantCulture), Age(p, now), Str(p, "spec", "nodeName"), containers, healthy);
    }

    /// <summary>kubectl get pods 의 STATUS 열과 같은 규칙 (대기·종료 사유가 있으면 그것)</summary>
    internal static string PodStatus(JsonElement p, IReadOnlyList<JsonElement> statuses)
    {
        if (p.TryGetProperty("metadata", out var m) && m.TryGetProperty("deletionTimestamp", out _)) return "Terminating";
        var reason = Str(p, "status", "reason");
        foreach (var s in statuses)
        {
            if (!s.TryGetProperty("state", out var st)) continue;
            if (st.TryGetProperty("waiting", out var w) && Str(w, "reason") is { Length: > 0 } wr) reason = wr;
            else if (st.TryGetProperty("terminated", out var t) && Str(t, "reason") is { Length: > 0 } tr && reason.Length == 0) reason = tr;
        }
        return reason.Length > 0 ? reason : Str(p, "status", "phase") is { Length: > 0 } ph ? ph : "Unknown";
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
