using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Nubis;

// =====================================================================
// OCI (Oracle Cloud): ~/.oci/config 프로필 (API 키 또는 oci session authenticate 세션 토큰),
// OciSigner 로 서명한 REST 호출. 리전(구독) × 컴파트먼트 단위로 인스턴스를 조회한다.
// =====================================================================
public sealed class OciProvider : ICloudProvider
{
    private const string ApiVersion = "20160918";
    /// <summary>세션 토큰이 이 시간 안에 만료되면 미리 갱신한다</summary>
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(60);
    /// <summary>페이지 무한 반복 방지 (페이지 토큰이 계속 바뀌며 끝나지 않는 경우)</summary>
    private const int MaxPages = 1000;

    private static readonly Regex ProfileNamePattern = new("^[A-Za-z0-9_.@-]+$", RegexOptions.CultureInvariant);
    private static readonly Regex RegionPattern = new("^[a-z0-9-]+$", RegexOptions.CultureInvariant);

    public CloudProvider Kind => CloudProvider.Oci;
    public string AuthHint => L.T("~/.oci/config 프로필 · 브라우저 로그인은 oci session authenticate");

    public string LoginCommand(CloudProfile? p)
    {
        var name = SafeProfileName(p?.Name);
        string? region = null;
        try { region = OciConfig.Get(name)?.Region; } catch { }
        return BuildLoginCommand(name, region);
    }

    public Task<List<CloudCandidate>> DiscoverAsync(CancellationToken ct)
    {
        // config 파일이 없으면 후보 없음 (AWS 와 같이 오류로 보지 않는다)
        if (!File.Exists(OciConfig.ConfigPath)) return Task.FromResult(new List<CloudCandidate>());
        var list = OciConfig.ReadProfiles()
            .Select(x => x.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.Ordinal)
            .Select(n => new CloudCandidate(n, n.ToUpperInvariant()))
            .ToList();
        return Task.FromResult(list);
    }

    public async Task<CloudFetchResult> FetchAsync(CloudProfile p, CancellationToken ct)
    {
        var prof = OciConfig.Get(p.Name)
            ?? throw new InvalidOperationException(L.F("OCI 프로필 '{0}' 을 찾을 수 없습니다 ({1}).", p.Name, OciConfig.ConfigPath));
        if (!IsValidRegion(prof.Region))
            throw new InvalidOperationException(L.F("OCI 프로필 '{0}' 의 region 설정이 올바르지 않습니다.", p.Name));
        if (string.IsNullOrWhiteSpace(prof.Tenancy))
            throw new InvalidOperationException(L.F("OCI 프로필 '{0}' 에 tenancy 설정이 없습니다.", p.Name));

        using var signer = await CreateSignerAsync(prof, ct);
        using var gate = new SemaphoreSlim(16);
        var ctx = new Ctx(signer, gate, RealmDomain(prof.Tenancy));
        var identity = $"https://identity.{prof.Region}.{ctx.Domain}/{ApiVersion}";
        var tenancy = Uri.EscapeDataString(prof.Tenancy);

        // 1. 구독 리전 (첫 호출 — 인증 실패는 여기서 로그인 안내로 바꾼다)
        List<string> regions;
        try
        {
            var (root, _) = await GetAsync(ctx, $"{identity}/tenancies/{tenancy}/regionSubscriptions", ct);
            regions = ParseRegions(root);
        }
        catch (OciApiException ex) when (ex.IsAuth)
        {
            throw LoginNeeded(prof, ex.Message);
        }
        var errors = new List<string>();
        foreach (var bad in regions.Where(r => !IsValidRegion(r))) errors.Add($"{bad}: invalid region name");
        regions = regions.Where(IsValidRegion).Distinct().ToList();
        // 리전이 0개면 "조회 성공 + 서버 없음" 으로 보여 이 프로필 호스트가 전부 지워지므로 실패로 처리
        if (regions.Count == 0)
            throw new InvalidOperationException(L.T("사용 가능한 리전 목록이 비어 있습니다 (regionSubscriptions) — 이번 동기화는 건너뜁니다."));

        // 2. 컴파트먼트 (루트 = 테넌시 포함). 하위 목록을 못 읽으면 루트만 조회하고 부분 결과로 둔다 (삭제 안 함)
        var compartments = new List<(string id, string name)> { (prof.Tenancy, "root") };
        try
        {
            var all = await ListAllAsync(ctx,
                $"{identity}/compartments?compartmentId={tenancy}&compartmentIdInSubtree=true&accessLevel=ACCESSIBLE&lifecycleState=ACTIVE", ct);
            compartments.AddRange(ParseCompartments(all).Where(c => c.id != prof.Tenancy));
        }
        catch (OciApiException ex) when (ex.IsAuth)
        {
            throw LoginNeeded(prof, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            errors.Add($"compartments: {CloudProviders.FirstLine(ex.Message)}");
        }

        // 3. 리전 × 컴파트먼트 인스턴스
        var scopes = regions.SelectMany(r => compartments.Select(c => new Scope(r, c.id, c.name))).ToList();
        var (items, scopeErrors) = await CloudProviders.ParallelCollect(scopes, 8,
            s => FetchScopeAsync(ctx, s, ct), s => $"{s.Region} · {s.CompartmentName}", ct);
        ct.ThrowIfCancellationRequested();
        errors.AddRange(scopeErrors);

        if (items.Count == 0 && errors.Count > 0) throw new InvalidOperationException(errors[0]);
        return new CloudFetchResult(items, errors);
    }

    // ----- 조회 -----

    private sealed record Scope(string Region, string CompartmentId, string CompartmentName);

    /// <summary>한 번의 동기화에서 공유하는 상태 (서명기 · 동시 요청 제한 · 이미지 OS 캐시)</summary>
    private sealed class Ctx
    {
        public Ctx(OciSigner signer, SemaphoreSlim gate, string domain) { Signer = signer; Gate = gate; Domain = domain; }
        public OciSigner Signer { get; }
        public object SignLock { get; } = new();
        public SemaphoreSlim Gate { get; }
        public string Domain { get; }
        public ConcurrentDictionary<string, Lazy<Task<string>>> Images { get; } = new();
    }

    private static async Task<IEnumerable<CloudInstance>> FetchScopeAsync(Ctx ctx, Scope s, CancellationToken ct)
    {
        var iaas = $"https://iaas.{s.Region}.{ctx.Domain}/{ApiVersion}";
        var comp = Uri.EscapeDataString(s.CompartmentId);
        var instances = (await ListAllAsync(ctx, $"{iaas}/instances?compartmentId={comp}", ct))
            .Where(i => !IsGone(i.Str("lifecycleState"))).ToList();
        if (instances.Count == 0) return Array.Empty<CloudInstance>();

        // VNIC 연결은 인스턴스마다가 아니라 컴파트먼트 단위로 한 번에 받는다
        var attachments = VnicIdsByInstance(await ListAllAsync(ctx, $"{iaas}/vnicAttachments?compartmentId={comp}", ct));

        var tasks = instances.Select(async inst =>
        {
            var (priv, pub) = await PrimaryIpsAsync(ctx, iaas, attachments.GetValueOrDefault(inst.Str("id")), ct);
            var imageId = ImageIdOf(inst);
            // 부트 볼륨에서 만든 인스턴스는 이미지 ID 가 없다 — 부트 볼륨의 원본 이미지로 OS 를 찾는다 (Windows 를 SSH 로 등록하지 않게)
            if (imageId.Length == 0 && BootVolumeIdOf(inst) is { Length: > 0 } bv)
                imageId = await BootVolumeImageIdAsync(ctx, iaas, bv, ct);
            var os = await ImageOsAsync(ctx, s.Region, iaas, imageId, ct);
            return MapInstance(inst, s.Region, priv, pub, os);
        });
        return await Task.WhenAll(tasks);
    }

    /// <summary>부트 볼륨의 원본 이미지 ID. 없거나 볼 권한이 없으면 ""</summary>
    private static async Task<string> BootVolumeImageIdAsync(Ctx ctx, string iaas, string bootVolumeId, CancellationToken ct)
    {
        try
        {
            var (vol, _) = await GetAsync(ctx, $"{iaas}/bootVolumes/{Uri.EscapeDataString(bootVolumeId)}", ct);
            return vol.Str("imageId");
        }
        catch (OciApiException ex) when (ex.Status is 404 or 403 or 401) { return ""; }
    }

    /// <summary>주 VNIC 의 사설/공인 IP. 주 VNIC 를 못 찾으면 처음 읽은 VNIC, 없으면 빈 값 (동기화가 기존 호스트를 유지)</summary>
    private static async Task<(string priv, string pub)> PrimaryIpsAsync(Ctx ctx, string iaas, List<string>? vnicIds, CancellationToken ct)
    {
        (string, string)? fallback = null;
        foreach (var id in vnicIds ?? new List<string>())
        {
            JsonElement vnic;
            try { (vnic, _) = await GetAsync(ctx, $"{iaas}/vnics/{Uri.EscapeDataString(id)}", ct); }
            catch (OciApiException ex) when (ex.Status == 404) { continue; }   // 막 분리된 VNIC
            var (primary, priv, pub) = ParseVnic(vnic);
            if (primary) return (priv, pub);
            fallback ??= (priv, pub);
        }
        return fallback ?? ("", "");
    }

    /// <summary>이미지의 OS 이름 (이미지 ID 별로 한 번만 조회). 삭제됐거나 볼 권한이 없는 이미지는 알 수 없음("")</summary>
    private static Task<string> ImageOsAsync(Ctx ctx, string region, string iaas, string imageId, CancellationToken ct)
    {
        if (imageId.Length == 0) return Task.FromResult("");
        return ctx.Images.GetOrAdd($"{region}|{imageId}", _ => new Lazy<Task<string>>(async () =>
        {
            try
            {
                var (img, _) = await GetAsync(ctx, $"{iaas}/images/{Uri.EscapeDataString(imageId)}", ct);
                return OsText(img);
            }
            catch (OciApiException ex) when (ex.Status is 404 or 403 or 401) { return ""; }
        })).Value;
    }

    /// <summary>opc-next-page 를 따라 모든 페이지를 받는다. 페이지 사이 목록이 바뀌어 id 가 겹치면 실패 (부분 결과 → 삭제 안 함)</summary>
    private static async Task<List<JsonElement>> ListAllAsync(Ctx ctx, string url, CancellationToken ct)
    {
        var all = new List<JsonElement>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        string? page = null;
        for (var n = 0; ; n++)
        {
            if (n >= MaxPages) throw new InvalidOperationException(L.T("페이지가 너무 많아 조회를 중단했습니다 — 다시 동기화하세요."));
            var (root, next) = await GetAsync(ctx, WithPage(url, page), ct);
            if (root.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException(L.T("OCI 응답 형식이 올바르지 않습니다 (목록이 아님)."));
            foreach (var item in root.EnumerateArray())
            {
                var id = item.Str("id");
                if (id.Length > 0 && !ids.Add(id))
                    throw new InvalidOperationException(L.T("조회 중 목록이 바뀌었습니다 (페이지 사이 중복) — 다시 동기화하세요."));
                all.Add(item);
            }
            if (next == null) return all;
            if (!tokens.Add(next))
                throw new InvalidOperationException(L.T("조회 중 목록이 바뀌었습니다 (페이지 사이 중복) — 다시 동기화하세요."));
            page = next;
        }
    }

    /// <summary>서명한 GET. 429/503 은 두 번까지 다시 시도. 실패는 OciApiException ("code: message")</summary>
    private static async Task<(JsonElement root, string? nextPage)> GetAsync(Ctx ctx, string url, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            TimeSpan delay;
            await ctx.Gate.WaitAsync(ct);
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                lock (ctx.SignLock) ctx.Signer.Sign(req, null);
                using var resp = await CloudProviders.Http.SendAsync(req, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (resp.IsSuccessStatusCode)
                {
                    if (string.IsNullOrWhiteSpace(body)) return (default, NextPage(resp));
                    using var doc = JsonDocument.Parse(body);
                    return (doc.RootElement.Clone(), NextPage(resp));
                }
                var status = (int)resp.StatusCode;
                if (status is not (429 or 503) || attempt >= 3) throw OciApiException.From(status, body);
                delay = resp.Headers.RetryAfter?.Delta is { } d && d > TimeSpan.Zero && d < TimeSpan.FromSeconds(30)
                    ? d : TimeSpan.FromSeconds(attempt * 2);
            }
            finally { ctx.Gate.Release(); }
            await Task.Delay(delay, ct);
        }
    }

    // ----- 인증 (세션 토큰 갱신) -----

    /// <summary>
    /// 서명기 생성. 세션 프로필이고 토큰이 만료됐거나 곧 만료되면 oci session refresh 를 한 번 시도한 뒤 다시 만든다
    /// (세션은 로그인 후 24시간 안에서만 갱신된다). 그래도 만료면 로그인 필요 오류.
    /// </summary>
    private static async Task<OciSigner> CreateSignerAsync(OciProfile prof, CancellationToken ct)
    {
        if (!prof.IsSession) return OciSigner.Create(prof);

        OciSigner? signer = null;
        try { signer = OciSigner.Create(prof); }
        catch (InvalidOperationException) { }   // 만료 등 — 갱신 후 다시 시도
        if (signer != null && !NeedsRefresh(signer.TokenExpiresUtc, DateTimeOffset.UtcNow)) return signer;
        signer?.Dispose();

        await TryRefreshSessionAsync(prof.Name, ct);

        try
        {
            signer = OciSigner.Create(prof);
        }
        catch (InvalidOperationException ex)
        {
            throw LoginNeeded(prof, ex.Message);
        }
        if (OciSigner.IsExpired(signer.TokenExpiresUtc, DateTimeOffset.UtcNow))
        {
            signer.Dispose();
            throw LoginNeeded(prof, "session token expired");
        }
        return signer;
    }

    /// <summary>oci session refresh 를 창 없이 실행 (최대 60초, 실패는 무시 — 결과는 토큰 만료 시각으로 다시 판단)</summary>
    private static async Task TryRefreshSessionAsync(string profile, CancellationToken ct)
    {
        if (!ProfileNamePattern.IsMatch(profile)) return;
        Process? proc = null;
        try
        {
            var psi = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            // oci 는 설치 방식에 따라 oci.exe / oci.cmd 등이라 cmd 로 찾게 한다 (프로필 이름은 위에서 검증)
            foreach (var a in new[] { "/d", "/c", "oci", "session", "refresh", "--profile", profile }) psi.ArgumentList.Add(a);
            psi.Environment["OCI_CLI_CONFIG_FILE"] = OciConfig.ConfigPath;
            psi.Environment["OCI_CLI_SUPPRESS_FILE_PERMISSIONS_WARNING"] = "True";
            proc = Process.Start(psi);
            if (proc == null) return;
            proc.StandardInput.Close();
            // 출력은 버리되 끝까지 읽어 파이프가 막히지 않게
            _ = proc.StandardOutput.ReadToEndAsync();
            _ = proc.StandardError.ReadToEndAsync();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(RefreshTimeout);
            try { await proc.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                ct.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }
        finally { proc?.Dispose(); }
    }

    private static InvalidOperationException LoginNeeded(OciProfile prof, string detail) =>
        prof.IsSession
            ? new InvalidOperationException(L.F("OCI 로그인이 필요합니다 ({0}) — {1}",
                BuildLoginCommand(SafeProfileName(prof.Name), prof.Region), CloudProviders.FirstLine(detail)))
            : new InvalidOperationException(L.F("OCI API 키 인증에 실패했습니다 (user · fingerprint · key_file 확인) — {0}",
                CloudProviders.FirstLine(detail)));

    // ----- 순수 함수 (테스트 대상) -----

    /// <summary>명령줄에 넣어도 안전한 프로필 이름. 허용 문자 밖이면 DEFAULT</summary>
    internal static string SafeProfileName(string? name) =>
        !string.IsNullOrEmpty(name) && ProfileNamePattern.IsMatch(name) ? name : "DEFAULT";

    internal static bool IsValidRegion(string? region) => !string.IsNullOrEmpty(region) && RegionPattern.IsMatch(region);

    /// <summary>브라우저 로그인 명령. 리전을 알면(형식 검증 통과) --region 을 붙인다</summary>
    internal static string BuildLoginCommand(string? profile, string? region)
    {
        var cmd = $"oci session authenticate --profile-name {SafeProfileName(profile)}";
        return IsValidRegion(region) ? cmd + $" --region {region}" : cmd;
    }

    /// <summary>토큰 만료 시각이 지났거나 5분 안이면 갱신 필요. 만료 시각을 모르면 갱신하지 않는다</summary>
    internal static bool NeedsRefresh(DateTimeOffset? expiresUtc, DateTimeOffset now) =>
        expiresUtc is { } exp && exp - now < RefreshMargin;

    /// <summary>
    /// 테넌시 OCID 의 realm(ocid1.tenancy.oc1..) 에 맞는 도메인. OCI SDK 의 realm 표와 같은 규칙:
    /// oc1 = oraclecloud.com, oc2·oc3 = oraclegovcloud.com, oc4 = oraclegovcloud.uk, oc19(EU 소버린) = oraclecloud.eu, oc22 = psn-pco.it,
    /// 그 밖의 ocN(oc8 · oc9 · oc10 · oc14 ...) = oraclecloudN.com. 형식을 모르면 상용(oraclecloud.com)
    /// </summary>
    internal static string RealmDomain(string? tenancyOcid)
    {
        var parts = (tenancyOcid ?? "").Split('.');
        var realm = parts.Length > 2 ? parts[2].ToLowerInvariant() : "";
        return realm switch
        {
            "oc1" => "oraclecloud.com",
            "oc2" or "oc3" => "oraclegovcloud.com",
            "oc4" => "oraclegovcloud.uk",
            "oc19" => "oraclecloud.eu",
            "oc22" => "psn-pco.it",   // 이탈리아 PSN 소버린 (ocN 규칙의 예외)
            _ when realm.Length > 2 && realm.StartsWith("oc", StringComparison.Ordinal) &&
                   int.TryParse(realm.AsSpan(2), System.Globalization.NumberStyles.None,
                       System.Globalization.CultureInfo.InvariantCulture, out var n) && n >= 5 => $"oraclecloud{n}.com",
            _ => "oraclecloud.com",
        };
    }

    /// <summary>regionSubscriptions 응답(배열)에서 READY 인 리전 이름</summary>
    internal static List<string> ParseRegions(JsonElement root) =>
        root.ValueKind != JsonValueKind.Array
            ? new List<string>()
            : root.EnumerateArray()
                .Where(r => r.Str("status").Equals("READY", StringComparison.OrdinalIgnoreCase))
                .Select(r => r.Str("regionName"))
                .Where(n => n.Length > 0)
                .ToList();

    /// <summary>컴파트먼트 목록에서 ACTIVE 인 것의 (id, 이름)</summary>
    internal static List<(string id, string name)> ParseCompartments(IEnumerable<JsonElement> items) =>
        items.Where(c => c.Str("id").Length > 0 &&
                         (c.Str("lifecycleState").Length == 0 || c.Str("lifecycleState").Equals("ACTIVE", StringComparison.OrdinalIgnoreCase)))
            .Select(c => (c.Str("id"), c.Str("name").Length > 0 ? c.Str("name") : c.Str("id")))
            .ToList();

    /// <summary>
    /// 없어진 인스턴스: TERMINATED · TERMINATING. 목록에서 뺀다 (조회 자체는 성공이므로 부분 실패로 보지 않음).
    /// STOPPED 등은 중지된 서버로 남긴다.
    /// </summary>
    internal static bool IsGone(string state) =>
        state.ToUpperInvariant() is "TERMINATED" or "TERMINATING";

    /// <summary>VNIC 연결 목록 → 인스턴스별 연결된(ATTACHED) VNIC ID (nicIndex 순 — 보통 0 이 주 VNIC)</summary>
    internal static Dictionary<string, List<string>> VnicIdsByInstance(IEnumerable<JsonElement> attachments) =>
        attachments
            .Where(a => a.Str("lifecycleState").Equals("ATTACHED", StringComparison.OrdinalIgnoreCase)
                        && a.Str("instanceId").Length > 0 && a.Str("vnicId").Length > 0)
            .GroupBy(a => a.Str("instanceId"))
            .ToDictionary(g => g.Key, g => g
                .OrderBy(a => a.TryGetProperty("nicIndex", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : int.MaxValue)
                .Select(a => a.Str("vnicId")).Distinct().ToList());

    /// <summary>VNIC 응답 → (주 VNIC 여부, 사설 IP, 공인 IP)</summary>
    internal static (bool isPrimary, string privateIp, string publicIp) ParseVnic(JsonElement vnic)
    {
        var primary = vnic.ValueKind == JsonValueKind.Object && vnic.TryGetProperty("isPrimary", out var p) &&
                      p.ValueKind == JsonValueKind.True;
        return (primary, vnic.Str("privateIp"), vnic.Str("publicIp"));
    }

    /// <summary>부팅 이미지 ID (sourceDetails.imageId, 없으면 예전 필드 imageId). 부트 볼륨에서 만든 인스턴스는 빈 값</summary>
    internal static string ImageIdOf(JsonElement inst)
    {
        var id = inst.Obj("sourceDetails").Str("imageId");
        return id.Length > 0 ? id : inst.Str("imageId");
    }

    /// <summary>부트 볼륨에서 만든 인스턴스의 부트 볼륨 ID (sourceDetails.sourceType = bootVolume). 아니면 ""</summary>
    internal static string BootVolumeIdOf(JsonElement inst)
    {
        var src = inst.Obj("sourceDetails");
        return src.Str("sourceType").Equals("bootVolume", StringComparison.OrdinalIgnoreCase) ? src.Str("bootVolumeId") : "";
    }

    /// <summary>이미지 응답 → "운영체제 버전" (예: "Oracle Linux 8", "Windows Server 2022 Standard")</summary>
    internal static string OsText(JsonElement image)
    {
        var os = image.Str("operatingSystem").Trim();
        var ver = image.Str("operatingSystemVersion").Trim();
        // 커스텀 이미지는 둘 다 "Custom" 으로 와서 "Custom Custom" 이 되므로 같으면 한 번만
        return ver.Length == 0 || string.Equals(os, ver, StringComparison.OrdinalIgnoreCase) ? os : $"{os} {ver}".Trim();
    }

    internal static bool IsWindowsOs(string os) => os.Contains("Windows", StringComparison.OrdinalIgnoreCase);

    /// <summary>가용성 도메인의 테넌시 접두어 제거 ("Uocm:AP-SEOUL-1-AD-1" → "AP-SEOUL-1-AD-1")</summary>
    internal static string ShortAd(string ad)
    {
        var i = ad.IndexOf(':');
        return i >= 0 ? ad[(i + 1)..] : ad;
    }

    /// <summary>인스턴스 JSON → 공통 형식. 이름은 displayName("이름:용도"), 용도 태그(Purpose/purpose)가 있으면 우선</summary>
    internal static CloudInstance MapInstance(JsonElement inst, string region, string privateIp, string publicIp, string os)
    {
        var id = inst.Str("id");
        var (name, purpose) = CloudInstance.SplitName(inst.Str("displayName"));
        var tags = inst.Obj("freeformTags");
        var tagPurpose = tags.Str("Purpose");
        if (tagPurpose.Length == 0) tagPurpose = tags.Str("purpose");
        if (tagPurpose.Length > 0) purpose = tagPurpose;
        var ad = ShortAd(inst.Str("availabilityDomain"));
        return new CloudInstance(id, name.Length > 0 ? name : id, purpose, inst.Str("lifecycleState"),
            ad.Length > 0 ? ad : region, privateIp, publicIp, IsWindowsOs(os), os);
    }

    internal static string? NextPage(HttpResponseMessage resp) =>
        resp.Headers.TryGetValues("opc-next-page", out var v) && v.FirstOrDefault() is { Length: > 0 } s ? s : null;

    /// <summary>URL 에 page= 쿼리 추가 (page 가 없으면 그대로)</summary>
    internal static string WithPage(string url, string? page) =>
        string.IsNullOrEmpty(page) ? url : url + (url.Contains('?') ? "&" : "?") + "page=" + Uri.EscapeDataString(page);

    /// <summary>OCI 오류 본문({"code","message"}) → "code: message" 첫 줄. JSON 이 아니면 "HTTP 상태: 본문 첫 줄"</summary>
    internal static string ErrorText(int status, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var code = doc.RootElement.Str("code");
            var msg = doc.RootElement.Str("message");
            if (code.Length > 0 || msg.Length > 0)
                return CloudProviders.FirstLine(code.Length > 0 ? $"{code}: {msg}" : msg);
        }
        catch (JsonException) { }
        var line = CloudProviders.FirstLine(body ?? "");
        if (line.Length > 300) line = line[..300];
        return line.Length > 0 ? $"HTTP {status}: {line}" : $"HTTP {status}";
    }
}

/// <summary>OCI API 오류 (상태 코드 · OCI 오류 코드 보존)</summary>
internal sealed class OciApiException : Exception
{
    public OciApiException(int status, string code, string message) : base(message) { Status = status; Code = code; }
    public int Status { get; }
    public string Code { get; }
    /// <summary>인증 실패 (토큰 만료 · 서명 불일치). 403 NotAuthorizedOrNotFound 는 권한(정책) 문제라 포함하지 않는다</summary>
    public bool IsAuth => Status == 401 || Code.Equals("NotAuthenticated", StringComparison.OrdinalIgnoreCase);

    internal static OciApiException From(int status, string body)
    {
        var code = "";
        try { using var doc = JsonDocument.Parse(body); code = doc.RootElement.Str("code"); } catch (JsonException) { }
        return new OciApiException(status, code, OciProvider.ErrorText(status, body));
    }
}
