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

    // ADC 자격 증명은 재사용한다 — 프로젝트(프로필)마다 ADC 파일을 다시 읽고 토큰을 새로 받지 않게 (전체 동기화 때 프로젝트 수만큼 토큰 요청이 나가던 것).
    // 토큰 만료·갱신은 GoogleCredential 이 스레드 안전하게 처리한다. ADC 파일 경로나 수정 시각이 바뀌면(로그인 버튼 · 경로 변경) 새로 만든다
    private static readonly object CredLock = new();
    private static (string key, GoogleCredential cred)? _cred;

    private static string CredKey()
    {
        var path = CloudPaths.Effective(CloudProvider.Gcp);
        long stamp = 0;
        try { stamp = File.GetLastWriteTimeUtc(path).Ticks; } catch { }
        return $"{path}|{stamp}";
    }

    private static async Task<(string token, string? quota)> TokenAsync(CancellationToken ct)
    {
        var key = CredKey();
        GoogleCredential? cred;
        lock (CredLock) cred = _cred is { } c && c.key == key ? c.cred : null;
        if (cred == null)
        {
            cred = (await GoogleCredential.GetApplicationDefaultAsync(ct))
                .CreateScoped("https://www.googleapis.com/auth/cloud-platform");
            lock (CredLock) _cred = (key, cred);
        }
        try
        {
            var token = await ((ITokenAccess)cred).GetAccessTokenForRequestAsync(null, ct);
            return (token, cred.QuotaProject);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 갱신이 거부된 자격 증명(재인증 필요 등)은 버려 다음 동기화가 ADC 를 다시 읽게 한다
            lock (CredLock) if (_cred is { } stale && ReferenceEquals(stale.cred, cred)) _cred = null;
            throw;
        }
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

    /// <summary>
    /// Compute Engine API 가 꺼져 있다는 오류인지. 실제 메시지는
    /// "Compute Engine API has not been used in project N before or it is disabled. Enable it by visiting ...compute.googleapis.com..." 형태.
    /// "disabled" 한 단어만 보면 결제 계정·프로젝트 비활성 등 다른 오류까지 이 안내로 바뀌므로 API 이름도 함께 본다
    /// </summary>
    internal static bool IsComputeApiDisabled(string message) =>
        message.Contains("has not been used", StringComparison.OrdinalIgnoreCase) ||
        (message.Contains("disabled", StringComparison.OrdinalIgnoreCase) &&
         (message.Contains("compute.googleapis.com", StringComparison.OrdinalIgnoreCase) ||
          message.Contains("Compute Engine API", StringComparison.OrdinalIgnoreCase)));

    public async Task<CloudFetchResult> FetchAsync(CloudProfile p, CancellationToken ct)
    {
        string token;
        string? quota;
        try { (token, quota) = await TokenAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException &&
                                   (ex is InvalidOperationException || ex is Google.Apis.Auth.OAuth2.Responses.TokenResponseException))
        {
            // ADC 파일이 없거나(InvalidOperationException) 토큰 갱신이 거부됨(재인증 필요 등)
            throw new InvalidOperationException(L.F("GCP 로그인이 필요합니다 ({0}) — {1}", LoginCommand(p), CloudProviders.FirstLine(ex.Message)));
        }
        var result = new List<CloudInstance>();
        var errors = new List<string>();
        string? page = null;
        do
        {
            var url = $"https://compute.googleapis.com/compute/v1/projects/{Uri.EscapeDataString(p.Name)}/aggregated/instances" +
                      "?returnPartialSuccess=true" + (page != null ? "&pageToken=" + Uri.EscapeDataString(page) : "");
            JsonElement r;
            try { r = await CloudProviders.GetJsonAsync(url, token, ct, quota); }
            catch (HttpRequestException ex) when (IsComputeApiDisabled(ex.Message))
            {
                throw new InvalidOperationException(L.T("이 프로젝트는 Compute Engine API 를 사용하지 않습니다."));
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

    // ---------------- GKE 클러스터 ----------------

    /// <summary>Kubernetes Engine(container) API 가 꺼져 있다는 오류인지 — 클러스터를 쓰지 않는 프로젝트라 빈 결과로 본다</summary>
    internal static bool IsContainerApiDisabled(string message) =>
        (message.Contains("has not been used", StringComparison.OrdinalIgnoreCase) || message.Contains("disabled", StringComparison.OrdinalIgnoreCase)) &&
        (message.Contains("container.googleapis.com", StringComparison.OrdinalIgnoreCase) || message.Contains("Kubernetes Engine API", StringComparison.OrdinalIgnoreCase));

    public async Task<ClusterFetchResult> FetchClustersAsync(CloudProfile p, CancellationToken ct)
    {
        string token;
        string? quota;
        try { (token, quota) = await TokenAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException &&
                                   (ex is InvalidOperationException || ex is Google.Apis.Auth.OAuth2.Responses.TokenResponseException))
        {
            throw new InvalidOperationException(L.F("GCP 로그인이 필요합니다 ({0}) — {1}", LoginCommand(p), CloudProviders.FirstLine(ex.Message)));
        }
        JsonElement r;
        try
        {
            r = await CloudProviders.GetJsonAsync(
                $"https://container.googleapis.com/v1/projects/{Uri.EscapeDataString(p.Name)}/locations/-/clusters", token, ct, quota);
        }
        catch (HttpRequestException ex) when (IsContainerApiDisabled(ex.Message))
        {
            return ClusterFetchResult.Empty;   // GKE 를 쓰지 않는 프로젝트
        }
        return ParseGkeClusters(r, p.Name);
    }

    /// <summary>
    /// gke-gcloud-auth-plugin 인자. 동기화는 ADC(GOOGLE_APPLICATION_CREDENTIALS 또는 gcloud 의 application_default_credentials.json)로
    /// 조회하므로, kubectl 도 같은 자격 증명을 쓰게 --use_application_default_credentials 를 준다 — 없으면 플러그인은
    /// gcloud 활성 계정(gcloud auth login)을 써서, 조회한 계정과 다른 계정으로 인증되거나 gcloud 로그인을 따로 요구한다.
    /// 앱의 사용자 지정 ADC 경로는 GOOGLE_APPLICATION_CREDENTIALS 로 kubectl 자식 프로세스에 그대로 상속된다 (CloudPaths.Apply)
    /// </summary>
    internal static readonly string[] GkeAuthPluginArgs = { "--use_application_default_credentials" };

    /// <summary>
    /// kubectl 이 붙을 API 서버 주소와 CA. IP 엔드포인트(공용 → 사설)를 먼저, IP 접근을 끈 클러스터는 DNS 엔드포인트
    /// (controlPlaneEndpointsConfig.dnsEndpointConfig, 외부 트래픽 허용일 때만). DNS 엔드포인트는 Google 공개 인증서를 쓰므로
    /// 클러스터 CA 를 넣지 않는다 (gcloud get-credentials --dns-endpoint 와 같음). 쓸 수 있는 주소가 없으면 ("", null)
    /// </summary>
    internal static (string host, string? ca) GkeEndpoint(JsonElement c)
    {
        var cfg = c.Obj("controlPlaneEndpointsConfig");
        var ip = cfg.Obj("ipEndpointsConfig");
        var ipDisabled = ip.ValueKind == JsonValueKind.Object && ip.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.False;
        if (!ipDisabled)
        {
            // 공용 엔드포인트가 꺼진 사설 클러스터는 사설 엔드포인트로 (같은 VPC·VPN 에서만 닿는다)
            var host = c.Str("endpoint");
            if (host.Length == 0) host = c.Obj("privateClusterConfig").Str("privateEndpoint");
            if (host.Length == 0) host = ip.Str("privateEndpoint");
            if (host.Length > 0) return (host, c.Obj("masterAuth").Str("clusterCaCertificate"));
        }
        var dns = cfg.Obj("dnsEndpointConfig");
        var external = dns.ValueKind == JsonValueKind.Object && dns.TryGetProperty("allowExternalTraffic", out var ax) && ax.ValueKind == JsonValueKind.True;
        if (external && dns.Str("endpoint") is { Length: > 0 } fqdn) return (fqdn, null);
        return ("", null);
    }

    /// <summary>
    /// projects.locations.clusters.list 응답 → 클러스터 (화면 무관, 테스트 대상). 인증은 gke-gcloud-auth-plugin (ADC — GkeAuthPluginArgs).
    /// 응답하지 못한 영역(missingZones)이 있으면 부분 결과로 본다 (지우지 않게).
    /// 목록에는 있지만 쓸 수 있는 엔드포인트가 없는 클러스터(생성 중 · DNS 엔드포인트 외부 차단 등)는 Errors 에 남기고 ProtectedIds 로 지우지 않는다
    /// </summary>
    internal static ClusterFetchResult ParseGkeClusters(JsonElement r, string project)
    {
        var items = new List<CloudCluster>();
        var errors = r.Arr("missingZones").Select(z => $"{z.GetString()}: unreachable").ToList();
        var protectedIds = new List<string>();
        foreach (var c in r.Arr("clusters"))
        {
            var name = c.Str("name");
            var location = c.Str("location");
            if (name.Length == 0) continue;
            var selfLink = c.Str("selfLink");
            var id = "gke:" + (selfLink.Length > 0 ? selfLink : $"projects/{project}/locations/{location}/clusters/{name}");
            var (host, ca) = GkeEndpoint(c);
            if (host.Length == 0)
            {
                protectedIds.Add(id);
                errors.Add($"{location} · {name}: no usable control plane endpoint (status {c.Str("status")})");
                continue;
            }
            var server = "https://" + host;
            var ctx = $"gke_{project}_{location}_{name}";
            var kubeconfig = Kubeconfig.BuildExec(ctx, server, ca, "gke-gcloud-auth-plugin", GkeAuthPluginArgs);
            items.Add(new CloudCluster(id, name, location, server, c.Str("currentMasterVersion"),
                CloudCluster.NormalizeState(c.Str("status")), K8sAuthKind.Gcp, kubeconfig, ctx));
        }
        return new ClusterFetchResult(items, errors) { ProtectedIds = protectedIds };
    }
}
