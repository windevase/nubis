using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>
/// 클라우드 클러스터 조회 수정 검증: kubeconfig 를 못 받은 클러스터만 보호(ProtectedIds) · GKE DNS 엔드포인트 · GKE ADC 플러그인 인자 ·
/// AKS kubelogin 테넌트 · Tencent *.CamNoAuth (가짜 값만 사용)
/// </summary>
[Collection("settings")]
public class K8sCloudProtectTests
{
    private static CloudCluster Cc(string id, string kubeconfig = "kc") =>
        new("tke:" + id, id, "ap-seoul", "https://e-" + id, "1.30", "Running", K8sAuthKind.Kubeconfig, kubeconfig, id);

    [Fact]
    public void 보호_ID_는_지우지_않고_다른_클러스터_삭제는_반영()
    {
        var p = new CloudProfile();
        var list = new List<K8sCluster>();
        K8sSync.Apply(p, new ClusterFetchResult(new() { Cc("a", "kc-a"), Cc("b"), Cc("c") }, new()), list);
        Assert.Equal(3, list.Count);

        // a 는 kubeconfig 를 못 받음(보호), c 는 사라짐
        var r = new ClusterFetchResult(new() { Cc("b") }, new() { "ap-seoul · a: ResourceUnavailable" }) { ProtectedIds = new() { "tke:a" } };
        Assert.True(r.IsPartial);
        Assert.False(r.HasUnexpectedErrors);
        var removed = 0;
        for (var i = 0; i < K8sSync.MissGrace + 2; i++) removed += K8sSync.Apply(p, r, list).removed;

        Assert.Equal(1, removed);   // c 만
        var a = list.Single(x => x.CloudClusterId == "tke:a");
        Assert.Equal("kc-a", a.Kubeconfig);   // 기존 kubeconfig 유지
        Assert.Equal(0, a.MissCount);
        Assert.DoesNotContain(list, x => x.CloudClusterId == "tke:c");
    }

    [Fact]
    public void 보호_ID_가_있어도_목록_조회_실패가_더_있으면_지우지_않는다()
    {
        var p = new CloudProfile();
        var list = new List<K8sCluster>();
        K8sSync.Apply(p, new ClusterFetchResult(new() { Cc("a"), Cc("c") }, new()), list);
        var r = new ClusterFetchResult(new(), new() { "ap-seoul · a: denied", "ap-tokyo: timeout" }) { ProtectedIds = new() { "tke:a" } };
        Assert.True(r.HasUnexpectedErrors);
        for (var i = 0; i < K8sSync.MissGrace + 1; i++) Assert.Equal(0, K8sSync.Apply(p, r, list).removed);
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public void 같은_보호_ID_가_두_번_와도_실제_오류를_가리지_않는다()
    {
        var r = new ClusterFetchResult(new(), new() { "a: x", "a: y" }) { ProtectedIds = new() { "tke:a", "tke:a" } };
        Assert.True(r.HasUnexpectedErrors);
    }

    // ----- GKE -----

    private static ClusterFetchResult Gke(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return GcpProvider.ParseGkeClusters(doc.RootElement.Clone(), "p1");
    }

    [Fact]
    public void GKE_IP_접근을_끈_클러스터는_DNS_엔드포인트로_CA_없이()
    {
        var r = Gke("""
            {"clusters":[{"name":"dns-only","location":"us-central1","status":"RUNNING","currentMasterVersion":"1.32",
              "masterAuth":{"clusterCaCertificate":"Q0E="},
              "controlPlaneEndpointsConfig":{
                "dnsEndpointConfig":{"endpoint":"gke-abc123-p1.us-central1.gke.goog","allowExternalTraffic":true},
                "ipEndpointsConfig":{"enabled":false}}}]}
            """);
        var c = Assert.Single(r.Items);
        Assert.Equal("https://gke-abc123-p1.us-central1.gke.goog", c.Endpoint);
        Assert.Equal("https://gke-abc123-p1.us-central1.gke.goog", Assert.Single(Kubeconfig.ReadContexts(c.Kubeconfig)).Server);
        Assert.DoesNotContain("Q0E=", c.Kubeconfig);   // 공개 인증서 — 클러스터 CA 를 넣으면 TLS 검증 실패
        Assert.False(r.IsPartial);
    }

    [Fact]
    public void GKE_IP_가_켜져_있으면_기존처럼_IP_엔드포인트와_CA()
    {
        var r = Gke("""
            {"clusters":[{"name":"both","location":"us-central1","status":"RUNNING","endpoint":"34.1.2.3",
              "masterAuth":{"clusterCaCertificate":"Q0E="},
              "controlPlaneEndpointsConfig":{
                "dnsEndpointConfig":{"endpoint":"gke-x.gke.goog","allowExternalTraffic":true},
                "ipEndpointsConfig":{"enabled":true}}}]}
            """);
        var c = Assert.Single(r.Items);
        Assert.Equal("https://34.1.2.3", c.Endpoint);
        Assert.Contains("Q0E=", c.Kubeconfig);
    }

    [Fact]
    public void GKE_쓸_수_있는_엔드포인트가_없으면_보호하고_오류로()
    {
        var r = Gke("""
            {"clusters":[
              {"name":"ok","location":"us-central1","status":"RUNNING","endpoint":"34.1.2.3"},
              {"name":"blocked","location":"us-east1","status":"RUNNING","selfLink":"https://container.googleapis.com/v1/projects/p1/locations/us-east1/clusters/blocked",
               "controlPlaneEndpointsConfig":{"dnsEndpointConfig":{"endpoint":"gke-y.gke.goog"},"ipEndpointsConfig":{"enabled":false}}}
            ]}
            """);
        Assert.Equal("ok", Assert.Single(r.Items).Name);
        Assert.Equal("gke:https://container.googleapis.com/v1/projects/p1/locations/us-east1/clusters/blocked", Assert.Single(r.ProtectedIds));
        Assert.Contains(r.Errors, e => e.Contains("blocked"));
        Assert.True(r.IsPartial);
        Assert.False(r.HasUnexpectedErrors);
    }

    [Fact]
    public void GKE_kubeconfig_는_ADC_로_인증()
    {
        var c = Assert.Single(Gke("""{"clusters":[{"name":"x","location":"l","endpoint":"1.2.3.4"}]}""").Items);
        Assert.Equal("gke-gcloud-auth-plugin", Kubeconfig.ExecCommand(c.Kubeconfig));
        Assert.Contains("--use_application_default_credentials", c.Kubeconfig);
    }

    // ----- AKS -----

    [Fact]
    public void AKS_kubelogin_은_프로필_테넌트를_고정()
    {
        const string tenant = "11111111-2222-3333-4444-555555555555";
        var args = AzureProvider.KubeloginArgs(tenant);
        Assert.Equal(new[] { "get-token", "--login", "azurecli", "--server-id", AzureProvider.AksAadServerId, "--tenant-id", tenant }, args);
        Assert.DoesNotContain("--tenant-id", AzureProvider.KubeloginArgs(null));
        Assert.DoesNotContain("--tenant-id", AzureProvider.KubeloginArgs(" "));
        Assert.DoesNotContain("--tenant-id", AzureProvider.KubeloginArgs("--login"));       // 옵션처럼 보이는 값 차단
        Assert.Contains("contoso.onmicrosoft.com", AzureProvider.KubeloginArgs("contoso.onmicrosoft.com"));

        const string kc = """
            apiVersion: v1
            kind: Config
            clusters:
            - name: x
              cluster:
                server: https://x.hcp.eastus.azmk8s.io:443
            """;
        var info = new AzureProvider.AksInfo("/Sub/X", "x", "eastus", "x.hcp.eastus.azmk8s.io", "1.30", "running", true);
        var c = AzureProvider.BuildAksCluster(info, kc, tenant);
        Assert.Contains(tenant, c.Kubeconfig);
        Assert.Equal(AzureProvider.AksClusterId("/Sub/X"), c.Id);   // 보호 ID 와 같은 형식
    }

    // ----- Tencent -----

    [Theory]
    [InlineData("InternalError.CamNoAuth", true)]
    [InlineData("FailedOperation.CamNoAuth", true)]
    [InlineData("UnauthorizedOperation.CamNoAuth", true)]
    [InlineData("CamNoAuth", false)]
    [InlineData("InternalError", false)]
    [InlineData("InternalError.CamNoAuthX", false)]
    public void Tencent_CamNoAuth_는_권한_거부(string code, bool expected) =>
        Assert.Equal(expected, TencentProvider.IsAccessDenied(code));
}
