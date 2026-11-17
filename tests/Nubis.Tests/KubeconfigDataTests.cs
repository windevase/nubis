using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nubis;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Nubis.Tests;

/// <summary>kubeconfig 문자열 스칼라 · KUBECONFIG 병합 · exec 상대 경로 · 엔드포인트 정규화 · 가져오기 중복 판정</summary>
[Collection("settings")]
public class KubeconfigDataTests
{
    // 숫자·bool·버전처럼 보이는 이름 (원본에서는 따옴표로 문자열)
    private const string NumericNames = """
        apiVersion: v1
        kind: Config
        current-context: "123"
        clusters:
        - name: "456"
          cluster:
            server: https://numeric.example.com
        users:
        - name: "true"
          user:
            token: "1.29"
        contexts:
        - name: "123"
          context: { cluster: "456", user: "true", namespace: "789" }
        """;

    /// <summary>YAML 에서 key 의 스칼라 값 노드들 (스타일 확인용)</summary>
    private static List<YamlScalarNode> Scalars(string yaml, string key)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        var found = new List<YamlScalarNode>();
        void Walk(YamlNode n)
        {
            switch (n)
            {
                case YamlMappingNode m:
                    foreach (var (k, v) in m.Children)
                    {
                        if (k is YamlScalarNode { Value: var kv } && kv == key && v is YamlScalarNode s) found.Add(s);
                        Walk(v);
                    }
                    break;
                case YamlSequenceNode seq:
                    foreach (var c in seq.Children) Walk(c);
                    break;
            }
        }
        Walk(stream.Documents[0].RootNode);
        return found;
    }

    private static void AssertQuoted(string yaml, string key, string value) =>
        Assert.Contains(Scalars(yaml, key), s => s.Value == value && s.Style != YamlDotNet.Core.ScalarStyle.Plain);

    [Fact]
    public void 숫자처럼_보이는_이름은_따옴표로_남는다()
    {
        var one = Kubeconfig.Extract(NumericNames, "123", baseDir: null);
        AssertQuoted(one, "name", "123");
        AssertQuoted(one, "name", "456");
        AssertQuoted(one, "name", "true");
        AssertQuoted(one, "current-context", "123");
        AssertQuoted(one, "token", "1.29");
        // 왕복: 다시 읽어도 같은 문자열
        var ctx = Assert.Single(Kubeconfig.ReadContexts(one));
        Assert.Equal(("123", "456", "true", "789"), (ctx.Name, ctx.Cluster, ctx.User, ctx.Namespace));
        Assert.Equal("123", Kubeconfig.CurrentContext(one));

        // 원본 context 가 따옴표 없이 적었어도 (cluster: 456) 잘라 낸 쪽은 문자열
        var plain = Kubeconfig.Extract(NumericNames.Replace("cluster: \"456\", user: \"true\"", "cluster: 456, user: true"), "123", null);
        AssertQuoted(plain, "cluster", "456");
        AssertQuoted(plain, "user", "true");

        var ns = Kubeconfig.WithNamespace(one, "123", "2024");
        AssertQuoted(ns, "namespace", "2024");
        Assert.Equal("2024", Kubeconfig.ReadContexts(ns)[0].Namespace);
    }

    [Fact]
    public void 파일에서_읽은_숫자_토큰도_문자열()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Nubis.Tests", "kube-tok-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "tok"), "123456\n");
            const string yaml = """
                clusters:
                - name: c
                  cluster: { server: https://x }
                users:
                - name: u
                  user: { tokenFile: tok }
                contexts:
                - name: x
                  context: { cluster: c, user: u }
                """;
            AssertQuoted(Kubeconfig.Extract(yaml, "x", dir), "token", "123456");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void exec_kubeconfig_의_계정ID_환경변수는_문자열이고_bool_은_plain()
    {
        var built = Kubeconfig.BuildExec("123456789012", "https://eks.example.com", "Q0E=", "aws",
            new[] { "eks", "get-token", "--cluster-name", "007" },
            new Dictionary<string, string> { ["AWS_PROFILE"] = "123456789012" });
        AssertQuoted(built, "value", "123456789012");
        AssertQuoted(built, "name", "123456789012");
        AssertQuoted(built, "current-context", "123456789012");
        AssertQuoted(built, "command", "aws");
        Assert.Contains(Scalars(built, "provideClusterInfo"), s => s.Value == "false" && s.Style == YamlDotNet.Core.ScalarStyle.Plain);
        Assert.Contains("- \"007\"", built);
        Assert.Equal(K8sAuthKind.Aws, Kubeconfig.DetectAuth(built));
    }

    [Theory]
    [InlineData(";", new[] { "DEFAULT" })]
    [InlineData("", new[] { "DEFAULT" })]
    [InlineData(null, new[] { "DEFAULT" })]
    [InlineData(@"C:\a\config;;C:\A\config; C:\b\config ", new[] { @"C:\a\config", @"C:\b\config" })]
    [InlineData("\"C:\\q\\config\"", new[] { @"C:\q\config" })]
    public void KUBECONFIG_파일_목록(string? env, string[] expected)
    {
        Assert.Equal(expected, Kubeconfig.EnvPaths(env, "DEFAULT"));
    }

    [Fact]
    public void 여러_파일은_먼저_나온_이름이_이기고_상대_경로는_각_파일_폴더_기준()
    {
        var root = Path.Combine(Path.GetTempPath(), "Nubis.Tests", "kube-merge-" + Guid.NewGuid().ToString("N")[..6]);
        var d1 = Path.Combine(root, "one");
        var d2 = Path.Combine(root, "two");
        Directory.CreateDirectory(d1);
        Directory.CreateDirectory(d2);
        try
        {
            File.WriteAllText(Path.Combine(d2, "ca.crt"), "CA-TWO");
            const string f1 = """
                clusters:
                - name: shared
                  cluster: { server: https://first.example.com }
                users:
                - name: u1
                  user: { token: t1 }
                contexts:
                - name: a
                  context: { cluster: shared, user: u1 }
                """;
            const string f2 = """
                current-context: b
                clusters:
                - name: shared
                  cluster: { server: https://second.example.com }
                - name: other
                  cluster: { server: https://other.example.com, certificate-authority: ca.crt }
                users:
                - name: u2
                  user:
                    exec: { command: bin/plugin, apiVersion: client.authentication.k8s.io/v1beta1 }
                contexts:
                - name: a
                  context: { cluster: other, user: u2 }
                - name: b
                  context: { cluster: other, user: u2 }
                """;
            var merged = Kubeconfig.Merge(new (string, string?)[] { ("", d1), (f1, d1), (f2, d2) });
            var ctxs = Kubeconfig.ReadContexts(merged);
            Assert.Equal(new[] { "a", "b" }, ctxs.Select(c => c.Name));
            Assert.Equal("https://first.example.com", ctxs[0].Server);   // 이름이 겹치면 첫 파일
            Assert.Equal("shared", ctxs[0].Cluster);
            Assert.Equal("b", Kubeconfig.CurrentContext(merged));          // 첫 파일엔 current-context 가 없다
            // 둘째 파일의 상대 경로 인증서·exec 명령은 그 파일 폴더 기준 — 기준 폴더 없이 잘라도 읽힌다
            var one = Kubeconfig.Extract(merged, "b", baseDir: null);
            Assert.Contains(Convert.ToBase64String("CA-TWO"u8.ToArray()), one);
            Assert.Equal(Path.Combine(d2, "bin", "plugin"), Kubeconfig.ExecCommand(one));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void exec_명령의_상대_경로는_원본_폴더_기준_절대_경로()
    {
        const string yaml = """
            clusters:
            - name: c
              cluster: { server: https://x }
            users:
            - name: rel
              user:
                exec: { command: ./tools/auth.exe }
            - name: path
              user:
                exec: { command: kubelogin }
            contexts:
            - name: rel
              context: { cluster: c, user: rel }
            - name: path
              context: { cluster: c, user: path }
            """;
        var dir = Path.Combine(Path.GetTempPath(), "Nubis.Tests", "kdir");
        Assert.Equal(Path.GetFullPath(Path.Combine(dir, "tools", "auth.exe")), Kubeconfig.ExecCommand(Kubeconfig.Extract(yaml, "rel", dir)));
        Assert.Equal("kubelogin", Kubeconfig.ExecCommand(Kubeconfig.Extract(yaml, "path", dir)));   // PATH 에서 찾는 명령은 그대로
        Assert.Throws<FormatException>(() => Kubeconfig.Extract(yaml, "rel", baseDir: null));        // 붙여넣기는 기준 폴더가 없다
        // 목록 배지용: 인증서 파일 없이도 인증 방식·명령을 안다
        var doc = Kubeconfig.Load(yaml);
        Assert.Equal((K8sAuthKind.Azure, "kubelogin"), Kubeconfig.ContextAuth(doc, "path", null));
        Assert.Equal(Path.GetFullPath(Path.Combine(dir, "tools", "auth.exe")), Kubeconfig.ContextAuth(doc, "rel", dir).Command);
    }

    [Theory]
    [InlineData("https://ABC.hcp.eastus.azmk8s.io", "https://abc.hcp.eastus.azmk8s.io:443/", true)]
    [InlineData("https://x.example.com:6443", "https://x.example.com:6443", true)]
    [InlineData("https://x.example.com:6443", "https://x.example.com", false)]
    [InlineData("http://x.example.com", "https://x.example.com", false)]
    [InlineData("34.1.2.3", "https://34.1.2.3:443", true)]
    [InlineData("https://rancher/k8s/clusters/c-1", "https://rancher:443/k8s/clusters/c-1/", true)]
    [InlineData("https://rancher/k8s/clusters/c-1", "https://rancher/k8s/clusters/c-2", false)]
    [InlineData("", "", true)]
    [InlineData("", "https://x", false)]
    public void API_서버_주소_비교(string a, string b, bool same)
    {
        Assert.Equal(same, K8sSync.SameEndpoint(a, b));
    }

    [Fact]
    public void 가져오기_중복_판정()
    {
        var existing = new List<K8sCluster>
        {
            new() { ContextName = "prod", Endpoint = "https://prod.example.com" },
            new() { ContextName = "empty", Endpoint = "" },
        };
        Assert.True(K8sSync.AlreadyImported(existing, new KubeContext("prod", "c", "u", null, "https://PROD.example.com:443/")));
        Assert.False(K8sSync.AlreadyImported(existing, new KubeContext("prod", "c", "u", null, "https://other.example.com")));
        // 주소가 빈 기존 클러스터는 이름만 같은 다른 서버의 context 를 막지 않는다
        Assert.False(K8sSync.AlreadyImported(existing, new KubeContext("empty", "c", "u", null, "https://new.example.com")));
        Assert.True(K8sSync.AlreadyImported(existing, new KubeContext("empty", "c", "u", null, null)));
    }

    [Fact]
    public void 동기화는_기본_포트만_다른_직접_가져온_클러스터를_중복으로_만들지_않는다()
    {
        var p = new CloudProfile();
        var list = new List<K8sCluster> { new() { Name = "aks", ContextName = "aks", Endpoint = "https://aks.hcp.eastus.azmk8s.io" } };
        var cc = new CloudCluster("aks:/sub/x", "aks", "eastus", "https://aks.hcp.eastus.azmk8s.io:443", "1.31", "Succeeded", K8sAuthKind.Azure, "kc", "aks");
        Assert.Equal((0, 0, 0), K8sSync.Apply(p, new ClusterFetchResult(new() { cc }, new()), list));
        Assert.Single(list);
    }

    [Theory]
    [InlineData("web", "web")]
    [InlineData(" web ", "web")]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("Bad_NS", "")]
    public void 가져올_때_context_네임스페이스를_기본_네임스페이스로(string? ns, string expected)
    {
        Assert.Equal(expected, K8sCluster.ImportedNamespace(ns));
    }

    [Fact]
    public void 명령_존재_확인()
    {
        var file = Path.Combine(Path.GetTempPath(), "Nubis.Tests", "cmd-" + Guid.NewGuid().ToString("N")[..6] + ".exe");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "");
        try
        {
            Assert.True(Kubectl.CommandExists(file));
            Assert.True(Kubectl.CommandExists(file));   // 캐시에서
            Assert.False(Kubectl.CommandExists(file + ".missing"));
            Assert.False(Kubectl.CommandExists(" "));
        }
        finally { File.Delete(file); }
    }
}
