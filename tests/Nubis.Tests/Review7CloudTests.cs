using System;
using System.Collections.Generic;
using System.Linq;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>리뷰 7차: Tencent 권한 거부 리전(가용 영역 접두어 보호) · 거부 오류 코드 분류</summary>
public class Review7CloudTests
{
    private static CloudProfile Profile() =>
        new() { Provider = CloudProvider.Tencent, Name = "r7", DisplayName = "R7" };

    private static CloudInstance Inst(string id, string zone, string ip) =>
        new(id, "web-" + id, "", "RUNNING", zone, ip, "", false, "Linux");

    private static List<HostEntry> Seed(CloudProfile p)
    {
        var hosts = new List<HostEntry>();
        CloudSync.ApplyResult(p, new CloudFetchResult(new List<CloudInstance>
        {
            Inst("ins-seoul", "ap-seoul-1", "10.0.0.1"),
            Inst("ins-tokyo", "ap-tokyo-2", "10.0.0.2"),
        }, new()), hosts);
        Assert.Equal(2, hosts.Count);
        return hosts;
    }

    [Fact]
    public void 거부된_리전의_가용영역_호스트는_지우지_않는다()
    {
        var p = Profile();
        var hosts = Seed(p);
        // ap-seoul 이 거부됨: 서울 영역 호스트는 보이지 않아도 유지. 도쿄만 정상 조회됨
        var denied = new CloudFetchResult(new List<CloudInstance> { Inst("ins-tokyo", "ap-tokyo-2", "10.0.0.2") },
            new List<string> { "ap-seoul: 권한 없음" }) { DeniedRegions = new() { "ap-seoul" } };
        for (var i = 0; i < CloudSync.SyncMissGrace + 1; i++)
        {
            var (_, _, removed) = CloudSync.ApplyResult(p, denied, hosts);
            Assert.Equal(0, removed);
        }
        Assert.Contains(hosts, h => h.CloudInstanceId == "ins-seoul");
    }

    [Fact]
    public void 거부되지_않은_리전에서_사라진_서버는_유예_후_지운다()
    {
        var p = Profile();
        var hosts = Seed(p);
        // ap-tokyo 는 정상 조회됐는데 서버가 없고, 거부된 리전은 ap-seoul 뿐 — 도쿄 서버는 유예 후 지운다
        var result = new CloudFetchResult(new List<CloudInstance>(), new List<string> { "ap-seoul: 권한 없음" })
            { DeniedRegions = new() { "ap-seoul" } };
        var total = 0;
        for (var i = 0; i < CloudSync.SyncMissGrace; i++) total += CloudSync.ApplyResult(p, result, hosts).removed;
        Assert.Equal(1, total);
        Assert.DoesNotContain(hosts, h => h.CloudInstanceId == "ins-tokyo");
        Assert.Contains(hosts, h => h.CloudInstanceId == "ins-seoul");
    }

    [Fact]
    public void 접두어는_하이픈_경계에서만_맞는다()
    {
        // "ap-seoul" 거부가 "ap-seoulx-1" 같은 다른 이름을 보호하지는 않는다 (경계 없는 접두어 일치 방지)
        var p = Profile();
        var hosts = new List<HostEntry>();
        CloudSync.ApplyResult(p, new CloudFetchResult(new List<CloudInstance> { Inst("ins-x", "ap-seoulx-1", "10.0.0.3") }, new()), hosts);
        var result = new CloudFetchResult(new List<CloudInstance>(), new List<string> { "ap-seoul: 권한 없음" })
            { DeniedRegions = new() { "ap-seoul" } };
        var total = 0;
        for (var i = 0; i < CloudSync.SyncMissGrace; i++) total += CloudSync.ApplyResult(p, result, hosts).removed;
        Assert.Equal(1, total);
    }

    [Theory]
    [InlineData("UnauthorizedOperation", true)]
    [InlineData("AuthFailure.UnauthorizedOperation", true)]
    [InlineData("UnauthorizedOperation.CamNoAuth", true)]
    [InlineData("AuthFailure.SignatureFailure", false)]
    [InlineData("AuthFailure.SecretIdNotFound", false)]
    [InlineData("InternalError", false)]
    [InlineData(null, false)]
    public void Tencent_권한_거부_코드만_거부_리전으로_본다(string? code, bool expected) =>
        Assert.Equal(expected, TencentProvider.IsAccessDenied(code));
}
