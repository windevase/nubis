using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Nubis;

/// <summary>GitHub 최신 릴리스 정보 (tag_name · html_url)</summary>
public sealed record ReleaseInfo(string Tag, string Url)
{
    /// <summary>화면에 쓸 버전 ("v0.6.2+abc" → "0.6.2")</summary>
    public string Version => UpdateCheck.Clean(Tag);
}

/// <summary>
/// 새 버전 알림 (포터블 zip 사용자용). GitHub 의 최신 정식 릴리스를 하루 한 번 익명으로 조회한다.
/// 보내는 것은 고정된 User-Agent("Nubis") 뿐 — 사용자 · PC · 앱 버전 같은 식별 정보는 넣지 않는다.
/// 버전 비교 · 응답 해석 · 확인 주기는 화면과 무관한 순수 함수로 두고 테스트한다 (UpdateCheckTests).
/// </summary>
public static class UpdateCheck
{
    public const string LatestApi = "https://api.github.com/repos/windevase/nubis/releases/latest";
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    /// <summary>응답 본문 상한 — 정상 응답은 수 KB. 잘못된 응답으로 메모리를 채우지 않게</summary>
    private const int MaxBody = 512 * 1024;

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
    })
    {
        Timeout = TimeSpan.FromSeconds(15),
        MaxResponseContentBufferSize = MaxBody,
    };

    /// <summary>마지막 확인 뒤 하루가 지났는지 (기록이 없거나 시계가 거꾸로 갔으면 확인)</summary>
    public static bool IsDue(DateTime? lastCheckUtc, DateTime nowUtc) =>
        lastCheckUtc is not { } last || nowUtc < last || nowUtc - last >= Interval;

    /// <summary>"v0.6.2+abc123" → "0.6.2" (앞의 v · 빌드 메타데이터 제거, 앞뒤 공백 제거)</summary>
    public static string Clean(string? version)
    {
        var s = (version ?? "").Trim();
        if (s.StartsWith('v') || s.StartsWith('V')) s = s[1..];
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        return s.Trim();
    }

    /// <summary>
    /// 버전 해석: 숫자 부분(최대 4자리, 없는 자리는 0) + 프리릴리스 꼬리표("-beta" 등) 여부.
    /// 형식이 맞지 않으면 false — 모르는 값은 새 버전으로 보지 않는다
    /// </summary>
    public static bool TryParse(string? version, out int[] parts, out bool preRelease)
    {
        parts = new int[4];
        preRelease = false;
        var s = Clean(version);
        var dash = s.IndexOf('-');
        if (dash >= 0) { preRelease = true; s = s[..dash]; }
        var nums = s.Split('.');
        if (nums.Length is < 1 or > 4) return false;
        for (var i = 0; i < nums.Length; i++)
            if (nums[i].Length == 0 || nums[i].Length > 9 || !int.TryParse(nums[i], System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out parts[i]))
                return false;
        return true;
    }

    /// <summary>latest 가 current 보다 새 버전인지. 둘 중 하나라도 해석할 수 없으면 false. 숫자가 같으면 정식 &gt; 프리릴리스</summary>
    public static bool IsNewer(string? current, string? latest)
    {
        if (!TryParse(current, out var cur, out var curPre) || !TryParse(latest, out var lat, out var latPre)) return false;
        for (var i = 0; i < 4; i++)
            if (lat[i] != cur[i]) return lat[i] > cur[i];
        return curPre && !latPre;
    }

    /// <summary>알릴지: latest 가 current 보다 새 버전이고, 사용자가 "이 버전 건너뛰기" 한 버전이 아닐 때 (더 새 버전이 나오면 다시 알림)</summary>
    public static bool ShouldNotify(string? current, ReleaseInfo? latest, string? skippedVersion) =>
        latest != null && IsNewer(current, latest.Tag) &&
        !string.Equals(Clean(skippedVersion), latest.Version, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// releases/latest 응답 해석. 프리릴리스 · 초안 · 태그 없음 · http/https 가 아닌 주소면 null (알리지 않는다)
    /// </summary>
    public static ReleaseInfo? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (IsTrue(root, "prerelease") || IsTrue(root, "draft")) return null;
            var tag = Str(root, "tag_name");
            var url = Str(root, "html_url");
            if (string.IsNullOrWhiteSpace(tag) || !TryParse(tag, out _, out var pre) || pre) return null;
            if (!IsWebUrl(url)) return null;
            return new ReleaseInfo(tag.Trim(), url!.Trim());
        }
        catch (JsonException) { return null; }
    }

    /// <summary>브라우저로 열어도 되는 주소인지 (절대 http/https 만)</summary>
    public static bool IsWebUrl(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);

    /// <summary>최신 정식 릴리스 조회. 실패(오프라인 · 레이트리밋 · 형식 오류)는 null — 부르는 쪽은 조용히 넘어간다</summary>
    public static async Task<ReleaseInfo?> FetchLatestAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, LatestApi);
            req.Headers.UserAgent.ParseAdd("Nubis");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var res = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;
            var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Parse(body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsTrue(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
