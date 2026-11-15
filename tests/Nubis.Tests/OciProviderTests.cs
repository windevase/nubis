using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>OCI 제공자의 순수 함수 (응답 파싱 · 매핑 · 페이지 · 로그인 명령). 네트워크 호출 없음</summary>
public class OciProviderTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    [Fact]
    public void 구독_리전은_READY_만_고른다()
    {
        var root = Json("""
            [
              {"regionKey":"ICN","regionName":"ap-seoul-1","status":"READY","isHomeRegion":true},
              {"regionKey":"NRT","regionName":"ap-tokyo-1","status":"IN_PROGRESS","isHomeRegion":false},
              {"regionKey":"IAD","regionName":"us-ashburn-1","status":"READY","isHomeRegion":false}
            ]
            """);
        Assert.Equal(new[] { "ap-seoul-1", "us-ashburn-1" }, OciProvider.ParseRegions(root));
    }

    [Fact]
    public void 리전_응답이_배열이_아니면_빈_목록()
    {
        Assert.Empty(OciProvider.ParseRegions(Json("""{"code":"x"}""")));
    }

    [Theory]
    [InlineData("ap-seoul-1", true)]
    [InlineData("us-ashburn-1", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("evil.com/x", false)]
    [InlineData("AP-SEOUL-1", false)]
    public void 리전_이름_검증(string? region, bool ok) => Assert.Equal(ok, OciProvider.IsValidRegion(region));

    [Fact]
    public void 컴파트먼트는_ACTIVE_만()
    {
        var items = Json("""
            [
              {"id":"ocid1.compartment.oc1..a","name":"dev","lifecycleState":"ACTIVE"},
              {"id":"ocid1.compartment.oc1..b","name":"old","lifecycleState":"DELETED"},
              {"id":"","name":"noid","lifecycleState":"ACTIVE"}
            ]
            """).EnumerateArray();
        var list = OciProvider.ParseCompartments(items);
        var c = Assert.Single(list);
        Assert.Equal("ocid1.compartment.oc1..a", c.id);
        Assert.Equal("dev", c.name);
    }

    [Theory]
    [InlineData("TERMINATED", true)]
    [InlineData("TERMINATING", true)]
    [InlineData("STOPPED", false)]
    [InlineData("RUNNING", false)]
    [InlineData("PROVISIONING", false)]
    public void 없어진_인스턴스_판정(string state, bool gone) => Assert.Equal(gone, OciProvider.IsGone(state));

    [Fact]
    public void VNIC_연결은_ATTACHED_만_nicIndex_순()
    {
        var items = Json("""
            [
              {"instanceId":"i1","vnicId":"v-sec","lifecycleState":"ATTACHED","nicIndex":1},
              {"instanceId":"i1","vnicId":"v-pri","lifecycleState":"ATTACHED","nicIndex":0},
              {"instanceId":"i1","vnicId":"v-old","lifecycleState":"DETACHED","nicIndex":0},
              {"instanceId":"i2","vnicId":null,"lifecycleState":"ATTACHING"},
              {"instanceId":"i3","vnicId":"v3","lifecycleState":"ATTACHED"}
            ]
            """).EnumerateArray();
        var map = OciProvider.VnicIdsByInstance(items);
        Assert.Equal(new[] { "v-pri", "v-sec" }, map["i1"]);
        Assert.False(map.ContainsKey("i2"));
        Assert.Equal(new[] { "v3" }, map["i3"]);
    }

    [Fact]
    public void VNIC_IP_파싱()
    {
        var (primary, priv, pub) = OciProvider.ParseVnic(Json("""{"isPrimary":true,"privateIp":"10.0.0.5","publicIp":"129.1.2.3"}"""));
        Assert.True(primary);
        Assert.Equal("10.0.0.5", priv);
        Assert.Equal("129.1.2.3", pub);

        var (p2, priv2, pub2) = OciProvider.ParseVnic(Json("""{"isPrimary":false,"privateIp":"10.0.1.5","publicIp":null}"""));
        Assert.False(p2);
        Assert.Equal("10.0.1.5", priv2);
        Assert.Equal("", pub2);
    }

    [Fact]
    public void 이미지_ID는_sourceDetails_우선_없으면_imageId()
    {
        Assert.Equal("img-a", OciProvider.ImageIdOf(Json("""{"sourceDetails":{"sourceType":"image","imageId":"img-a"},"imageId":"img-b"}""")));
        Assert.Equal("img-b", OciProvider.ImageIdOf(Json("""{"sourceDetails":{"sourceType":"bootVolume","bootVolumeId":"bv"},"imageId":"img-b"}""")));
        Assert.Equal("", OciProvider.ImageIdOf(Json("""{"sourceDetails":{"sourceType":"bootVolume"}}""")));
    }

    [Fact]
    public void 이미지_OS_와_Windows_판정()
    {
        var win = OciProvider.OsText(Json("""{"operatingSystem":"Windows","operatingSystemVersion":"Server 2022 Standard"}"""));
        Assert.Equal("Windows Server 2022 Standard", win);
        Assert.True(OciProvider.IsWindowsOs(win));

        var lin = OciProvider.OsText(Json("""{"operatingSystem":"Oracle Linux","operatingSystemVersion":"8"}"""));
        Assert.Equal("Oracle Linux 8", lin);
        Assert.False(OciProvider.IsWindowsOs(lin));
        Assert.False(OciProvider.IsWindowsOs(""));
    }

    [Fact]
    public void 인스턴스_매핑_이름_용도_태그_AD()
    {
        var inst = Json("""
            {
              "id":"ocid1.instance.oc1.ap-seoul-1.abc",
              "displayName":"web01:api",
              "lifecycleState":"RUNNING",
              "availabilityDomain":"Uocm:AP-SEOUL-1-AD-1",
              "freeformTags":{"purpose":"batch"},
              "sourceDetails":{"sourceType":"image","imageId":"img"}
            }
            """);
        var c = OciProvider.MapInstance(inst, "ap-seoul-1", "10.0.0.5", "129.1.2.3", "Windows Server 2022 Standard");
        Assert.Equal("ocid1.instance.oc1.ap-seoul-1.abc", c.InstanceId);
        Assert.Equal("web01", c.Name);
        Assert.Equal("batch", c.Purpose);       // 태그가 이름의 용도보다 우선
        Assert.Equal("RUNNING", c.State);
        Assert.True(c.IsRunning);
        Assert.Equal("AP-SEOUL-1-AD-1", c.Region);
        Assert.Equal("10.0.0.5", c.PrivateIp);
        Assert.Equal("129.1.2.3", c.PublicIp);
        Assert.True(c.IsWindows);
        Assert.Equal("Windows Server 2022 Standard", c.Os);
    }

    [Fact]
    public void 인스턴스_매핑_이름이_없으면_OCID_AD_없으면_리전()
    {
        var c = OciProvider.MapInstance(Json("""{"id":"ocid1.instance.x","lifecycleState":"STOPPED"}"""), "us-ashburn-1", "", "", "");
        Assert.Equal("ocid1.instance.x", c.Name);
        Assert.Equal("", c.Purpose);
        Assert.Equal("us-ashburn-1", c.Region);
        Assert.False(c.IsRunning);
        Assert.False(c.IsWindows);
    }

    [Fact]
    public void 다음_페이지_헤더()
    {
        using var resp = new HttpResponseMessage();
        Assert.Null(OciProvider.NextPage(resp));
        resp.Headers.TryAddWithoutValidation("opc-next-page", "AAAA==");
        Assert.Equal("AAAA==", OciProvider.NextPage(resp));
    }

    [Fact]
    public void 페이지_쿼리_추가()
    {
        Assert.Equal("https://x/instances?compartmentId=c", OciProvider.WithPage("https://x/instances?compartmentId=c", null));
        Assert.Equal("https://x/instances?compartmentId=c&page=a%2Bb%3D", OciProvider.WithPage("https://x/instances?compartmentId=c", "a+b="));
        Assert.Equal("https://x/list?page=p", OciProvider.WithPage("https://x/list", "p"));
    }

    [Fact]
    public void 오류_본문은_code_message_첫줄()
    {
        Assert.Equal("NotAuthenticated: The required information to complete authentication was not provided.",
            OciProvider.ErrorText(401, """{"code":"NotAuthenticated","message":"The required information to complete authentication was not provided.\nmore"}"""));
        Assert.Equal("HTTP 502: Bad Gateway", OciProvider.ErrorText(502, "Bad Gateway\n<html>"));
        Assert.Equal("HTTP 500", OciProvider.ErrorText(500, ""));
    }

    [Fact]
    public void 인증_오류_판정()
    {
        Assert.True(OciApiException.From(401, """{"code":"NotAuthenticated","message":"x"}""").IsAuth);
        Assert.False(OciApiException.From(404, """{"code":"NotAuthorizedOrNotFound","message":"x"}""").IsAuth);
        Assert.Equal(404, OciApiException.From(404, "not json").Status);
    }

    [Theory]
    [InlineData("DEFAULT", "ap-seoul-1", "oci session authenticate --profile-name DEFAULT --region ap-seoul-1")]
    [InlineData("my.prof-1@x", null, "oci session authenticate --profile-name my.prof-1@x")]
    [InlineData("bad name & calc", "ap-seoul-1", "oci session authenticate --profile-name DEFAULT --region ap-seoul-1")]
    [InlineData(null, "bad region;x", "oci session authenticate --profile-name DEFAULT")]
    public void 로그인_명령(string? profile, string? region, string expected) =>
        Assert.Equal(expected, OciProvider.BuildLoginCommand(profile, region));

    [Fact]
    public void 세션_갱신_필요_판정()
    {
        var now = new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero);
        Assert.True(OciProvider.NeedsRefresh(now.AddMinutes(-1), now));
        Assert.True(OciProvider.NeedsRefresh(now.AddMinutes(4), now));
        Assert.False(OciProvider.NeedsRefresh(now.AddMinutes(30), now));
        Assert.False(OciProvider.NeedsRefresh(null, now));
    }

    [Theory]
    [InlineData("ocid1.tenancy.oc1..aaaa", "oraclecloud.com")]
    [InlineData("ocid1.tenancy.oc2..aaaa", "oraclegovcloud.com")]
    [InlineData("ocid1.tenancy.oc4..aaaa", "oraclegovcloud.uk")]
    [InlineData("garbage", "oraclecloud.com")]
    public void realm_도메인(string tenancy, string domain) => Assert.Equal(domain, OciProvider.RealmDomain(tenancy));

    [Fact]
    public void AD_접두어_제거()
    {
        Assert.Equal("AP-SEOUL-1-AD-1", OciProvider.ShortAd("Uocm:AP-SEOUL-1-AD-1"));
        Assert.Equal("AD-1", OciProvider.ShortAd("AD-1"));
    }
}
