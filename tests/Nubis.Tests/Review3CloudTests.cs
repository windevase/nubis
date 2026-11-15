using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>리뷰 3차: 사라진 서버 삭제 유예(두 번 연속 미확인) · 숨긴 인스턴스 정리 유예 · 오류 분류 보강 · OCI realm</summary>
public class Review3CloudTests
{
    private static CloudProfile Profile() =>
        new() { Provider = CloudProvider.Aws, Name = "r3", DisplayName = "R3" };

    private static CloudInstance Inst(string id, string region = "ap-northeast-2", string ip = "10.0.0.1") =>
        new(id, "web-" + id, "", "running", region, ip, "", false, "Linux");

    private static CloudFetchResult Ok(params CloudInstance[] items) => new(items.ToList(), new());

    /// <summary>i-a(10.0.0.1) · i-b(10.0.0.2) 두 대로 시작</summary>
    private static List<HostEntry> Seed(CloudProfile p)
    {
        var hosts = new List<HostEntry>();
        CloudSync.ApplyResult(p, Ok(Inst("i-a"), Inst("i-b", ip: "10.0.0.2")), hosts);
        Assert.Equal(2, hosts.Count);
        return hosts;
    }

    [Fact]
    public void 사라진_서버는_두번_연속_빠짐없는_조회에서_안_보여야_지운다()
    {
        var p = Profile();
        var hosts = Seed(p);
        var removedHosts = new List<HostEntry>();

        var (_, _, r1) = CloudSync.ApplyResult(p, Ok(Inst("i-a")), hosts, removedHosts);
        Assert.Equal(0, r1);
        Assert.Contains(hosts, h => h.CloudInstanceId == "i-b");
        Assert.Equal(1, p.MissCounts["i-b"]);
        Assert.Equal(1, p.PendingRemoval);
        Assert.Empty(removedHosts);

        var (_, _, r2) = CloudSync.ApplyResult(p, Ok(Inst("i-a")), hosts, removedHosts);
        Assert.Equal(1, r2);
        Assert.DoesNotContain(hosts, h => h.CloudInstanceId == "i-b");
        Assert.Equal("i-b", Assert.Single(removedHosts).CloudInstanceId);
        Assert.Empty(p.MissCounts);
        Assert.Equal(0, p.PendingRemoval);
    }

    [Fact]
    public void 한번_빠졌다가_다시_보이면_횟수가_초기화된다()
    {
        var p = Profile();
        var hosts = Seed(p);

        CloudSync.ApplyResult(p, Ok(Inst("i-a")), hosts);                                // 빠짐 1
        CloudSync.ApplyResult(p, Ok(Inst("i-a"), Inst("i-b", ip: "10.0.0.2")), hosts);   // 다시 보임
        Assert.Empty(p.MissCounts);
        var (_, _, removed) = CloudSync.ApplyResult(p, Ok(Inst("i-a")), hosts);          // 다시 빠짐 1
        Assert.Equal(0, removed);
        Assert.Equal(2, hosts.Count);
    }

    [Fact]
    public void 부분_조회는_횟수를_올리지도_지우지도_않는다()
    {
        var p = Profile();
        var hosts = Seed(p);
        var partial = new CloudFetchResult(new() { Inst("i-a") }, new() { "eu-west-1: timeout" });

        for (var i = 0; i < 3; i++)
        {
            var (_, _, removed) = CloudSync.ApplyResult(p, partial, hosts);
            Assert.Equal(0, removed);
        }
        Assert.Empty(p.MissCounts);
        Assert.Equal(2, hosts.Count);
    }

    [Fact]
    public void 부분_조회에서_보이면_이전_빠짐_횟수가_지워진다()
    {
        var p = Profile();
        var hosts = Seed(p);
        CloudSync.ApplyResult(p, Ok(Inst("i-a")), hosts);   // i-b 빠짐 1
        var partial = new CloudFetchResult(new() { Inst("i-b", ip: "10.0.0.2") }, new() { "eu-west-1: timeout" });
        CloudSync.ApplyResult(p, partial, hosts);           // 부분 조회지만 i-b 는 보임 → 초기화
        Assert.False(p.MissCounts.ContainsKey("i-b"));

        var (_, _, removed) = CloudSync.ApplyResult(p, Ok(Inst("i-a")), hosts);
        Assert.Equal(0, removed);   // 연속 두 번이 아니므로 유지
    }

    [Fact]
    public void 권한_거부_리전의_호스트는_횟수도_올리지_않는다()
    {
        var p = Profile();
        var hosts = new List<HostEntry>();
        CloudSync.ApplyResult(p, Ok(Inst("i-east", "us-east-1"), Inst("i-seoul")), hosts);
        var denied = new CloudFetchResult(new() { Inst("i-seoul") }, new() { "us-east-1: denied" }) { DeniedRegions = new() { "us-east-1" } };

        for (var i = 0; i < 3; i++) CloudSync.ApplyResult(p, denied, hosts);

        Assert.Contains(hosts, h => h.CloudInstanceId == "i-east");
        Assert.False(p.MissCounts.ContainsKey("i-east"));
    }

    [Fact]
    public void 중복_호스트는_유예_없이_바로_지운다()
    {
        var p = Profile();
        var hosts = Seed(p);
        var dup = new HostEntry { CloudProfileId = p.Id, CloudInstanceId = "i-a", Label = "dup", Address = "10.0.0.1" };
        hosts.Add(dup);

        var (_, _, removed) = CloudSync.ApplyResult(p, Ok(Inst("i-a"), Inst("i-b", ip: "10.0.0.2")), hosts);

        Assert.Equal(1, removed);
        Assert.DoesNotContain(dup, hosts);
        Assert.Equal(2, hosts.Count);
    }

    [Fact]
    public void 결과에는_있지만_숨긴_인스턴스의_호스트는_바로_지운다()
    {
        // 숨긴 인스턴스는 "사라진" 것이 아니므로 유예하지 않는다 (유예하면 매번 횟수가 초기화되어 영영 남는다)
        var p = Profile();
        var hosts = Seed(p);
        p.HiddenInstances.Add("i-b");

        var (_, _, removed) = CloudSync.ApplyResult(p, Ok(Inst("i-a"), Inst("i-b", ip: "10.0.0.2")), hosts);

        Assert.Equal(1, removed);
        Assert.DoesNotContain(hosts, h => h.CloudInstanceId == "i-b");
        Assert.Contains("i-b", p.HiddenInstances);
    }

    [Fact]
    public void 숨긴_인스턴스_정리도_두번_연속_안_보여야_한다()
    {
        var p = Profile();
        p.HiddenInstances.Add("i-hidden");
        var hosts = new List<HostEntry>();

        CloudSync.ApplyResult(p, Ok(Inst("i-a")), hosts);
        Assert.Contains("i-hidden", p.HiddenInstances);       // 한 번 빠진 것만으로는 숨김을 풀지 않는다
        Assert.Equal(1, p.MissCounts["i-hidden"]);

        CloudSync.ApplyResult(p, Ok(Inst("i-a"), Inst("i-hidden")), hosts);
        Assert.Contains("i-hidden", p.HiddenInstances);
        Assert.False(p.MissCounts.ContainsKey("i-hidden"));   // 다시 보이면 초기화
        Assert.DoesNotContain(hosts, h => h.CloudInstanceId == "i-hidden");

        CloudSync.ApplyResult(p, Ok(Inst("i-a")), hosts);
        CloudSync.ApplyResult(p, Ok(Inst("i-a")), hosts);
        Assert.DoesNotContain("i-hidden", p.HiddenInstances);
        Assert.Empty(p.MissCounts);
    }

    [Fact]
    public void 사용자가_지운_호스트의_횟수는_버린다()
    {
        var p = Profile();
        var hosts = Seed(p);
        CloudSync.ApplyResult(p, Ok(Inst("i-a")), hosts);   // i-b 빠짐 1
        hosts.RemoveAll(h => h.CloudInstanceId == "i-b");    // 사용자가 직접 삭제 (숨김 없이)

        CloudSync.ApplyResult(p, new CloudFetchResult(new() { Inst("i-a") }, new() { "x: timeout" }), hosts);

        Assert.Empty(p.MissCounts);
    }

    [Fact]
    public void Apply_기본값은_예전처럼_바로_지운다()
    {
        var p = Profile();
        var hosts = Seed(p);
        var (_, _, removed) = CloudSync.Apply(p, new() { Inst("i-a") }, hosts);
        Assert.Equal(1, removed);
        Assert.Empty(p.MissCounts);
    }

    [Fact]
    public void 빠짐_횟수는_프로필과_함께_저장되고_null_이어도_안전하다()
    {
        var p = Profile();
        p.MissCounts["i-x"] = 1;
        var json = JsonSerializer.Serialize(p);
        var back = JsonSerializer.Deserialize<CloudProfile>(json)!;
        Assert.Equal(1, back.MissCounts["i-x"]);
        Assert.DoesNotContain("PendingRemoval", json);

        var nulled = JsonSerializer.Deserialize<CloudProfile>("""{"Name":"a","MissCounts":null}""")!;
        Assert.NotNull(nulled.MissCounts);
        var old = JsonSerializer.Deserialize<CloudProfile>("""{"Name":"a"}""")!;
        Assert.Empty(old.MissCounts);
    }

    [Fact]
    public void 같은_거부_리전이_두번_들어가도_실제_오류를_가리지_않는다()
    {
        var r = new CloudFetchResult(new() { Inst("i-a") }, new() { "eu-west-1: timeout", "us-east-1: denied" })
        {
            DeniedRegions = new() { "us-east-1", "US-EAST-1" },
        };
        Assert.True(r.HasUnexpectedErrors);
    }

    [Theory]
    [InlineData("ocid1.tenancy.oc22..aaaa", "psn-pco.it")]
    [InlineData("ocid1.tenancy.oc1..aaaa", "oraclecloud.com")]
    [InlineData("ocid1.tenancy.oc14..aaaa", "oraclecloud14.com")]
    public void OCI_realm_도메인_3차(string tenancy, string domain) => Assert.Equal(domain, OciProvider.RealmDomain(tenancy));
}
