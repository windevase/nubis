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
// Azure: az CLI 로그인 (AzureCliCredential), ARM REST 호출
// =====================================================================
public sealed class AzureProvider : ICloudProvider
{
    private const string Arm = "https://management.azure.com";

    public CloudProvider Kind => CloudProvider.Azure;
    public string AuthHint => "az CLI 로그인 · az login";
    public string LoginCommand(CloudProfile? p) =>
        string.IsNullOrEmpty(p?.TenantId) ? "az login" : $"az login --tenant {p.TenantId}";

    // az CLI 로 토큰을 받는 데 수 초가 걸리므로 테넌트별로 만료 5분 전까지 재사용
    private static readonly ConcurrentDictionary<string, AccessToken> TokenCache = new();

    private static async Task<string> TokenAsync(string? tenant, CancellationToken ct)
    {
        var key = (tenant ?? "") + "|" + (Environment.GetEnvironmentVariable("AZURE_CONFIG_DIR") ?? "");
        if (TokenCache.TryGetValue(key, out var cached) && cached.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5))
            return cached.Token;
        var cred = new AzureCliCredential(new AzureCliCredentialOptions
        {
            TenantId = string.IsNullOrEmpty(tenant) ? null : tenant,
            ProcessTimeout = TimeSpan.FromSeconds(60),
        });
        var t = await cred.GetTokenAsync(new TokenRequestContext(new[] { Arm + "/.default" }), ct);
        TokenCache[key] = t;
        return t.Token;
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
        var home = await TokenAsync(null, ct);
        var tenants = (await ListAsync($"{Arm}/tenants?api-version=2022-12-01", home, ct)).Select(t => t.Str("tenantId")).ToList();

        var list = new List<CloudCandidate>();
        foreach (var tenant in tenants)
        {
            try
            {
                var token = await TokenAsync(tenant, ct);
                foreach (var s in await ListAsync($"{Arm}/subscriptions?api-version=2022-12-01", token, ct))
                {
                    if (s.Str("state").Equals("Disabled", StringComparison.OrdinalIgnoreCase)) continue;
                    list.Add(new CloudCandidate(s.Str("subscriptionId"), s.Str("displayName"), tenant));
                }
            }
            catch { /* MFA 등으로 테넌트 토큰을 못 받으면 그 테넌트만 건너뜀 */ }
        }
        return list.GroupBy(c => c.Name).Select(g => g.First()).OrderBy(c => c.DisplayName).ToList();
    }

    public async Task<CloudFetchResult> FetchAsync(CloudProfile p, CancellationToken ct)
    {
        string token;
        try { token = await TokenAsync(p.TenantId, ct); }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"az 로그인이 필요합니다 ({LoginCommand(p)}) — {CloudProviders.FirstLine(ex.Message)}");
        }

        var sub = $"{Arm}/subscriptions/{p.Name}";
        // statusOnly=true 는 전원 상태(instanceView)만 주고 네트워크/OS 정보가 빠지므로 전체 목록과 따로 받아 합친다
        var vmsTask = ListAsync($"{sub}/providers/Microsoft.Compute/virtualMachines?api-version=2024-07-01", token, ct);
        var statusTask = ListAsync($"{sub}/providers/Microsoft.Compute/virtualMachines?api-version=2024-07-01&statusOnly=true", token, ct);
        var nicsTask = ListAsync($"{sub}/providers/Microsoft.Network/networkInterfaces?api-version=2024-05-01", token, ct);
        var pipsTask = ListAsync($"{sub}/providers/Microsoft.Network/publicIPAddresses?api-version=2024-05-01", token, ct);
        await Task.WhenAll(vmsTask, statusTask, nicsTask, pipsTask);

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
}
