using System.Collections.Generic;
using System.Linq;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>update_host · get_host · 승인 창 요약의 포트 포워딩 처리 (McpHostRules / McpHostParity — 편집 창과 같은 규칙)</summary>
public class McpHostForwardsTests
{
    private static PortForwardRule R(int local, string host, int port, string? name = null, bool on = true) =>
        new() { LocalPort = local, TargetHost = host, TargetPort = port, Name = name, Enabled = on };

    private static HostEntry SshWithForwards() => new()
    {
        Label = "db-bastion", Address = "10.0.0.5", Protocol = Protocol.Ssh, Port = 22,
        PortForwards = new List<PortForwardRule> { R(15432, "db", 5432, "prod-db"), R(18080, "localhost", 80, on: false) },
    };

    [Fact]
    public void SSH_가_아니게_바뀌면_규칙을_지우고_지운_수를_돌려준다()
    {
        var h = SshWithForwards();
        Assert.Equal(0, McpHostRules.ClearForwardsIfNotSsh(h));   // SSH 면 그대로
        Assert.Equal(2, h.PortForwards!.Count);

        h.Protocol = Protocol.Rdp;
        Assert.Equal(2, McpHostRules.ClearForwardsIfNotSsh(h));
        Assert.Null(h.PortForwards);
        Assert.Equal(0, McpHostRules.ClearForwardsIfNotSsh(h));   // 두 번째는 지울 것이 없다
    }

    [Fact]
    public void 사본에서_지워도_원본은_그대로()
    {
        // update_host 는 사본(Clone)에 먼저 적용한다 — 승인 전에는 원본 규칙이 남아 있어야 한다
        var h = SshWithForwards();
        var copy = h.Clone();
        copy.Protocol = Protocol.Rdp;
        McpHostRules.ClearForwardsIfNotSsh(copy);
        Assert.Null(copy.PortForwards);
        Assert.Equal(2, h.PortForwards!.Count);
    }

    [Fact]
    public void 포워딩_지우기는_위험_변경으로_보지_않는다()
    {
        // "포트 포워딩" 은 "포트" 로 시작하지만 노출을 줄이는 변경
        Assert.False(McpHostRules.IsRisky($"{McpHostRules.ForwardsLabel}: 2개 → 없음"));
        Assert.True(McpHostRules.IsRisky("포트: 22 → 2222"));
        Assert.False(McpHostRules.IsFavoriteOnly(new[] { $"{McpHostRules.ForwardsLabel}: 2개 → 없음" }));
    }

    [Fact]
    public void 규칙_요약은_주소_이름_꺼짐을_보인다()
    {
        Assert.Null(McpHostRules.ForwardsSummary(null));
        Assert.Null(McpHostRules.ForwardsSummary(new List<PortForwardRule>()));
        var s = McpHostRules.ForwardsSummary(SshWithForwards().PortForwards);
        Assert.Equal("2개 — 127.0.0.1:15432 → db:5432 (prod-db), 127.0.0.1:18080 → localhost:80 (꺼짐)", s);
    }

    [Fact]
    public void 규칙이_많으면_앞쪽만_보이고_나머지는_개수()
    {
        var rules = Enumerable.Range(1, 7).Select(i => R(20000 + i, "db", 5432)).ToList();
        var s = McpHostRules.ForwardsSummary(rules, max: 5)!;
        Assert.StartsWith("7개 — ", s);
        Assert.EndsWith(" 외 2개", s);
        Assert.Contains("127.0.0.1:20005", s);
        Assert.DoesNotContain("127.0.0.1:20006", s);
    }

    [Fact]
    public void 요약에는_보이지_않는_문자를_그대로_넣지_않는다()
    {
        // hosts.json 을 손으로 고친 값 — 승인 창에서 다른 대상처럼 보이게 꾸미지 못하게
        var s = McpHostRules.ForwardsSummary(new[] { R(15432, "db‮evil", 5432, "x‮y") })!;
        Assert.Equal("1개 — 127.0.0.1:15432 → db\\u{202E}evil:5432 (x\\u{202E}y)", s);
        Assert.False(s.Contains('‮'));   // 문자 비교 (문화권 비교는 서식 문자를 무시해 늘 "들어 있음" 으로 본다)
    }

    [Fact]
    public void get_host_에_저장된_규칙이_보인다()
    {
        var h = SshWithForwards();
        var d = McpHostParity.Detail(h, null, "-", null, 0, new List<int>());
        Assert.Contains($"{McpHostRules.ForwardsLabel}(저장된 규칙): 2개 — 127.0.0.1:15432 → db:5432 (prod-db)", d);
        h.PortForwards = null;
        Assert.Contains($"{McpHostRules.ForwardsLabel}(저장된 규칙): 없음", McpHostParity.Detail(h, null, "-", null, 0, new List<int>()));
    }
}
