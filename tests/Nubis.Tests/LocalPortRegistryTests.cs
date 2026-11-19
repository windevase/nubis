using System.Net;
using System.Net.Sockets;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>SSH 포트 포워딩과 쿠버네티스 포트 포워딩이 서로의 로컬 포트를 아는지 (LocalPortRegistry)</summary>
public class LocalPortRegistryTests
{
    private static PortForwardRule R(int local, bool on = true) =>
        new() { LocalPort = local, TargetHost = "db", TargetPort = 5432, Enabled = on };

    /// <summary>다른 테스트·프로그램과 겹치지 않게 지금 비어 있는 포트 하나</summary>
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public void 쿠버네티스가_잡은_포트는_SSH_임시_포워딩이_거부한다()
    {
        var port = FreePort();
        var k8s = new object();
        Assert.True(K8sPortForward.TryReserve(port, k8s));
        try
        {
            var set = new SshPortForwardSet();
            var (f, err) = set.AddTemporary(R(port));
            Assert.Null(f);
            Assert.Equal(SshPortForwardSet.ReservedError(port), err);
        }
        finally { K8sPortForward.Release(port, k8s); }
    }

    [Fact]
    public void 쿠버네티스가_잡은_포트의_저장된_규칙은_열지_않고_실패로_둔다()
    {
        var port = FreePort();
        var k8s = new object();
        Assert.True(K8sPortForward.TryReserve(port, k8s));
        try
        {
            var set = new SshPortForwardSet(new[] { R(port) });
            using var client = new Renci.SshNet.SshClient("127.0.0.1", "u", "p");
            set.Attach(client);
            Assert.Equal(ForwardState.Failed, set.Items[0].State);
            Assert.Equal(SshPortForwardSet.ReservedError(port), set.Items[0].Error);
            set.Close();
        }
        finally { K8sPortForward.Release(port, k8s); }
    }

    [Fact]
    public void SSH_탭의_켜진_규칙은_연결_전에도_쿠버네티스_빈_포트_제안에서_빠진다()
    {
        var on = FreePort();
        var off = FreePort();
        var set = new SshPortForwardSet(new[] { R(on), R(off, on: false) });
        try
        {
            Assert.True(LocalPortRegistry.IsClaimed(on));       // 연결 대기 중 (소켓 없음)
            Assert.False(LocalPortRegistry.IsClaimed(off));     // 꺼 둔 규칙은 쓰지 않는다
            Assert.False(K8sPortForward.PortProbe()(on));
            Assert.NotEqual(on, K8sPortForward.SuggestLocalPort(on, K8sPortForward.PortProbe()));

            // 이 탭에서 끄면 다시 고를 수 있다
            Assert.Null(set.SetEnabled(set.Items[0], false));
            Assert.False(LocalPortRegistry.IsClaimed(on));
            // 켜면 다시 빠진다
            Assert.Null(set.SetEnabled(set.Items[0], true));
            Assert.True(LocalPortRegistry.IsClaimed(on));
        }
        finally { set.Close(); }
        // 탭을 닫으면 빠진다 (닫은 뒤 저장된 규칙을 다시 받아도 되살아나지 않음)
        Assert.False(LocalPortRegistry.IsClaimed(on));
        set.SetSavedRules(new[] { R(on) });
        Assert.False(LocalPortRegistry.IsClaimed(on));
    }

    [Fact]
    public void 임시_포워딩도_희망_포트에_들고_지우면_빠진다()
    {
        var port = FreePort();
        var set = new SshPortForwardSet();
        try
        {
            var (f, err) = set.AddTemporary(R(port));
            Assert.Null(err);
            Assert.True(LocalPortRegistry.IsClaimed(port));
            Assert.False(LocalPortRegistry.IsClaimed(port, except: set));   // 자기 것은 빼고 볼 수 있다
            Assert.True(set.Remove(f!));
            Assert.False(LocalPortRegistry.IsClaimed(port));
        }
        finally { set.Close(); }
    }

    [Fact]
    public void 닫은_묶음은_다시_붙여도_포트를_열지_않고_희망_목록에도_없다()
    {
        // 탭을 닫은 뒤 늦게 도착한 Attach(재연결 경합 등)가 포트를 열면 탭 없이 포트가 남고 희망 목록에도 없다
        var port = FreePort();
        var set = new SshPortForwardSet(new[] { R(port) });
        set.Close();
        using var client = new Renci.SshNet.SshClient("127.0.0.1", "u", "p");
        Assert.False(set.Attach(client));
        Assert.False(set.IsAttached);
        Assert.NotEqual(ForwardState.Open, set.Items[0].State);
        Assert.False(LocalPortRegistry.IsClaimed(port));
        // 닫은 뒤 켜기·임시 추가도 희망 목록을 되살리지 않는다
        set.SetEnabled(set.Items[0], false);
        set.SetEnabled(set.Items[0], true);
        set.AddTemporary(R(FreePort()));
        Assert.False(LocalPortRegistry.IsClaimed(port));
    }

    [Fact]
    public void 쿠버네티스_승인_감사_대상은_엔드포인트의_보이지_않는_문자도_드러낸다()
    {
        var c = new K8sCluster { Name = "prod", Endpoint = "https://api.example\u202Emoc.lab:6443" };
        var label = MainWindow.K8sLabel(c);
        Assert.Equal("prod  (https://api.example\\u{202E}moc.lab:6443)", label);
        Assert.Equal("prod", MainWindow.K8sLabel(new K8sCluster { Name = "prod" }));
    }

    [Fact]
    public void 예약은_주인만_놓는다()
    {
        var port = FreePort();
        object a = new(), b = new();
        Assert.True(LocalPortRegistry.TryReserve(port, a));
        Assert.False(LocalPortRegistry.TryReserve(port, b));
        LocalPortRegistry.Release(port, b);
        Assert.True(LocalPortRegistry.IsReserved(port));
        LocalPortRegistry.Release(port, a);
        Assert.False(LocalPortRegistry.IsReserved(port));
    }
}
