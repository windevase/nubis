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

// 설정·프로필 JSON 에는 이름(문자열)으로 저장되지만, 순서 정렬(그룹 표시 순서)에 쓰이므로 새 클라우드는 끝에 추가한다
public enum CloudProvider { Aws, Tencent, Gcp, Azure, Oci }

/// <summary>
/// 클라우드 계정 단위(AWS/Tencent/OCI 프로필, GCP 프로젝트, Azure 구독). 동기화하면 이 프로필 그룹으로 호스트가 등록된다.
/// </summary>
public sealed class CloudProfile : INotifyPropertyChanged
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public CloudProvider Provider { get; set; }
    /// <summary>AWS/Tencent/OCI: 프로필 이름, GCP: 프로젝트 ID, Azure: 구독 ID</summary>
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
    /// <summary>
    /// 이 프로필의 쿠버네티스 클러스터(EKS·GKE·AKS·OKE·TKE)도 함께 동기화할지. Kubernetes 페이지의 "클라우드에서 찾기" 로
    /// 클러스터를 찾으면 켜진다 — 클러스터를 쓰지 않는 프로필은 동기화 때 추가 API 를 부르지 않게
    /// </summary>
    public bool SyncClusters { get; set; }
    public DateTime? LastSync { get; set; }
    /// <summary>사용자가 목록에서 삭제한 인스턴스 (동기화해도 다시 추가하지 않음)</summary>
    public List<string> HiddenInstances { get; set; } = new();

    /// <summary>
    /// 빠짐없는 동기화에서 연속으로 보이지 않은 횟수 (인스턴스 ID → 횟수). 클라우드 목록 API 가 잠깐 항목을 빠뜨려도
    /// 호스트(계정·포트 설정 포함)가 바로 지워지지 않게, 두 번 연속 안 보였을 때만 지운다 (CloudSync.ApplyResult).
    /// 다시 보이면 지운다. 숨긴 인스턴스 목록 정리에도 같은 기준을 쓴다
    /// </summary>
    public Dictionary<string, int> MissCounts { get => _missCounts; set => _missCounts = value ?? new(); }
    private Dictionary<string, int> _missCounts = new();

    /// <summary>직전 동기화에서 안 보였지만 아직 지우지 않은 호스트 수 (다음 빠짐없는 동기화에서도 안 보이면 제거). 화면 표시용</summary>
    [JsonIgnore] public int PendingRemoval { get; set; }

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
    // 동기화 중에 누르면 취소된다 (MainWindow.SyncOrCancelAsync) — 버튼 글자도 그 동작을 보여 준다
    [JsonIgnore] public string SyncButtonText => IsSyncing ? L.T("취소") : L.T("동기화");
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
        "AWS_CONFIG_FILE", "AWS_SHARED_CREDENTIALS_FILE", "GOOGLE_APPLICATION_CREDENTIALS", "AZURE_CONFIG_DIR", "OCI_CLI_CONFIG_FILE",
    }.ToDictionary(n => n, Environment.GetEnvironmentVariable);

    public static bool IsFolder(CloudProvider p) => p is CloudProvider.Tencent or CloudProvider.Azure;

    public static string Default(CloudProvider p) => p switch
    {
        CloudProvider.Aws => Original["AWS_CONFIG_FILE"] ?? Path.Combine(Home, ".aws", "config"),
        CloudProvider.Tencent => Path.Combine(Home, ".tccli"),
        CloudProvider.Gcp => Original["GOOGLE_APPLICATION_CREDENTIALS"]
                             ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "gcloud", "application_default_credentials.json"),
        CloudProvider.Oci => Original["OCI_CLI_CONFIG_FILE"] ?? Path.Combine(Home, ".oci", "config"),
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
        // OCI: oci CLI 와 OciConfig 모두 OCI_CLI_CONFIG_FILE 을 읽는다
        Environment.SetEnvironmentVariable("OCI_CLI_CONFIG_FILE", Custom(CloudProvider.Oci) ?? Original["OCI_CLI_CONFIG_FILE"]);
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

    /// <summary>
    /// 권한(SCP·IAM 정책)으로 조회가 거부된 리전 (CloudInstance.Region 과 같은 이름). 사유는 Errors 에도 한 줄씩 들어 있다.
    /// 이 리전의 호스트는 지우지 않고, 나머지 리전은 정상 조회됐으면 삭제 판단에 쓸 수 있다 (CloudSync.ApplyResult).
    /// </summary>
    public List<string> DeniedRegions { get; init; } = new();

    /// <summary>권한 거부 리전을 뺀 실제 조회 실패가 있는지 (있으면 어느 호스트도 지우면 안 된다)</summary>
    // 거부 리전은 리전마다 Errors 에 한 줄씩 들어가므로 개수로 비교한다. 같은 리전이 두 번 들어가도 실제 오류를 가리지 않게 중복은 한 번만 센다
    public bool HasUnexpectedErrors =>
        Errors.Count > DeniedRegions.Distinct(StringComparer.OrdinalIgnoreCase).Count();
}

/// <summary>
/// 클라우드에서 조회한 쿠버네티스 클러스터. Id 는 제공자 접두어를 붙인다 (eks:arn · gke:selfLink · aks:resourceId · oke:ocid · tke:id).
/// Kubeconfig 는 그 클러스터 하나만 담은 kubeconfig — 보통 exec 플러그인(aws · gke-gcloud-auth-plugin · kubelogin · oci)이라 비밀값이 없고,
/// TKE 처럼 토큰·인증서가 든 경우도 있어 언제나 암호화해 저장한다 (K8sCluster.ProtectedKubeconfig)
/// </summary>
public sealed record CloudCluster(string Id, string Name, string Region, string Endpoint, string Version, string State,
    K8sAuthKind AuthKind, string Kubeconfig, string ContextName)
{
    public bool IsRunning => State.Equals("running", StringComparison.OrdinalIgnoreCase);

    /// <summary>제공자마다 다른 정상 상태 이름을 "running" 으로 맞춘다 (EKS·OKE ACTIVE, GKE RUNNING, AKS Succeeded, TKE Running)</summary>
    public static string NormalizeState(string? state) => (state ?? "").Trim().ToLowerInvariant() switch
    {
        "active" or "running" or "succeeded" => "running",
        "" => "unknown",
        var s => s,
    };
}

/// <summary>클러스터 조회 결과 (CloudFetchResult 와 같은 규칙: Errors 가 있으면 삭제 판단에 쓰지 않는다)</summary>
public sealed record ClusterFetchResult(List<CloudCluster> Items, List<string> Errors)
{
    public bool IsPartial => Errors.Count > 0;
    public List<string> DeniedRegions { get; init; } = new();
    public bool HasUnexpectedErrors => Errors.Count > DeniedRegions.Distinct(StringComparer.OrdinalIgnoreCase).Count();
    public static ClusterFetchResult Empty => new(new List<CloudCluster>(), new List<string>());
}

public interface ICloudProvider
{
    CloudProvider Kind { get; }
    /// <summary>로그인 안내 / 로그인 명령 (cmd 에서 실행)</summary>
    string AuthHint { get; }
    string LoginCommand(CloudProfile? p);
    Task<List<CloudCandidate>> DiscoverAsync(CancellationToken ct);
    Task<CloudFetchResult> FetchAsync(CloudProfile p, CancellationToken ct);
    /// <summary>쿠버네티스 클러스터 (조회 API 만). 지원하지 않는 제공자는 빈 결과</summary>
    Task<ClusterFetchResult> FetchClustersAsync(CloudProfile p, CancellationToken ct) => Task.FromResult(ClusterFetchResult.Empty);
}

public static class CloudProviders
{
    public static IReadOnlyList<ICloudProvider> All { get; } = new ICloudProvider[]
    {
        new AwsProvider(), new TencentProvider(), new GcpProvider(), new AzureProvider(), new OciProvider(),
    };

    public static ICloudProvider Get(CloudProvider p) => All.First(x => x.Kind == p);

    public static string Label(CloudProvider p) => p switch
    {
        CloudProvider.Aws => "AWS",
        CloudProvider.Tencent => "Tencent",
        CloudProvider.Gcp => "GCP",
        CloudProvider.Oci => "OCI",
        _ => "Azure",
    };

    public static Brush Brush(CloudProvider p)
    {
        var b = new SolidColorBrush(p switch
        {
            CloudProvider.Aws => Color.FromRgb(0xFF, 0x99, 0x00),
            CloudProvider.Tencent => Color.FromRgb(0x00, 0x6E, 0xFF),
            CloudProvider.Gcp => Color.FromRgb(0x34, 0xA8, 0x53),
            CloudProvider.Oci => Color.FromRgb(0xC7, 0x46, 0x34),
            _ => Color.FromRgb(0x00, 0x78, 0xD4),
        });
        b.Freeze();
        return b;
    }

    // 앱 수명 동안 하나를 재사용한다. 오래 켜 두는 앱이라 연결을 주기적으로 새로 맺어 DNS 변경(엔드포인트 교체)을 따라가고,
    // 큰 목록 응답(GCP aggregated · Azure · OCI)은 gzip 으로 받아 전송량을 줄인다 (서명 대상 헤더가 아니라 서명과 무관)
    internal static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
    }) { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>
    /// 여러 항목을 동시성 제한을 두고 병렬 처리하며, 실패는 모아서 반환 (오류는 표시가 매번 같도록 정렬).
    /// ct 가 취소되면 취소 예외를 그대로 던진다 — 취소를 "일부 조회 실패" 로 바꾸지 않는다.
    /// </summary>
    internal static async Task<(List<T> items, List<string> errors)> ParallelCollect<TIn, T>(
        IEnumerable<TIn> inputs, int degree, Func<TIn, Task<IEnumerable<T>>> work, Func<TIn, string> label,
        CancellationToken ct = default)
    {
        var bag = new ConcurrentBag<T>();
        var errors = new ConcurrentBag<string>();
        using var gate = new SemaphoreSlim(degree);
        await Task.WhenAll(inputs.Select(async x =>
        {
            await gate.WaitAsync(ct);
            try { foreach (var r in await work(x)) bag.Add(r); }
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                errors.Add($"{label(x)}: {FirstLine(ex.Message)}");
            }
            finally { gate.Release(); }
        }));
        ct.ThrowIfCancellationRequested();
        return (bag.ToList(), errors.OrderBy(e => e, StringComparer.Ordinal).ToList());
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

    internal static Task<JsonElement> GetJsonAsync(string url, string bearer, CancellationToken ct, string? quotaProject = null) =>
        SendJsonAsync(HttpMethod.Get, url, bearer, null, ct, quotaProject);

    /// <summary>
    /// Bearer 토큰 JSON 요청. POST 는 조회성 동작에만 쓴다 (AKS listClusterUserCredential 처럼 자격 증명을 "읽는" API) — 리소스를 바꾸는 호출 금지
    /// </summary>
    internal static async Task<JsonElement> SendJsonAsync(HttpMethod method, string url, string bearer, string? jsonBody, CancellationToken ct, string? quotaProject = null)
    {
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (!string.IsNullOrEmpty(quotaProject)) req.Headers.Add("x-goog-user-project", quotaProject);
        if (jsonBody != null) req.Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");
        using var resp = await Http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            string msg;
            try
            {
                // JsonDocument 는 풀(ArrayPool)에서 빌린 버퍼를 쓰므로 Dispose 로 돌려준다 (매 조회마다 버퍼가 GC 로만 회수되지 않게)
                using var errDoc = JsonDocument.Parse(body);
                var err = errDoc.RootElement.Obj("error");
                msg = err.ValueKind == JsonValueKind.Object ? err.Str("message") : body;
            }
            catch { msg = body; }
            throw new HttpRequestException($"{(int)resp.StatusCode} {FirstLine(msg)}");
        }
        // Clone 한 값은 문서와 무관하게 남으므로 문서는 바로 돌려준다
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }
}
