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

public enum CloudProvider { Aws, Tencent, Gcp, Azure }

/// <summary>
/// 클라우드 계정 단위(AWS/Tencent 프로필, GCP 프로젝트, Azure 구독). 동기화하면 이 프로필 그룹으로 호스트가 등록된다.
/// </summary>
public sealed class CloudProfile : INotifyPropertyChanged
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public CloudProvider Provider { get; set; }
    /// <summary>AWS/Tencent: 프로필 이름, GCP: 프로젝트 ID, Azure: 구독 ID</summary>
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    /// <summary>Azure 구독의 테넌트 ID</summary>
    public string? TenantId { get; set; }

    public string DefaultUsername { get; set; } = "";
    public string? ProtectedDefaultPassword { get; set; }
    public int SshPort { get; set; } = 22;
    public int RdpPort { get; set; } = 3389;
    public bool UsePublicIp { get; set; }
    public bool IncludeStopped { get; set; } = true;
    public DateTime? LastSync { get; set; }
    /// <summary>사용자가 목록에서 삭제한 인스턴스 (동기화해도 다시 추가하지 않음)</summary>
    public List<string> HiddenInstances { get; set; } = new();

    [JsonIgnore]
    public string DefaultPassword
    {
        get => Secret.Unprotect(ProtectedDefaultPassword);
        set => ProtectedDefaultPassword = string.IsNullOrEmpty(value) ? null : Secret.Protect(value);
    }

    [JsonIgnore] public string GroupName => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName;
    [JsonIgnore] public string ProviderText => CloudProviders.Label(Provider);
    [JsonIgnore] public Brush ProviderBrush => CloudProviders.Brush(Provider);

    // ----- 화면 표시용 (저장 안 함) -----
    private bool _isSyncing;
    private string _status = "";
    private int _hostCount, _runningCount;

    [JsonIgnore] public bool IsSyncing { get => _isSyncing; set { _isSyncing = value; OnChanged(); OnChanged(nameof(SyncButtonText)); } }
    [JsonIgnore] public string StatusText { get => _status; set { _status = value; OnChanged(); } }
    [JsonIgnore] public int HostCount { get => _hostCount; set { _hostCount = value; OnChanged(); OnChanged(nameof(SummaryText)); } }
    [JsonIgnore] public int RunningCount { get => _runningCount; set { _runningCount = value; OnChanged(); OnChanged(nameof(SummaryText)); } }
    [JsonIgnore] public string SyncButtonText => IsSyncing ? "동기화 중..." : "동기화";
    /// <summary>직전 동기화의 변경 요약 (예: "추가 3 · 제거 1")</summary>
    [JsonIgnore] public string LastResult { get; set; } = "";
    [JsonIgnore]
    public string SummaryText => LastSync == null
        ? "아직 동기화하지 않음"
        : $"{HostCount}대 (실행 {RunningCount})  ·  {LastSync:MM-dd HH:mm} 동기화" + (LastResult.Length > 0 ? $"  ·  {LastResult}" : "");

    public void NotifySummary() => OnChanged(nameof(SummaryText));

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>
/// 클라우드 인증 파일 위치. 기본은 각 CLI 의 표준 위치이고, 설정에서 바꾸면 해당 CLI 가 공식 지원하는 환경변수로
/// 이 프로세스(와 로그인 버튼으로 띄우는 CLI)에 적용한다.
/// </summary>
public static class CloudPaths
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    // 앱 시작 시점의 원래 환경변수 (기본값으로 되돌릴 때 복원)
    private static readonly Dictionary<string, string?> Original = new[]
    {
        "AWS_CONFIG_FILE", "AWS_SHARED_CREDENTIALS_FILE", "GOOGLE_APPLICATION_CREDENTIALS", "AZURE_CONFIG_DIR",
    }.ToDictionary(n => n, Environment.GetEnvironmentVariable);

    public static bool IsFolder(CloudProvider p) => p is CloudProvider.Tencent or CloudProvider.Azure;

    public static string Default(CloudProvider p) => p switch
    {
        CloudProvider.Aws => Original["AWS_CONFIG_FILE"] ?? Path.Combine(Home, ".aws", "config"),
        CloudProvider.Tencent => Path.Combine(Home, ".tccli"),
        CloudProvider.Gcp => Original["GOOGLE_APPLICATION_CREDENTIALS"]
                             ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "gcloud", "application_default_credentials.json"),
        _ => Original["AZURE_CONFIG_DIR"] ?? Path.Combine(Home, ".azure"),
    };

    public static string? Custom(CloudProvider p) =>
        SettingsStore.Current.CloudPaths.TryGetValue(p.ToString(), out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    public static string Effective(CloudProvider p) => Custom(p) ?? Default(p);

    /// <summary>설정한 경로를 환경변수로 적용 (앱 시작·경로 변경 시)</summary>
    public static void Apply()
    {
        var aws = Custom(CloudProvider.Aws);
        Environment.SetEnvironmentVariable("AWS_CONFIG_FILE", aws ?? Original["AWS_CONFIG_FILE"]);
        Environment.SetEnvironmentVariable("AWS_SHARED_CREDENTIALS_FILE",
            aws != null ? Path.Combine(Path.GetDirectoryName(aws) ?? "", "credentials") : Original["AWS_SHARED_CREDENTIALS_FILE"]);
        Environment.SetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS", Custom(CloudProvider.Gcp) ?? Original["GOOGLE_APPLICATION_CREDENTIALS"]);
        Environment.SetEnvironmentVariable("AZURE_CONFIG_DIR", Custom(CloudProvider.Azure) ?? Original["AZURE_CONFIG_DIR"]);
        AzureProvider.ClearTokenCache();
    }
}

/// <summary>프로필 추가 화면에 보여줄 후보 (자동 탐색 결과)</summary>
public sealed record CloudCandidate(string Name, string DisplayName, string? TenantId = null);

/// <summary>클라우드에서 조회한 인스턴스 (클라우드 공통 형식)</summary>
public sealed record CloudInstance(
    string InstanceId, string Name, string Purpose, string State, string Region,
    string PrivateIp, string PublicIp, bool IsWindows, string Os)
{
    public bool IsRunning => State.Equals("running", StringComparison.OrdinalIgnoreCase);

    /// <summary>"이름:용도" 형식 태그 분리 (기존 스크립트 규칙)</summary>
    public static (string name, string purpose) SplitName(string raw)
    {
        var i = raw.IndexOf(':');
        return i < 0 ? (raw, "") : (raw[..i], raw[(i + 1)..]);
    }
}

/// <summary>조회 결과. Errors 가 있으면 일부 리전/범위를 못 읽은 것이므로 삭제 판단에 쓰면 안 된다.</summary>
public sealed record CloudFetchResult(List<CloudInstance> Items, List<string> Errors)
{
    public bool IsPartial => Errors.Count > 0;
}

public interface ICloudProvider
{
    CloudProvider Kind { get; }
    /// <summary>로그인 안내 / 로그인 명령 (cmd 에서 실행)</summary>
    string AuthHint { get; }
    string LoginCommand(CloudProfile? p);
    Task<List<CloudCandidate>> DiscoverAsync(CancellationToken ct);
    Task<CloudFetchResult> FetchAsync(CloudProfile p, CancellationToken ct);
}

public static class CloudProviders
{
    public static IReadOnlyList<ICloudProvider> All { get; } = new ICloudProvider[]
    {
        new AwsProvider(), new TencentProvider(), new GcpProvider(), new AzureProvider(),
    };

    public static ICloudProvider Get(CloudProvider p) => All.First(x => x.Kind == p);

    public static string Label(CloudProvider p) => p switch
    {
        CloudProvider.Aws => "AWS",
        CloudProvider.Tencent => "Tencent",
        CloudProvider.Gcp => "GCP",
        _ => "Azure",
    };

    public static Brush Brush(CloudProvider p)
    {
        var b = new SolidColorBrush(p switch
        {
            CloudProvider.Aws => Color.FromRgb(0xFF, 0x99, 0x00),
            CloudProvider.Tencent => Color.FromRgb(0x00, 0x6E, 0xFF),
            CloudProvider.Gcp => Color.FromRgb(0x34, 0xA8, 0x53),
            _ => Color.FromRgb(0x00, 0x78, 0xD4),
        });
        b.Freeze();
        return b;
    }

    internal static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>여러 항목을 동시성 제한을 두고 병렬 처리하며, 실패는 모아서 반환</summary>
    internal static async Task<(List<T> items, List<string> errors)> ParallelCollect<TIn, T>(
        IEnumerable<TIn> inputs, int degree, Func<TIn, Task<IEnumerable<T>>> work, Func<TIn, string> label)
    {
        var bag = new ConcurrentBag<T>();
        var errors = new ConcurrentBag<string>();
        using var gate = new SemaphoreSlim(degree);
        await Task.WhenAll(inputs.Select(async x =>
        {
            await gate.WaitAsync();
            try { foreach (var r in await work(x)) bag.Add(r); }
            catch (Exception ex) { errors.Add($"{label(x)}: {FirstLine(ex.Message)}"); }
            finally { gate.Release(); }
        }));
        return (bag.ToList(), errors.ToList());
    }

    internal static string FirstLine(string s) => (s ?? "").Split('\n')[0].Trim();

    // JsonElement 편의 함수
    internal static string Str(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    internal static JsonElement Obj(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    internal static IEnumerable<JsonElement> Arr(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();

    internal static async Task<JsonElement> GetJsonAsync(string url, string bearer, CancellationToken ct, string? quotaProject = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (!string.IsNullOrEmpty(quotaProject)) req.Headers.Add("x-goog-user-project", quotaProject);
        using var resp = await Http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            string msg;
            try
            {
                var err = JsonDocument.Parse(body).RootElement.Obj("error");
                msg = err.ValueKind == JsonValueKind.Object ? err.Str("message") : body;
            }
            catch { msg = body; }
            throw new HttpRequestException($"{(int)resp.StatusCode} {FirstLine(msg)}");
        }
        return JsonDocument.Parse(body).RootElement.Clone();
    }
}

// =====================================================================
// AWS: ~/.aws/config 프로필 (SSO 포함). 먼저 aws sso login 필요
// =====================================================================
public sealed class AwsProvider : ICloudProvider
{
    public CloudProvider Kind => CloudProvider.Aws;
    public string AuthHint => "~/.aws/config 프로필 · SSO 는 aws sso login 필요";
    public string LoginCommand(CloudProfile? p) => $"aws sso login --profile {p?.Name ?? "default"}";

    public Task<List<CloudCandidate>> DiscoverAsync(CancellationToken ct)
    {
        var path = CloudPaths.Effective(CloudProvider.Aws);
        var list = new List<CloudCandidate>();
        if (File.Exists(path))
        {
            foreach (var line in File.ReadAllLines(path))
            {
                var t = line.Trim();
                if (!t.StartsWith("[profile ") || !t.EndsWith("]")) continue;
                var name = t[9..^1].Trim();
                list.Add(new CloudCandidate(name, name.ToUpperInvariant()));
            }
        }
        return Task.FromResult(list);
    }

    public async Task<CloudFetchResult> FetchAsync(CloudProfile p, CancellationToken ct)
    {
        var chain = new CredentialProfileStoreChain();
        if (!chain.TryGetAWSCredentials(p.Name, out var creds))
            throw new InvalidOperationException($"AWS 프로필 '{p.Name}' 을 찾을 수 없습니다.");

        List<string> regions;
        try
        {
            using var ec2 = new AmazonEC2Client(creds, RegionEndpoint.USEast1);
            var resp = await ec2.DescribeRegionsAsync(new DescribeRegionsRequest(), ct);
            regions = (resp.Regions ?? new()).Select(r => r.RegionName).ToList();
        }
        catch (Exception ex) when (ex.Message.Contains("SSO", StringComparison.OrdinalIgnoreCase) ||
                                   ex.Message.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                                   ex.Message.Contains("expired", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"SSO 로그인이 필요합니다 (aws sso login --profile {p.Name}) — {CloudProviders.FirstLine(ex.Message)}");
        }

        var (items, errors) = await CloudProviders.ParallelCollect(regions, 10, async region =>
        {
            using var client = new AmazonEC2Client(creds, RegionEndpoint.GetBySystemName(region));
            var result = new List<CloudInstance>();
            string? token = null;
            do
            {
                var page = await client.DescribeInstancesAsync(new DescribeInstancesRequest { NextToken = token }, ct);
                foreach (var res in page.Reservations ?? new())
                foreach (var inst in res.Instances ?? new())
                {
                    var state = inst.State?.Name?.Value ?? "";
                    if (state == "terminated") continue;
                    var tags = (inst.Tags ?? new()).GroupBy(t => t.Key).ToDictionary(g => g.Key, g => g.First().Value);
                    var (name, purpose) = CloudInstance.SplitName(tags.GetValueOrDefault("Name") ?? "");
                    if (purpose.Length == 0) purpose = tags.GetValueOrDefault("ServerType") ?? tags.GetValueOrDefault("Purpose") ?? "";
                    var os = inst.PlatformDetails ?? "";
                    var win = inst.Platform?.Value == "windows" || os.Contains("Windows", StringComparison.OrdinalIgnoreCase);
                    result.Add(new CloudInstance(inst.InstanceId, name.Length > 0 ? name : inst.InstanceId, purpose, state, region,
                        inst.PrivateIpAddress ?? "", inst.PublicIpAddress ?? "", win, os));
                }
                token = page.NextToken;
            } while (!string.IsNullOrEmpty(token));
            return (IEnumerable<CloudInstance>)result;
        }, r => r);

        if (items.Count == 0 && errors.Count > 0) throw new InvalidOperationException(errors[0]);
        return new CloudFetchResult(items, errors);
    }
}

// =====================================================================
// Tencent: ~/.tccli/<프로필>.credential (secretId/secretKey), TC3-HMAC-SHA256 서명으로 직접 호출
// =====================================================================
public sealed class TencentProvider : ICloudProvider
{
    private const string Host = "cvm.tencentcloudapi.com";
    private const string Version = "2017-03-12";

    public CloudProvider Kind => CloudProvider.Tencent;
    public string AuthHint => "~/.tccli/<프로필>.credential (tccli configure)";
    public string LoginCommand(CloudProfile? p) => $"tccli configure --profile {p?.Name ?? "default"}";

    private static string TccliDir => CloudPaths.Effective(CloudProvider.Tencent);

    public Task<List<CloudCandidate>> DiscoverAsync(CancellationToken ct)
    {
        var list = Directory.Exists(TccliDir)
            ? Directory.GetFiles(TccliDir, "*.credential")
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .Where(n => !n.Equals("default", StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n)
                .Select(n => new CloudCandidate(n, n.ToUpperInvariant()))
                .ToList()
            : new List<CloudCandidate>();
        return Task.FromResult(list);
    }

    public async Task<CloudFetchResult> FetchAsync(CloudProfile p, CancellationToken ct)
    {
        var file = Path.Combine(TccliDir, p.Name + ".credential");
        if (!File.Exists(file)) throw new InvalidOperationException($"{file} 이 없습니다 (tccli configure --profile {p.Name}).");
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

// =====================================================================
// 저장 + 동기화 병합
// =====================================================================
public static class CloudStore
{
    private static string FilePath => Path.Combine(AppPaths.DataDir, "cloud_profiles.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static List<CloudProfile> Load() =>
        SafeFile.Load(FilePath, s => JsonSerializer.Deserialize<List<CloudProfile>>(s, Options), () => new List<CloudProfile>());

    public static void Save(IEnumerable<CloudProfile> profiles) =>
        SafeFile.WriteAtomic(FilePath, JsonSerializer.Serialize(profiles, Options));
}

public static class CloudSync
{
    /// <summary>
    /// 조회 결과를 호스트 목록에 반영한다. 이름·주소·상태 등 클라우드 정보는 갱신하고,
    /// 사용자가 바꾼 프로토콜·포트·계정은 유지한다. 클라우드에서 사라진 인스턴스는 제거한다.
    /// </summary>
    public static (int added, int updated, int removed) Apply(CloudProfile p, List<CloudInstance> instances, List<HostEntry> hosts,
        bool removeMissing = true)
    {
        var existing = hosts.Where(h => h.CloudProfileId == p.Id && h.CloudInstanceId != null)
            .GroupBy(h => h.CloudInstanceId!).ToDictionary(g => g.Key, g => g.First());
        var seen = new HashSet<string>();
        int added = 0, updated = 0;

        foreach (var inst in instances)
        {
            if (!p.IncludeStopped && !inst.IsRunning) continue;
            if (p.HiddenInstances.Contains(inst.InstanceId)) continue;
            // 기본 접속 주소는 사설 IP (없으면 공인). 공인 IP 는 따로 저장해 호스트별로 골라 쓴다
            var address = inst.PrivateIp.Length > 0 ? inst.PrivateIp : inst.PublicIp;
            if (address.Length == 0 || inst.InstanceId.Length == 0) continue;
            seen.Add(inst.InstanceId);

            if (!existing.TryGetValue(inst.InstanceId, out var h))
            {
                h = new HostEntry
                {
                    CloudProfileId = p.Id,
                    CloudInstanceId = inst.InstanceId,
                    Protocol = inst.IsWindows ? Protocol.Rdp : Protocol.Ssh,
                    Port = inst.IsWindows ? p.RdpPort : p.SshPort,
                };
                h.UsePublicIp = p.UsePublicIp && inst.PublicIp.Length > 0;   // 새 호스트는 프로필 기본값을 따름
                hosts.Add(h);
                added++;
            }
            else
            {
                // 사설/공인 IP 를 처음 저장하는 경우(이전 버전에서 동기화된 호스트)엔 프로필 기본값으로
                if (h.PrivateIp == null && h.PublicIp == null) h.UsePublicIp = p.UsePublicIp && inst.PublicIp.Length > 0;
                updated++;
            }

            h.Label = inst.Name;
            h.Address = address;
            h.PrivateIp = inst.PrivateIp.Length > 0 ? inst.PrivateIp : null;
            h.PublicIp = inst.PublicIp.Length > 0 ? inst.PublicIp : null;   // EIP 를 떼면 사라짐
            if (h.PublicIp == null) h.UsePublicIp = false;
            h.Purpose = inst.Purpose;
            h.CloudState = inst.State;
            h.CloudRegion = inst.Region;
            h.Os = inst.Os;
        }

        // 일부 리전/영역 조회가 실패한 경우(removeMissing=false)엔 "안 보인 서버" 를 지우지 않는다 — 일시 장애로 호스트·저장 계정이 사라지는 것 방지
        var removed = removeMissing
            ? hosts.RemoveAll(h => h.CloudProfileId == p.Id && (h.CloudInstanceId == null || !seen.Contains(h.CloudInstanceId)))
            : 0;
        p.LastSync = DateTime.Now;
        return (added, updated, removed);
    }
}
