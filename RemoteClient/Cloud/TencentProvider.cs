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
    private const string Host = "cvm.tencentcloudapi.com";
    private const string Version = "2017-03-12";

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

    public async Task<CloudFetchResult> FetchAsync(CloudProfile p, CancellationToken ct)
    {
        var file = Path.Combine(TccliDir, p.Name + ".credential");
        if (!File.Exists(file)) throw new InvalidOperationException(L.F("{0} 이 없습니다 (tccli configure --profile {1}).", file, p.Name));
        JsonElement cred;
        // Clone: 문서를 바로 Dispose 해 풀 버퍼를 돌려준다
        try { using var credDoc = JsonDocument.Parse(await File.ReadAllTextAsync(file, ct)); cred = credDoc.RootElement.Clone(); }
        catch (JsonException) { throw new InvalidOperationException(L.F("{0} 을 읽을 수 없습니다 (JSON 형식 확인).", file)); }
        var id = cred.Str("secretId");
        var key = cred.Str("secretKey");
        var token = cred.Str("token");

        var regionsResp = await CallAsync(id, key, token, "DescribeRegions", "ap-seoul", "{}", ct);
        var regions = regionsResp.Arr("RegionSet")
            .Where(r => r.Str("RegionState") == "AVAILABLE")
            .Select(r => r.Str("Region")).ToList();
        // 리전이 0개면 "조회 성공 + 서버 없음" 으로 보여 이 프로필 호스트가 전부 지워지므로 실패로 처리
        if (regions.Count == 0)
            throw new InvalidOperationException(L.T("사용 가능한 리전 목록이 비어 있습니다 (DescribeRegions) — 이번 동기화는 건너뜁니다."));

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
                var resp = await CallAsync(id, key, token, "DescribeInstances", region, $"{{\"Offset\":{offset},\"Limit\":100}}", ct);
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
        return new CloudFetchResult(items, errors);
    }

    /// <summary>
    /// 없어진 인스턴스: TERMINATING(삭제 중), LAUNCH_FAILED(생성 실패).
    /// AWS 의 terminated 와 같이 목록에서 뺀다 (조회 자체는 성공이므로 부분 실패로 보지 않음).
    /// SHUTDOWN(격리·회수 대기)은 갱신·복구할 수 있는 상태라 빼지 않는다 — 빼면 동기화가 그 호스트와 사용자 설정(계정·포트 등)을 지운다.
    /// 상태는 그대로 SHUTDOWN 으로 남아 중지된 서버처럼 보인다.
    /// </summary>
    internal static bool IsGone(string state) =>
        state.ToUpperInvariant() is "TERMINATING" or "LAUNCH_FAILED";

    /// <summary>TC3-HMAC-SHA256 Authorization 헤더 값 (CVM, POST /, content-type;host;x-tc-action 서명)</summary>
    internal static string Authorization(string secretId, string secretKey, string action, string payload, long ts)
    {
        var date = DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var canonical = $"POST\n/\n\ncontent-type:application/json; charset=utf-8\nhost:{Host}\nx-tc-action:{action.ToLowerInvariant()}\n\n" +
                        $"content-type;host;x-tc-action\n{Sha256Hex(payload)}";
        var scope = $"{date}/cvm/tc3_request";
        var toSign = $"TC3-HMAC-SHA256\n{ts}\n{scope}\n{Sha256Hex(canonical)}";
        var kDate = Hmac(Encoding.UTF8.GetBytes("TC3" + secretKey), date);
        var kService = Hmac(kDate, "cvm");
        var kSigning = Hmac(kService, "tc3_request");
        var signature = Convert.ToHexString(Hmac(kSigning, toSign)).ToLowerInvariant();
        return $"TC3-HMAC-SHA256 Credential={secretId}/{scope}, SignedHeaders=content-type;host;x-tc-action, Signature={signature}";
    }

    /// <summary>TC3-HMAC-SHA256 서명 API 호출. 반환값은 Response 객체</summary>
    private static async Task<JsonElement> CallAsync(string secretId, string secretKey, string token,
        string action, string region, string payload, CancellationToken ct)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        const string contentType = "application/json; charset=utf-8";

        using var req = new HttpRequestMessage(HttpMethod.Post, $"https://{Host}/");
        req.Headers.TryAddWithoutValidation("Authorization", Authorization(secretId, secretKey, action, payload, ts));
        req.Headers.Add("X-TC-Action", action);
        req.Headers.Add("X-TC-Timestamp", ts.ToString());
        req.Headers.Add("X-TC-Version", Version);
        req.Headers.Add("X-TC-Region", region);
        if (!string.IsNullOrEmpty(token)) req.Headers.Add("X-TC-Token", token);
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
            throw new InvalidOperationException($"{err.Str("Code")}: {err.Str("Message")}");
        // 성공 상태인데 Response 가 없으면 빈 결과로 오인하지 않게 실패로 (리전 0개 · 서버 0개로 보여 삭제되는 것 방지)
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: unexpected response (no Response object)");
        return root;
    }

    private static string Sha256Hex(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    private static byte[] Hmac(byte[] key, string msg) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(msg));
}
