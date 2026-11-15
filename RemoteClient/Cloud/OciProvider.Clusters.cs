using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Nubis;

// =====================================================================
// OCI 조회 준비(VM·클러스터 공통: 프로필 · 서명기 · 구독 리전 × 컴파트먼트) 와 OKE 클러스터 조회.
// 클러스터마다 kubeconfig/content(조회성 POST)로 API 서버 주소·CA 를 받아
// oci ce cluster generate-token exec kubeconfig 를 이 프로필에 맞게 새로 만든다.
// =====================================================================
public sealed partial class OciProvider
{
    private const string ContainerEngineApiVersion = "20180222";

    // ----- 조회 준비 (VM · 클러스터 공통) -----

    /// <summary>한 번의 조회 준비 결과: 프로필 · 서명기 · 리전 × 컴파트먼트 범위 · 준비 단계의 부분 실패 (VM·클러스터 조회 공통)</summary>
    private sealed class Session(OciProfile prof, Ctx ctx, List<Scope> scopes, List<string> errors) : IDisposable
    {
        public OciProfile Prof { get; } = prof;
        public Ctx Ctx { get; } = ctx;
        public List<Scope> Scopes { get; } = scopes;
        /// <summary>준비 단계에서 생긴 부분 실패 (잘못된 리전 이름 · 컴파트먼트 목록 실패)</summary>
        public List<string> Errors { get; } = errors;
        public void Dispose() => Ctx.Dispose();
    }

    private static async Task<Session> OpenAsync(CloudProfile p, CancellationToken ct)
    {
        var prof = OciConfig.Get(p.Name)
            ?? throw new InvalidOperationException(L.F("OCI 프로필 '{0}' 을 찾을 수 없습니다 ({1}).", p.Name, OciConfig.ConfigPath));
        if (!IsValidRegion(prof.Region))
            throw new InvalidOperationException(L.F("OCI 프로필 '{0}' 의 region 설정이 올바르지 않습니다.", p.Name));
        if (string.IsNullOrWhiteSpace(prof.Tenancy))
            throw new InvalidOperationException(L.F("OCI 프로필 '{0}' 에 tenancy 설정이 없습니다.", p.Name));

        var ctx = new Ctx(await CreateSignerAsync(prof, ct), new SemaphoreSlim(16), RealmDomain(prof.Tenancy));
        try
        {
            var identity = $"https://identity.{prof.Region}.{ctx.Domain}/{ApiVersion}";
            var tenancy = Uri.EscapeDataString(prof.Tenancy);

            // 1. 구독 리전 (첫 호출 — 인증 실패는 여기서 로그인 안내로 바꾼다)
            List<string> regions;
            try
            {
                var (root, _) = await GetAsync(ctx, $"{identity}/tenancies/{tenancy}/regionSubscriptions", ct);
                regions = ParseRegions(root);
            }
            catch (OciApiException ex) when (ex.IsAuth)
            {
                throw LoginNeeded(prof, ex.Message);
            }
            var errors = new List<string>();
            foreach (var bad in regions.Where(r => !IsValidRegion(r))) errors.Add($"{bad}: invalid region name");
            regions = regions.Where(IsValidRegion).Distinct().ToList();
            // 리전이 0개면 "조회 성공 + 서버 없음" 으로 보여 이 프로필 호스트가 전부 지워지므로 실패로 처리
            if (regions.Count == 0)
                throw new InvalidOperationException(L.T("사용 가능한 리전 목록이 비어 있습니다 (regionSubscriptions) — 이번 동기화는 건너뜁니다."));

            // 2. 컴파트먼트 (루트 = 테넌시 포함). 하위 목록을 못 읽으면 루트만 조회하고 부분 결과로 둔다 (삭제 안 함)
            var compartments = new List<(string id, string name)> { (prof.Tenancy, "root") };
            try
            {
                var all = await ListAllAsync(ctx,
                    $"{identity}/compartments?compartmentId={tenancy}&compartmentIdInSubtree=true&accessLevel=ACCESSIBLE&lifecycleState=ACTIVE", ct);
                compartments.AddRange(ParseCompartments(all).Where(c => c.id != prof.Tenancy));
            }
            catch (OciApiException ex) when (ex.IsAuth)
            {
                throw LoginNeeded(prof, ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"compartments: {CloudProviders.FirstLine(ex.Message)}");
            }

            var scopes = regions.SelectMany(r => compartments.Select(c => new Scope(r, c.id, c.name))).ToList();
            return new Session(prof, ctx, scopes, errors);
        }
        catch
        {
            ctx.Dispose();
            throw;
        }
    }

    // ----- OKE 클러스터 -----

    /// <summary>scope 조회에서 모은 클러스터 (context 이름은 전체를 모은 뒤 정한다)</summary>
    private sealed record OkeFound(string Ocid, string Name, string Region, string Endpoint, string Version, string State,
        string Server, string? CaData);

    public async Task<ClusterFetchResult> FetchClustersAsync(CloudProfile p, CancellationToken ct)
    {
        // exec 인자에 그대로 들어가므로 OCI CLI 에 넘겨도 안전한 이름만 받는다 (DEFAULT 로 바꾸면 다른 계정으로 인증된다)
        if (!ProfileNamePattern.IsMatch(p.Name))
            throw new InvalidOperationException(L.F("OCI 프로필 이름 '{0}' 에 쓸 수 없는 문자가 있어 클러스터 kubeconfig 를 만들 수 없습니다.", p.Name));

        using var s = await OpenAsync(p, ct);
        var errors = s.Errors;
        var kubeconfigErrors = new ConcurrentBag<string>();

        var (found, scopeErrors) = await CloudProviders.ParallelCollect(s.Scopes, 8,
            x => FetchClusterScopeAsync(s.Ctx, x, kubeconfigErrors, ct), x => $"{x.Region} · {x.CompartmentName}", ct);
        ct.ThrowIfCancellationRequested();
        errors.AddRange(scopeErrors);
        errors.AddRange(kubeconfigErrors.OrderBy(e => e, StringComparer.Ordinal));

        // 같은 이름이 여러 리전·컴파트먼트에 있으면 context 이름이 겹치지 않게 (순서를 고정해 동기화마다 같은 이름)
        var ordered = found.DistinctBy(f => f.Ocid)
            .OrderBy(f => f.Name, StringComparer.Ordinal).ThenBy(f => f.Region, StringComparer.Ordinal)
            .ThenBy(f => f.Ocid, StringComparer.Ordinal).ToList();
        var names = UniqueContextNames(ordered.Select(f => (f.Name, f.Region)).ToList());
        var items = ordered.Select((f, i) => new CloudCluster(
            "oke:" + f.Ocid, f.Name, f.Region, f.Endpoint, f.Version, CloudCluster.NormalizeState(f.State), K8sAuthKind.Oci,
            BuildOkeKubeconfig(names[i], f.Server, f.CaData, f.Ocid, f.Region, p.Name, s.Prof.IsSession), names[i])).ToList();

        if (items.Count == 0 && errors.Count > 0) throw new InvalidOperationException(errors[0]);
        return new ClusterFetchResult(items, errors);
    }

    private static async Task<IEnumerable<OkeFound>> FetchClusterScopeAsync(Ctx ctx, Scope s, ConcurrentBag<string> kubeconfigErrors,
        CancellationToken ct)
    {
        var ce = $"https://containerengine.{s.Region}.{ctx.Domain}/{ContainerEngineApiVersion}";
        var clusters = (await ListAllAsync(ctx, $"{ce}/clusters?compartmentId={Uri.EscapeDataString(s.CompartmentId)}", ct))
            .Where(c => c.Str("id").Length > 0 && !IsClusterGone(c.Str("lifecycleState"))).ToList();
        if (clusters.Count == 0) return Array.Empty<OkeFound>();

        var tasks = clusters.Select(async c =>
        {
            var ocid = c.Str("id");
            var name = c.Str("name").Length > 0 ? c.Str("name") : ocid;
            var (endpointType, host) = OkeEndpoint(c);
            try
            {
                var (yaml, _) = await SendAsync(ctx, HttpMethod.Post, $"{ce}/clusters/{Uri.EscapeDataString(ocid)}/kubeconfig/content",
                    Encoding.UTF8.GetBytes(KubeconfigRequestBody(endpointType)), ct);
                var (server, ca) = ReadOkeServer(yaml);
                var endpoint = host.Length > 0 ? WithHttps(host) : server;
                return new OkeFound(ocid, name, s.Region, endpoint, c.Str("kubernetesVersion"), c.Str("lifecycleState"), server, ca);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // kubeconfig 를 못 받은 클러스터는 건너뛰고 오류로 남긴다 (부분 결과 → 아무것도 지우지 않음)
                kubeconfigErrors.Add($"{s.Region} · {name}: {CloudProviders.FirstLine(ex.Message)}");
                return (OkeFound?)null;
            }
        });
        return (await Task.WhenAll(tasks)).Where(f => f != null).Select(f => f!).ToList();
    }

    // ----- 순수 함수 (테스트 대상) -----

    /// <summary>없어진 클러스터: DELETED · DELETING. 목록에서 뺀다 (FAILED · CREATING 등은 남긴다)</summary>
    internal static bool IsClusterGone(string state) =>
        state.ToUpperInvariant() is "DELETED" or "DELETING";

    /// <summary>
    /// kubeconfig 를 받을 엔드포인트 종류와 주소. 공용 엔드포인트가 있으면 PUBLIC_ENDPOINT, 없으면 PRIVATE_ENDPOINT,
    /// VCN 네이티브가 아닌 예전 클러스터(endpoints.kubernetes 만 있음)는 LEGACY_KUBERNETES
    /// </summary>
    internal static (string type, string host) OkeEndpoint(JsonElement cluster)
    {
        var ep = cluster.Obj("endpoints");
        if (ep.Str("publicEndpoint") is { Length: > 0 } pub) return ("PUBLIC_ENDPOINT", pub);
        if (ep.Str("privateEndpoint") is { Length: > 0 } priv) return ("PRIVATE_ENDPOINT", priv);
        if (ep.Str("kubernetes") is { Length: > 0 } legacy) return ("LEGACY_KUBERNETES", legacy);
        return ("PUBLIC_ENDPOINT", "");
    }

    /// <summary>kubeconfig/content 요청 본문 (토큰 v2 — exec 로 oci ce cluster generate-token 을 부르는 형식)</summary>
    internal static string KubeconfigRequestBody(string endpointType) =>
        JsonSerializer.Serialize(new Dictionary<string, string> { ["tokenVersion"] = "2.0.0", ["endpoint"] = endpointType });

    /// <summary>scheme 이 없으면 https:// 를 붙인다</summary>
    internal static string WithHttps(string endpoint) =>
        endpoint.Contains("://", StringComparison.Ordinal) ? endpoint : "https://" + endpoint;

    /// <summary>OKE 가 준 kubeconfig 에서 첫 클러스터의 server · certificate-authority-data. 없거나 형식이 다르면 FormatException</summary>
    internal static (string server, string? caData) ReadOkeServer(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml)) throw new FormatException(L.T("kubeconfig 내용이 비어 있습니다."));
        var text = yaml.TrimStart();
        // 혹시 JSON 문자열로 감싸 오면 푼다
        if (text.StartsWith('"'))
        {
            try { text = JsonSerializer.Deserialize<string>(text) ?? ""; }
            catch (JsonException) { }
        }
        var stream = new YamlStream();
        try { stream.Load(new StringReader(text)); }
        catch (YamlException ex) { throw new FormatException(L.F("kubeconfig 를 읽지 못했습니다: {0}", ex.Message)); }
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
            throw new FormatException(L.T("kubeconfig 형식이 아닙니다."));
        var cluster = (root.Children.TryGetValue(new YamlScalarNode("clusters"), out var cs) && cs is YamlSequenceNode seq
                ? seq.Children.OfType<YamlMappingNode>() : Enumerable.Empty<YamlMappingNode>())
            .Select(c => c.Children.TryGetValue(new YamlScalarNode("cluster"), out var m) ? m as YamlMappingNode : null)
            .FirstOrDefault(m => m != null);
        string? Scalar(string key) =>
            cluster != null && cluster.Children.TryGetValue(new YamlScalarNode(key), out var v) && v is YamlScalarNode sv &&
            !string.IsNullOrWhiteSpace(sv.Value) ? sv.Value!.Trim() : null;
        var server = Scalar("server") ?? throw new FormatException(L.T("kubeconfig 에 API 서버 주소(server)가 없습니다."));
        return (server, Scalar("certificate-authority-data"));
    }

    /// <summary>
    /// oci ce cluster generate-token 인자. 프로필을 고정하고(이 앱에 등록한 프로필로 인증),
    /// 세션(보안 토큰) 프로필이면 --auth security_token 을 붙인다
    /// </summary>
    internal static List<string> OkeExecArgs(string clusterOcid, string region, string profile, bool isSession)
    {
        var args = new List<string> { "ce", "cluster", "generate-token", "--cluster-id", clusterOcid, "--region", region, "--profile", profile };
        if (isSession) args.AddRange(new[] { "--auth", "security_token" });
        return args;
    }

    /// <summary>이 클러스터 하나만 담은 exec kubeconfig (비밀값 없음)</summary>
    internal static string BuildOkeKubeconfig(string contextName, string server, string? caData, string clusterOcid, string region,
        string profile, bool isSession) =>
        Kubeconfig.BuildExec(contextName, server, caData, "oci", OkeExecArgs(clusterOcid, region, profile, isSession));

    /// <summary>
    /// context 이름: 클러스터 이름. 같은 이름이 여럿이면 "이름-리전", 그래도 겹치면 뒤에 -2, -3 … (입력 순서대로)
    /// </summary>
    internal static List<string> UniqueContextNames(IReadOnlyList<(string name, string region)> clusters)
    {
        var counts = clusters.GroupBy(c => c.name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(clusters.Count);
        foreach (var (name, region) in clusters)
        {
            var baseName = counts[name] > 1 ? $"{name}-{region}" : name;
            var candidate = baseName;
            for (var n = 2; !used.Add(candidate); n++) candidate = $"{baseName}-{n}";
            result.Add(candidate);
        }
        return result;
    }
}
