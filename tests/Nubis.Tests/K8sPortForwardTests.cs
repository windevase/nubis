using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>쿠버네티스 포트 포워딩의 화면 무관 판단: 포트 파싱 · 인자 조립 · 로컬 포트 제안 · kubectl 출력 해석 · 브라우저 판단</summary>
[Collection("settings")]
public class K8sPortForwardTests
{
    private static readonly DateTime Now = new(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);

    private static K8sItem One(K8sKind kind, string json) => K8sResources.Parse(kind, "{\"items\":[" + json + "]}", Now).Single();

    // ---------------------------------------------------------------- 포트 파싱

    [Fact]
    public void 파드의_컨테이너_포트를_컨테이너_이름과_함께_읽는다()
    {
        var p = One(K8sKind.Pods, """
            {"metadata":{"name":"web-1","namespace":"shop"},
             "spec":{"containers":[
               {"name":"app","ports":[{"containerPort":8080,"name":"http"},{"containerPort":9090,"name":"metrics","protocol":"TCP"}]},
               {"name":"dns","ports":[{"containerPort":53,"protocol":"UDP"}]},
               {"name":"sidecar","ports":[{"containerPort":8080}]},
               {"name":"bad","ports":[{"containerPort":0},{"containerPort":70000},{"name":"noport"}]}]},
             "status":{"phase":"Running"}}
            """);
        Assert.Equal(new[]
        {
            new K8sPort(8080, "http", "TCP", "app"),
            new K8sPort(9090, "metrics", "TCP", "app"),
            new K8sPort(53, "", "UDP", "dns"),
        }, p.Ports);
        Assert.Equal(new[] { 8080, 9090 }, K8sPortForward.TcpPorts(p.Ports).Select(x => x.Port));
    }

    [Fact]
    public void 서비스_포트는_대상_포트와_appProtocol_을_함께_읽는다()
    {
        var s = One(K8sKind.Services, """
            {"metadata":{"name":"web","namespace":"shop"},
             "spec":{"type":"ClusterIP","clusterIP":"10.0.0.1","ports":[
               {"name":"http","port":80,"targetPort":8080,"protocol":"TCP"},
               {"name":"grpc","port":9000,"targetPort":"grpc-port","appProtocol":"kubernetes.io/h2c"},
               {"port":443}]}}
            """);
        Assert.Equal(new[]
        {
            new K8sPort(80, "http", "TCP", "8080"),
            new K8sPort(9000, "grpc", "TCP", "grpc-port", "kubernetes.io/h2c"),
            new K8sPort(443, "", "TCP", ""),
        }, s.Ports);
        Assert.Equal("80/TCP  http  → 8080", K8sPortForward.PortText(K8sKind.Services, s.Ports[0]));
        Assert.Equal("443/TCP", K8sPortForward.PortText(K8sKind.Services, s.Ports[2]));
    }

    [Fact]
    public void 디플로이먼트는_템플릿의_컨테이너_포트()
    {
        var d = One(K8sKind.Deployments, """
            {"metadata":{"name":"api","namespace":"shop"},"spec":{"replicas":1,"template":{"spec":{"containers":[
              {"name":"api","image":"api:1","ports":[{"containerPort":3000,"name":"web"}]}]}}},"status":{"readyReplicas":1}}
            """);
        Assert.Equal(new[] { new K8sPort(3000, "web", "TCP", "api") }, d.Ports);
        Assert.Equal("3000/TCP  web  (api)", K8sPortForward.PortText(K8sKind.Deployments, d.Ports[0]));
    }

    [Fact]
    public void 포트가_바뀌면_다른_행으로_본다()
    {
        var a = new K8sItem(K8sKind.Pods, "web", "shop", "1/1", "Running", "0", "1d", "", new[] { "app" }, true) { Ports = new[] { new K8sPort(80, "", "TCP", "app") } };
        Assert.True(K8sResources.SameItem(a, a with { Ports = new[] { new K8sPort(80, "", "TCP", "app") } }));
        Assert.False(K8sResources.SameItem(a, a with { Ports = new[] { new K8sPort(81, "", "TCP", "app") } }));
    }

    // ---------------------------------------------------------------- 인자 · 문구

    [Fact]
    public void 인자는_127_0_0_1_에만_바인드하고_대상과_포트를_붙인다()
    {
        Assert.Equal(new[] { "port-forward", "--address", "127.0.0.1", "-n", "shop", "svc/web", "8080:80" },
            K8sPortForward.Args("shop", K8sKind.Services, "web", 8080, 80));
        Assert.Equal(new[] { "port-forward", "--address", "127.0.0.1", "-n", "kube-system", "pod/coredns-1", "10053:53" },
            K8sPortForward.Args("kube-system", K8sKind.Pods, "coredns-1", 10053, 53));
        Assert.Equal("deploy/api", K8sPortForward.TargetRef(K8sKind.Deployments, "api"));
        // 만든 인자는 kubectl 인자 검사도 통과한다
        Assert.Null(KubectlArgs.Validate(K8sPortForward.Args("shop", K8sKind.Services, "web", 8080, 80)));
    }

    [Theory]
    [InlineData("Shop", "web", 80, 80)]          // 대문자
    [InlineData("shop", "--address=0.0.0.0", 80, 80)]
    [InlineData("shop", "-n", 80, 80)]
    [InlineData("shop", "web x", 80, 80)]
    [InlineData("", "web", 80, 80)]
    [InlineData("shop", "web", 0, 80)]
    [InlineData("shop", "web", 80, 65536)]
    public void 이름이나_포트가_규칙에_맞지_않으면_인자를_만들지_않는다(string ns, string name, int local, int remote) =>
        Assert.Throws<ArgumentException>(() => K8sPortForward.Args(ns, K8sKind.Services, name, local, remote));

    [Fact]
    public void 노드는_포워딩_대상이_아니다()
    {
        Assert.Throws<ArgumentException>(() => K8sPortForward.Args("shop", K8sKind.Nodes, "node-a", 80, 80));
        Assert.False(K8sPortForward.CanForward(new K8sItem(K8sKind.Nodes, "node-a", "", "", "", "", "", "", Array.Empty<string>(), true)));
        Assert.True(K8sPortForward.CanForward(new K8sItem(K8sKind.Services, "web", "shop", "", "", "", "", "", Array.Empty<string>(), true)));
        Assert.False(K8sPortForward.CanForward(new K8sItem(K8sKind.Pods, "web", "", "", "", "", "", "", Array.Empty<string>(), true)));
    }

    [Fact]
    public void 목록_문구는_로컬_주소_화살표_대상_포트()
    {
        Assert.Equal("localhost:8080 → svc/web:80", K8sPortForward.Label(8080, "svc/web", 80));
        Assert.Equal("127.0.0.1:8080", K8sPortForward.LocalAddress(8080));
    }

    [Theory]
    [InlineData("pod/web-1", K8sKind.Pods, "web-1")]
    [InlineData("web-1", K8sKind.Pods, "web-1")]
    [InlineData("po/web-1", K8sKind.Pods, "web-1")]
    [InlineData("svc/web", K8sKind.Services, "web")]
    [InlineData("service/web", K8sKind.Services, "web")]
    [InlineData("Services/web", K8sKind.Services, "web")]
    [InlineData("deploy/api", K8sKind.Deployments, "api")]
    [InlineData("deployment/api", K8sKind.Deployments, "api")]
    public void 대상_문자열을_종류와_이름으로(string spec, K8sKind kind, string name) =>
        Assert.Equal((kind, name), K8sPortForward.ParseTarget(spec));

    [Theory]
    [InlineData("")]
    [InlineData("node/a")]
    [InlineData("svc/")]
    [InlineData("svc/Web")]
    [InlineData("svc/web --address=0.0.0.0")]
    [InlineData("svc/-n")]
    public void 모르는_대상은_null(string spec) => Assert.Null(K8sPortForward.ParseTarget(spec));

    [Theory]
    [InlineData("8080", 8080)]
    [InlineData(" 80 ", 80)]
    [InlineData("65535", 65535)]
    [InlineData("0", null)]
    [InlineData("65536", null)]
    [InlineData("-1", null)]
    [InlineData("+80", null)]
    [InlineData("80a", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void 포트_문자열(string? s, int? want) => Assert.Equal(want, K8sPortForward.ParsePort(s));

    // ---------------------------------------------------------------- 로컬 포트 제안

    [Fact]
    public void 같은_번호가_비었으면_같은_번호()
    {
        Assert.Equal(8080, K8sPortForward.SuggestLocalPort(8080, _ => true));
        Assert.Equal(80, K8sPortForward.SuggestLocalPort(80, _ => true));
    }

    [Fact]
    public void 쓰이면_그_다음_빈_번호()
    {
        var used = new HashSet<int> { 8080, 8081 };
        Assert.Equal(8082, K8sPortForward.SuggestLocalPort(8080, p => !used.Contains(p)));
    }

    [Fact]
    public void 근처가_모두_쓰이면_10000_위의_끝자리_맞춘_번호()
    {
        Assert.Equal(10080, K8sPortForward.SuggestLocalPort(80, p => p >= 10000));
        Assert.Equal(20080, K8sPortForward.SuggestLocalPort(80, p => p >= 20000));
        Assert.Null(K8sPortForward.SuggestLocalPort(80, _ => false));
        Assert.Null(K8sPortForward.SuggestLocalPort(0, _ => true));
        // 65535 근처: 범위를 넘지 않는다
        Assert.Equal(65535, K8sPortForward.SuggestLocalPort(65535, _ => true));
        var got = K8sPortForward.SuggestLocalPort(65535, p => p != 65535);
        Assert.True(got is >= 1 and <= 65535);
    }

    [Fact]
    public void 실제로_쓰이는_로컬_포트는_비지_않았다고_본다()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try
        {
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            Assert.False(K8sPortForward.IsLocalPortFree(port));
            Assert.NotEqual(port, K8sPortForward.SuggestLocalPort(port, K8sPortForward.IsLocalPortFree));
        }
        finally { l.Stop(); }
        Assert.False(K8sPortForward.IsLocalPortFree(0));
        Assert.False(K8sPortForward.IsLocalPortFree(70000));
    }

    [Fact]
    public void 앱_안에서_잡은_포트는_다른_포워딩이_못_쓰고_제안에서도_빠진다()
    {
        // 다른 테스트·프로그램과 겹치지 않게 지금 빈 포트를 하나 받아 쓴다
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        object a = new(), b = new();
        Assert.True(K8sPortForward.TryReserve(port, a));
        try
        {
            Assert.True(K8sPortForward.TryReserve(port, a));    // 같은 주인은 다시 잡아도 된다 (다시 시작)
            Assert.False(K8sPortForward.TryReserve(port, b));   // 시작 중(아직 소켓이 없음)이어도 다른 포워딩은 못 잡는다
            Assert.False(K8sPortForward.IsPortAvailable(port));
            Assert.False(K8sPortForward.PortProbe()(port));
            Assert.NotEqual(port, K8sPortForward.SuggestLocalPort(port, K8sPortForward.PortProbe()));
            K8sPortForward.Release(port, b);                    // 남의 예약은 놓지 않는다
            Assert.True(K8sPortForward.IsReserved(port));
        }
        finally { K8sPortForward.Release(port, a); }
        Assert.False(K8sPortForward.IsReserved(port));
        Assert.True(K8sPortForward.TryReserve(port, b));
        K8sPortForward.Release(port, b);
    }

    [Fact]
    public void 포트_검사_함수는_듣는_소켓을_비지_않았다고_본다()
    {
        var l = new TcpListener(IPAddress.Any, 0);
        l.Start();
        try
        {
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            var probe = K8sPortForward.PortProbe();
            Assert.False(probe(port));    // 0.0.0.0 으로 듣는 프로그램이 있으면 127.0.0.1 바인드가 돼도 쓰지 않는다
            Assert.False(probe(0));
            Assert.False(probe(70000));
        }
        finally { l.Stop(); }
    }

    // ---------------------------------------------------------------- kubectl 출력

    [Theory]
    [InlineData("Forwarding from 127.0.0.1:8080 -> 80", 8080, 80)]
    [InlineData("Forwarding from [::1]:10053 -> 53", 10053, 53)]
    [InlineData("  Forwarding from 127.0.0.1:1 -> 65535  ", 1, 65535)]
    public void 포워딩_시작_줄(string line, int local, int remote) =>
        Assert.Equal((local, remote), K8sPortForward.ParseForwarding(line));

    [Theory]
    [InlineData("Handling connection for 8080")]
    [InlineData("Forwarding from 0.0.0.0:8080 -> 80")]
    [InlineData("Forwarding from 127.0.0.1:99999 -> 80")]
    [InlineData("error: unable to listen on any of the requested ports")]
    public void 다른_줄은_시작_줄이_아니다(string line) => Assert.Null(K8sPortForward.ParseForwarding(line));

    [Fact]
    public void 연결마다_찍히는_줄은_오류가_아니다()
    {
        Assert.True(K8sPortForward.IsNoise("Handling connection for 8080"));
        Assert.True(K8sPortForward.IsNoise("Forwarding from 127.0.0.1:8080 -> 80"));
        Assert.True(K8sPortForward.IsNoise("   "));
        Assert.False(K8sPortForward.IsNoise("error: lost connection to pod"));
    }

    [Fact]
    public void 오류_줄의_klog_머리와_error_접두어를_뗀다()
    {
        Assert.Equal("an error occurred forwarding 8080 -> 80: connection refused",
            K8sPortForward.CleanError("E1119 10:00:00.123456   12345 portforward.go:413] an error occurred forwarding 8080 -> 80: connection refused"));
        Assert.Equal("lost connection to pod", K8sPortForward.CleanError("error: lost connection to pod"));
        Assert.Equal(301, K8sPortForward.CleanError(new string('x', 500)).Length);
    }

    // ---------------------------------------------------------------- 브라우저

    [Theory]
    [InlineData(80, null, null, "http")]
    [InlineData(8080, null, null, "http")]
    [InlineData(443, null, null, "https")]
    [InlineData(5432, null, null, null)]
    [InlineData(6379, null, null, null)]
    [InlineData(1234, "http", null, "http")]
    [InlineData(1234, "https-web", null, "https")]
    [InlineData(1234, "web", null, "http")]
    [InlineData(1234, "", "kubernetes.io/h2c", "http")]
    [InlineData(8080, "grpc", null, null)]
    [InlineData(80, "postgres", null, null)]
    [InlineData(9090, "metrics", null, "http")]
    public void 웹_포트로_보이면_브라우저_스킴(int remote, string? name, string? app, string? want)
    {
        var port = name == null && app == null ? null : new K8sPort(remote, name ?? "", "TCP", "", app ?? "");
        Assert.Equal(want, K8sPortForward.WebScheme(remote, port));
    }

    [Fact]
    public void 브라우저_주소는_127_0_0_1()
    {
        Assert.Equal("http://127.0.0.1:8080/", K8sPortForward.BrowserUrl(8080, "http"));
        Assert.Equal("https://127.0.0.1:8443/", K8sPortForward.BrowserUrl(8443, "https"));
        Assert.Null(K8sPortForward.BrowserUrl(8080, null));
        Assert.Null(K8sPortForward.BrowserUrl(8080, "file"));
    }

    // ---------------------------------------------------------------- MCP

    [Fact]
    public void run_kubectl_의_port_forward_거부는_새_도구를_안내한다()
    {
        var why = MainWindow.KubectlInteractiveReason(new[] { "port-forward", "svc/web", "8080:80" });
        Assert.NotNull(why);
        Assert.Contains("k8s_port_forward", why);
    }
}
