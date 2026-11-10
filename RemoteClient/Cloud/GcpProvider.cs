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
// GCP: ADC (gcloud auth application-default login), REST 호출
// =====================================================================
public sealed class GcpProvider : ICloudProvider
{
    private static readonly string[] SkipPrefixes = { "sys-", "gen-lang-client-" };

    public CloudProvider Kind => CloudProvider.Gcp;
    public string AuthHint => "ADC · gcloud auth application-default login";
    public string LoginCommand(CloudProfile? p) => "gcloud auth application-default login";

    private static async Task<(string token, string? quota)> TokenAsync(CancellationToken ct)
    {
        var cred = (await GoogleCredential.GetApplicationDefaultAsync(ct))
            .CreateScoped("https://www.googleapis.com/auth/cloud-platform");
        var token = await ((ITokenAccess)cred).GetAccessTokenForRequestAsync(null, ct);
        return (token, cred.QuotaProject);
    }

    public async Task<List<CloudCandidate>> DiscoverAsync(CancellationToken ct)
    {
        var (token, quota) = await TokenAsync(ct);
        var list = new List<CloudCandidate>();
        string? page = null;
        do
        {
            var url = "https://cloudresourcemanager.googleapis.com/v3/projects:search?query=state:ACTIVE&pageSize=500" +
                      (page != null ? "&pageToken=" + Uri.EscapeDataString(page) : "");
            var r = await CloudProviders.GetJsonAsync(url, token, ct, quota);
            foreach (var proj in r.Arr("projects"))
            {
                var id = proj.Str("projectId");
                if (SkipPrefixes.Any(id.StartsWith)) continue;
                list.Add(new CloudCandidate(id, id));
            }
            page = r.Str("nextPageToken");
            if (page.Length == 0) page = null;
        } while (page != null);
        return list.OrderBy(c => c.Name).ToList();
    }

    public async Task<CloudFetchResult> FetchAsync(CloudProfile p, CancellationToken ct)
    {
        var (token, quota) = await TokenAsync(ct);
        var result = new List<CloudInstance>();
        var errors = new List<string>();
        string? page = null;
        do
        {
            var url = $"https://compute.googleapis.com/compute/v1/projects/{Uri.EscapeDataString(p.Name)}/aggregated/instances" +
                      "?returnPartialSuccess=true" + (page != null ? "&pageToken=" + Uri.EscapeDataString(page) : "");
            JsonElement r;
            try { r = await CloudProviders.GetJsonAsync(url, token, ct, quota); }
            catch (HttpRequestException ex) when (ex.Message.Contains("has not been used") || ex.Message.Contains("disabled"))
            {
                throw new InvalidOperationException("이 프로젝트는 Compute Engine API 를 사용하지 않습니다.");
            }

            if (r.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
            {
                foreach (var scope in items.EnumerateObject())
                {
                    // returnPartialSuccess: 응답하지 못한 영역(zone)은 warning.code = UNREACHABLE 로 온다
                    var warn = scope.Value.Obj("warning");
                    if (warn.Str("code") == "UNREACHABLE") errors.Add($"{scope.Name}: {warn.Str("message")}");
                }
                foreach (var scope in items.EnumerateObject())
                foreach (var inst in scope.Value.Arr("instances"))
                {
                    var nic = inst.Arr("networkInterfaces").FirstOrDefault();
                    var pub = nic.Arr("accessConfigs").Select(a => a.Str("natIP")).FirstOrDefault(x => x.Length > 0) ?? "";
                    var boot = inst.Arr("disks").FirstOrDefault(d => d.TryGetProperty("boot", out var b) && b.GetBoolean());
                    var licenses = boot.Arr("licenses").Select(l => Last(l.GetString())).ToList();
                    var os = licenses.FirstOrDefault(l => l.Contains("windows")) ?? licenses.FirstOrDefault() ?? "";
                    var win = os.Contains("windows") ||
                              boot.Arr("guestOsFeatures").Any(g => g.Str("type") == "WINDOWS");
                    var labels = inst.Obj("labels");
                    var purpose = labels.Str("purpose");
                    if (purpose.Length == 0) purpose = labels.Str("servertype");
                    result.Add(new CloudInstance(
                        inst.Str("id"), inst.Str("name"), purpose, inst.Str("status"), Last(inst.Str("zone")),
                        nic.Str("networkIP"), pub, win, os));
                }
            }
            foreach (var u in r.Arr("unreachables")) errors.Add($"{u.GetString()}: unreachable");
            page = r.Str("nextPageToken");
            if (page.Length == 0) page = null;
        } while (page != null);
        return new CloudFetchResult(result, errors);
    }

    private static string Last(string? url) => (url ?? "").TrimEnd('/').Split('/').Last();
}
