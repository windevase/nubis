using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Nubis;

public sealed class LoginException(string message) : Exception(message);

/// <summary>Device Flow 1단계 결과: 사용자가 브라우저에서 입력할 코드</summary>
public sealed record DeviceCodeInfo(string UserCode, string VerificationUri, string DeviceCode, int IntervalSec, int ExpiresInSec);

/// <summary>
/// OAuth 2.0 Device Authorization Grant (RFC 8628) — GitHub · Google · Microsoft(Entra ID).
/// 데스크톱 앱이라 client secret 을 숨길 수 없으므로 Client ID 만으로 되는 이 방식을 쓴다 (Google 만 secret 필요).
/// 받은 토큰은 계정 확인에만 쓰고 저장하지 않는다.
/// </summary>
public sealed class DeviceFlow
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly LoginConfig _cfg;

    public DeviceFlow(LoginConfig cfg) => _cfg = cfg;

    private string Tenant => Uri.EscapeDataString(_cfg.Tenant.Trim().Length > 0 ? _cfg.Tenant.Trim() : "organizations");

    private (string DeviceUrl, string TokenUrl, string Scope) Endpoints => _cfg.Provider switch
    {
        LoginProviderKind.GitHub => ("https://github.com/login/device/code", "https://github.com/login/oauth/access_token",
            _cfg.GitHubOrg.Trim().Length > 0 ? "read:user read:org" : "read:user"),
        LoginProviderKind.Google => ("https://oauth2.googleapis.com/device/code", "https://oauth2.googleapis.com/token", "openid email profile"),
        LoginProviderKind.Microsoft => ($"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/devicecode",
            $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/token", "openid profile email"),
        _ => throw new LoginException(L.T("로그인 제공자가 설정되지 않았습니다.")),
    };

    private static async Task<JsonElement> PostFormAsync(string url, Dictionary<string, string> form, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var res = await Http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        try { return JsonDocument.Parse(body).RootElement.Clone(); }
        catch (JsonException) { throw new LoginException(L.F("로그인 서버 응답을 읽을 수 없습니다 (HTTP {0}). Client ID 를 확인하세요.", (int)res.StatusCode)); }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name, int fallback) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : fallback;

    /// <summary>1단계 응답 해석 (제공자마다 이름이 조금씩 다름: verification_uri / verification_url)</summary>
    public static DeviceCodeInfo ParseDeviceCode(JsonElement r)
    {
        if (Str(r, "error") is { } err)
            throw new LoginException(Describe(err, Str(r, "error_description")));
        var user = Str(r, "user_code");
        var device = Str(r, "device_code");
        var uri = Str(r, "verification_uri") ?? Str(r, "verification_url");
        if (user == null || device == null || uri == null) throw new LoginException(L.T("로그인 코드를 받지 못했습니다. Client ID 와 Device Flow 사용 설정을 확인하세요."));
        return new DeviceCodeInfo(user, uri, device, Math.Max(1, Int(r, "interval", 5)), Int(r, "expires_in", 900));
    }

    public async Task<DeviceCodeInfo> StartAsync(CancellationToken ct)
    {
        var (deviceUrl, _, scope) = Endpoints;
        var r = await PostFormAsync(deviceUrl, new() { ["client_id"] = _cfg.ClientId.Trim(), ["scope"] = scope }, ct);
        return ParseDeviceCode(r);
    }

    /// <summary>폴링 응답 해석: 토큰이면 응답 전체, 아직이면 null(대기 시간 증가 여부 함께), 거부·만료면 예외</summary>
    public static (JsonElement? Token, bool SlowDown) ParseTokenPoll(JsonElement r)
    {
        switch (Str(r, "error"))
        {
            case null:
                if (Str(r, "access_token") == null) throw new LoginException(L.T("토큰을 받지 못했습니다."));
                return (r, false);
            case "authorization_pending": return (null, false);
            case "slow_down": return (null, true);
            case var e: throw new LoginException(Describe(e, Str(r, "error_description")));
        }
    }

    /// <summary>
    /// 사용자가 브라우저에서 승인할 때까지 기다린 뒤 계정을 확인한다. GitHub Organization 이 설정돼 있으면 멤버 여부도.
    /// 토큰은 이 안에서만 쓰고 버린다. 허용 여부 판단은 호출하는 쪽(LoginConfig.IsAllowed)에서.
    /// </summary>
    public async Task<(LoginIdentity Who, bool OrgMember)> WaitAsync(DeviceCodeInfo code, CancellationToken ct)
    {
        var token = await PollTokenAsync(code, ct);
        var who = await IdentityAsync(token, ct);
        var member = _cfg.Provider == LoginProviderKind.GitHub && _cfg.GitHubOrg.Trim().Length > 0 &&
                     await IsGitHubOrgMemberAsync(Str(token, "access_token")!, ct);
        return (who, member);
    }

    private async Task<JsonElement> PollTokenAsync(DeviceCodeInfo code, CancellationToken ct)
    {
        var (_, tokenUrl, _) = Endpoints;
        var form = new Dictionary<string, string>
        {
            ["client_id"] = _cfg.ClientId.Trim(),
            ["device_code"] = code.DeviceCode,
            ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
        };
        if (_cfg.Provider == LoginProviderKind.Google) form["client_secret"] = _cfg.ClientSecret;

        var interval = TimeSpan.FromSeconds(code.IntervalSec);
        var deadline = DateTime.UtcNow.AddSeconds(code.ExpiresInSec);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(interval, ct);
            var (token, slow) = ParseTokenPoll(await PostFormAsync(tokenUrl, form, ct));
            if (slow) interval += TimeSpan.FromSeconds(5);
            if (token is { } t) return t;
        }
        throw new LoginException(L.T("로그인 코드가 만료되었습니다. 다시 시도하세요."));
    }

    private async Task<LoginIdentity> IdentityAsync(JsonElement token, CancellationToken ct)
    {
        var access = Str(token, "access_token")!;
        switch (_cfg.Provider)
        {
            case LoginProviderKind.GitHub:
            {
                var u = await GetJsonAsync("https://api.github.com/user", access, ct);
                var login = Str(u, "login") ?? throw new LoginException(L.T("GitHub 계정 정보를 읽지 못했습니다."));
                var id = u.TryGetProperty("id", out var idEl) ? idEl.ToString() : "";
                return new LoginIdentity(LoginProviderKind.GitHub, login, Str(u, "name") ?? login, id);
            }
            case LoginProviderKind.Google:
            {
                var u = await GetJsonAsync("https://openidconnect.googleapis.com/v1/userinfo", access, ct);
                var email = Str(u, "email") ?? throw new LoginException(L.T("Google 계정 이메일을 읽지 못했습니다."));
                if (u.TryGetProperty("email_verified", out var ev) && ev.ValueKind == JsonValueKind.False)
                    throw new LoginException(L.T("이메일이 확인되지 않은 Google 계정입니다."));
                return new LoginIdentity(LoginProviderKind.Google, email, Str(u, "name") ?? email, Str(u, "sub") ?? "");
            }
            case LoginProviderKind.Microsoft:
            {
                // id_token 은 TLS 로 토큰 엔드포인트에서 직접 받은 것이라 내용만 읽는다
                var claims = JwtPayload(Str(token, "id_token") ?? throw new LoginException(L.T("Microsoft 로그인 정보(id_token)를 받지 못했습니다.")));
                var account = Str(claims, "preferred_username") ?? Str(claims, "email") ?? throw new LoginException(L.T("Microsoft 계정 이름을 읽지 못했습니다."));
                return new LoginIdentity(LoginProviderKind.Microsoft, account, Str(claims, "name") ?? account, Str(claims, "oid") ?? "");
            }
        }
        throw new LoginException(L.T("로그인 제공자가 설정되지 않았습니다."));
    }

    /// <summary>GitHub Organization 활성 멤버인지 (read:org 권한)</summary>
    public async Task<bool> IsGitHubOrgMemberAsync(string accessToken, CancellationToken ct)
    {
        var org = _cfg.GitHubOrg.Trim();
        if (org.Length == 0) return false;
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/user/memberships/orgs/{Uri.EscapeDataString(org)}");
        Prepare(req, accessToken);
        using var res = await Http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) return false;
        var m = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct)).RootElement;
        return Str(m, "state") == "active";
    }

    private static void Prepare(HttpRequestMessage req, string accessToken)
    {
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        req.Headers.UserAgent.ParseAdd("Nubis-RemoteClient");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private static async Task<JsonElement> GetJsonAsync(string url, string accessToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        Prepare(req, accessToken);
        using var res = await Http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) throw new LoginException(L.F("계정 정보를 읽지 못했습니다 (HTTP {0}).", (int)res.StatusCode));
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct)).RootElement.Clone();
    }

    /// <summary>JWT 의 payload(가운데 부분)를 JSON 으로</summary>
    public static JsonElement JwtPayload(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) throw new LoginException(L.T("로그인 정보 형식이 올바르지 않습니다."));
        var b64 = parts[1].Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
        return JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64))).RootElement.Clone();
    }

    /// <summary>OAuth 오류 코드를 한국어 안내로</summary>
    public static string Describe(string error, string? description) => error switch
    {
        "access_denied" => L.T("로그인이 거부되었습니다."),
        "expired_token" => L.T("로그인 코드가 만료되었습니다. 다시 시도하세요."),
        "incorrect_client_credentials" or "invalid_client" or "unauthorized_client" =>
            L.T("Client ID(또는 Secret)가 올바르지 않거나 이 앱에서 Device Flow 가 켜져 있지 않습니다."),
        "device_flow_disabled" => L.T("GitHub OAuth 앱 설정에서 'Enable Device Flow' 를 켜세요."),
        "Not Found" => L.T("Client ID 에 해당하는 OAuth 앱을 찾을 수 없습니다. Client ID 를 확인하세요."),
        "invalid_scope" => L.T("요청한 권한(scope)을 이 앱에서 쓸 수 없습니다."),
        _ => L.F("로그인 실패: {0}", error) + (string.IsNullOrWhiteSpace(description) ? "" : $" — {description.Split('\n')[0]}"),
    };
}
