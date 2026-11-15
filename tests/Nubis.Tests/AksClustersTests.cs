using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>Azure AKS 클러스터 조회: 목록 항목 파싱 · 자격 증명 kubeconfig 변환 (가짜 값만 사용)</summary>
[Collection("settings")]
public class AksClustersTests
{
    private const string ResId = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/RG-Demo/providers/Microsoft.ContainerService/managedClusters/aks-demo";

    // 인증서·키·토큰은 실제 값이 아닌 자리표시자
    private const string AadKubeconfig = """
        apiVersion: v1
        kind: Config
        clusters:
        - name: aks-demo
          cluster:
            server: https://aks-demo-dns-abc123.hcp.koreacentral.azmk8s.io:443
            certificate-authority-data: RkFLRS1DQQ==
        users:
        - name: clusterUser_RG-Demo_aks-demo
          user:
            exec:
              apiVersion: client.authentication.k8s.io/v1beta1
              command: kubelogin
              args:
              - get-token
              - --login
              - devicecode
        contexts:
        - name: aks-demo
          context:
            cluster: aks-demo
            user: clusterUser_RG-Demo_aks-demo
        current-context: aks-demo
        """;

    private const string LocalKubeconfig = """
        apiVersion: v1
        kind: Config
        clusters:
        - name: aks-local
          cluster:
            server: https://aks-local-dns.hcp.eastus.azmk8s.io:443
            certificate-authority-data: RkFLRS1DQQ==
        - name: other
          cluster:
            server: https://other.example.com
        users:
        - name: clusterUser_RG_aks-local
          user:
            client-certificate-data: RkFLRS1DRVJU
            client-key-data: RkFLRS1LRVk=
            token: fake-token-0000
        - name: other-user
          user:
            token: other-token
        contexts:
        - name: aks-local
          context:
            cluster: aks-local
            user: clusterUser_RG_aks-local
        - name: other
          context:
            cluster: other
            user: other-user
        current-context: aks-local
        """;

    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    [Fact]
    public void ParseAks_ReadsFields()
    {
        var a = AzureProvider.ParseAks(Json($$"""
            {
              "id": "{{ResId}}",
              "name": "aks-demo",
              "location": "koreacentral",
              "properties": {
                "provisioningState": "Succeeded",
                "powerState": { "code": "Running" },
                "kubernetesVersion": "1.30",
                "currentKubernetesVersion": "1.30.5",
                "fqdn": "aks-demo-dns-abc123.hcp.koreacentral.azmk8s.io",
                "aadProfile": { "managed": true, "enableAzureRBAC": true }
              }
            }
            """));
        Assert.Equal(ResId, a.Id);
        Assert.Equal("aks-demo", a.Name);
        Assert.Equal("koreacentral", a.Location);
        Assert.Equal("aks-demo-dns-abc123.hcp.koreacentral.azmk8s.io", a.Fqdn);
        Assert.Equal("1.30.5", a.Version);
        Assert.Equal("running", a.State);
        Assert.True(a.Aad);
    }

    [Fact]
    public void ParseAks_StoppedPrivateNoAad()
    {
        var a = AzureProvider.ParseAks(Json("""
            {
              "id": "/x", "name": "p", "location": "eastus",
              "properties": {
                "provisioningState": "Succeeded",
                "powerState": { "code": "Stopped" },
                "kubernetesVersion": "1.29.2",
                "privateFQDN": "p-private.privatelink.eastus.azmk8s.io"
              }
            }
            """));
        Assert.Equal("stopped", a.State);
        Assert.Equal("1.29.2", a.Version);
        Assert.Equal("p-private.privatelink.eastus.azmk8s.io", a.Fqdn);
        Assert.False(a.Aad);
    }

    [Theory]
    [InlineData("Creating", "creating")]
    [InlineData("Failed", "failed")]
    [InlineData("", "unknown")]
    public void ParseAks_ProvisioningState(string prov, string expected)
    {
        var a = AzureProvider.ParseAks(Json($$"""{ "id": "/x", "name": "n", "properties": { "provisioningState": "{{prov}}", "powerState": { "code": "Running" } } }"""));
        Assert.Equal(expected, a.State);
    }

    [Fact]
    public void ParseAks_NullAadProfileIsLocal()
    {
        var a = AzureProvider.ParseAks(Json("""{ "id": "/x", "name": "n", "properties": { "aadProfile": null } }"""));
        Assert.False(a.Aad);
    }

    [Fact]
    public void CredentialKubeconfig_DecodesFirst()
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(AadKubeconfig));
        var yaml = AzureProvider.CredentialKubeconfig(Json($$"""{ "kubeconfigs": [ { "name": "clusterUser", "value": "{{b64}}" } ] }"""));
        Assert.Equal(AadKubeconfig, yaml);
        Assert.Null(AzureProvider.CredentialKubeconfig(Json("""{ "kubeconfigs": [] }""")));
        Assert.Throws<InvalidOperationException>(() =>
            AzureProvider.CredentialKubeconfig(Json("""{ "kubeconfigs": [ { "value": "not*base64" } ] }""")));
    }

    [Fact]
    public void ClusterOfKubeconfig_ReadsServerAndCa()
    {
        var (server, ca) = AzureProvider.ClusterOfKubeconfig(AadKubeconfig);
        Assert.Equal("https://aks-demo-dns-abc123.hcp.koreacentral.azmk8s.io:443", server);
        Assert.Equal("RkFLRS1DQQ==", ca);
    }

    [Fact]
    public void BuildAksCluster_Aad_UsesKubeloginAzureCli()
    {
        var info = new AzureProvider.AksInfo(ResId, "aks-demo", "koreacentral", "aks-demo-dns-abc123.hcp.koreacentral.azmk8s.io", "1.30.5", "running", true);
        var c = AzureProvider.BuildAksCluster(info, AadKubeconfig);

        Assert.Equal("aks:" + ResId.ToLowerInvariant(), c.Id);
        Assert.Equal("aks-demo", c.Name);
        Assert.Equal("koreacentral", c.Region);
        Assert.Equal("https://aks-demo-dns-abc123.hcp.koreacentral.azmk8s.io", c.Endpoint);
        Assert.Equal("1.30.5", c.Version);
        Assert.Equal("running", c.State);
        Assert.Equal(K8sAuthKind.Azure, c.AuthKind);
        Assert.Equal("aks-demo", c.ContextName);

        Assert.Equal(K8sAuthKind.Azure, Kubeconfig.DetectAuth(c.Kubeconfig));
        Assert.Equal("kubelogin", Kubeconfig.ExecCommand(c.Kubeconfig));
        var ctx = Assert.Single(Kubeconfig.ReadContexts(c.Kubeconfig));
        Assert.Equal("aks-demo", ctx.Name);
        Assert.Equal("https://aks-demo-dns-abc123.hcp.koreacentral.azmk8s.io:443", ctx.Server);
        Assert.Contains("RkFLRS1DQQ==", c.Kubeconfig);
        Assert.Contains("azurecli", c.Kubeconfig);
        Assert.Contains(AzureProvider.AksAadServerId, c.Kubeconfig);
        Assert.DoesNotContain("devicecode", c.Kubeconfig);
    }

    [Fact]
    public void BuildAksCluster_Aad_FallsBackToFqdnWhenNoServer()
    {
        const string noServer = """
            apiVersion: v1
            kind: Config
            clusters:
            - name: x
              cluster:
                certificate-authority-data: RkFLRS1DQQ==
            """;
        var info = new AzureProvider.AksInfo("/x", "x", "eastus", "x.hcp.eastus.azmk8s.io", "1.30", "running", true);
        var c = AzureProvider.BuildAksCluster(info, noServer);
        Assert.Equal("https://x.hcp.eastus.azmk8s.io", Assert.Single(Kubeconfig.ReadContexts(c.Kubeconfig)).Server);
    }

    [Fact]
    public void BuildAksCluster_Local_KeepsFirstContextOnly()
    {
        var info = new AzureProvider.AksInfo("/X/Local", "aks-local", "eastus", "aks-local-dns.hcp.eastus.azmk8s.io", "1.29.2", "stopped", false);
        var c = AzureProvider.BuildAksCluster(info, LocalKubeconfig);

        Assert.Equal("aks:/x/local", c.Id);
        Assert.Equal("stopped", c.State);
        Assert.Equal(K8sAuthKind.Kubeconfig, c.AuthKind);
        Assert.Equal("aks-local", c.ContextName);
        var ctx = Assert.Single(Kubeconfig.ReadContexts(c.Kubeconfig));
        Assert.Equal("aks-local", ctx.Name);
        Assert.Contains("RkFLRS1DRVJU", c.Kubeconfig);
        Assert.DoesNotContain("other-token", c.Kubeconfig);
    }

    [Fact]
    public void BuildAksCluster_LocalWithoutContext_Throws()
    {
        var info = new AzureProvider.AksInfo("/x", "x", "eastus", "", "", "running", false);
        Assert.Throws<InvalidOperationException>(() => AzureProvider.BuildAksCluster(info, "apiVersion: v1\nkind: Config\n"));
    }

    [Theory]
    [InlineData("409 The subscription is not registered to use namespace 'Microsoft.ContainerService'. See https://aka.ms/rps-not-found", true)]
    [InlineData("409 MissingSubscriptionRegistration", true)]
    [InlineData("403 The client does not have authorization to perform action", false)]
    [InlineData(null, false)]
    public void IsProviderNotRegistered(string? message, bool expected) =>
        Assert.Equal(expected, AzureProvider.IsProviderNotRegistered(message));
}
