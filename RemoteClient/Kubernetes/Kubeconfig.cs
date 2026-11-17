using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Nubis;

/// <summary>kubeconfig 의 context 하나 (가져오기 대화상자 목록용)</summary>
public sealed record KubeContext(string Name, string Cluster, string User, string? Namespace, string? Server);

/// <summary>한 번 읽은 kubeconfig (같은 내용을 context 마다 다시 읽지 않게). 읽기 전용으로만 쓴다</summary>
public sealed class KubeconfigDoc
{
    internal KubeconfigDoc(YamlMappingNode root) => Root = root;
    internal YamlMappingNode Root { get; }
}

/// <summary>
/// kubeconfig 읽기·자르기·만들기 (화면 무관, KubeconfigTests).
/// YAML 은 YamlDotNet 표현 모델(YamlStream)로만 읽는다 — 타입 인스턴스화 없음.
/// 클러스터마다 인증을 따로 두려고, 여러 context 가 든 파일에서 context 하나(cluster + user + context)만 담은
/// 독립 kubeconfig 를 만든다. 파일 경로로 가리킨 인증서·토큰은 내용으로 넣어(인라인) 원본 파일이 없어도 쓰이게 한다.
/// 새로 만드는 문자열 값은 큰따옴표로 쓴다 — plain 으로 쓰면 "123"·"true"·"1.29" 같은 이름이 숫자·bool 로 읽혀 kubectl 이 거부한다.
/// </summary>
public static class Kubeconfig
{
    /// <summary>받아들이는 최대 크기 (붙여넣기·파일). kubeconfig 는 보통 수십 KB</summary>
    public const int MaxChars = 1 << 20;

    /// <summary>파일 경로를 담는 키 (여러 파일 병합 때 각 파일 폴더 기준 절대 경로로 바꾼다)</summary>
    private static readonly string[] ClusterPathKeys = { "certificate-authority" };
    private static readonly string[] UserPathKeys = { "client-certificate", "client-key", "tokenFile" };

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

    /// <summary>한 번 읽어 두고 여러 번 쓴다 (가져오기 대화상자). 읽지 못하면 FormatException</summary>
    public static KubeconfigDoc Load(string yaml) => new(LoadRoot(yaml));

    /// <summary>큰따옴표 문자열 스칼라 (값이 숫자·bool 처럼 보여도 문자열로 읽히게)</summary>
    private static YamlScalarNode Q(string value) => new(value) { Style = ScalarStyle.DoubleQuoted };

    private static YamlNode? Get(YamlMappingNode m, string key) =>
        m.Children.TryGetValue(new YamlScalarNode(key), out var n) ? n : null;

    private static string? Str(YamlMappingNode m, string key) => Get(m, key) is YamlScalarNode s ? s.Value : null;

    private static YamlMappingNode? Map(YamlMappingNode m, string key) => Get(m, key) as YamlMappingNode;

    private static IEnumerable<YamlMappingNode> Items(YamlMappingNode root, string key) =>
        Get(root, key) is YamlSequenceNode seq ? seq.Children.OfType<YamlMappingNode>() : Enumerable.Empty<YamlMappingNode>();

    private static YamlMappingNode? Named(YamlMappingNode root, string key, string name) =>
        Items(root, key).FirstOrDefault(i => Str(i, "name") == name);

    /// <summary>context 목록 (이름 순서는 파일 순서)</summary>
    public static List<KubeContext> ReadContexts(string yaml) => ReadContexts(Load(yaml));

    public static List<KubeContext> ReadContexts(KubeconfigDoc doc)
    {
        var root = doc.Root;
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
    public static string? CurrentContext(string yaml) => CurrentContext(Load(yaml));

    public static string? CurrentContext(KubeconfigDoc doc) => Str(doc.Root, "current-context") is { Length: > 0 } c ? c : null;

    /// <summary>
    /// context 하나만 담은 독립 kubeconfig. baseDir 은 원본 파일이 있던 폴더 (상대 경로 인증서 파일을 찾는 기준, 붙여넣기면 null).
    /// 인증서·키·토큰 파일은 *-data / token 으로 넣는다. 읽지 못하면 FormatException (화면에 보이는 문구)
    /// </summary>
    public static string Extract(string yaml, string context, string? baseDir) => Extract(Load(yaml), context, baseDir);

    public static string Extract(KubeconfigDoc d, string context, string? baseDir)
    {
        var root = d.Root;
        var ctxItem = Named(root, "contexts", context) ?? throw new FormatException(L.F("context '{0}' 이(가) 없습니다.", context));
        var ctx = Map(ctxItem, "context") ?? throw new FormatException(L.F("context '{0}' 의 내용이 없습니다.", context));
        var clusterName = Str(ctx, "cluster") ?? "";
        var userName = Str(ctx, "user") ?? "";
        var clusterItem = Named(root, "clusters", clusterName) ?? throw new FormatException(L.F("cluster '{0}' 이(가) 없습니다.", clusterName));
        var userItem = Named(root, "users", userName);

        var cluster = CopyInlined(Map(clusterItem, "cluster") ?? new YamlMappingNode(), baseDir,
            ("certificate-authority", "certificate-authority-data", true));
        var newCluster = new YamlMappingNode { { "name", Q(clusterName) }, { "cluster", cluster } };

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
            ResolveExecCommand(user, baseDir, required: true);
            doc.Add("users", new YamlSequenceNode(new YamlMappingNode { { "name", Q(userName) }, { "user", user } }));
        }
        doc.Add("contexts", new YamlSequenceNode(new YamlMappingNode { { "name", Q(context) }, { "context", CopyContext(ctx) } }));
        doc.Add("current-context", Q(context));
        return Save(doc);
    }

    /// <summary>
    /// 클라우드 클러스터용 kubeconfig: 인증은 exec 플러그인(aws eks get-token, gke-gcloud-auth-plugin, kubelogin, oci)이
    /// 그때그때 토큰을 받는다 — 비밀값은 들어가지 않는다
    /// </summary>
    public static string BuildExec(string name, string server, string? caData, string command, IEnumerable<string> args,
        IReadOnlyDictionary<string, string>? env = null)
    {
        var cluster = new YamlMappingNode { { "server", Q(server) } };
        if (!string.IsNullOrEmpty(caData)) cluster.Add("certificate-authority-data", Q(caData));
        var exec = new YamlMappingNode
        {
            { "apiVersion", "client.authentication.k8s.io/v1beta1" },
            { "command", Q(command) },
            { "args", new YamlSequenceNode(args.Select(a => (YamlNode)Q(a))) },
            { "interactiveMode", "Never" },
            // bool 값 — plain 으로 써야 bool 로 읽힌다
            { "provideClusterInfo", new YamlScalarNode("false") { Style = ScalarStyle.Plain } },
        };
        if (env is { Count: > 0 })
            exec.Add("env", new YamlSequenceNode(env.Select(kv => (YamlNode)new YamlMappingNode { { "name", Q(kv.Key) }, { "value", Q(kv.Value) } })));
        var doc = new YamlMappingNode
        {
            { "apiVersion", "v1" },
            { "kind", "Config" },
            { "clusters", new YamlSequenceNode(new YamlMappingNode { { "name", Q(name) }, { "cluster", cluster } }) },
            { "users", new YamlSequenceNode(new YamlMappingNode { { "name", Q(name) }, { "user", new YamlMappingNode { { "exec", exec } } } }) },
            { "contexts", new YamlSequenceNode(new YamlMappingNode { { "name", Q(name) }, { "context", new YamlMappingNode { { "cluster", Q(name) }, { "user", Q(name) } } } }) },
            { "current-context", Q(name) },
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
        ctx.Children[new YamlScalarNode("namespace")] = Q(ns);
        return Save(root);
    }

    /// <summary>
    /// 인증 방식 (카드 배지·필요한 플러그인 확인). 독립 kubeconfig(사용자 하나)를 가정 — 첫 사용자를 본다
    /// </summary>
    public static K8sAuthKind DetectAuth(string yaml) => AuthOfUser(FirstUser(LoadRoot(yaml)));

    /// <summary>exec 플러그인 명령 (없으면 null) — PC 에 그 명령이 있는지 확인할 때</summary>
    public static string? ExecCommand(string yaml) => FirstUser(LoadRoot(yaml)) is { } user && Map(user, "exec") is { } exec ? Str(exec, "command") : null;

    /// <summary>
    /// 여러 context 가 든 원본에서 context 의 인증 방식과 exec 명령 (인증서 파일은 읽지 않는다 — 목록 배지용).
    /// 구분자가 든 상대 경로 명령은 baseDir 기준 절대 경로로 (붙여넣기면 그대로)
    /// </summary>
    public static (K8sAuthKind Kind, string? Command) ContextAuth(KubeconfigDoc doc, string context, string? baseDir)
    {
        var root = doc.Root;
        var userName = Named(root, "contexts", context) is { } ci && Map(ci, "context") is { } ctx ? Str(ctx, "user") ?? "" : "";
        var user = Named(root, "users", userName) is { } ui ? Map(ui, "user") : null;
        var kind = AuthOfUser(user);
        var cmd = user != null && Map(user, "exec") is { } exec ? Str(exec, "command") : null;
        if (cmd != null && baseDir != null && IsRelativeWithSeparator(cmd)) cmd = Path.GetFullPath(Path.Combine(baseDir, cmd));
        return (kind, cmd);
    }

    /// <summary>
    /// context 를 가져올 때(Extract) 읽게 될 인증 파일 경로 — certificate-authority · client-certificate · client-key · tokenFile 중
    /// 같은 맵에 *-data / token 값이 없는 것 (Extract 와 같은 규칙). 환경 변수·~·상대 경로(baseDir 기준)를 푼 경로,
    /// 풀 수 없으면(붙여넣기의 상대 경로) 원문. 파일은 읽지 않는다 — MCP 가져오기가 읽기 전에 경로를 검사할 때 쓴다
    /// </summary>
    public static List<string> ReferencedFiles(KubeconfigDoc doc, string context, string? baseDir)
    {
        var root = doc.Root;
        var list = new List<string>();
        if (Named(root, "contexts", context) is not { } ci || Map(ci, "context") is not { } ctx) return list;
        void Collect(YamlMappingNode? m, params (string FileKey, string DataKey)[] rules)
        {
            if (m == null) return;
            foreach (var (fileKey, dataKey) in rules)
            {
                if (m.Children.ContainsKey(new YamlScalarNode(dataKey))) continue;
                if (Get(m, fileKey) is not YamlScalarNode { Value: { Length: > 0 } p }) continue;
                list.Add(ResolvePath(p, baseDir) ?? p);
            }
        }
        var clusterName = Str(ctx, "cluster") ?? "";
        var userName = Str(ctx, "user") ?? "";
        Collect(Named(root, "clusters", clusterName) is { } c ? Map(c, "cluster") : null, ("certificate-authority", "certificate-authority-data"));
        Collect(Named(root, "users", userName) is { } u ? Map(u, "user") : null,
            ("client-certificate", "client-certificate-data"), ("client-key", "client-key-data"), ("tokenFile", "token"));
        return list;
    }

    // ----- KUBECONFIG 여러 파일 -----

    /// <summary>
    /// KUBECONFIG 값(; 구분)의 파일 목록. 빈 항목·겹친 경로는 뺀다. 남는 게 없으면 기본 파일(~/.kube/config) 하나
    /// </summary>
    public static List<string> EnvPaths(string? env, string defaultPath)
    {
        var list = (env ?? "").Split(';')
            .Select(p => p.Trim().Trim('"').Trim())
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (list.Count == 0) list.Add(defaultPath);
        return list;
    }

    /// <summary>
    /// 여러 kubeconfig 를 kubectl 규칙으로 합친다: 같은 이름의 cluster·user·context 는 먼저 나온 파일 것이 이기고,
    /// current-context 도 처음 정해진 값. 파일마다 상대 경로(인증서·exec 명령)는 그 파일 폴더 기준 절대 경로로 바꿔 둔다 —
    /// 그래서 결과는 기준 폴더 없이(baseDir = null) 쓸 수 있다. 빈 내용은 건너뛰고, 읽지 못하면 FormatException
    /// </summary>
    public static string Merge(IEnumerable<(string Yaml, string? BaseDir)> files)
    {
        var lists = new Dictionary<string, (List<YamlNode> Items, HashSet<string> Seen)>
        {
            ["clusters"] = (new(), new(StringComparer.Ordinal)),
            ["users"] = (new(), new(StringComparer.Ordinal)),
            ["contexts"] = (new(), new(StringComparer.Ordinal)),
        };
        string? current = null;
        foreach (var (yaml, baseDir) in files)
        {
            if (string.IsNullOrWhiteSpace(yaml)) continue;
            var root = LoadRoot(yaml);
            current ??= Str(root, "current-context") is { Length: > 0 } cc ? cc : null;
            foreach (var (key, (items, seen)) in lists)
                foreach (var item in Items(root, key))
                {
                    if (Str(item, "name") is not { Length: > 0 } name || !seen.Add(name)) continue;
                    items.Add(key switch
                    {
                        "clusters" => WithResolvedPaths(item, "cluster", ClusterPathKeys, baseDir, exec: false),
                        "users" => WithResolvedPaths(item, "user", UserPathKeys, baseDir, exec: true),
                        _ => item,
                    });
                }
        }
        var doc = new YamlMappingNode
        {
            { "apiVersion", "v1" },
            { "kind", "Config" },
            { "clusters", new YamlSequenceNode(lists["clusters"].Items) },
            { "users", new YamlSequenceNode(lists["users"].Items) },
            { "contexts", new YamlSequenceNode(lists["contexts"].Items) },
        };
        if (current != null) doc.Add("current-context", Q(current));
        return Save(doc);
    }

    /// <summary>item(name + body) 의 사본 — body 안 파일 경로 키를 baseDir 기준 절대 경로로</summary>
    private static YamlMappingNode WithResolvedPaths(YamlMappingNode item, string bodyKey, string[] pathKeys, string? baseDir, bool exec)
    {
        if (baseDir == null || Map(item, bodyKey) is not { } body) return item;
        var newBody = CopyMap(body);
        foreach (var k in pathKeys)
            if (Str(body, k) is { Length: > 0 } p && ResolvePath(p, baseDir) is { } abs)
                newBody.Children[new YamlScalarNode(k)] = Q(abs);
        if (exec) ResolveExecCommand(newBody, baseDir, required: false);
        var copy = CopyMap(item);
        copy.Children[new YamlScalarNode(bodyKey)] = newBody;
        return copy;
    }

    private static YamlMappingNode? FirstUser(YamlMappingNode root) =>
        Items(root, "users").Select(u => Map(u, "user")).FirstOrDefault(u => u != null);

    private static K8sAuthKind AuthOfUser(YamlMappingNode? user)
    {
        if (user == null) return K8sAuthKind.Kubeconfig;
        if (Map(user, "exec") is { } exec) return AuthFromCommand(Str(exec, "command"));
        if (Map(user, "auth-provider") != null) return K8sAuthKind.Exec;
        return K8sAuthKind.Kubeconfig;
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

    /// <summary>context 맵 사본 — cluster·user·namespace 는 문자열이므로 plain 으로 적혀 있으면(예: cluster: 456) 큰따옴표로 바꾼다</summary>
    private static YamlMappingNode CopyContext(YamlMappingNode ctx)
    {
        var copy = new YamlMappingNode();
        foreach (var (k, v) in ctx.Children)
            copy.Add(k, k is YamlScalarNode { Value: "cluster" or "user" or "namespace" } && v is YamlScalarNode { Style: ScalarStyle.Plain or ScalarStyle.Any, Value: { } s }
                ? Q(s) : v);
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
            copy.Add(rule.DataKey, Q(rule.Base64 ? Convert.ToBase64String(bytes) : System.Text.Encoding.UTF8.GetString(bytes).Trim()));
        }
        return copy;
    }

    /// <summary>
    /// exec.command 가 구분자가 든 상대 경로면 (kubectl 은 kubeconfig 파일 폴더 기준으로 찾는다) baseDir 기준 절대 경로로 바꾼다 —
    /// 독립 kubeconfig 는 임시 파일로 쓰이므로 상대 경로로 두면 엉뚱한 곳을 찾는다. user 맵을 직접 바꾼다 (exec 맵은 사본으로 교체).
    /// required 면 기준 폴더가 없을 때(붙여넣기) FormatException
    /// </summary>
    private static void ResolveExecCommand(YamlMappingNode user, string? baseDir, bool required)
    {
        if (Map(user, "exec") is not { } exec || Get(exec, "command") is not YamlScalarNode { Value: { } cmd } || !IsRelativeWithSeparator(cmd)) return;
        if (baseDir == null)
        {
            if (required) throw new FormatException(L.F("붙여 넣은 kubeconfig 의 exec 명령이 상대 경로입니다: '{0}'. 파일로 가져오거나 절대 경로로 바꿔 주세요.", cmd));
            return;
        }
        var copy = CopyMap(exec);
        copy.Children[new YamlScalarNode("command")] = Q(Path.GetFullPath(Path.Combine(baseDir, cmd)));
        user.Children[new YamlScalarNode("exec")] = copy;
    }

    /// <summary>"bin/plugin", ".\x.exe" 처럼 경로 구분자가 든 상대 경로인지 (그냥 "aws" 는 PATH 에서 찾는 명령)</summary>
    internal static bool IsRelativeWithSeparator(string cmd) =>
        (cmd.Contains('/') || cmd.Contains('\\')) && !Path.IsPathRooted(cmd) && !cmd.StartsWith('~');

    /// <summary>환경 변수·~ 를 풀고 상대 경로면 baseDir 기준으로. 상대 경로인데 baseDir 이 없으면 null</summary>
    private static string? ResolvePath(string path, string? baseDir)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path);
        if (expanded.StartsWith("~/") || expanded.StartsWith("~\\"))
            expanded = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), expanded[2..]);
        if (Path.IsPathRooted(expanded)) return expanded;
        return baseDir == null ? null : Path.Combine(baseDir, expanded);
    }

    private static byte[] ReadReferenced(string path, string? baseDir)
    {
        var expanded = ResolvePath(path, baseDir)
                       ?? throw new FormatException(L.F("붙여 넣은 kubeconfig 가 파일 '{0}' 을(를) 가리킵니다. 파일로 가져오거나 *-data 값으로 바꿔 주세요.", path));
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

/// <summary>
/// kubeconfig 가져오기 공용 (가져오기 창 · MCP import_k8s_clusters): 원본 읽기 · context 정보 · 클러스터 만들기.
/// 화면 개체는 건드리지 않는다 (백그라운드에서 불러도 된다)
/// </summary>
public static class KubeconfigImport
{
    /// <summary>읽은 원본. BaseDir 은 상대 경로(인증서·exec 명령)를 찾는 기준 폴더 (여러 파일을 합쳤으면 null — 경로는 이미 절대 경로)</summary>
    public sealed record Source(string Yaml, string? BaseDir, string? Error);

    /// <summary>context 한 줄의 정보 (토큰·인증서 값 없음)</summary>
    public sealed record ContextInfo(KubeContext Context, K8sAuthKind Auth, string? ExecCommand, bool AlreadyImported, bool DefaultSelected);

    public static string HomeConfig => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kube", "config");

    public static string? EnvKubeconfig => Environment.GetEnvironmentVariable("KUBECONFIG");

    /// <summary>기본 원본: KUBECONFIG 의 파일들 (없으면 ~/.kube/config) — kubectl 과 같은 규칙</summary>
    public static List<string> DefaultPaths => Kubeconfig.EnvPaths(EnvKubeconfig, HomeConfig);

    /// <summary>파일을 읽는다. 성공하면 파일 폴더, 없으면 null(err null), 읽지 못하면 null + err</summary>
    public static string? ReadFile(string path, out string text, out string? err)
    {
        text = "";
        err = null;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            if (info.Length > Kubeconfig.MaxChars) { err = L.T("kubeconfig 가 너무 큽니다 (1MB 이하)."); return null; }
            text = File.ReadAllText(info.FullName);
            return info.DirectoryName ?? "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            err = $"{path}: {ex.Message}";
            return null;
        }
    }

    /// <summary>파일 하나를 읽는다 (없으면 오류)</summary>
    public static Source ReadPath(string path) =>
        ReadFile(path, out var text, out var err) is { } dir ? new(text, dir, null) : new("", null, err ?? L.F("파일이 없습니다: {0}", path));

    /// <summary>
    /// 기본 원본 읽기: 파일이 하나면 그 파일, 여럿이면 kubectl 규칙으로 합친다 (없는 파일은 kubectl 처럼 건너뛰고, 읽지 못한 파일이 있으면 오류)
    /// </summary>
    public static Source ReadDefault(IReadOnlyList<string> paths)
    {
        if (paths.Count == 1) return ReadPath(paths[0]);
        var parts = new List<(string, string?)>();
        foreach (var p in paths)
        {
            if (ReadFile(p, out var text, out var err) is not { } dir)
            {
                if (err == null) continue;
                return new("", null, err);
            }
            parts.Add((text, dir));
        }
        if (parts.Count == 0) return new("", null, L.T("KUBECONFIG 가 가리키는 파일이 하나도 없습니다."));
        try { return new(Kubeconfig.Merge(parts), null, null); }
        catch (FormatException ex) { return new("", null, ex.Message); }
    }

    /// <summary>
    /// context 목록과 각 정보. 같은 API 서버 + context 이름이 이미 있으면 AlreadyImported (다시 가져오지 않는다).
    /// DefaultSelected: 이미 있지 않고 current-context 이거나 등록된 클러스터가 하나도 없을 때 (가져오기 창의 처음 체크와 같다)
    /// </summary>
    public static List<ContextInfo> Describe(KubeconfigDoc doc, string? baseDir, IReadOnlyCollection<K8sCluster> existing)
    {
        var current = Kubeconfig.CurrentContext(doc);
        return Kubeconfig.ReadContexts(doc).Select(c =>
        {
            var dup = K8sSync.AlreadyImported(existing, c);
            var (kind, cmd) = Kubeconfig.ContextAuth(doc, c.Name, baseDir);
            return new ContextInfo(c, kind, cmd, dup, !dup && (c.Name == current || existing.Count == 0));
        }).ToList();
    }

    /// <summary>context 하나를 독립 kubeconfig 를 가진 클러스터로 (인증 파일을 읽어 넣는다). 읽지 못하면 FormatException</summary>
    public static K8sCluster ToCluster(KubeconfigDoc doc, KubeContext ctx, string? baseDir, string group)
    {
        var one = Kubeconfig.Extract(doc, ctx.Name, baseDir);
        return new K8sCluster
        {
            Name = ctx.Name,
            Group = group,
            Source = K8sSource.Imported,
            ContextName = ctx.Name,
            Endpoint = ctx.Server ?? "",
            // context 의 namespace 를 기본 네임스페이스로 (패널·카드·MCP 가 default 대신 이 값을 쓴다)
            DefaultNamespace = K8sCluster.ImportedNamespace(ctx.Namespace),
            AuthKind = Kubeconfig.DetectAuth(one),
            Kubeconfig = one,
        };
    }
}
