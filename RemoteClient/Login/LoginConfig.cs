using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nubis;

/// <summary>앱 시작 로그인 제공자</summary>
public enum LoginProviderKind { None, GitHub, Google, Microsoft }

/// <summary>
/// 앱 시작 화면 로그인 설정 (settings.json 의 Login). 로그인은 "앱에 들어가는 문" 이다 —
/// 데이터 암호화(DPAPI)와 묶여 있지 않으므로 같은 Windows 계정의 프로그램까지 막지는 못한다.
/// 설정을 잘못해 들어갈 수 없으면 settings.json 의 Login.Provider 를 "None" 으로 바꾸면 된다.
/// </summary>
public sealed class LoginConfig
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public LoginProviderKind Provider { get; set; } = LoginProviderKind.None;
    /// <summary>OAuth 앱 Client ID (비밀 값 아님)</summary>
    public string ClientId { get; set; } = "";
    /// <summary>Google 만 필요 ("TV 및 입력 제한 기기" 클라이언트의 secret) — DPAPI 암호문</summary>
    public string? ProtectedClientSecret { get; set; }
    /// <summary>Microsoft 테넌트: organizations(회사·학교 계정), common, 또는 테넌트 ID·도메인</summary>
    public string Tenant { get; set; } = "organizations";
    /// <summary>허용 계정: GitHub 로그인 이름 / 이메일. "@회사.com" 은 그 도메인 전체</summary>
    public List<string> AllowedAccounts { get; set; } = new();
    /// <summary>GitHub: 이 Organization 의 멤버면 허용 (비우면 사용 안 함)</summary>
    public string GitHubOrg { get; set; } = "";
    /// <summary>로그인 유지 시간 (0 = 실행할 때마다 로그인)</summary>
    public int RememberHours { get; set; }
    /// <summary>인터넷이 안 될 때 최근 로그인 기록으로 들어갈 수 있는 기간 (0 = 허용 안 함)</summary>
    public int OfflineDays { get; set; }

    [JsonIgnore]
    public string ClientSecret
    {
        get => Secret.Unprotect(ProtectedClientSecret);
        set => ProtectedClientSecret = string.IsNullOrEmpty(value) ? null : Secret.Protect(value);
    }

    [JsonIgnore] public bool Enabled => Provider != LoginProviderKind.None && ClientId.Trim().Length > 0;

    public LoginConfig Clone()
    {
        var c = (LoginConfig)MemberwiseClone();
        c.AllowedAccounts = AllowedAccounts.ToList();
        return c;
    }

    public static string ProviderLabel(LoginProviderKind p) => p switch
    {
        LoginProviderKind.GitHub => "GitHub",
        LoginProviderKind.Google => "Google",
        LoginProviderKind.Microsoft => "Microsoft (Entra ID)",
        _ => "사용 안 함",
    };

    /// <summary>"a, b ;c" → [a, b, c] (빈 값·중복 제거)</summary>
    public static List<string> ParseList(string text) =>
        text.Split(new[] { ',', ';', '\n', '\r', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// 허용 계정 목록과 비교 (대소문자 무시). "@domain" 항목은 그 도메인의 이메일 전체.
    /// GitHub Organization 멤버 여부는 호출하는 쪽에서 따로 확인해 orgMember 로 넘긴다.
    /// </summary>
    public bool IsAllowed(LoginIdentity who, bool orgMember)
    {
        if (orgMember) return true;
        foreach (var raw in AllowedAccounts)
        {
            var a = raw.Trim();
            if (a.Length == 0) continue;
            if (a.StartsWith('@'))
            {
                if (who.Account.Contains('@') && who.Account.EndsWith(a, StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (a.Equals(who.Account, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>저장 전 설정 확인. 문제가 있으면 사유, 없으면 null</summary>
    public string? Validate()
    {
        if (Provider == LoginProviderKind.None) return null;
        if (ClientId.Trim().Length == 0) return "Client ID 를 넣으세요.";
        if (Provider == LoginProviderKind.Google && ClientSecret.Length == 0) return "Google 은 Client Secret 도 필요합니다 (\"TV 및 입력 제한 기기\" 유형 클라이언트).";
        if (Provider == LoginProviderKind.Microsoft && Tenant.Trim().Length == 0) return "Microsoft 테넌트를 넣으세요 (회사 계정이면 organizations).";
        if (AllowedAccounts.Count == 0 && (Provider != LoginProviderKind.GitHub || GitHubOrg.Trim().Length == 0))
            return "허용할 계정을 하나 이상 넣으세요" + (Provider == LoginProviderKind.GitHub ? " (또는 GitHub Organization)." : ".");
        if (RememberHours is < 0 or > 24 * 30) return "로그인 유지 시간은 0 ~ 720 시간입니다.";
        if (OfflineDays is < 0 or > 30) return "오프라인 허용 기간은 0 ~ 30 일입니다.";
        return null;
    }
}

/// <summary>로그인한 사람 (토큰은 담지 않는다)</summary>
public sealed record LoginIdentity(LoginProviderKind Provider, string Account, string Name, string Id);

/// <summary>마지막 로그인 기록 (login.json, DPAPI 파일 암호화). 로그인 유지·오프라인 허용 판단에 쓴다</summary>
public sealed class LoginState
{
    public LoginProviderKind Provider { get; set; }
    public string ClientId { get; set; } = "";
    public string Account { get; set; } = "";
    public DateTime LastSuccessUtc { get; set; }

    public const string FileName = "login.json";
    private static string FilePath => Path.Combine(AppPaths.DataDir, FileName);

    public static LoginState? Load() =>
        File.Exists(FilePath) ? SafeFile.LoadProtected<LoginState>(FilePath, s => JsonSerializer.Deserialize<LoginState>(s), () => new LoginState()) : null;

    public static void Save(LoginIdentity who, LoginConfig cfg) =>
        SafeFile.WriteProtected(FilePath, JsonSerializer.Serialize(new LoginState
        {
            Provider = cfg.Provider, ClientId = cfg.ClientId, Account = who.Account, LastSuccessUtc = DateTime.UtcNow,
        }));

    public static void Clear()
    {
        try { File.Delete(FilePath); } catch { }
    }

    /// <summary>같은 제공자·앱이고, 마지막 로그인이 hours 시간 안이면 true</summary>
    public bool Within(LoginConfig cfg, TimeSpan span, DateTime nowUtc) =>
        span > TimeSpan.Zero && Provider == cfg.Provider && ClientId == cfg.ClientId && Account.Length > 0 &&
        LastSuccessUtc <= nowUtc && nowUtc - LastSuccessUtc <= span;
}
