using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>쿠버네티스: kubeconfig 자르기 · 인자 검사 · 저장소 · 보안 임시 파일 · Claude 차단 규칙</summary>
[Collection("settings")]
public class KubernetesTests
{
    // 인증서·토큰은 실제 값이 아닌 자리표시자
    private const string Multi = """
        apiVersion: v1
        kind: Config
        current-context: prod
        clusters:
        - name: prod-cluster
          cluster:
            server: https://prod.example.com:6443
            certificate-authority-data: Q0EtUFJPRA==
        - name: dev-cluster
          cluster:
            server: https://dev.example.com
            certificate-authority: ca-dev.crt
        users:
        - name: prod-user
          user:
            token: prod-token
        - name: dev-user
          user:
            client-certificate: dev.crt
            client-key-data: S0VZLURFVg==
        - name: eks-user
          user:
            exec:
              apiVersion: client.authentication.k8s.io/v1beta1
              command: aws
              args: [eks, get-token, --cluster-name, demo]
        contexts:
        - name: prod
          context: { cluster: prod-cluster, user: prod-user, namespace: web }
        - name: dev
          context: { cluster: dev-cluster, user: dev-user }
        - name: eks
          context: { cluster: prod-cluster, user: eks-user }
        """;

    [Fact]
    public void context_목록을_읽는다()
    {
        var list = Kubeconfig.ReadContexts(Multi);
        Assert.Equal(new[] { "prod", "dev", "eks" }, list.Select(c => c.Name));
        Assert.Equal("https://prod.example.com:6443", list[0].Server);
        Assert.Equal("web", list[0].Namespace);
        Assert.Equal("prod", Kubeconfig.CurrentContext(Multi));
    }

    [Fact]
    public void context_하나만_담은_독립_kubeconfig_를_만든다()
    {
        var one = Kubeconfig.Extract(Multi, "prod", baseDir: null);
        var ctx = Assert.Single(Kubeconfig.ReadContexts(one));
        Assert.Equal("prod", ctx.Name);
        Assert.Equal("https://prod.example.com:6443", ctx.Server);
        Assert.Contains("prod-token", one);
        // 다른 클러스터의 인증은 섞이지 않는다
        Assert.DoesNotContain("dev-cluster", one);
        Assert.DoesNotContain("S0VZLURFVg==", one);
        Assert.Equal("prod", Kubeconfig.CurrentContext(one));
        Assert.Equal(K8sAuthKind.Kubeconfig, Kubeconfig.DetectAuth(one));
    }

    [Fact]
    public void 파일로_가리킨_인증서는_내용으로_넣는다()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Nubis.Tests", "kube-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "ca-dev.crt"), "CA-DEV");
            File.WriteAllText(Path.Combine(dir, "dev.crt"), "CERT-DEV");
            var one = Kubeconfig.Extract(Multi, "dev", dir);
            // 새로 넣는 값은 문자열로 읽히게 큰따옴표
            Assert.Contains("certificate-authority-data: \"" + Convert.ToBase64String("CA-DEV"u8.ToArray()) + "\"", one);
            Assert.Contains("client-certificate-data: \"" + Convert.ToBase64String("CERT-DEV"u8.ToArray()) + "\"", one);
            Assert.Contains("S0VZLURFVg==", one);
            Assert.DoesNotContain("ca-dev.crt", one);
            // 붙여 넣은 kubeconfig(기준 폴더 없음)가 상대 경로 파일을 가리키면 알린다
            Assert.Throws<FormatException>(() => Kubeconfig.Extract(Multi, "dev", baseDir: null));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void exec_플러그인과_기본_네임스페이스()
    {
        var eks = Kubeconfig.Extract(Multi, "eks", null);
        Assert.Equal(K8sAuthKind.Aws, Kubeconfig.DetectAuth(eks));
        Assert.Equal("aws", Kubeconfig.ExecCommand(eks));

        var built = Kubeconfig.BuildExec("gke_demo", "https://1.2.3.4", "Q0E=", "gke-gcloud-auth-plugin", Array.Empty<string>());
        Assert.Equal(K8sAuthKind.Gcp, Kubeconfig.DetectAuth(built));
        Assert.Equal("gke_demo", Kubeconfig.CurrentContext(built));

        var ns = Kubeconfig.WithNamespace(Kubeconfig.Extract(Multi, "prod", null), "prod", "payments");
        Assert.Equal("payments", Kubeconfig.ReadContexts(ns)[0].Namespace);
    }

    [Theory]
    [InlineData("")]
    [InlineData("just text")]
    [InlineData("a: [unclosed")]
    public void 깨진_kubeconfig_는_FormatException(string yaml)
    {
        Assert.Throws<FormatException>(() => Kubeconfig.ReadContexts(yaml));
    }

    [Theory]
    [InlineData("get pods --kubeconfig=/tmp/x")]
    [InlineData("get pods --kubeconfig /tmp/x")]
    [InlineData("get pods -s https://evil.example")]
    [InlineData("get pods -shttps://evil.example")]
    [InlineData("get pods --server=https://evil.example")]
    [InlineData("get pods --serv=https://evil.example")]
    [InlineData("get pods --context other")]
    [InlineData("get pods --user admin")]
    [InlineData("get pods --token abc")]
    [InlineData("get pods --as system:admin")]
    [InlineData("get pods --insecure-skip-tls-verify")]
    public void 클러스터와_인증을_바꾸는_인자는_거부(string line)
    {
        Assert.NotNull(KubectlArgs.Validate(KubectlArgs.Split(line)));
        Assert.False(KubectlArgs.IsReadOnly(KubectlArgs.Split(line)));
    }

    [Theory]
    [InlineData("get pods -n kube-system", true)]
    [InlineData("kubectl get pods -A -o wide", true)]
    [InlineData("describe pod web-1", true)]
    [InlineData("logs web-1 --tail=100", true)]
    [InlineData("top pods", true)]
    [InlineData("exec web-1 -- ls", false)]
    [InlineData("delete pod web-1", false)]
    [InlineData("apply -f x.yaml", false)]
    [InlineData("scale deploy web --replicas=0", false)]
    [InlineData("port-forward svc/web 8080:80", false)]
    [InlineData("config get-contexts", true)]   // config view(--raw 포함)는 거부로 바뀜 (K8sMcpParityTests)
    public void 읽기_전용_판정(string line, bool readOnly)
    {
        var args = KubectlArgs.Split(line);
        Assert.Null(KubectlArgs.Validate(args));
        Assert.Equal(readOnly, KubectlArgs.IsReadOnly(args));
    }

    [Fact]
    public void exec_의_컨테이너_명령은_검사하지_않고_줄바꿈은_거부()
    {
        // -- 뒤는 컨테이너 안 명령이라 -s 같은 값이 와도 kubectl 옵션이 아니다
        Assert.Null(KubectlArgs.Validate(new[] { "exec", "web", "--", "sh", "-s" }));
        Assert.NotNull(KubectlArgs.Validate(new[] { "get", "pods\nrm" }));
        Assert.NotNull(KubectlArgs.Validate(Array.Empty<string>()));
        Assert.Equal(new[] { "get", "pods", "-l", "app=web x" }, KubectlArgs.Split("kubectl get pods -l 'app=web x'"));
        Assert.Throws<FormatException>(() => KubectlArgs.Split("get 'pods"));
    }

    [Fact]
    public void 클러스터_kubeconfig_는_암호화_저장되고_다시_읽힌다()
    {
        K8sStore.ResetForTests();
        var c = new K8sCluster { Name = "prod", ContextName = "prod", Kubeconfig = Kubeconfig.Extract(Multi, "prod", null) };
        K8sStore.Upsert(c);
        var bytes = File.ReadAllBytes(K8sStore.FilePath);
        Assert.True(SafeFile.IsProtected(bytes));
        Assert.DoesNotContain("prod-token", SafeFile.UnprotectBytes(bytes));   // 필드도 따로 암호화
        K8sStore.ResetForTests();
        var back = K8sStore.Get(c.Id)!;
        Assert.Contains("prod-token", back.Kubeconfig);
        K8sStore.Remove(c.Id);
        Assert.Null(K8sStore.Get(c.Id));
    }

    [Fact]
    public void 임시_kubeconfig_는_현재_사용자만_읽고_닫으면_지워진다()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Nubis.Tests", "k8s-run-" + Guid.NewGuid().ToString("N")[..6]);
        string path;
        using (var f = SecureTempFile.Create(dir, "secret: x"))
        {
            path = f.Path;
            Assert.Equal("secret: x", File.ReadAllText(path));
            var rules = new FileInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
            using var me = WindowsIdentity.GetCurrent();
            Assert.All(rules, r => Assert.Equal(me.User, r.IdentityReference));
            Assert.True(new FileInfo(path).GetAccessControl().AreAccessRulesProtected);
            // 쓰는 중에는 정리가 지우지 못한다
            Assert.Equal(0, SecureTempFile.CleanupStale(dir));
        }
        Assert.False(File.Exists(path));
        File.WriteAllText(Path.Combine(dir, "left.yaml"), "x");
        Assert.Equal(1, SecureTempFile.CleanupStale(dir));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Claude_는_클러스터_파일과_임시_kubeconfig_를_읽지_못한다()
    {
        var rules = ClaudeGuard.DenyRules(@"C:\Users\a\AppData\Roaming\Nubis");
        Assert.Contains("Read(//c/Users/a/AppData/Roaming/Nubis/k8s_clusters.json*)", rules);
        Assert.Contains("Read(//c/Users/a/AppData/Roaming/Nubis/k8s-run/**)", rules);
        Assert.Contains("PowerShell(*k8s-run*)", rules);
        Assert.Contains("Bash(*ProtectedKubeconfig*)", rules);
    }

    [Fact]
    public void 추가_환경변수가_자식_환경_블록에_들어간다()
    {
        var block = ChildEnvironment.Build(new Dictionary<string, string> { ["KUBECONFIG"] = @"C:\x\a.yaml" });
        Assert.Contains("KUBECONFIG=C:\\x\\a.yaml\0", block);
        Assert.Single(block.Split('\0'), s => s.StartsWith("KUBECONFIG=", StringComparison.OrdinalIgnoreCase));
    }
}
