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
        var cred = JsonDocument.Parse(await File.ReadAllTextAsync(file, ct)).RootElement;
        var id = cred.Str("secretId");
        var key = cred.Str("secretKey");
        var token = cred.Str("token");

        var regionsResp = await CallAsync(id, key, token, "DescribeRegions", "ap-seoul", "{}", ct);
        var regions = regionsResp.Arr("RegionSet")
            .Where(r => r.Str("RegionState") == "AVAILABLE")
            .Select(r => r.Str("Region")).ToList();

        var (items, errors) = await CloudProviders.ParallelCollect(regions, 8, async region =>
        {
            var result = new List<CloudInstance>();
            for (var offset = 0; ; offset += 100)
            {
                var resp = await CallAsync(id, key, token, "DescribeInstances", region, $"{{\"Offset\":{offset},\"Limit\":100}}", ct);
                foreach (var inst in resp.Arr("InstanceSet"))
                {
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
                if (offset + 100 >= total) break;
            }
            return (IEnumerable<CloudInstance>)result;
        }, r => r);

        if (items.Count == 0 && errors.Count > 0) throw new InvalidOperationException(errors[0]);
        return new CloudFetchResult(items, errors);
    }

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
        var root = JsonDocument.Parse(body).RootElement.Obj("Response");
        var err = root.Obj("Error");
        if (err.ValueKind == JsonValueKind.Object)
            throw new InvalidOperationException($"{err.Str("Code")}: {err.Str("Message")}");
        return root.Clone();
    }

    private static string Sha256Hex(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    private static byte[] Hmac(byte[] key, string msg) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(msg));
}
