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
    /// <summary>
    /// 소유자 프로필 (로그인을 설정한 계정, Profiles.Key). 로그인을 켜기 전 데이터는 이 계정이 처음 로그인할 때 넘겨받고,
    /// 로그인 설정은 이 계정만 바꿀 수 있다. 로그인을 꺼도 이 프로필을 쓴다.
    /// </summary>
    public string OwnerKey { get; set; } = "";
    /// <summary>소유자가 로그인 방식을 바꿨을 때 예전 프로필 이름 (다음 실행 때 새 이름으로 옮긴다)</summary>
    public string PreviousOwnerKey { get; set; } = "";

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
        _ => L.T("사용 안 함"),
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
                // 도메인 전체 허용은 그 도메인을 조직이 관리하는 계정만 (Google hd 클레임 · Microsoft 회사/학교 테넌트).
                // 회사 이메일로 만든 개인 Google·Microsoft 계정이 들어오지 못하게
                if (who.DomainVerified && who.Account.Contains('@') && who.Account.EndsWith(a, StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (a.Equals(who.Account, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>저장 전 설정 확인. 문제가 있으면 사유, 없으면 null</summary>
    public string? Validate()
    {
        if (Provider == LoginProviderKind.None) return null;
        if (ClientId.Trim().Length == 0) return L.T("Client ID 를 넣으세요.");
        if (Provider == LoginProviderKind.Google && ClientSecret.Length == 0) return L.T("Google 은 Client Secret 도 필요합니다 (\"TV 및 입력 제한 기기\" 유형 클라이언트).");
        if (Provider == LoginProviderKind.Microsoft && Tenant.Trim().Length == 0) return L.T("Microsoft 테넌트를 넣으세요 (회사 계정이면 organizations).");
        if (AllowedAccounts.Count == 0 && (Provider != LoginProviderKind.GitHub || GitHubOrg.Trim().Length == 0))
            return Provider == LoginProviderKind.GitHub ? L.T("허용할 계정을 하나 이상 넣으세요 (또는 GitHub Organization).") : L.T("허용할 계정을 하나 이상 넣으세요.");
        if (RememberHours is < 0 or > 24 * 30) return L.T("로그인 유지 시간은 0 ~ 720 시간입니다.");
        if (OfflineDays is < 0 or > 30) return L.T("오프라인 허용 기간은 0 ~ 30 일입니다.");
        return null;
    }
}

/// <summary>로그인한 사람 (토큰은 담지 않는다)</summary>
/// <param name="DomainVerified">
/// Account 의 이메일 도메인을 조직이 관리하는 계정인지 ("@도메인" 허용 규칙에 쓴다). Google = hd 클레임이 그 도메인,
/// Microsoft = 개인 계정(MSA) 테넌트가 아님. GitHub 는 이메일을 쓰지 않아 상관없다.
/// </param>
public sealed record LoginIdentity(LoginProviderKind Provider, string Account, string Name, string Id, bool DomainVerified = true)
{
    public string ProfileKey => Profiles.Key(Provider, Id, Account);
}

/// <summary>마지막 로그인 기록 (login.json, DPAPI 파일 암호화). 로그인 유지·오프라인 허용 판단에 쓴다</summary>
public sealed class LoginState
{
    public LoginProviderKind Provider { get; set; }
    public string ClientId { get; set; } = "";
    public string Account { get; set; } = "";
    /// <summary>바뀌지 않는 계정 ID (GitHub 사용자 번호, Google sub, Microsoft oid) — 프로필 폴더 이름</summary>
    public string AccountId { get; set; } = "";
    /// <summary>마지막 온라인 로그인 시각 (오프라인 진입으로는 갱신하지 않는다)</summary>
    public DateTime LastSuccessUtc { get; set; }
    /// <summary>마지막 온라인 로그인(LastSuccessUtc) 때 GitHub Organization 멤버 확인을 통과했는지</summary>
    public bool OrgMember { get; set; }
    /// <summary>그때 확인한 Organization 이름 (설정의 GitHubOrg 를 바꾸면 인정하지 않는다)</summary>
    public string Org { get; set; } = "";
    /// <summary>Microsoft: 로그인한 때의 테넌트 설정 (다른 제공자는 빈 값). 예전 기록에는 없다 → Microsoft 면 다시 로그인</summary>
    public string Tenant { get; set; } = "";
    /// <summary>
    /// 마지막 온라인 로그인 때 도메인 확인(LoginIdentity.DomainVerified)을 통과했는지. 예전 기록에는 없다(null) →
    /// Google·Microsoft 는 "@도메인" 규칙으로 다시 인정하지 않는다 (한 번 다시 로그인하면 기록됨)
    /// </summary>
    public bool? DomainVerified { get; set; }

    public string ProfileKey => Profiles.Key(Provider, AccountId, Account);

    public const string FileName = "login.json";
    private static string FilePath => Path.Combine(AppPaths.RootDir, FileName);   // 공통 폴더 (로그인 전에 읽음)

    public static LoginState? Load() =>
        File.Exists(FilePath) ? SafeFile.LoadProtected<LoginState>(FilePath, s => JsonSerializer.Deserialize<LoginState>(s), () => new LoginState()) : null;

    /// <summary>온라인 로그인 성공 기록. orgMember = 이번 로그인에서 GitHub Organization 멤버 확인을 통과했는지</summary>
    public static void Save(LoginIdentity who, LoginConfig cfg, bool orgMember = false) =>
        SafeFile.WriteProtected(FilePath, JsonSerializer.Serialize(Create(who, cfg, orgMember, DateTime.UtcNow)));

    internal static LoginState Create(LoginIdentity who, LoginConfig cfg, bool orgMember, DateTime nowUtc)
    {
        var org = cfg.Provider == LoginProviderKind.GitHub ? cfg.GitHubOrg.Trim() : "";
        return new LoginState
        {
            Provider = cfg.Provider, ClientId = cfg.ClientId, Account = who.Account, AccountId = who.Id, LastSuccessUtc = nowUtc,
            OrgMember = orgMember && org.Length > 0, Org = orgMember ? org : "",
            Tenant = TenantOf(cfg),
            DomainVerified = who.DomainVerified,
        };
    }

    /// <summary>기록·비교에 쓰는 테넌트 값: Microsoft 만 (앞뒤 공백 제거), 다른 제공자는 빈 값</summary>
    private static string TenantOf(LoginConfig cfg) =>
        cfg.Provider == LoginProviderKind.Microsoft ? (cfg.Tenant ?? "").Trim() : "";

    public static void Clear()
    {
        try { File.Delete(FilePath); } catch { }
    }

    /// <summary>
    /// 같은 제공자·앱(Microsoft 는 테넌트까지)이고, 마지막 로그인이 span 안이면 true.
    /// 테넌트를 바꾸면 다른 테넌트에서 확인한 기록을 인정하지 않는다 — 테넌트 기록이 없는 예전 기록도 Microsoft 면 다시 로그인.
    /// </summary>
    public bool Within(LoginConfig cfg, TimeSpan span, DateTime nowUtc) =>
        span > TimeSpan.Zero && Provider == cfg.Provider && ClientId == cfg.ClientId && Account.Length > 0 &&
        (cfg.Provider != LoginProviderKind.Microsoft ||
         (TenantOf(cfg).Length > 0 && string.Equals((Tenant ?? "").Trim(), TenantOf(cfg), StringComparison.OrdinalIgnoreCase))) &&
        LastSuccessUtc <= nowUtc && nowUtc - LastSuccessUtc <= span;

    /// <summary>
    /// 저장된 계정이 지금 설정으로도 허용되는지. 허용 목록에서 지운 계정이 로그인 유지·오프라인 허용으로 계속 들어오지 않게.
    /// GitHub Organization 멤버 허용은 로그인 없이 다시 확인할 수 없으므로, 마지막 온라인 로그인 때 같은 Organization 확인을
    /// 통과한 기록이 있을 때만 인정한다 (기간은 호출하는 쪽의 Within — 유지 시간·오프라인 허용 기간 — 으로 제한).
    /// offline = 오프라인 진입: 소유자(OwnerKey)는 허용 목록과 상관없이 인정 (인터넷이 없을 때 소유자가 갇히지 않게).
    /// </summary>
    public bool StillAllowed(LoginConfig cfg, bool offline = false)
    {
        if (Account.Length == 0) return false;
        var domainVerified = DomainVerified ?? Provider == LoginProviderKind.GitHub;
        if (cfg.IsAllowed(new LoginIdentity(Provider, Account, "", AccountId, domainVerified), orgMember: OrgStillVerified(cfg))) return true;
        return offline && cfg.OwnerKey.Length > 0 && ProfileKey == cfg.OwnerKey;
    }

    /// <summary>마지막 온라인 로그인 때 지금 설정과 같은 GitHub Organization 의 멤버로 확인됐는지</summary>
    private bool OrgStillVerified(LoginConfig cfg)
    {
        var org = cfg.GitHubOrg.Trim();
        return OrgMember && Provider == LoginProviderKind.GitHub && cfg.Provider == LoginProviderKind.GitHub &&
               org.Length > 0 && string.Equals(Org, org, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>로그인 화면 없이 들어갈 수 있는지: 기간(마지막 온라인 로그인 기준) 안이고 지금도 허용 계정</summary>
    public bool CanEnterWithout(LoginConfig cfg, TimeSpan span, DateTime nowUtc, bool offline = false) =>
        Within(cfg, span, nowUtc) && StillAllowed(cfg, offline);
}
