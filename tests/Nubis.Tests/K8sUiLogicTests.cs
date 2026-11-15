using System;
using System.Collections.Generic;
using System.Linq;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>쿠버네티스 화면의 화면 무관 로직: kubectl JSON 파싱 · 명령줄 인용 · 닫은 탭 기록</summary>
[Collection("settings")]
public class K8sUiLogicTests
{
    private static readonly DateTime Now = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);

    private const string Pods = """
        {"items":[
          {"metadata":{"name":"web-1","namespace":"shop","creationTimestamp":"2026-11-12T12:00:00Z"},
           "spec":{"nodeName":"node-a","containers":[{"name":"app"},{"name":"sidecar"}]},
           "status":{"phase":"Running","containerStatuses":[
             {"name":"app","ready":true,"restartCount":2,"state":{"running":{}}},
             {"name":"sidecar","ready":true,"restartCount":1,"state":{"running":{}}}]}},
          {"metadata":{"name":"api-0","namespace":"shop","creationTimestamp":"2026-11-15T11:30:00Z"},
           "spec":{"containers":[{"name":"api"}]},
           "status":{"phase":"Running","containerStatuses":[
             {"name":"api","ready":false,"restartCount":7,"state":{"waiting":{"reason":"CrashLoopBackOff"}}}]}},
          {"metadata":{"name":"old","namespace":"a","creationTimestamp":"2026-11-15T11:59:20Z","deletionTimestamp":"2026-11-15T12:00:00Z"},
           "spec":{"containers":[{"name":"x"}]},"status":{"phase":"Running"}}
        ]}
        """;

    [Fact]
    public void 파드_목록을_kubectl_과_같은_모양으로()
    {
        var items = K8sResources.Parse(K8sKind.Pods, Pods, Now);
        Assert.Equal(new[] { "old", "api-0", "web-1" }, items.Select(i => i.Name));   // 네임스페이스 · 이름 순
        var web = items.Single(i => i.Name == "web-1");
        Assert.Equal("2/2", web.Ready);
        Assert.Equal("Running", web.Status);
        Assert.Equal("3", web.Restarts);
        Assert.Equal("3d", web.Age);
        Assert.Equal(new[] { "app", "sidecar" }, web.Containers);
        Assert.True(web.Healthy);
        var api = items.Single(i => i.Name == "api-0");
        Assert.Equal("CrashLoopBackOff", api.Status);
        Assert.Equal("0/1", api.Ready);
        Assert.Equal("30m", api.Age);
        Assert.False(api.Healthy);
        Assert.Equal("Terminating", items[0].Status);
        Assert.Equal("40s", items[0].Age);
        Assert.Equal("pod/web-1", web.Ref);
    }

    [Fact]
    public void 디플로이먼트_서비스_노드_네임스페이스()
    {
        var dep = K8sResources.Parse(K8sKind.Deployments, """
            {"items":[{"metadata":{"name":"web","namespace":"shop","creationTimestamp":"2026-11-15T09:00:00Z"},
              "spec":{"replicas":3,"template":{"spec":{"containers":[{"image":"nginx:1.27"}]}}},
              "status":{"readyReplicas":2,"availableReplicas":2}}]}
            """, Now).Single();
        Assert.Equal("2/3", dep.Ready);
        Assert.False(dep.Healthy);
        Assert.Equal("nginx:1.27", dep.Extra);

        var svc = K8sResources.Parse(K8sKind.Services, """
            {"items":[{"metadata":{"name":"web","namespace":"shop"},
              "spec":{"type":"LoadBalancer","clusterIP":"10.0.0.5","ports":[{"port":80,"nodePort":30080,"protocol":"TCP"}]},
              "status":{"loadBalancer":{"ingress":[{"ip":"203.0.113.9"}]}}}]}
            """, Now).Single();
        Assert.Equal("LoadBalancer", svc.Ready);
        Assert.Equal("80:30080/TCP", svc.Restarts);
        Assert.Equal("203.0.113.9", svc.Extra);

        var node = K8sResources.Parse(K8sKind.Nodes, """
            {"items":[{"metadata":{"name":"n1","labels":{"node-role.kubernetes.io/control-plane":""}},
              "spec":{"unschedulable":true},
              "status":{"conditions":[{"type":"Ready","status":"True"}],"nodeInfo":{"kubeletVersion":"v1.31.2"}}}]}
            """, Now).Single();
        Assert.Equal("control-plane", node.Ready);
        Assert.Equal("Ready,SchedulingDisabled", node.Status);
        Assert.Equal("v1.31.2", node.Restarts);

        Assert.Equal(new[] { "default", "kube-system" },
            K8sResources.ParseNamespaces("""{"items":[{"metadata":{"name":"kube-system"}},{"metadata":{"name":"default"}}]}"""));
        Assert.Contains("--all-namespaces", K8sResources.ListArgs(K8sKind.Pods, null));
        Assert.DoesNotContain("-n", K8sResources.ListArgs(K8sKind.Nodes, "shop"));
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("", "\"\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"C:\dir with space\", "\"C:\\dir with space\\\\\"")]
    public void 명령줄_인자_인용(string arg, string expected)
    {
        Assert.Equal(expected, K8sTerminal.Quote(arg));
    }

    [Fact]
    public void 파드_셸과_로그_인자()
    {
        var c = new K8sCluster { Name = "prod", ContextName = "prod-ctx" };
        var pod = new K8sItem(K8sKind.Pods, "web-1", "shop", "1/1", "Running", "0", "1d", "", new[] { "app" }, true);
        var shell = K8sTerminal.ShellArgs(c, pod, "app");
        Assert.Equal(new[] { "--context", "prod-ctx", "exec", "-it", "-n", "shop", "web-1", "-c", "app", "--" }, shell.Take(10));
        var logs = K8sTerminal.LogsArgs(c, pod, null, previous: true);
        Assert.Contains("--previous", logs);
        Assert.Contains("-f", logs);
        Assert.Contains("\"command -v bash >/dev/null 2>&1 && exec bash || exec sh\"", K8sTerminal.CommandLine("kubectl.exe", shell));
    }

    [Fact]
    public void 닫은_클러스터_탭은_클러스터가_있을_때만_다시_연다()
    {
        var h = new ClosedTabHistory();
        var gone = Guid.NewGuid();
        var alive = Guid.NewGuid();
        h.Push(new ClosedTabHistory.Entry(null, null, alive));
        h.Push(new ClosedTabHistory.Entry(null, null, gone));
        Func<Guid, bool> hosts = _ => false;
        Func<Guid, bool> clusters = id => id == alive;
        Assert.True(h.HasAny(hosts, clusters));
        Assert.Equal(alive, h.Pop(hosts, clusters)?.K8sClusterId);
        Assert.Null(h.Pop(hosts, clusters));
        // 클러스터 확인 함수가 없으면(예전 호출) 클러스터 항목은 건너뛴다 — PowerShell 탭으로 잘못 열지 않게
        h.Push(new ClosedTabHistory.Entry(null, null, alive));
        Assert.False(h.HasAny(hosts));
    }

    [Fact]
    public void 서버_버전_읽기()
    {
        Assert.Equal("v1.31.4-gke.100", MainWindow.K8sVersion("""{"clientVersion":{"gitVersion":"v1.32"},"serverVersion":{"gitVersion":"v1.31.4-gke.100"}}"""));
        Assert.Null(MainWindow.K8sVersion("error: unauthorized"));
    }
}
