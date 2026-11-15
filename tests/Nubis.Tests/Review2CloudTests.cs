using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>리뷰 2차: 권한 거부 리전 처리 · 숨긴 인스턴스 정리 · 오류 분류 · OCI realm/부트 볼륨 · 취소 전파</summary>
public class Review2CloudTests
{
    private static CloudProfile Profile() =>
        new() { Provider = CloudProvider.Aws, Name = "r2", DisplayName = "R2" };

    private static CloudInstance Inst(string id, string region, string ip = "10.0.0.1") =>
        new(id, "web-" + id, "", "running", region, ip, "", false, "Linux");

    private static List<HostEntry> Seed(CloudProfile p)
    {
        var hosts = new List<HostEntry>();
        CloudSync.Apply(p, new() { Inst("i-east", "us-east-1"), Inst("i-seoul", "ap-northeast-2", "10.0.0.2"), Inst("i-gone", "ap-northeast-2", "10.0.0.3") }, hosts);
        return hosts;
    }

    [Fact]
    public void 권한_거부_리전의_호스트는_남기고_다른_리전의_사라진_서버는_지운다()
    {
        var p = Profile();
        var hosts = Seed(p);
        var result = new CloudFetchResult(new() { Inst("i-seoul", "ap-northeast-2", "10.0.0.2") },
            new() { "us-east-1: denied" }) { DeniedRegions = new() { "us-east-1" } };
        var removedHosts = new List<HostEntry>();

        Assert.False(result.HasUnexpectedErrors);
        var (_, _, removed) = CloudSync.ApplyResult(p, result, hosts, removedHosts);

        Assert.Equal(1, removed);
        Assert.Contains(hosts, h => h.CloudInstanceId == "i-east");    // 거부 리전 → 유지
        Assert.Contains(hosts, h => h.CloudInstanceId == "i-seoul");
        Assert.DoesNotContain(hosts, h => h.CloudInstanceId == "i-gone");
        Assert.Equal("i-gone", Assert.Single(removedHosts).CloudInstanceId);
    }

    [Fact]
    public void 실제_조회_오류가_섞이면_아무것도_지우지_않는다()
    {
        var p = Profile();
        var hosts = Seed(p);
        var result = new CloudFetchResult(new() { Inst("i-seoul", "ap-northeast-2", "10.0.0.2") },
            new() { "eu-west-1: timeout", "us-east-1: denied" }) { DeniedRegions = new() { "us-east-1" } };

        Assert.True(result.HasUnexpectedErrors);
        var (_, _, removed) = CloudSync.ApplyResult(p, result, hosts);

        Assert.Equal(0, removed);
        Assert.Equal(3, hosts.Count);
    }

    [Fact]
    public void 거부_리전이_있으면_리전을_모르는_호스트도_지우지_않는다()
    {
        var p = Profile();
        var legacy = new HostEntry { CloudProfileId = p.Id, CloudInstanceId = "i-old", Label = "old", Address = "10.0.0.9" };
        var hosts = new List<HostEntry> { legacy };
        var result = new CloudFetchResult(new(), new() { "us-east-1: denied" }) { DeniedRegions = new() { "us-east-1" } };

        var (_, _, removed) = CloudSync.ApplyResult(p, result, hosts);

        Assert.Equal(0, removed);
        Assert.Contains(legacy, hosts);
    }

    [Fact]
    public void 숨긴_인스턴스는_빠짐없는_조회에서_없어진_것만_정리된다()
    {
        var p = Profile();
        p.HiddenInstances.AddRange(new[] { "i-hidden-alive", "i-hidden-terminated" });
        var hosts = new List<HostEntry>();

        // 부분 조회에서는 정리하지 않는다
        CloudSync.Apply(p, new() { Inst("i-hidden-alive", "us-east-1") }, hosts, removeMissing: false);
        Assert.Equal(2, p.HiddenInstances.Count);

        // 거부 리전이 있으면 정리하지 않는다 (그 리전의 숨긴 서버가 안 보였을 뿐일 수 있음)
        var denied = new CloudFetchResult(new() { Inst("i-hidden-alive", "us-east-1") }, new() { "x: denied" }) { DeniedRegions = new() { "x" } };
        CloudSync.ApplyResult(p, denied, hosts);
        Assert.Equal(2, p.HiddenInstances.Count);

        // 빠짐없는 조회: 살아 있는 숨긴 서버는 계속 숨기고, 없어진 것만 목록에서 뺀다
        CloudSync.Apply(p, new() { Inst("i-hidden-alive", "us-east-1") }, hosts);
        Assert.Equal(new[] { "i-hidden-alive" }, p.HiddenInstances);
        Assert.Empty(hosts);   // 숨긴 서버는 다시 추가되지 않는다
    }

    [Theory]
    [InlineData("UnauthorizedOperation", true)]
    [InlineData("AccessDenied", true)]
    [InlineData("AccessDeniedException", true)]
    [InlineData("AuthFailure", false)]
    [InlineData("RequestExpired", false)]
    [InlineData(null, false)]
    public void AWS_정책_거부_오류_분류(string? code, bool expected) => Assert.Equal(expected, AwsProvider.IsAccessDenied(code));

    [Theory]
    [InlineData("403 Compute Engine API has not been used in project 123 before or it is disabled. Enable it by visiting https://console.developers.google.com/apis/api/compute.googleapis.com/overview?project=123 then retry.", true)]
    [InlineData("403 Access Not Configured. compute.googleapis.com is disabled for this project", true)]
    [InlineData("403 The billing account for the owning project is disabled in state absent", false)]
    [InlineData("403 Project has been disabled", false)]
    public void GCP_API_꺼짐_판정(string message, bool expected) => Assert.Equal(expected, GcpProvider.IsComputeApiDisabled(message));

    [Theory]
    [InlineData("ocid1.tenancy.oc3..aaaa", "oraclegovcloud.com")]
    [InlineData("ocid1.tenancy.oc8..aaaa", "oraclecloud8.com")]
    [InlineData("ocid1.tenancy.oc10..aaaa", "oraclecloud10.com")]
    [InlineData("ocid1.tenancy.oc19..aaaa", "oraclecloud.eu")]
    [InlineData("ocid1.tenancy.ocx..aaaa", "oraclecloud.com")]
    public void OCI_realm_도메인_확장(string tenancy, string domain) => Assert.Equal(domain, OciProvider.RealmDomain(tenancy));

    [Fact]
    public void OCI_부트_볼륨_ID()
    {
        static JsonElement J(string s) => JsonDocument.Parse(s).RootElement.Clone();
        Assert.Equal("bv-1", OciProvider.BootVolumeIdOf(J("""{"sourceDetails":{"sourceType":"bootVolume","bootVolumeId":"bv-1"}}""")));
        Assert.Equal("", OciProvider.BootVolumeIdOf(J("""{"sourceDetails":{"sourceType":"image","imageId":"img"}}""")));
        Assert.Equal("", OciProvider.BootVolumeIdOf(J("""{}""")));
    }

    [Fact]
    public async Task 병렬_수집은_취소를_부분_실패로_바꾸지_않는다()
    {
        using var cts = new CancellationTokenSource();
        var task = CloudProviders.ParallelCollect(new[] { 1, 2, 3 }, 2, async i =>
        {
            if (i == 1) cts.Cancel();
            await Task.Delay(10, cts.Token);
            return (IEnumerable<int>)new[] { i };
        }, i => i.ToString(), cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task 병렬_수집_오류는_정렬되어_모인다()
    {
        var (items, errors) = await CloudProviders.ParallelCollect(new[] { "b", "a", "ok" }, 3, s =>
            s == "ok" ? Task.FromResult((IEnumerable<string>)new[] { s }) : throw new InvalidOperationException("fail " + s),
            s => s);

        Assert.Equal(new[] { "ok" }, items);
        Assert.Equal(new[] { "a: fail a", "b: fail b" }, errors);
    }
}
