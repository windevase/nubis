using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>OCI OKE · Tencent TKE 클러스터 조회의 화면 무관 로직 (가짜 JSON · 가짜 kubeconfig)</summary>
[Collection("settings")]
public class OkeTkeClustersTests
{
    private static JsonElement Json(string s)
    {
        using var doc = JsonDocument.Parse(s);
        return doc.RootElement.Clone();
    }

    // ----- OCI OKE -----

    [Theory]
    [InlineData("DELETED", true)]
    [InlineData("deleting", true)]
    [InlineData("ACTIVE", false)]
    [InlineData("CREATING", false)]
    [InlineData("FAILED", false)]
    [InlineData("", false)]
    public void OKE_삭제된_클러스터만_뺀다(string state, bool gone) =>
        Assert.Equal(gone, OciProvider.IsClusterGone(state));

    [Fact]
    public void OKE_엔드포인트는_공용_사설_예전_순으로_고른다()
    {
        Assert.Equal(("PUBLIC_ENDPOINT", "203.0.113.10:6443"), OciProvider.OkeEndpoint(Json(
            """{"endpoints":{"publicEndpoint":"203.0.113.10:6443","privateEndpoint":"10.0.0.5:6443","kubernetes":"x"}}""")));
        Assert.Equal(("PRIVATE_ENDPOINT", "10.0.0.5:6443"), OciProvider.OkeEndpoint(Json(
            """{"endpoints":{"publicEndpoint":null,"privateEndpoint":"10.0.0.5:6443"}}""")));
        Assert.Equal(("LEGACY_KUBERNETES", "abc.example.invalid"), OciProvider.OkeEndpoint(Json(
            """{"endpoints":{"kubernetes":"abc.example.invalid"}}""")));
        Assert.Equal(("PUBLIC_ENDPOINT", ""), OciProvider.OkeEndpoint(Json("""{"name":"x"}""")));
    }

    [Fact]
    public void OKE_kubeconfig_요청_본문()
    {
        var body = Json(OciProvider.KubeconfigRequestBody("PRIVATE_ENDPOINT"));
        Assert.Equal("2.0.0", body.GetProperty("tokenVersion").GetString());
        Assert.Equal("PRIVATE_ENDPOINT", body.GetProperty("endpoint").GetString());
    }

    [Theory]
    [InlineData("203.0.113.10:6443", "https://203.0.113.10:6443")]
    [InlineData("https://203.0.113.10:6443", "https://203.0.113.10:6443")]
    public void OKE_엔드포인트에_scheme_을_붙인다(string input, string expected) =>
        Assert.Equal(expected, OciProvider.WithHttps(input));

    private const string OkeYaml = """
        ---
        apiVersion: v1
        kind: ""
        clusters:
        - name: "cluster-cabc"
          cluster:
            server: "https://203.0.113.10:6443"
            certificate-authority-data: "RkFLRS1DQQ=="
        users:
        - name: "user-cabc"
          user:
            exec:
              apiVersion: client.authentication.k8s.io/v1beta1
              command: oci
              args:
              - ce
              - cluster
              - generate-token
              - --cluster-id
              - ocid1.cluster.oc1.ap-seoul-1.fake
              - --region
              - ap-seoul-1
        contexts:
        - name: "context-cabc"
          context:
            cluster: "cluster-cabc"
            user: "user-cabc"
        current-context: "context-cabc"
        """;

    [Fact]
    public void OKE_kubeconfig_에서_server_와_CA_를_읽는다()
    {
        var (server, ca) = OciProvider.ReadOkeServer(OkeYaml);
        Assert.Equal("https://203.0.113.10:6443", server);
        Assert.Equal("RkFLRS1DQQ==", ca);
        // JSON 문자열로 감싸 와도 읽는다
        Assert.Equal(server, OciProvider.ReadOkeServer(JsonSerializer.Serialize(OkeYaml)).server);
    }

    [Fact]
    public void OKE_kubeconfig_에_server_가_없으면_FormatException()
    {
        Assert.Throws<FormatException>(() => OciProvider.ReadOkeServer("apiVersion: v1\nclusters: []\n"));
        Assert.Throws<FormatException>(() => OciProvider.ReadOkeServer(""));
    }

    [Fact]
    public void OKE_exec_인자에_프로필과_세션_인증을_붙인다()
    {
        var api = OciProvider.OkeExecArgs("ocid1.cluster.oc1.x", "ap-seoul-1", "WORK", false);
        Assert.Equal(new[] { "ce", "cluster", "generate-token", "--cluster-id", "ocid1.cluster.oc1.x", "--region", "ap-seoul-1", "--profile", "WORK" }, api);
        var session = OciProvider.OkeExecArgs("ocid1.cluster.oc1.x", "ap-seoul-1", "SSO", true);
        Assert.Equal(new[] { "--auth", "security_token" }, session.TakeLast(2));
        Assert.Contains("SSO", session);
    }

    [Fact]
    public void OKE_kubeconfig_는_oci_exec_한_클러스터만_담는다()
    {
        var kc = OciProvider.BuildOkeKubeconfig("prod", "https://203.0.113.10:6443", "RkFLRS1DQQ==",
            "ocid1.cluster.oc1.x", "ap-seoul-1", "WORK", true);
        var contexts = Kubeconfig.ReadContexts(kc);
        Assert.Single(contexts);
        Assert.Equal("prod", contexts[0].Name);
        Assert.Equal("https://203.0.113.10:6443", contexts[0].Server);
        Assert.Equal(K8sAuthKind.Oci, Kubeconfig.DetectAuth(kc));
        Assert.Equal("oci", Kubeconfig.ExecCommand(kc));
        Assert.Contains("security_token", kc);
        Assert.Contains("RkFLRS1DQQ==", kc);
    }

    [Fact]
    public void OKE_context_이름이_겹치면_리전을_붙인다()
    {
        var names = OciProvider.UniqueContextNames(new List<(string, string)>
        {
            ("dev", "ap-seoul-1"), ("prod", "ap-seoul-1"), ("prod", "ap-tokyo-1"), ("prod", "ap-tokyo-1"),
        });
        Assert.Equal(new[] { "dev", "prod-ap-seoul-1", "prod-ap-tokyo-1", "prod-ap-tokyo-1-2" }, names);
    }

    [Fact]
    public void OKE_상태는_ACTIVE_가_running()
    {
        Assert.Equal("running", CloudCluster.NormalizeState("ACTIVE"));
        Assert.Equal("creating", CloudCluster.NormalizeState("CREATING"));
    }

    // ----- Tencent TKE -----

    [Fact]
    public void TC3_서명_일반화_후에도_CVM_서명값이_같다()
    {
        const string expected = "TC3-HMAC-SHA256 Credential=AKIDtest123/2025-11-10/cvm/tc3_request, " +
                                "SignedHeaders=content-type;host;x-tc-action, " +
                                "Signature=cdc1ddaa5c1acad9d4caf0be9f51898de9a2a81619d118ec472cef3914602ac8";
        Assert.Equal(expected, TencentProvider.Authorization(TencentProvider.Cvm, "AKIDtest123", "secretKeyForTest456",
            "DescribeInstances", "{\"Offset\":0,\"Limit\":100}", 1762750000));
        Assert.Equal(expected, TencentProvider.Authorization("AKIDtest123", "secretKeyForTest456",
            "DescribeInstances", "{\"Offset\":0,\"Limit\":100}", 1762750000));
    }

    [Fact]
    public void TC3_서명_TKE_는_tke_호스트와_서비스로_서명한다()
    {
        // 기대값: 문서 알고리즘을 Python(hmac/hashlib)으로 따로 구현해 계산한 값 (host tke.tencentcloudapi.com, service tke)
        const string expected = "TC3-HMAC-SHA256 Credential=AKIDtest123/2025-11-10/tke/tc3_request, " +
                                "SignedHeaders=content-type;host;x-tc-action, " +
                                "Signature=d1c0bd83ef4a6bb744368f9680a69597e7a488550436a014743155c74aed9bb1";
        Assert.Equal(expected, TencentProvider.Authorization(TencentProvider.Tke, "AKIDtest123", "secretKeyForTest456",
            "DescribeClusters", "{\"Offset\":0,\"Limit\":100}", 1762750000));
        Assert.Equal("2018-05-25", TencentProvider.Tke.Version);
    }

    [Fact]
    public void TKE_kubeconfig_요청_본문()
    {
        var body = Json(TencentProvider.KubeconfigRequest("cls-fake01", true));
        Assert.Equal("cls-fake01", body.GetProperty("ClusterId").GetString());
        Assert.True(body.GetProperty("IsExtranet").GetBoolean());
        Assert.False(Json(TencentProvider.KubeconfigRequest("cls-fake01", false)).GetProperty("IsExtranet").GetBoolean());
    }

    [Theory]
    [InlineData("UnsupportedRegion", true)]
    [InlineData("UnsupportedRegion.Tke", true)]
    [InlineData("UnauthorizedOperation", false)]
    [InlineData(null, false)]
    public void TKE_미지원_리전_오류코드(string? code, bool expected) =>
        Assert.Equal(expected, TencentProvider.IsUnsupportedRegion(code));

    [Theory]
    [InlineData("Deleting", true)]
    [InlineData("Deleted", true)]
    [InlineData("Running", false)]
    [InlineData("Abnormal", false)]
    public void TKE_삭제중인_클러스터만_뺀다(string status, bool gone) =>
        Assert.Equal(gone, TencentProvider.IsClusterGone(status));

    private const string TkeKubeconfig = """
        apiVersion: v1
        clusters:
        - cluster:
            certificate-authority-data: RkFLRS1DQQ==
            server: https://cls-fake01.ccs.tencent-cloud.invalid
          name: cls-fake01
        contexts:
        - context:
            cluster: cls-fake01
            user: "100000000001"
          name: cls-fake01-100000000001-context-default
        current-context: cls-fake01-100000000001-context-default
        kind: Config
        users:
        - name: "100000000001"
          user:
            token: fake-token-not-real
        """;

    [Fact]
    public void TKE_클러스터를_공통_형식으로_바꾼다()
    {
        var c = TencentProvider.MapTkeCluster(Json(
            """{"ClusterId":"cls-fake01","ClusterName":"prod","ClusterVersion":"1.30.0","ClusterStatus":"Running"}"""),
            "ap-seoul", TkeKubeconfig);
        Assert.Equal("tke:cls-fake01", c.Id);
        Assert.Equal("prod", c.Name);
        Assert.Equal("ap-seoul", c.Region);
        Assert.Equal("https://cls-fake01.ccs.tencent-cloud.invalid", c.Endpoint);
        Assert.Equal("1.30.0", c.Version);
        Assert.Equal("running", c.State);
        Assert.Equal(K8sAuthKind.Kubeconfig, c.AuthKind);
        Assert.Equal("cls-fake01-100000000001-context-default", c.ContextName);
        Assert.Equal(TkeKubeconfig, c.Kubeconfig);   // context 하나면 받은 그대로
    }

    [Fact]
    public void TKE_context_가_여럿이면_current_context_하나만_남긴다()
    {
        var multi = TkeKubeconfig
            .Replace("current-context: cls-fake01-100000000001-context-default", "current-context: second")
            .Replace("""
                contexts:
                - context:
                """, """
                contexts:
                - context:
                    cluster: cls-fake01
                    user: "100000000001"
                  name: second
                - context:
                """);
        Assert.Equal(2, Kubeconfig.ReadContexts(multi).Count);
        var c = TencentProvider.MapTkeCluster(Json("""{"ClusterId":"cls-fake01","ClusterStatus":"Running"}"""), "ap-seoul", multi);
        Assert.Equal("second", c.ContextName);
        Assert.Equal("cls-fake01", c.Name);   // 이름이 없으면 ID
        var only = Kubeconfig.ReadContexts(c.Kubeconfig);
        Assert.Single(only);
        Assert.Equal("second", only[0].Name);
    }

    [Fact]
    public void TKE_context_가_없는_kubeconfig_는_FormatException()
    {
        Assert.Throws<FormatException>(() => TencentProvider.MapTkeCluster(Json("""{"ClusterId":"cls-x"}"""), "ap-seoul",
            "apiVersion: v1\nkind: Config\nclusters: []\n"));
    }
}
