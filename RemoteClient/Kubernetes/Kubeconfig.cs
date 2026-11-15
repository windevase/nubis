using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Nubis;

/// <summary>kubeconfig 의 context 하나 (가져오기 대화상자 목록용)</summary>
public sealed record KubeContext(string Name, string Cluster, string User, string? Namespace, string? Server);

/// <summary>
/// kubeconfig 읽기·자르기·만들기 (화면 무관, KubeconfigTests).
/// YAML 은 YamlDotNet 표현 모델(YamlStream)로만 읽는다 — 타입 인스턴스화 없음.
/// 클러스터마다 인증을 따로 두려고, 여러 context 가 든 파일에서 context 하나(cluster + user + context)만 담은
/// 독립 kubeconfig 를 만든다. 파일 경로로 가리킨 인증서·토큰은 내용으로 넣어(인라인) 원본 파일이 없어도 쓰이게 한다.
/// </summary>
public static class Kubeconfig
{
    /// <summary>받아들이는 최대 크기 (붙여넣기·파일). kubeconfig 는 보통 수십 KB</summary>
    public const int MaxChars = 1 << 20;

    private static YamlMappingNode LoadRoot(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) throw new FormatException(L.T("kubeconfig 내용이 비어 있습니다."));
        if (yaml.Length > MaxChars) throw new FormatException(L.T("kubeconfig 가 너무 큽니다 (1MB 이하)."));
        var stream = new YamlStream();
        try { stream.Load(new StringReader(yaml)); }
        catch (YamlException ex) { throw new FormatException(L.F("kubeconfig 를 읽지 못했습니다: {0}", ex.Message)); }
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
            throw new FormatException(L.T("kubeconfig 형식이 아닙니다."));
        return root;
    }

    private static YamlNode? Get(YamlMappingNode m, string key) =>
        m.Children.TryGetValue(new YamlScalarNode(key), out var n) ? n : null;

    private static string? Str(YamlMappingNode m, string key) => Get(m, key) is YamlScalarNode s ? s.Value : null;

    private static YamlMappingNode? Map(YamlMappingNode m, string key) => Get(m, key) as YamlMappingNode;

    private static IEnumerable<YamlMappingNode> Items(YamlMappingNode root, string key) =>
        Get(root, key) is YamlSequenceNode seq ? seq.Children.OfType<YamlMappingNode>() : Enumerable.Empty<YamlMappingNode>();

    private static YamlMappingNode? Named(YamlMappingNode root, string key, string name) =>
        Items(root, key).FirstOrDefault(i => Str(i, "name") == name);

    /// <summary>context 목록 (이름 순서는 파일 순서)</summary>
    public static List<KubeContext> ReadContexts(string yaml)
    {
        var root = LoadRoot(yaml);
        var list = new List<KubeContext>();
        foreach (var item in Items(root, "contexts"))
        {
            var name = Str(item, "name");
            var ctx = Map(item, "context");
            if (string.IsNullOrEmpty(name) || ctx == null) continue;
            var cluster = Str(ctx, "cluster") ?? "";
            var server = Named(root, "clusters", cluster) is { } c && Map(c, "cluster") is { } cm ? Str(cm, "server") : null;
            list.Add(new KubeContext(name, cluster, Str(ctx, "user") ?? "", Str(ctx, "namespace"), server));
        }
        return list;
    }

    /// <summary>current-context (없으면 null)</summary>
    public static string? CurrentContext(string yaml) => Str(LoadRoot(yaml), "current-context") is { Length: > 0 } c ? c : null;

    /// <summary>
    /// context 하나만 담은 독립 kubeconfig. baseDir 은 원본 파일이 있던 폴더 (상대 경로 인증서 파일을 찾는 기준, 붙여넣기면 null).
    /// 인증서·키·토큰 파일은 *-data / token 으로 넣는다. 읽지 못하면 FormatException (화면에 보이는 문구)
    /// </summary>
    public static string Extract(string yaml, string context, string? baseDir)
    {
        var root = LoadRoot(yaml);
        var ctxItem = Named(root, "contexts", context) ?? throw new FormatException(L.F("context '{0}' 이(가) 없습니다.", context));
        var ctx = Map(ctxItem, "context") ?? throw new FormatException(L.F("context '{0}' 의 내용이 없습니다.", context));
        var clusterName = Str(ctx, "cluster") ?? "";
        var userName = Str(ctx, "user") ?? "";
        var clusterItem = Named(root, "clusters", clusterName) ?? throw new FormatException(L.F("cluster '{0}' 이(가) 없습니다.", clusterName));
        var userItem = Named(root, "users", userName);

        var cluster = CopyInlined(Map(clusterItem, "cluster") ?? new YamlMappingNode(), baseDir,
            ("certificate-authority", "certificate-authority-data", true));
        var newCluster = new YamlMappingNode { { "name", clusterName }, { "cluster", cluster } };

        var doc = new YamlMappingNode
        {
            { "apiVersion", "v1" },
            { "kind", "Config" },
            { "clusters", new YamlSequenceNode(newCluster) },
        };
        if (userItem != null)
        {
            var user = CopyInlined(Map(userItem, "user") ?? new YamlMappingNode(), baseDir,
                ("client-certificate", "client-certificate-data", true),
                ("client-key", "client-key-data", true),
                ("tokenFile", "token", false));
            doc.Add("users", new YamlSequenceNode(new YamlMappingNode { { "name", userName }, { "user", user } }));
        }
        doc.Add("contexts", new YamlSequenceNode(new YamlMappingNode { { "name", context }, { "context", CopyMap(ctx) } }));
        doc.Add("current-context", context);
        return Save(doc);
    }

    /// <summary>
    /// 클라우드 클러스터용 kubeconfig: 인증은 exec 플러그인(aws eks get-token, gke-gcloud-auth-plugin, kubelogin, oci)이
    /// 그때그때 토큰을 받는다 — 비밀값은 들어가지 않는다
    /// </summary>
    public static string BuildExec(string name, string server, string? caData, string command, IEnumerable<string> args,
        IReadOnlyDictionary<string, string>? env = null)
    {
        var cluster = new YamlMappingNode { { "server", server } };
        if (!string.IsNullOrEmpty(caData)) cluster.Add("certificate-authority-data", caData);
        var exec = new YamlMappingNode
        {
            { "apiVersion", "client.authentication.k8s.io/v1beta1" },
            { "command", command },
            { "args", new YamlSequenceNode(args.Select(a => (YamlNode)new YamlScalarNode(a) { Style = YamlDotNet.Core.ScalarStyle.DoubleQuoted })) },
            { "interactiveMode", "Never" },
            { "provideClusterInfo", "false" },
        };
        if (env is { Count: > 0 })
            exec.Add("env", new YamlSequenceNode(env.Select(kv => (YamlNode)new YamlMappingNode { { "name", kv.Key }, { "value", kv.Value } })));
        var doc = new YamlMappingNode
        {
            { "apiVersion", "v1" },
            { "kind", "Config" },
            { "clusters", new YamlSequenceNode(new YamlMappingNode { { "name", name }, { "cluster", cluster } }) },
            { "users", new YamlSequenceNode(new YamlMappingNode { { "name", name }, { "user", new YamlMappingNode { { "exec", exec } } } }) },
            { "contexts", new YamlSequenceNode(new YamlMappingNode { { "name", name }, { "context", new YamlMappingNode { { "cluster", name }, { "user", name } } } }) },
            { "current-context", name },
        };
        return Save(doc);
    }

    /// <summary>context 의 기본 네임스페이스를 바꾼 사본 (클러스터의 "기본 네임스페이스" 설정을 kubectl 에 반영)</summary>
    public static string WithNamespace(string yaml, string context, string ns)
    {
        var root = LoadRoot(yaml);
        var item = Named(root, "contexts", context) ?? Items(root, "contexts").FirstOrDefault()
                   ?? throw new FormatException(L.F("context '{0}' 이(가) 없습니다.", context));
        var ctx = Map(item, "context");
        if (ctx == null) { ctx = new YamlMappingNode(); item.Children[new YamlScalarNode("context")] = ctx; }
        ctx.Children[new YamlScalarNode("namespace")] = new YamlScalarNode(ns);
        return Save(root);
    }

    /// <summary>
    /// 인증 방식 (카드 배지·필요한 플러그인 확인). 독립 kubeconfig(사용자 하나)를 가정 — 첫 사용자를 본다
    /// </summary>
    public static K8sAuthKind DetectAuth(string yaml)
    {
        var root = LoadRoot(yaml);
        var user = Items(root, "users").Select(u => Map(u, "user")).FirstOrDefault(u => u != null);
        if (user == null) return K8sAuthKind.Kubeconfig;
        if (Map(user, "exec") is { } exec) return AuthFromCommand(Str(exec, "command"));
        if (Map(user, "auth-provider") != null) return K8sAuthKind.Exec;
        return K8sAuthKind.Kubeconfig;
    }

    /// <summary>exec 플러그인 명령 (없으면 null) — PC 에 그 명령이 있는지 확인할 때</summary>
    public static string? ExecCommand(string yaml)
    {
        var root = LoadRoot(yaml);
        var user = Items(root, "users").Select(u => Map(u, "user")).FirstOrDefault(u => u != null);
        return user != null && Map(user, "exec") is { } exec ? Str(exec, "command") : null;
    }

    internal static K8sAuthKind AuthFromCommand(string? command)
    {
        var name = Path.GetFileNameWithoutExtension(command ?? "").ToLowerInvariant();
        return name switch
        {
            "aws" or "aws-iam-authenticator" => K8sAuthKind.Aws,
            "gke-gcloud-auth-plugin" or "gcloud" => K8sAuthKind.Gcp,
            "kubelogin" => K8sAuthKind.Azure,
            "oci" => K8sAuthKind.Oci,
            _ => K8sAuthKind.Exec,
        };
    }

    // ----- 내부 -----

    private static YamlMappingNode CopyMap(YamlMappingNode m)
    {
        var copy = new YamlMappingNode();
        foreach (var (k, v) in m.Children) copy.Add(k, v);
        return copy;
    }

    /// <summary>파일 경로 키(certificate-authority 등)를 내용 키(*-data)로 바꾼 사본</summary>
    private static YamlMappingNode CopyInlined(YamlMappingNode m, string? baseDir, params (string FileKey, string DataKey, bool Base64)[] rules)
    {
        var copy = new YamlMappingNode();
        foreach (var (k, v) in m.Children)
        {
            var key = (k as YamlScalarNode)?.Value;
            var rule = rules.FirstOrDefault(r => r.FileKey == key);
            if (rule.FileKey == null) { copy.Add(k, v); continue; }
            // 같은 맵에 내용 키가 이미 있으면 그것이 우선 (kubectl 과 같음) — 파일 키는 버린다
            if (m.Children.ContainsKey(new YamlScalarNode(rule.DataKey))) continue;
            var path = (v as YamlScalarNode)?.Value ?? "";
            var bytes = ReadReferenced(path, baseDir);
            copy.Add(rule.DataKey, rule.Base64 ? Convert.ToBase64String(bytes) : System.Text.Encoding.UTF8.GetString(bytes).Trim());
        }
        return copy;
    }

    private static byte[] ReadReferenced(string path, string? baseDir)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path);
        if (expanded.StartsWith("~/") || expanded.StartsWith("~\\"))
            expanded = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), expanded[2..]);
        if (!Path.IsPathRooted(expanded))
        {
            if (baseDir == null) throw new FormatException(L.F("붙여 넣은 kubeconfig 가 파일 '{0}' 을(를) 가리킵니다. 파일로 가져오거나 *-data 값으로 바꿔 주세요.", path));
            expanded = Path.Combine(baseDir, expanded);
        }
        try
        {
            var info = new FileInfo(expanded);
            if (info.Length > MaxChars) throw new FormatException(L.F("인증 파일이 너무 큽니다: {0}", path));
            return File.ReadAllBytes(expanded);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new FormatException(L.F("kubeconfig 가 가리키는 파일을 읽지 못했습니다: {0} ({1})", path, ex.Message));
        }
    }

    private static string Save(YamlMappingNode doc)
    {
        var sw = new StringWriter { NewLine = "\n" };
        new YamlStream(new YamlDocument(doc)).Save(sw, assignAnchors: false);
        var text = sw.ToString().TrimEnd();
        if (text.EndsWith("...")) text = text[..^3].TrimEnd();
        return text + "\n";
    }
}
