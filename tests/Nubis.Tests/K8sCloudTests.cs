using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>클라우드 클러스터: 동기화 병합 규칙 · GKE 응답 파싱 · EKS kubeconfig</summary>
[Collection("settings")]
public class K8sCloudTests
{
    private static CloudCluster Cc(string id, string name = "c", string region = "us-east-1", string state = "ACTIVE", string endpoint = "https://e1") =>
        new("eks:" + id, name, region, endpoint, "1.31", state, K8sAuthKind.Aws, $"kubeconfig-{id}", id);

    private static ClusterFetchResult R(params CloudCluster[] items) => new(items.ToList(), new List<string>());

    [Fact]
    public void 추가_갱신_사용자_이름_유지()
    {
        var p = new CloudProfile();
        var list = new List<K8sCluster>();
        Assert.Equal((1, 0, 0), K8sSync.Apply(p, R(Cc("a", "prod")), list));
        var c = list.Single();
        Assert.Equal("prod", c.Name);
        Assert.Equal("running", c.State);
        Assert.Equal("kubeconfig-a", c.Kubeconfig);
        c.Name = "내 이름";
        c.DefaultNamespace = "web";
        Assert.Equal((0, 1, 0), K8sSync.Apply(p, R(Cc("a", "prod-renamed", endpoint: "https://e2")), list));
        Assert.Equal("내 이름", c.Name);       // 사용자가 바꾼 이름 유지
        Assert.Equal("web", c.DefaultNamespace);
        Assert.Equal("https://e2", c.Endpoint);   // 주소는 갱신
    }

    [Fact]
    public void 두_번_연속_안_보여야_지우고_부분_실패면_지우지_않는다()
    {
        var p = new CloudProfile();
        var list = new List<K8sCluster>();
        K8sSync.Apply(p, R(Cc("a"), Cc("b")), list);
        Assert.Equal(0, K8sSync.Apply(p, R(Cc("a")), list).removed);
        Assert.Equal(2, list.Count);
        // 부분 실패: 삭제 판단 안 함 (횟수도 늘리지 않음)
        Assert.Equal(0, K8sSync.Apply(p, new ClusterFetchResult(new() { Cc("a") }, new() { "eu-west-1: timeout" }), list).removed);
        Assert.Equal(2, list.Count);
        Assert.Equal(1, K8sSync.Apply(p, R(Cc("a")), list).removed);
        Assert.Equal("eks:a", Assert.Single(list).CloudClusterId);
        // 다시 보이면 횟수는 0 으로
        Assert.Equal(0, list[0].MissCount);
    }

    [Fact]
    public void 권한_거부_리전과_다른_프로필_직접_등록은_건드리지_않는다()
    {
        var p = new CloudProfile();
        var other = new CloudProfile();
        var list = new List<K8sCluster>
        {
            new() { Name = "manual", Endpoint = "https://m" },
            new() { Name = "other", CloudProfileId = other.Id, CloudClusterId = "eks:x", Region = "us-east-1" },
        };
        K8sSync.Apply(p, R(Cc("a", region: "ap-northeast-2")), list);
        var denied = new ClusterFetchResult(new(), new() { "ap-northeast-2: denied" }) { DeniedRegions = new() { "ap-northeast-2" } };
        K8sSync.Apply(p, denied, list);
        K8sSync.Apply(p, denied, list);
        Assert.Equal(3, list.Count);
    }

    [Fact]
    public void 직접_가져온_같은_주소의_클러스터는_다시_만들지_않는다()
    {
        var p = new CloudProfile();
        var list = new List<K8sCluster> { new() { Name = "gke_x", Endpoint = "https://136.112.87.142" } };
        Assert.Equal((0, 0, 0), K8sSync.Apply(p, R(Cc("a", endpoint: "https://136.112.87.142/")), list));
        Assert.Single(list);
    }

    [Theory]
    [InlineData("ACTIVE", "running")]
    [InlineData("RUNNING", "running")]
    [InlineData("Succeeded", "running")]
    [InlineData("CREATING", "creating")]
    [InlineData(null, "unknown")]
    public void 상태_정규화(string? raw, string expected) => Assert.Equal(expected, CloudCluster.NormalizeState(raw));

    [Fact]
    public void GKE_응답을_exec_kubeconfig_로()
    {
        using var doc = JsonDocument.Parse("""
            {"clusters":[
              {"name":"lab","location":"us-central1","endpoint":"10.1.2.3","currentMasterVersion":"1.31.4-gke.1",
               "status":"RUNNING","masterAuth":{"clusterCaCertificate":"Q0E="},"selfLink":"https://container.googleapis.com/v1/projects/p1/locations/us-central1/clusters/lab"},
              {"name":"private","location":"asia-northeast3-a","endpoint":"","privateClusterConfig":{"privateEndpoint":"172.16.0.2"},"status":"PROVISIONING"}
            ],"missingZones":["europe-west1-b"]}
            """);
        var r = GcpProvider.ParseGkeClusters(doc.RootElement, "p1");
        Assert.Equal(2, r.Items.Count);
        var lab = r.Items[0];
        Assert.Equal("https://10.1.2.3", lab.Endpoint);
        Assert.Equal("running", lab.State);
        Assert.Equal("gke_p1_us-central1_lab", lab.ContextName);
        Assert.Equal(K8sAuthKind.Gcp, Kubeconfig.DetectAuth(lab.Kubeconfig));
        Assert.Equal("gke-gcloud-auth-plugin", Kubeconfig.ExecCommand(lab.Kubeconfig));
        Assert.Equal("gke_p1_us-central1_lab", Kubeconfig.CurrentContext(lab.Kubeconfig));
        Assert.Contains("Q0E=", lab.Kubeconfig);
        Assert.Equal("https://172.16.0.2", r.Items[1].Endpoint);
        Assert.True(r.IsPartial);   // 응답하지 못한 영역 → 지우지 않음
    }

    [Fact]
    public void EKS_는_aws_eks_get_token_과_프로필()
    {
        var c = AwsProvider.EksCluster("arn:aws:eks:ap-northeast-2:111:cluster/prod", "prod", "ap-northeast-2", "https://ABC.eks.amazonaws.com",
            "Q0E=", "1.31", "ACTIVE", "sso-dev");
        Assert.Equal("eks:arn:aws:eks:ap-northeast-2:111:cluster/prod", c.Id);
        Assert.Equal("running", c.State);
        Assert.Equal("aws", Kubeconfig.ExecCommand(c.Kubeconfig));
        Assert.Contains("AWS_PROFILE", c.Kubeconfig);
        Assert.Contains("sso-dev", c.Kubeconfig);
        Assert.Contains("\"--cluster-name\"", c.Kubeconfig);
        Assert.Equal("https://ABC.eks.amazonaws.com", Kubeconfig.ReadContexts(c.Kubeconfig)[0].Server);
    }
}
