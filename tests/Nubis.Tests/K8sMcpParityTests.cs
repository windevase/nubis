using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>
/// 쿠버네티스 관리 MCP 도구(update · delete · test · find · import_k8s_clusters)의 화면 무관 판단과
/// run_kubectl 의 config view --raw 차단
/// </summary>
[Collection("settings")]
public class K8sMcpParityTests
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
            token: prod-token-SECRET
        - name: dev-user
          user:
            client-certificate: dev.crt
            client-key-data: S0VZLURFVg==
        - name: eks-user
          user:
            exec:
              apiVersion: client.authentication.k8s.io/v1beta1
              command: aws
              args: [eks, get-token, --cluster-name, demo, --role-arn, SECRET-ARG]
        contexts:
        - name: prod
          context: { cluster: prod-cluster, user: prod-user, namespace: web }
        - name: dev
          context: { cluster: dev-cluster, user: dev-user }
        - name: eks
          context: { cluster: prod-cluster, user: eks-user }
        """;

    private static List<string> A(string line) => KubectlArgs.Split(line);

    // ---------------------------------------------------------------- run_kubectl: config view --raw

    [Theory]
    [InlineData("config view --raw")]
    [InlineData("config view --raw=true")]
    [InlineData("config view --raw=false")]   // 값과 상관없이 보수적으로 거부
    [InlineData("config view --Raw")]
    [InlineData("config view --flatten")]
    [InlineData("config view --minify --raw")]
    [InlineData("config view --minify --flatten -o yaml")]
    [InlineData("config view -o jsonpath={.users[0].user.token} --raw")]
    [InlineData("-n x config view --raw")]
    [InlineData("--raw config view")]
    [InlineData("config --raw view")]
    [InlineData("config --flatten")]
    // 옵션 없는 config view 도 password · auth-provider(OIDC) 토큰 · exec env 는 가리지 않는다 (client-go ShortenConfig)
    [InlineData("config view")]
    [InlineData("config view --minify")]
    [InlineData("config view -o yaml")]
    [InlineData("config view -o jsonpath={.users[0].user.auth-provider.config}")]
    [InlineData("-v 6 config view")]
    public void config_view_는_저장된_인증이_나오므로_거부(string line)
    {
        Assert.NotNull(KubectlArgs.Validate(A(line)));
        Assert.False(KubectlArgs.IsReadOnly(A(line)));
    }

    [Fact]
    public void 원격_셸의_kubectl_config_view_는_승인_대상()
    {
        // 원격 서버의 kubeconfig 는 그 서버 것이라 인자 검사(Check fixedCluster=false)는 통과하지만, 자동 승인(읽기 전용)은 하지 않는다 —
        // 옵션 없는 config view 도 basic 인증 password · OIDC refresh-token · exec env 를 가리지 않고(client-go ShortenConfig),
        // --flatten 은 가리기 자체를 건너뛴다. 이름만 보이는 get-contexts · current-context 는 그대로 자동 승인
        Assert.Null(KubectlArgs.Check(A("config view"), fixedCluster: false));
        Assert.False(ReadOnlyCommands.IsReadOnly("kubectl config view", CommandShell.Sh));
        Assert.False(ReadOnlyCommands.IsReadOnly("kubectl config view --raw", CommandShell.Sh));
        Assert.False(ReadOnlyCommands.IsReadOnly("kubectl config view --flatten", CommandShell.Sh));
        Assert.True(ReadOnlyCommands.IsReadOnly("kubectl config get-contexts", CommandShell.Sh));
    }

    [Theory]
    [InlineData("config get-contexts", true)]
    [InlineData("config current-context", true)]
    [InlineData("get --raw /api/v1/namespaces", false)]   // config 없는 --raw 는 API 경로 조회 (자동 승인은 기존대로 안 함)
    [InlineData("get configmap view -o yaml", true)]       // configmap 은 config 가 아니다
    [InlineData("exec web -- kubectl config view --raw", false)]   // -- 뒤는 컨테이너 안 명령 (그 컨테이너의 kubeconfig)
    public void 비밀값이_없는_config_하위_명령과_API_raw_는_허용(string line, bool readOnly)
    {
        Assert.Null(KubectlArgs.Validate(A(line)));
        Assert.Equal(readOnly, KubectlArgs.IsReadOnly(A(line)));
    }

    // ---------------------------------------------------------------- update_k8s_cluster

    private static K8sCluster Imported() => new() { Name = "prod", Group = "team", DefaultNamespace = "web" };

    [Fact]
    public void 편집_검사는_UI_와_같은_규칙()
    {
        var c = Imported();
        Assert.NotNull(McpK8sRules.ParseEdit(c, null, "Bad_NS", null).Error);
        Assert.NotNull(McpK8sRules.ParseEdit(c, null, "a.b", null).Error);
        Assert.NotNull(McpK8sRules.ParseEdit(c, "  ", null, null).Error);
        Assert.NotNull(McpK8sRules.ParseEdit(c, "a\u202Eb", null, null).Error);
        // 클라우드 클러스터는 그룹을 바꿀 수 없다 (UI 메뉴에도 없음)
        var cloud = new K8sCluster { Name = "eks", CloudProfileId = Guid.NewGuid() };
        Assert.NotNull(McpK8sRules.ParseEdit(cloud, null, null, "x").Error);
        Assert.Null(McpK8sRules.ParseEdit(cloud, "eks2", "kube-system", null).Error);
    }

    [Fact]
    public void 같은_값이면_바꾸지_않고_빈_네임스페이스는_kubeconfig_값으로()
    {
        var c = Imported();
        var (same, _) = McpK8sRules.ParseEdit(c, " prod ", "web", "team");
        Assert.True(same!.Empty);
        var (edit, err) = McpK8sRules.ParseEdit(c, "prod-2", "", "");
        Assert.Null(err);
        Assert.Equal("prod-2", edit!.Name);
        Assert.Equal("", edit.Namespace);
        Assert.Equal("", edit.Group);
        Assert.Equal(3, edit.Changes.Count);
    }

    [Fact]
    public void 승인_대기_중_같은_항목이_바뀌면_반영하지_않는다()
    {
        var before = Imported();
        var (edit, _) = McpK8sRules.ParseEdit(before, null, "api", null);
        var now = before.Clone();
        now.Name = "renamed";   // 다른 항목은 상관없다
        Assert.False(McpK8sRules.ChangedWhileWaiting(before, now, edit!));
        now.DefaultNamespace = "other";
        Assert.True(McpK8sRules.ChangedWhileWaiting(before, now, edit!));
    }

    [Fact]
    public void 삭제_확인_문구는_클라우드_클러스터에_다시_나타남을_알린다()
    {
        Assert.DoesNotContain("다음 동기화", MainWindow.K8sDeleteMessage("prod", isCloud: false));
        Assert.Contains("다음 동기화", MainWindow.K8sDeleteMessage("eks", isCloud: true));
    }

    // ---------------------------------------------------------------- find_k8s_clusters

    [Fact]
    public void 프로필_생략은_UI_기본_선택과_같고_이름은_정확히_하나여야()
    {
        var a = new CloudProfile { Name = "prod-a", SyncClusters = true };
        var b = new CloudProfile { Name = "prod-b" };
        var list = new List<CloudProfile> { a, b };
        Assert.Equal(new[] { a.Id }.ToHashSet(), McpK8sRules.ResolveFindProfiles(list, Array.Empty<string>()).Ids);
        Assert.Equal(new[] { b.Id }.ToHashSet(), McpK8sRules.ResolveFindProfiles(list, new[] { "prod-b" }).Ids);
        Assert.NotNull(McpK8sRules.ResolveFindProfiles(list, new[] { "prod" }).Error);    // 둘과 부분 일치
        Assert.NotNull(McpK8sRules.ResolveFindProfiles(list, new[] { "nope" }).Error);
        Assert.NotNull(McpK8sRules.ResolveFindProfiles(list, new[] { " " }).Error);
    }

    [Fact]
    public void 고르지_않은_프로필의_동기화_끄기와_제거는_요청했을_때만()
    {
        var a = new CloudProfile { SyncClusters = true };
        var b = new CloudProfile { SyncClusters = true };
        var clusters = new List<K8sCluster> { new() { Name = "b1", CloudProfileId = b.Id, CloudClusterId = "1" } };
        var sel = new HashSet<Guid> { a.Id };
        var keep = McpK8sRules.FindPlan(new[] { a, b }, sel, clusters, disableUnselected: false, removeUnselected: false);
        Assert.Equal(new[] { a }, keep.Fetch);
        Assert.Empty(keep.Disable);   // UI 는 끄지만 MCP 는 요청했을 때만
        Assert.Empty(keep.Remove);
        var all = McpK8sRules.FindPlan(new[] { a, b }, sel, clusters, disableUnselected: true, removeUnselected: true);
        Assert.Equal(new[] { b }, all.Disable);
        Assert.Single(all.Remove);
    }

    [Fact]
    public void 승인_뒤_지울_클러스터는_승인한_것_중_아직_있고_여전히_해제된_프로필만()
    {
        var b = Guid.NewGuid();
        var c1 = new K8sCluster { Name = "1", CloudProfileId = b };
        var c2 = new K8sCluster { Name = "2", CloudProfileId = b };
        var added = new K8sCluster { Name = "new", CloudProfileId = b };
        var left = McpK8sRules.StillRemovable(new[] { c1, c2 }, new[] { c1, added }, new HashSet<Guid>());
        Assert.Equal(new[] { c1 }, left);   // 대기 중 지워진 c2 · 새로 생긴 클러스터(승인 안 받음) 제외
        Assert.Empty(McpK8sRules.StillRemovable(new[] { c1 }, new[] { c1 }, new HashSet<Guid> { b }));
    }

    // ---------------------------------------------------------------- import_k8s_clusters: 경로

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [Fact]
    public void 원본_경로_kube_폴더는_허용_데이터_폴더_ssh_UNC_는_거부()
    {
        var data = Path.Combine(Path.GetTempPath(), "nubis-k8s-mcp-data");
        var ssh = Path.Combine(Home, ".ssh");
        // ~/.kube 는 UI 가져오기의 기본 원본이고, 내용은 Claude 에게 돌려주지 않고 앱이 DPAPI 로 저장만 한다
        Assert.Null(McpK8sRules.BlockedSourcePath(Path.Combine(Home, ".kube", "config"), data, null, ssh));
        Assert.Null(McpK8sRules.BlockedSourcePath(@"C:\work\eks.kubeconfig", data, null, ssh));
        Assert.NotNull(McpK8sRules.BlockedSourcePath(Path.Combine(data, "hosts.json"), data, null, ssh));
        Assert.NotNull(McpK8sRules.BlockedSourcePath(Path.Combine(ssh, "config"), data, null, ssh));
        Assert.NotNull(McpK8sRules.BlockedSourcePath(@"\\evil.example\share\kubeconfig", data, null, ssh));
        Assert.NotNull(McpK8sRules.BlockedSourcePath(@"//evil.example/share/kubeconfig", data, null, ssh));
        Assert.NotNull(McpK8sRules.BlockedSourcePath(@"\\?\C:\x\kubeconfig", data, null, ssh));
        Assert.NotNull(McpK8sRules.BlockedSourcePath(@"C:\x\kubeconfig:ads", data, null, ssh));
    }

    [Fact]
    public void 인증_파일은_kube_인증서_이름은_허용_데이터_ssh_UNC_상대경로는_거부()
    {
        var data = Path.Combine(Path.GetTempPath(), "nubis-k8s-mcp-data");
        var root = Path.Combine(Path.GetTempPath(), "nubis-k8s-mcp-root");
        var ssh = Path.Combine(Home, ".ssh");
        Assert.Null(McpK8sRules.BlockedReferencedFile(Path.Combine(Home, ".minikube", "profiles", "minikube", "client.key"), data, root, ssh));
        Assert.Null(McpK8sRules.BlockedReferencedFile(@"C:\certs\ca.pem", data, root, ssh));
        Assert.NotNull(McpK8sRules.BlockedReferencedFile(Path.Combine(data, "vault.json"), data, root, ssh));
        Assert.NotNull(McpK8sRules.BlockedReferencedFile(Path.Combine(root, "login.json"), data, root, ssh));
        Assert.NotNull(McpK8sRules.BlockedReferencedFile(Path.Combine(ssh, "id_rsa"), data, root, ssh));
        Assert.NotNull(McpK8sRules.BlockedReferencedFile(@"\\attacker.example\s\token", data, root, ssh));
        Assert.NotNull(McpK8sRules.BlockedReferencedFile(@"/\attacker.example\s\token", data, root, ssh));
        Assert.NotNull(McpK8sRules.BlockedReferencedFile("dev.crt", data, root, ssh));
    }

    [Fact]
    public void 가져올_때_읽을_인증_파일_경로를_미리_안다()
    {
        var doc = Kubeconfig.Load(Multi);
        var dir = Path.Combine(Path.GetTempPath(), "kc");
        // dev: certificate-authority 파일 + client-certificate 파일 (client-key 는 -data 라 읽지 않음)
        Assert.Equal(new[] { Path.Combine(dir, "ca-dev.crt"), Path.Combine(dir, "dev.crt") }, Kubeconfig.ReferencedFiles(doc, "dev", dir));
        Assert.Empty(Kubeconfig.ReferencedFiles(doc, "prod", dir));
        Assert.Empty(Kubeconfig.ReferencedFiles(doc, "missing", dir));
        // 붙여넣기처럼 기준 폴더가 없으면 원문 (BlockedReferencedFile 이 상대 경로로 거부)
        Assert.Equal(new[] { "ca-dev.crt", "dev.crt" }, Kubeconfig.ReferencedFiles(doc, "dev", null));
    }

    // ---------------------------------------------------------------- import_k8s_clusters: 고르기 · 미리보기

    [Fact]
    public void 기본_선택은_가져오기_창과_같다()
    {
        var doc = Kubeconfig.Load(Multi);
        // 등록된 클러스터가 없으면 전부
        Assert.All(KubeconfigImport.Describe(doc, null, new List<K8sCluster>()), i => Assert.True(i.DefaultSelected));
        // 있으면 current-context 만, 이미 있는 것은 고르지 않는다
        var existing = new List<K8sCluster> { new() { Name = "x", ContextName = "eks", Endpoint = "https://prod.example.com:6443/" } };
        var infos = KubeconfigImport.Describe(doc, null, existing);
        Assert.Equal(new[] { "prod" }, infos.Where(i => i.DefaultSelected).Select(i => i.Context.Name));
        Assert.True(infos.Single(i => i.Context.Name == "eks").AlreadyImported);
        Assert.Equal(K8sAuthKind.Aws, infos.Single(i => i.Context.Name == "eks").Auth);
    }

    [Fact]
    public void context_지정과_없는_이름()
    {
        var infos = KubeconfigImport.Describe(Kubeconfig.Load(Multi), null, new List<K8sCluster> { new() { Name = "y" } });
        var (sel, unknown) = McpK8sRules.SelectContexts(infos, new[] { "dev", "eks", "nope", "dev" });
        Assert.Equal(new[] { "dev", "eks" }, sel.Select(i => i.Context.Name));
        Assert.Equal(new[] { "nope" }, unknown);
        var (def, none) = McpK8sRules.SelectContexts(infos, Array.Empty<string>());
        Assert.Equal(new[] { "prod" }, def.Select(i => i.Context.Name));
        Assert.Empty(none);
    }

    [Fact]
    public void 미리보기에는_토큰_인증서_exec_인자가_없다()
    {
        var doc = Kubeconfig.Load(Multi);
        var infos = KubeconfigImport.Describe(doc, null, new List<K8sCluster>());
        var counts = infos.ToDictionary(i => i.Context.Name, i => Kubeconfig.ReferencedFiles(doc, i.Context.Name, null).Count);
        var text = McpK8sRules.FormatPreview("C:\\kc\\config", infos, infos, counts);
        Assert.Contains("https://prod.example.com:6443", text);
        Assert.Contains("exec: aws", text);
        foreach (var secret in new[] { "SECRET", "Q0EtUFJPRA", "S0VZLURFVg", "prod-token" })
            Assert.DoesNotContain(secret, text);
    }

    [Fact]
    public void 가져온_클러스터는_UI_와_같은_모양()
    {
        var doc = Kubeconfig.Load(Multi);
        var ctx = Kubeconfig.ReadContexts(doc).Single(c => c.Name == "prod");
        var c = KubeconfigImport.ToCluster(doc, ctx, null, "team");
        Assert.Equal("prod", c.Name);
        Assert.Equal("team", c.Group);
        Assert.Equal("web", c.DefaultNamespace);
        Assert.Equal(K8sSource.Imported, c.Source);
        Assert.Equal("https://prod.example.com:6443", c.Endpoint);
        Assert.Contains("prod-token-SECRET", c.Kubeconfig);   // 인증은 앱 안에만 (DPAPI 저장)
        Assert.Single(Kubeconfig.ReadContexts(c.Kubeconfig));
    }

    [Fact]
    public void 기본_원본_여러_파일은_없는_파일을_건너뛰고_합친다()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nubis-k8s-mcp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var one = Path.Combine(dir, "a.yaml");
            File.WriteAllText(one, Multi);
            var s = KubeconfigImport.ReadDefault(new[] { Path.Combine(dir, "missing.yaml"), one });
            Assert.Null(s.Error);
            Assert.Null(s.BaseDir);   // 합친 결과는 경로가 절대 경로로 바뀌어 기준 폴더가 없다
            Assert.Equal(3, Kubeconfig.ReadContexts(s.Yaml).Count);
            Assert.NotNull(KubeconfigImport.ReadDefault(new[] { Path.Combine(dir, "missing.yaml") }).Error);
            Assert.Equal(dir, KubeconfigImport.ReadPath(one).BaseDir);
        }
        finally { Directory.Delete(dir, true); }
    }
}
