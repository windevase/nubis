using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using Amazon;
using Amazon.EC2;
using Amazon.EC2.Model;
using Amazon.Runtime.CredentialManagement;
using Azure.Core;
using Azure.Identity;
using Google.Apis.Auth.OAuth2;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Nubis;

// =====================================================================
// Azure: az CLI 로그인 (AzureCliCredential), ARM REST 호출
// =====================================================================
public sealed class AzureProvider : ICloudProvider
{
    private const string Arm = "https://management.azure.com";

    public CloudProvider Kind => CloudProvider.Azure;
    public string AuthHint => L.T("az CLI 로그인 · az login");
    public string LoginCommand(CloudProfile? p) =>
        string.IsNullOrEmpty(p?.TenantId) ? "az login" : $"az login --tenant {p.TenantId}";

    // az CLI 로 토큰을 받는 데 수 초가 걸리므로 테넌트별로 만료 5분 전까지 재사용
    private static readonly ConcurrentDictionary<string, AccessToken> TokenCache = new();

    /// <summary>구독 찾기(DiscoverAsync)에서 테넌트 토큰을 동시에 받는 수 (az 프로세스 동시 실행 수)</summary>
    internal const int TenantParallelism = 3;

    /// <summary>
    /// 토큰 캐시 키: 테넌트 + az 설정 폴더 + azureProfile.json 수정 시각.
    /// az login / az logout 은 azureProfile.json 을 다시 쓰므로, 로그인 버튼으로 다른 계정에 로그인하면 키가 바뀌어 이전 계정 토큰을 쓰지 않는다
    /// </summary>
    private static string CacheKey(string? tenant)
    {
        var dir = CloudPaths.Effective(CloudProvider.Azure);
        long stamp = 0;
        try { stamp = File.GetLastWriteTimeUtc(Path.Combine(dir, "azureProfile.json")).Ticks; } catch { }
        return $"{tenant}|{dir}|{stamp}";
    }

    /// <summary>ARM 이 401 로 거부한 토큰은 캐시에서 뺀다 (az 로 다른 계정에 로그인했거나 토큰이 폐기된 경우 — 다음 동기화가 새 토큰을 받게)</summary>
    private static void ForgetTokenOn401(string? tenant, Exception ex)
    {
        if (ex is HttpRequestException && ex.Message.StartsWith("401", StringComparison.Ordinal))
            TokenCache.TryRemove(CacheKey(tenant), out _);
    }

    private static async Task<string> TokenAsync(string? tenant, CancellationToken ct)
    {
        var key = CacheKey(tenant);
        if (TokenCache.TryGetValue(key, out var cached) && cached.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5))
            return cached.Token;
        var cred = new AzureCliCredential(new AzureCliCredentialOptions
        {
            TenantId = string.IsNullOrEmpty(tenant) ? null : tenant,
            ProcessTimeout = TimeSpan.FromSeconds(60),
        });
        var t = await cred.GetTokenAsync(new TokenRequestContext(new[] { Arm + "/.default" }), ct);
        // 로그인할 때마다 키(azureProfile.json 수정 시각)가 바뀌어 이전 키의 토큰이 남으므로, 만료된 항목은 넣을 때 정리한다
        var now = DateTimeOffset.UtcNow;
        foreach (var old in TokenCache)
            if (old.Value.ExpiresOn <= now) TokenCache.TryRemove(old.Key, out _);
        TokenCache[key] = t;
        return t.Token;
    }

    /// <summary>
    /// 토큰을 못 받은 오류를 안내 문구로. 로그인 안 됨·만료·MFA 필요(CredentialUnavailable / AuthenticationFailed)만 "az 로그인 필요" 로,
    /// az 응답 시간 초과 등 그 밖의 오류는 로그인 문제로 단정하지 않는다
    /// </summary>
    private InvalidOperationException TokenError(CloudProfile? p, Exception ex)
    {
        var detail = CloudProviders.FirstLine(ex.Message);
        var timedOut = ex.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
                       ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase);
        return !timedOut && ex is CredentialUnavailableException or AuthenticationFailedException
            ? new InvalidOperationException(L.F("az 로그인이 필요합니다 ({0}) — {1}", LoginCommand(p), detail))
            : new InvalidOperationException(L.F("az 에서 토큰을 받지 못했습니다 — {0}", detail));
    }

    /// <summary>az login 후 다른 계정으로 바뀌었을 수 있으므로 캐시 비우기</summary>
    public static void ClearTokenCache() => TokenCache.Clear();

    private static async Task<List<JsonElement>> ListAsync(string url, string token, CancellationToken ct)
    {
        var all = new List<JsonElement>();
        string? next = url;
        while (!string.IsNullOrEmpty(next))
        {
            var r = await CloudProviders.GetJsonAsync(next, token, ct);
            all.AddRange(r.Arr("value"));
            next = r.Str("nextLink");
        }
        return all;
    }

    public async Task<List<CloudCandidate>> DiscoverAsync(CancellationToken ct)
    {
        string home;
        try { home = await TokenAsync(null, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw TokenError(null, ex);
        }
        List<string> tenants;
        try { tenants = (await ListAsync($"{Arm}/tenants?api-version=2022-12-01", home, ct)).Select(t => t.Str("tenantId")).ToList(); }
        catch (Exception ex) { ForgetTokenOn401(null, ex); throw; }

        // 테넌트마다 az 프로세스로 토큰을 받는 데 수 초씩 걸리므로 몇 개씩 동시에 받는다.
        // az 는 토큰 캐시 파일을 프로세스 간 잠금으로 다루지만, 동시 실행을 많이 늘리지는 않는다 (TenantParallelism).
        // 결과·건너뛴 사유는 테넌트 순서대로 모아 화면 표시가 매번 같게 한다
        using var gate = new SemaphoreSlim(TenantParallelism);
        var perTenant = await Task.WhenAll(tenants.Select(OneTenantAsync));
        ct.ThrowIfCancellationRequested();
        var list = perTenant.SelectMany(t => t.subs).ToList();
        var skipped = perTenant.Where(t => t.skip != null).Select(t => t.skip!).ToList();

        async Task<(List<CloudCandidate> subs, string? skip)> OneTenantAsync(string tenant)
        {
            await gate.WaitAsync(ct);
            try
            {
                var subs = new List<CloudCandidate>();
                var token = await TokenAsync(tenant, ct);
                foreach (var s in await ListAsync($"{Arm}/subscriptions?api-version=2022-12-01", token, ct))
                {
                    if (s.Str("state").Equals("Disabled", StringComparison.OrdinalIgnoreCase)) continue;
                    subs.Add(new CloudCandidate(s.Str("subscriptionId"), s.Str("displayName"), tenant));
                }
                return (subs, null);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // MFA 등으로 테넌트 토큰을 못 받으면 그 테넌트만 건너뛰되, 사유를 모아 둔다
                ForgetTokenOn401(tenant, ex);
                return (new List<CloudCandidate>(), SkippedTenant(tenant, ex.Message));
            }
            finally { gate.Release(); }
        }
        // 찾은 구독이 없는데 건너뛴 테넌트가 있으면 "구독 없음" 으로 보이지 않게 사유와 함께 실패로 알린다
        // (이 목록은 프로필 추가 후보일 뿐 동기화가 아니므로 호스트 삭제와는 무관. 동기화는 구독 단위 FetchAsync 로, 실패하면 예외)
        if (list.Count == 0 && skipped.Count > 0)
            throw new InvalidOperationException(L.F("구독을 찾지 못했습니다 — 토큰을 받지 못해 건너뛴 테넌트 {0}개: {1}", skipped.Count, string.Join(" / ", skipped)));
        return list.GroupBy(c => c.Name).Select(g => g.First()).OrderBy(c => c.DisplayName).ToList();
    }

    /// <summary>건너뛴 테넌트 한 줄 요약 ("테넌트: 사유 첫 줄", 사유는 160자까지)</summary>
    internal static string SkippedTenant(string tenant, string? reason)
    {
        var r = CloudProviders.FirstLine(reason ?? "");
        if (r.Length > 160) r = r[..160] + "…";
        return $"{tenant}: {r}";
    }

    public async Task<CloudFetchResult> FetchAsync(CloudProfile p, CancellationToken ct)
    {
        string token;
        try { token = await TokenAsync(p.TenantId, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw TokenError(p, ex);
        }

        var sub = $"{Arm}/subscriptions/{Uri.EscapeDataString(p.Name)}";
        // statusOnly=true 는 전원 상태(instanceView)만 주고 네트워크/OS 정보가 빠지므로 전체 목록과 따로 받아 합친다
        var vmsTask = ListAsync($"{sub}/providers/Microsoft.Compute/virtualMachines?api-version=2024-07-01", token, ct);
        var statusTask = ListAsync($"{sub}/providers/Microsoft.Compute/virtualMachines?api-version=2024-07-01&statusOnly=true", token, ct);
        var nicsTask = ListAsync($"{sub}/providers/Microsoft.Network/networkInterfaces?api-version=2024-05-01", token, ct);
        var pipsTask = ListAsync($"{sub}/providers/Microsoft.Network/publicIPAddresses?api-version=2024-05-01", token, ct);
        try { await Task.WhenAll(vmsTask, statusTask, nicsTask, pipsTask); }
        catch (Exception ex)
        {
            ForgetTokenOn401(p.TenantId, ex);
            throw;
        }

        var power = new Dictionary<string, string>();
        foreach (var s in statusTask.Result)
        {
            var code = s.Obj("properties").Obj("instanceView").Arr("statuses")
                .Select(x => x.Str("code")).FirstOrDefault(c => c.StartsWith("PowerState/"));
            if (code != null) power[s.Str("id").ToLowerInvariant()] = code["PowerState/".Length..];
        }

        var pips = pipsTask.Result.ToDictionary(x => x.Str("id").ToLowerInvariant(), x => x.Obj("properties").Str("ipAddress"));
        var nics = new Dictionary<string, (string priv, string pub)>();
        foreach (var n in nicsTask.Result)
        {
            string priv = "", pub = "";
            foreach (var cfg in n.Obj("properties").Arr("ipConfigurations"))
            {
                var props = cfg.Obj("properties");
                var primary = props.TryGetProperty("primary", out var pr) && pr.ValueKind == JsonValueKind.True;
                if (!primary && priv.Length > 0) continue;
                priv = props.Str("privateIPAddress");
                var pipId = props.Obj("publicIPAddress").Str("id").ToLowerInvariant();
                pub = pips.GetValueOrDefault(pipId) ?? "";
            }
            nics[n.Str("id").ToLowerInvariant()] = (priv, pub);
        }

        var result = new List<CloudInstance>();
        foreach (var vm in vmsTask.Result)
        {
            var props = vm.Obj("properties");
            var state = power.GetValueOrDefault(vm.Str("id").ToLowerInvariant()) ?? "unknown";
            var osType = props.Obj("storageProfile").Obj("osDisk").Str("osType");
            var img = props.Obj("storageProfile").Obj("imageReference");
            var os = img.Str("offer").Length > 0 ? $"{img.Str("offer")} {img.Str("sku")}".Trim() : osType;

            string priv = "", pub = "";
            foreach (var r in props.Obj("networkProfile").Arr("networkInterfaces"))
            {
                var primary = r.Obj("properties").TryGetProperty("primary", out var pr) && pr.ValueKind == JsonValueKind.True;
                if (!primary && priv.Length > 0) continue;
                if (nics.TryGetValue(r.Str("id").ToLowerInvariant(), out var ip)) (priv, pub) = ip;
            }

            var tags = vm.Obj("tags");
            var tagName = tags.ValueKind == JsonValueKind.Object
                ? tags.EnumerateObject().FirstOrDefault(t => t.Name.Equals("Name", StringComparison.OrdinalIgnoreCase)).Value
                : default;
            var raw = tagName.ValueKind == JsonValueKind.String ? tagName.GetString() ?? "" : vm.Str("name");
            var (name, purpose) = CloudInstance.SplitName(raw.Length > 0 ? raw : vm.Str("name"));

            result.Add(new CloudInstance(props.Str("vmId"), name, purpose, state, vm.Str("location"),
                priv, pub, (osType + os).Contains("windows", StringComparison.OrdinalIgnoreCase), os));
        }
        return new CloudFetchResult(result, new List<string>());
    }

    // ----- 쿠버네티스 (AKS) -----

    private const string AksApi = "2024-09-01";
    /// <summary>AKS AAD 서버 앱 ID (모든 AAD 통합 클러스터 공통) — kubelogin --server-id</summary>
    internal const string AksAadServerId = "6dae42f8-4368-4678-94ff-3960e28e3630";
    /// <summary>listClusterUserCredential 동시 호출 수</summary>
    private const int AksParallelism = 4;

    /// <summary>AKS 목록 한 항목에서 꺼낸 값</summary>
    internal sealed record AksInfo(string Id, string Name, string Location, string Fqdn, string Version, string State, bool Aad);

    /// <summary>
    /// 구독의 AKS 클러스터. 목록에는 CA 인증서가 없어 클러스터마다 listClusterUserCredential(자격 증명을 읽는 POST, 리소스는 바뀌지 않음)로
    /// kubeconfig 를 받는다. 그 호출이 실패한 클러스터는 건너뛰고 Errors 에 "이름: 사유" 로 남긴다 (삭제 판단 금지)
    /// </summary>
    public async Task<ClusterFetchResult> FetchClustersAsync(CloudProfile p, CancellationToken ct)
    {
        string token;
        try { token = await TokenAsync(p.TenantId, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw TokenError(p, ex);
        }

        var sub = $"{Arm}/subscriptions/{Uri.EscapeDataString(p.Name)}";
        List<JsonElement> raw;
        try { raw = await ListAsync($"{sub}/providers/Microsoft.ContainerService/managedClusters?api-version={AksApi}", token, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ContainerService 리소스 공급자를 등록하지 않은 구독 = 클러스터가 없는 구독 (오류 아님)
            if (IsProviderNotRegistered(ex.Message)) return ClusterFetchResult.Empty;
            ForgetTokenOn401(p.TenantId, ex);
            throw;
        }

        var infos = raw.Select(ParseAks).Where(a => a.Id.Length > 0 && a.Name.Length > 0).ToList();
        var (items, errors) = await CloudProviders.ParallelCollect(infos, AksParallelism, async a =>
        {
            var cred = await CloudProviders.SendJsonAsync(HttpMethod.Post,
                $"{Arm}{a.Id}/listClusterUserCredential?api-version={AksApi}", token, null, ct);
            var yaml = CredentialKubeconfig(cred) ?? throw new InvalidOperationException("listClusterUserCredential returned no kubeconfig");
            return (IEnumerable<CloudCluster>)new[] { BuildAksCluster(a, yaml) };
        }, a => a.Name, ct);
        return new ClusterFetchResult(items.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList(), errors);
    }

    /// <summary>managedClusters 목록 한 항목 → 값 (화면 무관)</summary>
    internal static AksInfo ParseAks(JsonElement c)
    {
        var props = c.Obj("properties");
        var fqdn = props.Str("fqdn");
        if (fqdn.Length == 0) fqdn = props.Str("privateFQDN");
        var version = props.Str("currentKubernetesVersion");
        if (version.Length == 0) version = props.Str("kubernetesVersion");
        var power = props.Obj("powerState").Str("code");
        var state = power.Equals("Stopped", StringComparison.OrdinalIgnoreCase)
            ? "stopped"
            : CloudCluster.NormalizeState(props.Str("provisioningState"));
        var aad = props.Obj("aadProfile").ValueKind == JsonValueKind.Object;
        return new AksInfo(c.Str("id"), c.Str("name"), c.Str("location"), fqdn, version, state, aad);
    }

    /// <summary>listClusterUserCredential 응답의 첫 kubeconfig (base64) 를 YAML 로. 없으면 null</summary>
    internal static string? CredentialKubeconfig(JsonElement resp)
    {
        var value = resp.Arr("kubeconfigs").Select(k => k.Str("value")).FirstOrDefault(v => v.Length > 0);
        if (value == null) return null;
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(value)); }
        catch (FormatException) { throw new InvalidOperationException("listClusterUserCredential returned an invalid kubeconfig"); }
    }

    /// <summary>
    /// 목록 값 + 받은 kubeconfig → CloudCluster. AAD 클러스터는 server·CA 만 꺼내 kubelogin(azurecli) exec 로 다시 만들고 (비밀값 없음),
    /// 로컬 계정 클러스터는 받은 kubeconfig 의 첫 context 만 남긴다 (클라이언트 인증서 포함 — 앱이 암호화해 저장)
    /// </summary>
    internal static CloudCluster BuildAksCluster(AksInfo a, string kubeconfig)
    {
        var endpoint = a.Fqdn.Length > 0 ? "https://" + a.Fqdn : "";
        string yaml, context;
        K8sAuthKind auth;
        if (a.Aad)
        {
            var (server, ca) = ClusterOfKubeconfig(kubeconfig);
            if (string.IsNullOrEmpty(server)) server = endpoint;
            if (string.IsNullOrEmpty(server)) throw new InvalidOperationException("kubeconfig has no server");
            context = a.Name;
            yaml = Kubeconfig.BuildExec(context, server, ca, "kubelogin",
                new[] { "get-token", "--login", "azurecli", "--server-id", AksAadServerId });
            auth = K8sAuthKind.Azure;
        }
        else
        {
            // context 이름은 kubeconfig 에 든 그대로 (AKS 는 보통 클러스터 이름) — kubectl --context 와 맞아야 한다
            var contexts = Kubeconfig.ReadContexts(kubeconfig);
            if (contexts.Count == 0) throw new InvalidOperationException("kubeconfig has no context");
            context = contexts[0].Name;
            yaml = contexts.Count == 1 ? kubeconfig : Kubeconfig.Extract(kubeconfig, context, null);
            auth = Kubeconfig.DetectAuth(yaml);
        }
        return new CloudCluster("aks:" + a.Id.ToLowerInvariant(), a.Name, a.Location, endpoint, a.Version, a.State,
            auth, yaml, context);
    }

    /// <summary>kubeconfig 의 첫 context(없으면 첫 cluster)가 가리키는 cluster 의 server · certificate-authority-data</summary>
    internal static (string? Server, string? CaData) ClusterOfKubeconfig(string yaml)
    {
        var stream = new YamlStream();
        try { stream.Load(new StringReader(yaml)); }
        catch (YamlException ex) { throw new InvalidOperationException("invalid kubeconfig: " + ex.Message); }
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root) return (null, null);

        static YamlNode? Get(YamlMappingNode m, string key) => m.Children.TryGetValue(new YamlScalarNode(key), out var n) ? n : null;
        static string? Str(YamlMappingNode m, string key) => Get(m, key) is YamlScalarNode s ? s.Value : null;
        static IEnumerable<YamlMappingNode> Items(YamlMappingNode m, string key) =>
            Get(m, key) is YamlSequenceNode seq ? seq.Children.OfType<YamlMappingNode>() : Enumerable.Empty<YamlMappingNode>();

        var clusters = Items(root, "clusters").ToList();
        var ctx = Items(root, "contexts").Select(c => Get(c, "context") as YamlMappingNode).FirstOrDefault(c => c != null);
        var clusterName = ctx != null ? Str(ctx, "cluster") : null;
        var item = clusters.FirstOrDefault(c => clusterName != null && Str(c, "name") == clusterName) ?? clusters.FirstOrDefault();
        if (item == null || Get(item, "cluster") is not YamlMappingNode cm) return (null, null);
        return (Str(cm, "server"), Str(cm, "certificate-authority-data"));
    }

    /// <summary>구독에 리소스 공급자가 등록되지 않았다는 ARM 오류인지 (MissingSubscriptionRegistration)</summary>
    internal static bool IsProviderNotRegistered(string? message) =>
        message != null &&
        (message.Contains("MissingSubscriptionRegistration", StringComparison.OrdinalIgnoreCase) ||
         message.Contains("not registered to use namespace", StringComparison.OrdinalIgnoreCase));
}
