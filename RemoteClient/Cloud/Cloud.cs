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
    /// <summary>이 프로필 서버들의 기본 Vault 계정 (Linux/SSH · Windows/RDP 따로). 호스트에 직접 고른 계정이 우선</summary>
    public Guid? SshVaultId { get; set; }
    public Guid? RdpVaultId { get; set; }
    /// <summary>이 프로필 서버들의 포트 서버들의 포트. null 이면 설정의 기본 포트(Settings → 기본 포트)를 따른다</summary>
    public int? SshPort { get; set; }
    public int? RdpPort { get; set; }
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

    [JsonIgnore] public int EffectiveSshPort => SshPort ?? HostEntry.PreferredPort(Protocol.Ssh);
    [JsonIgnore] public int EffectiveRdpPort => RdpPort ?? HostEntry.PreferredPort(Protocol.Rdp);

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
    [JsonIgnore] public string SyncButtonText => IsSyncing ? L.T("동기화 중...") : L.T("동기화");
    /// <summary>직전 동기화의 변경 요약 (예: "추가 3 · 제거 1")</summary>
    [JsonIgnore] public string LastResult { get; set; } = "";
    [JsonIgnore]
    public string SummaryText => LastSync == null
        ? L.T("아직 동기화하지 않음")
        : L.F("{0}대 (실행 {1})  ·  {2:MM-dd HH:mm} 동기화", HostCount, RunningCount, LastSync) + (LastResult.Length > 0 ? $"  ·  {LastResult}" : "");

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
