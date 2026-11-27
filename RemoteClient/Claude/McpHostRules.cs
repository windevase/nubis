using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Nubis;

// MCP 호스트 도구(add_host · update_host · get_host · list_hosts · open_session 빠른 연결 등)의 UI 와 무관한 판단 — 테스트 대상

/// <summary>add_host / update_host 의 UI 와 무관한 판정 (테스트 대상)</summary>
internal static class McpHostRules
{
    // 변경 목록 줄의 머리말 (ApplyHostArgs 의 Note 라벨) — 위험 판정·즐겨찾기만 판정이 이 문자열에 기댄다
    public const string FavoriteLabel = "즐겨찾기";
    public const string JumpLabel = "점프 서버";
    public const string GatewayLabel = "RD 게이트웨이";
    /// <summary>RDP NLA(CredSSP) — 끄면 접속 전 인증 없이 원격 로그인 화면까지 연결되므로 위험 변경으로 본다</summary>
    public const string NlaLabel = "NLA(CredSSP)";
    public const string AnswersLabel = "기억한 인증 답";
    public const string TagLabel = "태그";
    /// <summary>저장된 SSH 포트 포워딩 규칙 (SSH 가 아닌 프로토콜로 바꾸면 지운다 — 편집 창과 같음)</summary>
    public const string ForwardsLabel = "포트 포워딩";

    /// <summary>
    /// 편집 창(SaveEditor)과 같게: SSH 가 아닌 호스트면 저장된 포트 포워딩 규칙을 지운다. 지운 규칙 수 (지운 것이 없으면 0)
    /// </summary>
    public static int ClearForwardsIfNotSsh(HostEntry h)
    {
        if (h.Protocol == Protocol.Ssh || h.PortForwards == null) return 0;
        var n = h.PortForwards.Count;
        h.PortForwards = null;
        return n;
    }

    /// <summary>
    /// 저장된 포트 포워딩 규칙 요약 (승인 창·감사 로그·get_host): "2개 — 127.0.0.1:15432 → db:5432 (prod-db), 127.0.0.1:8080 → localhost:80 (꺼짐)".
    /// 앞의 max 개만 보이고 나머지는 "외 N개". 규칙이 없으면 null
    /// </summary>
    public static string? ForwardsSummary(IReadOnlyCollection<PortForwardRule>? rules, int max = 5)
    {
        if (rules is not { Count: > 0 }) return null;
        var shown = rules.Take(max).Select(r =>
        {
            var extra = new List<string>();
            if (!string.IsNullOrWhiteSpace(r.Name)) extra.Add(McpHelpers.VisibleControlChars(r.Name.Trim()));
            if (!r.Enabled) extra.Add("꺼짐");
            // hosts.json 을 손으로 고친 값일 수 있어 대상 호스트도 보이게 바꿔 넣는다
            return McpHelpers.VisibleControlChars(PortForwards.Describe(r)) + (extra.Count > 0 ? $" ({string.Join(", ", extra)})" : "");
        });
        return $"{rules.Count}개 — {string.Join(", ", shown)}" + (rules.Count > max ? $" 외 {rules.Count - max}개" : "");
    }

    /// <summary>두 태그 묶음 사이에 값이 달라진 키 (대소문자 무시)</summary>
    public static List<string> ChangedTagKeys(IReadOnlyDictionary<string, string>? before, IReadOnlyDictionary<string, string>? after)
    {
        var b = before == null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>(before, StringComparer.OrdinalIgnoreCase);
        var a = after == null ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) : new Dictionary<string, string>(after, StringComparer.OrdinalIgnoreCase);
        return b.Keys.Union(a.Keys, StringComparer.OrdinalIgnoreCase)
            .Where(k => b.GetValueOrDefault(k) != a.GetValueOrDefault(k)).ToList();
    }

    private static readonly string[] RiskyLabels = { "주소", "공인", "Vault", "포트", JumpLabel, GatewayLabel, NlaLabel };

    /// <summary>접속 대상·계정·경로를 바꾸는 변경 (승인 창에 경고)</summary>
    public static bool IsRisky(string change) =>
        // "포트 포워딩" 은 "포트" 로 시작하지만 규칙을 지우기만 하므로(노출이 줄어듦) 위험 변경이 아니다
        !change.StartsWith(ForwardsLabel, StringComparison.Ordinal) && RiskyLabels.Any(l => change.StartsWith(l, StringComparison.Ordinal));

    /// <summary>즐겨찾기만 바꾸는 변경인가 (승인 생략 대상). 빈 목록은 false</summary>
    public static bool IsFavoriteOnly(IReadOnlyCollection<string> changes) =>
        changes.Count > 0 && changes.All(c => c.StartsWith(FavoriteLabel + ":", StringComparison.Ordinal));

    /// <summary>
    /// target 의 점프 서버로 jump 를 쓸 수 없으면 이유, 쓸 수 있으면 null. 편집 창(OnPickJump)과 같은 규칙:
    /// 둘 다 SSH, 자기 자신 불가, 한 단계만 (jump 가 다시 점프 서버를 거치면 불가 — 이미 지정된 점프 서버는 그대로 허용)
    /// </summary>
    public static string? JumpError(HostEntry target, HostEntry jump)
    {
        if (target.Protocol != Protocol.Ssh) return "점프 서버는 SSH 호스트에만 지정할 수 있습니다.";
        if (jump.Id == target.Id || ReferenceEquals(jump, target)) return "자기 자신을 점프 서버로 지정할 수 없습니다.";
        if (jump.Protocol != Protocol.Ssh) return $"'{jump.DisplayName}' 은 SSH 호스트가 아니라 점프 서버로 쓸 수 없습니다.";
        if (jump.JumpHostId != null && jump.Id != target.JumpHostId)
            return $"'{jump.DisplayName}' 도 점프 서버를 거쳐 접속합니다. 점프 서버는 한 단계만 지원합니다.";
        return null;
    }

    /// <summary>
    /// RD 게이트웨이 값 검사: "" 이면 null(지우기), "host" 또는 "host:port" ("[IPv6]:port" 포함).
    /// 공백·제어 문자·경로·계정 구분자(/ \ @ ; 등)는 거부 — 게이트웨이 칸에 다른 값을 끼워 넣지 못하게
    /// </summary>
    public static string? NormalizeGateway(string value, out string? error)
    {
        error = null;
        var v = value.Trim();
        if (v.Length == 0) return null;
        if (v.Length > 255 || McpHelpers.HasInvisibleChars(v)) { error = "rdp_gateway 가 올바르지 않습니다."; return null; }
        foreach (var ch in v)
            // ASCII 만 — 유니코드 글자(키릴 'о' 등)로 승인 창에서 다른 게이트웨이처럼 보이게 꾸미지 못하게
            if (!(char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_' or ':' or '[' or ']'))
            { error = $"rdp_gateway 에 쓸 수 없는 문자가 있습니다: '{ch}' (\"호스트\" 또는 \"호스트:포트\")"; return null; }

        string host;
        string? port = null;
        if (v.StartsWith('['))
        {
            var close = v.IndexOf(']');
            if (close < 2) { error = "rdp_gateway 의 IPv6 주소 형식이 올바르지 않습니다."; return null; }
            host = v[1..close];
            var rest = v[(close + 1)..];
            if (rest.Length > 0)
            {
                if (rest[0] != ':') { error = "rdp_gateway 형식이 올바르지 않습니다."; return null; }
                port = rest[1..];
            }
            if (!System.Net.IPAddress.TryParse(host, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
            { error = "rdp_gateway 의 IPv6 주소가 올바르지 않습니다."; return null; }
        }
        else
        {
            if (v.Contains('[') || v.Contains(']') || v.Count(c => c == ':') > 1) { error = "rdp_gateway 형식이 올바르지 않습니다 (IPv6 는 [주소]:포트)."; return null; }
            var colon = v.IndexOf(':');
            host = colon < 0 ? v : v[..colon];
            if (colon >= 0) port = v[(colon + 1)..];
            if (host.Length == 0 || host.StartsWith('.') || host.StartsWith('-')) { error = "rdp_gateway 의 호스트가 올바르지 않습니다."; return null; }
        }
        if (port != null && !(int.TryParse(port, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var p) && p is >= 1 and <= 65535))
        { error = "rdp_gateway 의 포트는 1~65535 입니다."; return null; }
        return v;
    }
}

/// <summary>MCP 호스트 도구의 UI 와 무관한 판단 (테스트 대상)</summary>
internal static class McpHostParity
{
    // update_host 변경 목록 줄의 머리말 (원본 반영 판정이 이 문자열에 기댄다)
    public const string PasswordLabel = "저장된 비밀번호";
    public const string KeyPathLabel = "키 파일 경로";

    /// <summary>HostEntry.ResolveVault 의 출처 → MCP 결과 표시 (번역하지 않음)</summary>
    public static string VaultSourceText(string? source) => source switch
    {
        "host" => "직접",
        "profile" => "프로필 기본",
        "default" => "설정 기본",
        _ => "",
    };

    /// <summary>list_hosts 줄 끝에 붙는 계정 표시 (실제로 쓰이는 Vault 계정이 있을 때만): " | 계정 이름(직접)"</summary>
    public static string AccountSuffix(HostEntry h, CloudProfile? p)
    {
        var (id, source) = h.ResolveVault(p);
        return id != null && VaultStore.Get(id) is { } v ? $" | 계정 {McpHelpers.VisibleControlChars(v.Name)}({VaultSourceText(source)})" : "";
    }

    /// <summary>
    /// 사용자가 고른 카드: Ctrl/Shift+클릭 다중 선택이 있으면 그것(화면 순서), 없으면 키보드로 고른 카드 1개.
    /// 목록에서 사라진 호스트는 뺀다. Kind = "multi" · "highlighted" · "none"
    /// </summary>
    public static (List<HostEntry> Hosts, string Kind) Picked(IReadOnlyList<HostEntry> selectedOrdered, HostEntry? highlighted, ICollection<HostEntry> all)
    {
        var sel = selectedOrdered.Where(all.Contains).Distinct().ToList();
        if (sel.Count > 0) return (sel, "multi");
        if (highlighted != null && all.Contains(highlighted)) return (new List<HostEntry> { highlighted }, "highlighted");
        return (new List<HostEntry>(), "none");
    }

    public static string PickedHeader(int count, string kind) => kind switch
    {
        "multi" => $"선택 {count}개 (Ctrl/Shift+클릭으로 여러 개 고른 카드)",
        "highlighted" => "선택 1개 (키보드·클릭으로 고른 카드 — 여러 개 선택은 없음)",
        _ => "선택 0개 (사용자가 고른 카드가 없습니다)",
    };

    /// <summary>호스트가 속한 화면 그룹의 접힘 상태 저장 키 (RefreshHosts 와 같은 규칙: 클라우드 "C:프로필 id", 직접 등록 "L:정규화한 그룹 이름")</summary>
    public static string GroupStateKey(HostEntry h, bool profileExists, IReadOnlyDictionary<string, string> canon)
    {
        if (h.CloudProfileId is { } pid && profileExists) return "C:" + pid;
        var g = string.IsNullOrWhiteSpace(h.Group) ? "" : h.Group.Trim();
        return "L:" + (canon.TryGetValue(g, out var n) ? n : g);
    }

    /// <summary>저장된 접힘 상태: 클라우드 그룹은 기본 접힘(펼친 것만 기억), 직접 등록 그룹은 기본 펼침(접은 것만 기억)</summary>
    public static bool IsCollapsedState(string key, IReadOnlySet<string> collapsedLocal, IReadOnlySet<string> expandedCloud) =>
        key.StartsWith("C:", StringComparison.Ordinal) ? !expandedCloud.Contains(key) : collapsedLocal.Contains(key);

    /// <summary>update_settings: 기본 포트 변경 때 함께 옮길 클라우드 호스트 수 (move_cloud_hosts=false 면 0 — UI "기본 포트만 바꾸기")</summary>
    public static int PortMoves(bool moveCloudHosts, int affected) => moveCloudHosts ? affected : 0;

    /// <summary>빠른 연결 승인 창·감사 로그의 대상 표시: "SSH user@host:port"</summary>
    public static string QuickTargetLabel(HostEntry h) =>
        $"{h.ProtocolText} {(h.Username.Length > 0 ? McpHelpers.VisibleControlChars(h.Username) + "@" : "")}{h.Address}:{h.Port}";

    /// <summary>JSON 객체에서 몇 개 속성을 뺀 사본 (duplicate_host 가 원본 지정·이름·그룹을 빼고 update_host 인자 처리에 넘길 때)</summary>
    public static JsonElement Without(JsonElement a, params string[] names)
    {
        if (a.ValueKind != JsonValueKind.Object) return a;
        var dict = new Dictionary<string, JsonElement>();
        foreach (var p in a.EnumerateObject())
            if (!names.Contains(p.Name, StringComparer.Ordinal)) dict[p.Name] = p.Value.Clone();
        return JsonSerializer.SerializeToElement(dict);
    }

    /// <summary>
    /// get_host 결과. 비밀번호(암호문 포함)·키 파일 경로·기억한 답 내용은 넣지 않고 있음/없음·개수만.
    /// jump = 점프 서버 표시 이름 (없으면 null), jumpUsers = 이 호스트를 점프 서버로 쓰는 호스트 수
    /// </summary>
    public static string Detail(HostEntry h, CloudProfile? p, string group, string? jump, int jumpUsers, IReadOnlyCollection<int> sessions)
    {
        static string V(string? s) => string.IsNullOrWhiteSpace(s) ? "-" : McpHelpers.VisibleControlChars(s.Trim());
        var sb = new StringBuilder();
        sb.AppendLine($"id: {h.Id}");
        sb.AppendLine($"이름: {V(h.DisplayName)}" + (h.Label.Trim().Length == 0 ? " (이름 없음 — 주소로 표시)" : ""));
        sb.AppendLine($"그룹: {V(group)}");
        sb.AppendLine($"접속: {h.ProtocolText} {h.PrimaryAddress}:{h.Port}" +
                      (h.HasPublicIp ? $" (공인 {h.PublicIp}{(h.UsePublicIp ? ", 공인으로 접속" : ", 사설로 접속")})" : ""));
        if (h.IsCloud)
        {
            sb.AppendLine("클라우드: " + (p != null ? $"[{p.ProviderText}] {V(p.GroupName)} (프로필 {V(p.Name)})" : "(프로필 없음)") +
                          $" · 인스턴스 {V(h.CloudInstanceId)} · 리전 {V(h.CloudRegion)} · 상태 {V(h.CloudState)}");
        }
        if (!string.IsNullOrWhiteSpace(h.Os) || !string.IsNullOrWhiteSpace(h.Purpose))
            sb.AppendLine($"OS: {V(h.Os)} · 용도(클라우드 태그): {V(h.Purpose)}");
        // 접속해 알아낸 배포판 (os-release ID·ID_LIKE 낱말 — 소문자·숫자·._- 만 저장) · 화면의 OS 로고
        if (!string.IsNullOrWhiteSpace(h.OsDistro)) sb.AppendLine($"Linux 배포판(접속 때 /etc/os-release 또는 SSH 배너로 판별): {V(h.OsDistro)}");
        if (ServerOsKind.DisplayName(h.OsKind) is { } osLogo) sb.AppendLine($"OS 로고: {osLogo}");

        // ----- 계정 (값은 넣지 않음) -----
        var (vid, source) = h.ResolveVault(p);
        sb.AppendLine($"사용자명(호스트에 적은 값): {V(h.Username)}");
        sb.AppendLine("Vault 계정: " + (VaultStore.Get(vid) is { } v
            ? $"{V(v.Name)} ({VaultSourceText(source)})" + (v.Username.Length > 0 ? $" · 사용자 {V(v.Username)}" : "")
            : "없음"));
        if (h.VaultId != null && VaultStore.Get(h.VaultId) == null)
            sb.AppendLine("  직접 고른 Vault 계정이 지워져 있음 (호스트 값 → 기본 계정 순으로 쓰임)");
        sb.AppendLine($"접속 계정 출처: {McpHelpers.CredentialSourceForAudit(h, p) ?? "없음 (접속할 때 묻는다)"}");
        var hasPw = !string.IsNullOrEmpty(h.ProtectedPassword);
        var hasKey = !string.IsNullOrWhiteSpace(h.KeyPath);
        sb.AppendLine($"{McpHostParity.PasswordLabel}: {(hasPw ? "있음" : "없음")} · {KeyPathLabel}: {(hasKey ? "있음" : "없음")}" +
                      (source == "host" && (hasPw || hasKey) ? " (직접 고른 Vault 계정이 우선이라 지금은 쓰이지 않음)" : ""));
        sb.AppendLine($"기억한 인증 답: {h.PromptAnswers?.Count ?? 0}개");

        // ----- 경로 · 프로토콜 전용 -----
        if (h.Protocol == Protocol.Ssh)
        {
            sb.AppendLine($"점프 서버: {jump ?? "없음"}");
            sb.AppendLine($"{McpHostRules.ForwardsLabel}(저장된 규칙): {McpHostRules.ForwardsSummary(h.PortForwards) ?? "없음"}");
            if (!string.IsNullOrWhiteSpace(h.SshBannerOs)) sb.AppendLine($"서버 OS(SSH 배너로 판별): {V(h.SshBannerOs)}");
        }
        if (h.Protocol == Protocol.Rdp)
        {
            sb.AppendLine($"RD 게이트웨이: {h.RdpGateway ?? "없음 (mstsc 기본 설정)"}");
            sb.AppendLine($"NLA(CredSSP): {(h.RdpDisableNla ? "끔" : "켬")}");
        }
        if (jumpUsers > 0) sb.AppendLine($"이 호스트를 점프 서버로 쓰는 호스트: {jumpUsers}개");
        sb.AppendLine($"태그: {(h.TagsText.Length > 0 ? McpHelpers.VisibleControlChars(h.TagsText) : "없음")}");
        sb.AppendLine($"즐겨찾기: {(h.Favorite ? "예" : "아니오")}");
        sb.AppendLine($"마지막 접속: {(h.LastConnected is { } last ? last.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) : "없음")}");
        sb.Append($"열린 탭: {(sessions.Count > 0 ? string.Join(", ", sessions.Select(s => "session " + s)) : "없음")}");
        return sb.ToString();
    }
}
