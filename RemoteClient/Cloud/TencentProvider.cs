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

namespace Nubis;

// =====================================================================
// Tencent: ~/.tccli/<프로필>.credential (secretId/secretKey), TC3-HMAC-SHA256 서명으로 직접 호출
// =====================================================================
public sealed class TencentProvider : ICloudProvider
{
    /// <summary>TC3 서명·호출 대상 (호스트 · 서명 scope 의 서비스 이름 · API 버전)</summary>
    internal sealed record Api(string Host, string Service, string Version);

    internal static readonly Api Cvm = new("cvm.tencentcloudapi.com", "cvm", "2017-03-12");
    internal static readonly Api Tke = new("tke.tencentcloudapi.com", "tke", "2018-05-25");

    public CloudProvider Kind => CloudProvider.Tencent;
    public string AuthHint => L.T("~/.tccli/<프로필>.credential (tccli configure)");
    public string LoginCommand(CloudProfile? p) => $"tccli configure --profile {p?.Name ?? "default"}";

    private static string TccliDir => CloudPaths.Effective(CloudProvider.Tencent);

    public Task<List<CloudCandidate>> DiscoverAsync(CancellationToken ct)
    {
        var list = Directory.Exists(TccliDir)
            ? Directory.GetFiles(TccliDir, "*.credential")
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .OrderBy(n => n)
                .Select(n => new CloudCandidate(n, n.ToUpperInvariant()))
                .ToList()
            : new List<CloudCandidate>();
        return Task.FromResult(list);
    }

    /// <summary>~/.tccli/&lt;프로필&gt;.credential 의 키 (값은 메시지·로그에 넣지 않는다)</summary>
    private sealed record Credential(string Id, string Key, string Token);

    private static async Task<Credential> ReadCredentialAsync(CloudProfile p, CancellationToken ct)
    {
        var file = Path.Combine(TccliDir, p.Name + ".credential");
        if (!File.Exists(file)) throw new InvalidOperationException(L.F("{0} 이 없습니다 (tccli configure --profile {1}).", file, p.Name));
        JsonElement cred;
        // Clone: 문서를 바로 Dispose 해 풀 버퍼를 돌려준다
        try { using var credDoc = JsonDocument.Parse(await File.ReadAllTextAsync(file, ct)); cred = credDoc.RootElement.Clone(); }
        catch (JsonException) { throw new InvalidOperationException(L.F("{0} 을 읽을 수 없습니다 (JSON 형식 확인).", file)); }
        return new Credential(cred.Str("secretId"), cred.Str("secretKey"), cred.Str("token"));
    }

    /// <summary>사용 가능한 리전 (CVM DescribeRegions). 0개면 실패 — "조회 성공 + 0개" 로 보여 전부 지워지는 것 방지</summary>
    private static async Task<List<string>> RegionsAsync(Credential c, CancellationToken ct)
    {
        var regionsResp = await CallAsync(c, Cvm, "DescribeRegions", "ap-seoul", "{}", ct);
        var regions = regionsResp.Arr("RegionSet")
            .Where(r => r.Str("RegionState") == "AVAILABLE")
            .Select(r => r.Str("Region")).ToList();
        if (regions.Count == 0)
            throw new InvalidOperationException(L.T("사용 가능한 리전 목록이 비어 있습니다 (DescribeRegions) — 이번 동기화는 건너뜁니다."));
        return regions;
    }

    public async Task<CloudFetchResult> FetchAsync(CloudProfile p, CancellationToken ct)
    {
        var cred = await ReadCredentialAsync(p, ct);
        var regions = await RegionsAsync(cred, ct);

        // CAM 정책으로 막힌 리전은 오류와 따로 모은다 (AWS 와 같은 이유 — 막힌 리전 하나 때문에 매번 "부분 실패" 가 되어
        // 다른 리전의 삭제가 영영 반영되지 않는 것을 피한다). 호스트의 CloudRegion 은 가용 영역(ap-seoul-1)이라
        // CloudSync 가 "리전-" 접두어로도 보호한다
        var denied = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var (items, errors) = await CloudProviders.ParallelCollect(regions, 8, async region =>
        {
            var result = new List<CloudInstance>();
            // Offset/Limit 페이지는 스냅숏이 아니다 — 페이지 사이에 생성·삭제가 있으면 항목이 밀려 빠지거나 중복된다.
            // 받은 원본 개수·ID 중복·TotalCount 변화를 확인해 어긋나면 이 리전을 실패로 돌려 부분 결과(삭제 안 함)가 되게 한다
            var ids = new HashSet<string>();
            int rawCount = 0, firstTotal = -1, lastTotal = 0;
            var inconsistent = false;
            for (var offset = 0; ; offset += 100)
            {
                JsonElement resp;
                try { resp = await CallAsync(cred, Cvm, "DescribeInstances", region, $"{{\"Offset\":{offset},\"Limit\":100}}", ct); }
                catch (TencentApiException ex) when (offset == 0 && IsAccessDenied(ex.Code))
                {
                    // 첫 페이지부터 거부 = 이 리전 전체가 정책으로 막힘. 중간 페이지 거부는 일반 오류로 둔다
                    denied[region] = CloudProviders.FirstLine(ex.Message);
                    return Enumerable.Empty<CloudInstance>();
                }
                foreach (var inst in resp.Arr("InstanceSet"))
                {
                    rawCount++;
                    if (!ids.Add(inst.Str("InstanceId"))) inconsistent = true;
                    if (IsGone(inst.Str("InstanceState"))) continue;
                    var tags = inst.Arr("Tags").GroupBy(t => t.Str("Key")).ToDictionary(g => g.Key, g => g.First().Str("Value"));
                    var (name, purpose) = CloudInstance.SplitName(inst.Str("InstanceName"));
                    purpose = tags.GetValueOrDefault("Purpose") ?? tags.GetValueOrDefault("purpose") ?? purpose;
                    var os = inst.Str("OsName");
                    result.Add(new CloudInstance(
                        inst.Str("InstanceId"), name, purpose, inst.Str("InstanceState"), inst.Obj("Placement").Str("Zone"),
                        inst.Arr("PrivateIpAddresses").Select(x => x.GetString()).FirstOrDefault() ?? "",
                        inst.Arr("PublicIpAddresses").Select(x => x.GetString()).FirstOrDefault() ?? "",
                        os.Contains("windows", StringComparison.OrdinalIgnoreCase), os));
                }
                var total = resp.TryGetProperty("TotalCount", out var tc) ? tc.GetInt32() : 0;
                if (firstTotal < 0) firstTotal = total;
                else if (total != firstTotal) inconsistent = true;
                lastTotal = total;
                if (offset + 100 >= total) break;
            }
            if (inconsistent || rawCount != lastTotal)
                throw new InvalidOperationException(L.F("조회 중 인스턴스 목록이 바뀌었습니다 (받은 {0}개 / 전체 {1}개) — 다시 동기화하세요.", rawCount, lastTotal));
            return (IEnumerable<CloudInstance>)result;
        }, r => r, ct);

        if (items.Count == 0 && errors.Count > 0) throw new InvalidOperationException(errors[0]);
        if (items.Count == 0 && denied.Count == regions.Count)
            throw new InvalidOperationException(L.F("모든 리전에서 인스턴스 조회 권한이 없습니다 (cvm:DescribeInstances) — {0}", denied.Values.First()));
        // 거부 리전 사유는 실제 오류 뒤에 붙인다 (화면은 첫 오류를 보여 주므로 실제 오류가 먼저 보이게)
        var deniedRegions = denied.Keys.OrderBy(r => r, StringComparer.Ordinal).ToList();
        foreach (var r in deniedRegions)
            errors.Add(L.F("{0}: 권한 없음 (CAM 정책으로 거부 — 이 리전 호스트는 유지) — {1}", r, denied[r]));
        return new CloudFetchResult(items, errors) { DeniedRegions = deniedRegions };
    }

    /// <summary>CAM 정책으로 거부됐다는 오류 코드인지. 서명·키 자체의 문제(AuthFailure.SignatureFailure 등)는 포함하지 않는다</summary>
    internal static bool IsAccessDenied(string? code) =>
        code is "UnauthorizedOperation" or "AuthFailure.UnauthorizedOperation" ||
        (code?.StartsWith("UnauthorizedOperation.", StringComparison.Ordinal) ?? false);

    // ----- TKE 클러스터 -----

    public async Task<ClusterFetchResult> FetchClustersAsync(CloudProfile p, CancellationToken ct)
    {
        var cred = await ReadCredentialAsync(p, ct);
        var regions = await RegionsAsync(cred, ct);

        // 권한 거부 리전은 VM 조회와 같은 규칙으로 따로 모은다 (그 리전 클러스터는 지우지 않음)
        var denied = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var kubeconfigErrors = new ConcurrentBag<string>();
        var (items, errors) = await CloudProviders.ParallelCollect(regions, 8, async region =>
        {
            var clusters = new List<JsonElement>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            int rawCount = 0, firstTotal = -1, lastTotal = 0;
            var inconsistent = false;
            for (var offset = 0; ; offset += 100)
            {
                JsonElement resp;
                try { resp = await CallAsync(cred, Tke, "DescribeClusters", region, $"{{\"Offset\":{offset},\"Limit\":100}}", ct); }
                catch (TencentApiException ex) when (offset == 0 && IsAccessDenied(ex.Code))
                {
                    denied[region] = CloudProviders.FirstLine(ex.Message);
                    return Enumerable.Empty<CloudCluster>();
                }
                catch (TencentApiException ex) when (offset == 0 && IsUnsupportedRegion(ex.Code))
                {
                    // CVM 은 있지만 TKE 를 제공하지 않는 리전 — 클러스터 없음
                    return Enumerable.Empty<CloudCluster>();
                }
                foreach (var c in resp.Arr("Clusters"))
                {
                    rawCount++;
                    if (!ids.Add(c.Str("ClusterId"))) inconsistent = true;
                    clusters.Add(c);
                }
                var total = resp.TryGetProperty("TotalCount", out var tc) && tc.ValueKind == JsonValueKind.Number ? tc.GetInt32() : 0;
                if (firstTotal < 0) firstTotal = total;
                else if (total != firstTotal) inconsistent = true;
                lastTotal = total;
                if (offset + 100 >= total) break;
            }
            if (inconsistent || rawCount != lastTotal)
                throw new InvalidOperationException(L.F("조회 중 클러스터 목록이 바뀌었습니다 (받은 {0}개 / 전체 {1}개) — 다시 동기화하세요.", rawCount, lastTotal));

            var result = new List<CloudCluster>();
            foreach (var c in clusters.Where(c => c.Str("ClusterId").Length > 0 && !IsClusterGone(c.Str("ClusterStatus"))))
            {
                var label = $"{region} · {(c.Str("ClusterName").Length > 0 ? c.Str("ClusterName") : c.Str("ClusterId"))}";
                try
                {
                    var kubeconfig = await KubeconfigAsync(cred, region, c.Str("ClusterId"), ct);
                    result.Add(MapTkeCluster(c, region, kubeconfig));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // kubeconfig 를 못 받은 클러스터는 건너뛰고 오류로 남긴다 (부분 결과 → 아무것도 지우지 않음)
                    kubeconfigErrors.Add($"{label}: {CloudProviders.FirstLine(ex.Message)}");
                }
            }
            return (IEnumerable<CloudCluster>)result;
        }, r => r, ct);

        errors.AddRange(kubeconfigErrors.OrderBy(e => e, StringComparer.Ordinal));
        if (items.Count == 0 && errors.Count > 0) throw new InvalidOperationException(errors[0]);
        if (items.Count == 0 && denied.Count == regions.Count)
            throw new InvalidOperationException(L.F("모든 리전에서 클러스터 조회 권한이 없습니다 (tke:DescribeClusters) — {0}", denied.Values.First()));
        var deniedRegions = denied.Keys.OrderBy(r => r, StringComparer.Ordinal).ToList();
        foreach (var r in deniedRegions)
            errors.Add(L.F("{0}: 권한 없음 (CAM 정책으로 거부 — 이 리전 클러스터는 유지) — {1}", r, denied[r]));
        return new ClusterFetchResult(items, errors) { DeniedRegions = deniedRegions };
    }

    /// <summary>DescribeClusterKubeconfig (조회 API). 외부 주소(IsExtranet=true)를 먼저, 안 되면 내부 주소</summary>
    private static async Task<string> KubeconfigAsync(Credential cred, string region, string clusterId, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var extranet in new[] { true, false })
        {
            try
            {
                var resp = await CallAsync(cred, Tke, "DescribeClusterKubeconfig", region, KubeconfigRequest(clusterId, extranet), ct);
                if (resp.Str("Kubeconfig") is { Length: > 0 } kc) return kc;
                last = new InvalidOperationException(L.T("DescribeClusterKubeconfig 응답에 kubeconfig 가 없습니다."));
            }
            catch (InvalidOperationException ex) { last = ex; }
        }
        throw last!;
    }

    /// <summary>DescribeClusterKubeconfig 요청 본문</summary>
    internal static string KubeconfigRequest(string clusterId, bool extranet) =>
        JsonSerializer.Serialize(new Dictionary<string, object> { ["ClusterId"] = clusterId, ["IsExtranet"] = extranet });

    /// <summary>TKE 를 제공하지 않는 리전이라는 오류 코드인지 (그 리전은 클러스터 0개로 본다)</summary>
    internal static bool IsUnsupportedRegion(string? code) =>
        code == "UnsupportedRegion" || (code?.StartsWith("UnsupportedRegion.", StringComparison.Ordinal) ?? false);

    /// <summary>없어진 클러스터: Deleting · Deleted. 목록에서 뺀다 (Abnormal · Creating 등은 남긴다)</summary>
    internal static bool IsClusterGone(string status) =>
        status.ToUpperInvariant() is "DELETING" or "DELETED";

    /// <summary>
    /// DescribeClusters 항목 + kubeconfig → 공통 형식. context 가 여럿이면 current-context(없으면 첫 context) 하나만 남긴다.
    /// kubeconfig 를 읽지 못하면 FormatException
    /// </summary>
    internal static CloudCluster MapTkeCluster(JsonElement cluster, string region, string kubeconfig)
    {
        var contexts = Kubeconfig.ReadContexts(kubeconfig);
        if (contexts.Count == 0) throw new FormatException(L.T("kubeconfig 에 context 가 없습니다."));
        var current = Kubeconfig.CurrentContext(kubeconfig);
        var ctx = contexts.FirstOrDefault(c => c.Name == current) ?? contexts[0];
        var single = contexts.Count > 1 ? Kubeconfig.Extract(kubeconfig, ctx.Name, null) : kubeconfig;
        var id = cluster.Str("ClusterId");
        var name = cluster.Str("ClusterName");
        return new CloudCluster("tke:" + id, name.Length > 0 ? name : id, region, ctx.Server ?? "", cluster.Str("ClusterVersion"),
            CloudCluster.NormalizeState(cluster.Str("ClusterStatus")), Kubeconfig.DetectAuth(single), single, ctx.Name);
    }

    /// <summary>Tencent API 오류 (오류 코드 보존 — 권한 거부 리전 판별용)</summary>
    private sealed class TencentApiException(string code, string message) : InvalidOperationException(message)
    {
        public string Code { get; } = code;
    }

    /// <summary>
    /// 없어진 인스턴스: TERMINATING(삭제 중), LAUNCH_FAILED(생성 실패).
    /// AWS 의 terminated 와 같이 목록에서 뺀다 (조회 자체는 성공이므로 부분 실패로 보지 않음).
    /// SHUTDOWN(격리·회수 대기)은 갱신·복구할 수 있는 상태라 빼지 않는다 — 빼면 동기화가 그 호스트와 사용자 설정(계정·포트 등)을 지운다.
    /// 상태는 그대로 SHUTDOWN 으로 남아 중지된 서버처럼 보인다.
    /// </summary>
    internal static bool IsGone(string state) =>
        state.ToUpperInvariant() is "TERMINATING" or "LAUNCH_FAILED";

    /// <summary>TC3-HMAC-SHA256 Authorization 헤더 값 (CVM)</summary>
    internal static string Authorization(string secretId, string secretKey, string action, string payload, long ts) =>
        Authorization(Cvm, secretId, secretKey, action, payload, ts);

    /// <summary>TC3-HMAC-SHA256 Authorization 헤더 값 (POST /, content-type;host;x-tc-action 서명, scope 는 api.Service)</summary>
    internal static string Authorization(Api api, string secretId, string secretKey, string action, string payload, long ts)
    {
        var date = DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var canonical = $"POST\n/\n\ncontent-type:application/json; charset=utf-8\nhost:{api.Host}\nx-tc-action:{action.ToLowerInvariant()}\n\n" +
                        $"content-type;host;x-tc-action\n{Sha256Hex(payload)}";
        var scope = $"{date}/{api.Service}/tc3_request";
        var toSign = $"TC3-HMAC-SHA256\n{ts}\n{scope}\n{Sha256Hex(canonical)}";
        var kDate = Hmac(Encoding.UTF8.GetBytes("TC3" + secretKey), date);
        var kService = Hmac(kDate, api.Service);
        var kSigning = Hmac(kService, "tc3_request");
        var signature = Convert.ToHexString(Hmac(kSigning, toSign)).ToLowerInvariant();
        return $"TC3-HMAC-SHA256 Credential={secretId}/{scope}, SignedHeaders=content-type;host;x-tc-action, Signature={signature}";
    }

    /// <summary>TC3-HMAC-SHA256 서명 API 호출. 반환값은 Response 객체</summary>
    private static async Task<JsonElement> CallAsync(Credential c, Api api,
        string action, string region, string payload, CancellationToken ct)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        const string contentType = "application/json; charset=utf-8";

        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://{api.Host}/");
        req.Headers.TryAddWithoutValidation("Authorization", Authorization(api, c.Id, c.Key, action, payload, ts));
        req.Headers.Add("X-TC-Action", action);
        req.Headers.Add("X-TC-Timestamp", ts.ToString());
        req.Headers.Add("X-TC-Version", api.Version);
        req.Headers.Add("X-TC-Region", region);
        if (!string.IsNullOrEmpty(c.Token)) req.Headers.Add("X-TC-Token", c.Token);
        req.Content = new StringContent(payload, Encoding.UTF8);
        req.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);

        using var resp = await CloudProviders.Http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement.Obj("Response");
            root = r.ValueKind == JsonValueKind.Undefined ? default : r.Clone();
        }
        catch (JsonException)
        {
            // 게이트웨이 오류 페이지(HTML) 등 — JSON 파서 메시지 대신 HTTP 상태를 보여 준다
            throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {CloudProviders.FirstLine(body.Length > 200 ? body[..200] : body)}");
        }
        var err = root.Obj("Error");
        if (err.ValueKind == JsonValueKind.Object)
            throw new TencentApiException(err.Str("Code"), $"{err.Str("Code")}: {err.Str("Message")}");
        // 성공 상태인데 Response 가 없으면 빈 결과로 오인하지 않게 실패로 (리전 0개 · 서버 0개로 보여 삭제되는 것 방지)
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: unexpected response (no Response object)");
        return root;
    }

    private static string Sha256Hex(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    private static byte[] Hmac(byte[] key, string msg) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(msg));
}
