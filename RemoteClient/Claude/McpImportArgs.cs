using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Nubis;

/// <summary>import_hosts 의 출처</summary>
internal enum McpImportSource { Table, SshConfig, MRemoteNg, Ansible, Ad }

/// <summary>import_hosts 인자 (McpImport.ParseArgs 가 검사해서 만든다)</summary>
internal sealed record McpImportArgs
{
    public McpImportSource Source { get; init; }
    /// <summary>붙여 넣은 내용 (table · ansible · ssh_config · mremoteng)</summary>
    public string? Text { get; init; }
    /// <summary>파일 경로 (table 은 CSV/TSV 텍스트 파일)</summary>
    public string? Path { get; init; }

    // ----- Active Directory -----
    public string? Domain { get; init; }
    public string? Server { get; init; }
    public string? SearchBase { get; init; }
    public bool ServersOnly { get; init; } = true;
    public bool SkipDisabled { get; init; } = true;
    public int? ActiveWithinDays { get; init; }
    public bool ResolveIp { get; init; }
    public bool UseCurrentUser { get; init; } = true;
    /// <summary>AD 조회에 쓸 Vault 계정 (이름·id). 지정하면 UseCurrentUser = false</summary>
    public string? AdVault { get; init; }

    // ----- 등록 옵션 -----
    public string? GroupPrefix { get; init; }
    /// <summary>null 이 아니면 모두 이 그룹으로 ("" = 그룹 없음)</summary>
    public string? Group { get; init; }
    public string? SshVault { get; init; }
    public string? RdpVault { get; init; }
    public bool UpdateExisting { get; init; }
    public List<string> Select { get; init; } = new();
    public bool Apply { get; init; }
}

/// <summary>
/// import_hosts MCP 도구의 UI 와 무관한 부분: 인자 검사 · 줄 고르기 · 결과 표 · 경로 차단 (McpImportToolTests).
/// 메시지는 Claude 만 읽으므로 번역하지 않는다 (승인 창에 보이는 대상 줄의 "외 N개" 만 번역).
/// </summary>
internal static class McpImport
{
    /// <summary>붙여 넣은 내용 최대 길이 (문자)</summary>
    public const int MaxTextLength = 5_000_000;
    /// <summary>읽을 파일 최대 크기</summary>
    public const long MaxFileBytes = 20L * 1024 * 1024;
    /// <summary>select 최대 개수</summary>
    public const int MaxSelect = 5000;
    /// <summary>미리보기 표에 보여 줄 최대 줄 수</summary>
    public const int MaxPreviewRows = 200;
    /// <summary>승인 창 대상 목록에 보여 줄 최대 줄 수</summary>
    public const int MaxApprovalTargets = 30;

    private static readonly string[] AdKeys =
        { "domain", "server", "search_base", "servers_only", "skip_disabled", "active_within_days", "resolve_ip", "use_current_user", "ad_vault" };

    private static readonly HashSet<string> AllowedKeys = new(StringComparer.Ordinal)
    {
        "source", "text", "path", "domain", "server", "search_base", "servers_only", "skip_disabled", "active_within_days", "resolve_ip",
        "use_current_user", "ad_vault", "group_prefix", "group", "ssh_vault", "rdp_vault", "update_existing", "select", "apply",
    };

    public static string SourceName(McpImportSource s) => s switch
    {
        McpImportSource.Table => "table",
        McpImportSource.SshConfig => "ssh_config",
        McpImportSource.MRemoteNg => "mremoteng",
        McpImportSource.Ansible => "ansible",
        _ => "ad",
    };

    /// <summary>인자를 엄격하게 검사한다 (모르는 인자 · 형식 오류 · 출처에 맞지 않는 인자는 McpToolException)</summary>
    public static McpImportArgs ParseArgs(JsonElement a)
    {
        if (a.ValueKind != JsonValueKind.Object) throw new McpToolException("인자는 객체여야 합니다.");
        foreach (var p in a.EnumerateObject())
        {
            if (AllowedKeys.Contains(p.Name)) continue;
            if (p.Name.Contains("password", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
                p.Name.Equals("pw", StringComparison.OrdinalIgnoreCase))
                throw new McpToolException($"'{p.Name}' 인자는 받지 않습니다. 비밀번호는 MCP 로 넘기지 않습니다 — AD 조회 계정은 ad_vault 에 Vault 계정 이름을 지정하세요.");
            throw new McpToolException($"알 수 없는 인자 '{p.Name}'. 쓸 수 있는 인자: {string.Join(", ", AllowedKeys.OrderBy(k => k, StringComparer.Ordinal))}");
        }

        var source = (Str(a, "source") ?? "").Trim().ToLowerInvariant() switch
        {
            "table" => McpImportSource.Table,
            "ssh_config" => McpImportSource.SshConfig,
            "mremoteng" => McpImportSource.MRemoteNg,
            "ansible" => McpImportSource.Ansible,
            "ad" => McpImportSource.Ad,
            "" => throw new McpToolException("source 가 필요합니다 (table / ssh_config / mremoteng / ansible / ad)."),
            var s => throw new McpToolException($"source '{s}' 는 지원하지 않습니다 (table / ssh_config / mremoteng / ansible / ad)."),
        };

        var text = Str(a, "text");
        var path = Str(a, "path")?.Trim().Trim('"');
        if (path is { Length: 0 }) path = null;
        if (text != null && text.Length > MaxTextLength) throw new McpToolException($"text 가 너무 깁니다 (최대 {MaxTextLength:N0}자).");
        if (text != null && text.Trim().Length == 0) throw new McpToolException("text 가 비어 있습니다.");

        if (source == McpImportSource.Ad)
        {
            if (text != null || path != null) throw new McpToolException("source=ad 에는 text / path 를 쓰지 않습니다 (domain · server 등으로 조회).");
        }
        else
        {
            var adGiven = AdKeys.Where(k => Has(a, k)).ToList();
            if (adGiven.Count > 0) throw new McpToolException($"{string.Join(", ", adGiven)} 는 source=ad 에서만 씁니다.");
            if (text != null && path != null) throw new McpToolException("text 와 path 중 하나만 지정하세요.");
            if (text == null && path == null && source is McpImportSource.Table or McpImportSource.Ansible)
                throw new McpToolException(source == McpImportSource.Table
                    ? "source=table 에는 text(붙여 넣은 표) 또는 path(CSV/TSV 파일)가 필요합니다."
                    : "source=ansible 에는 text(인벤토리 내용) 또는 path(인벤토리 파일)가 필요합니다.");
            if (path != null && source == McpImportSource.Table &&
                System.IO.Path.GetExtension(path).ToLowerInvariant() is ".xlsx" or ".xls" or ".xlsm" or ".xlsb" or ".ods")
                throw new McpToolException("엑셀 파일(.xlsx 등)은 직접 읽지 않습니다. CSV/TSV 로 저장한 파일을 path 로 주거나, 표 내용을 text 로 넘기세요.");
        }

        var adVault = Str(a, "ad_vault")?.Trim();
        if (adVault is { Length: 0 }) adVault = null;
        var useCurrent = Bool(a, "use_current_user");
        if (adVault != null && useCurrent == true) throw new McpToolException("ad_vault 를 지정하면 use_current_user=true 와 함께 쓸 수 없습니다.");
        if (source == McpImportSource.Ad && adVault == null && useCurrent == false)
            throw new McpToolException("use_current_user=false 이면 ad_vault(조회에 쓸 Vault 계정)가 필요합니다. 비밀번호 인자는 받지 않습니다.");

        var days = Int(a, "active_within_days");
        if (days is < 1 or > 3650) throw new McpToolException("active_within_days 는 1~3650 입니다.");

        var group = Str(a, "group");
        var prefix = Str(a, "group_prefix")?.Trim();
        if (prefix is { Length: 0 }) prefix = null;
        if (group != null && prefix != null) throw new McpToolException("group 과 group_prefix 중 하나만 지정하세요.");

        var select = StrList(a, "select");
        if (select.Count > MaxSelect) throw new McpToolException($"select 는 최대 {MaxSelect}개입니다.");

        return new McpImportArgs
        {
            Source = source,
            Text = text,
            Path = path,
            Domain = Trimmed(Str(a, "domain")),
            Server = Trimmed(Str(a, "server")),
            SearchBase = Trimmed(Str(a, "search_base")),
            ServersOnly = Bool(a, "servers_only") ?? true,
            SkipDisabled = Bool(a, "skip_disabled") ?? true,
            ActiveWithinDays = days,
            ResolveIp = Bool(a, "resolve_ip") ?? false,
            UseCurrentUser = adVault == null,
            AdVault = adVault,
            GroupPrefix = prefix,
            Group = group?.Trim(),
            SshVault = Trimmed(Str(a, "ssh_vault")),
            RdpVault = Trimmed(Str(a, "rdp_vault")),
            UpdateExisting = Bool(a, "update_existing") ?? false,
            Select = select,
            Apply = Bool(a, "apply") ?? false,
        };
    }

    private static string? Trimmed(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static bool Has(JsonElement a, string name) =>
        a.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null;

    private static string? Str(JsonElement a, string name)
    {
        if (!a.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind != JsonValueKind.String) throw new McpToolException($"{name} 은 문자열이어야 합니다.");
        return v.GetString() ?? "";
    }

    private static bool? Bool(JsonElement a, string name)
    {
        if (!a.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind is JsonValueKind.True or JsonValueKind.False) return v.GetBoolean();
        if (v.ValueKind == JsonValueKind.String && bool.TryParse((v.GetString() ?? "").Trim(), out var b)) return b;
        throw new McpToolException($"{name} 은 true / false 여야 합니다.");
    }

    private static int? Int(JsonElement a, string name)
    {
        if (!Has(a, name)) return null;
        if (McpHelpers.NumberArg(a, name) is { } d && d % 1 == 0 && d is >= int.MinValue and <= int.MaxValue) return (int)d;
        throw new McpToolException($"{name} 은 정수여야 합니다.");
    }

    private static List<string> StrList(JsonElement a, string name)
    {
        if (!a.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return new();
        IEnumerable<JsonElement> items = v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : new[] { v };
        var list = new List<string>();
        foreach (var x in items)
        {
            if (x.ValueKind != JsonValueKind.String) throw new McpToolException($"{name} 은 문자열 배열이어야 합니다.");
            var s = (x.GetString() ?? "").Trim();
            if (s.Length > 0) list.Add(s);
        }
        return list;
    }

    // ───────────── 줄 고르기 ─────────────

    /// <summary>등록 대상이 될 수 있는 줄: 새 호스트, update_existing 이면 클라우드가 아닌 기존 호스트도</summary>
    public static bool IsCandidate(ImportRow r, bool updateExisting) =>
        r.Status == ImportStatus.New || (updateExisting && r.Status == ImportStatus.Existing && r.IsSelectable);

    /// <summary>
    /// select 로 줄 고르기. select 가 비어 있으면 후보 전체.
    /// 각 항목은 이름(표시 이름 · 원본 이름) · 주소 · "주소:포트" 와 대소문자 무시로 정확히 비교한다.
    /// 후보 중 어느 줄과도 맞지 않는 항목은 unmatched 로 돌려준다 (호출한 쪽이 오류로 알림).
    /// </summary>
    public static (List<ImportRow> Selected, List<string> Unmatched) SelectRows(IReadOnlyList<ImportRow> rows, IReadOnlyList<string> select, bool updateExisting)
    {
        var candidates = rows.Where(r => IsCandidate(r, updateExisting)).ToList();
        if (select.Count == 0) return (candidates, new List<string>());

        var picked = new HashSet<ImportRow>(ReferenceEqualityComparer.Instance);
        var unmatched = new List<string>();
        foreach (var spec in select)
        {
            var hits = candidates.Where(r => RowKeys(r).Any(k => k.Equals(spec, StringComparison.OrdinalIgnoreCase))).ToList();
            if (hits.Count == 0) unmatched.Add(spec);
            foreach (var h in hits) picked.Add(h);
        }
        // 원래 순서 유지
        return (candidates.Where(picked.Contains).ToList(), unmatched);
    }

    private static IEnumerable<string> RowKeys(ImportRow r)
    {
        var keys = new[] { r.NameText, r.Source.Name, r.AddressText, r.Source.Address, $"{r.AddressText}:{r.PortText}" };
        return keys.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim());
    }

    /// <summary>select 의 항목이 어떤 줄과도 맞지 않을 때 Claude 에 줄 설명 (어느 상태의 줄과 맞는지 알려 줌)</summary>
    public static string UnmatchedMessage(IReadOnlyList<ImportRow> rows, IReadOnlyList<string> unmatched, bool updateExisting)
    {
        var sb = new StringBuilder($"select 의 {unmatched.Count}개 항목이 등록할 수 있는 줄과 맞지 않습니다:\n");
        foreach (var spec in unmatched.Take(50))
        {
            var other = rows.FirstOrDefault(r => RowKeys(r).Any(k => k.Equals(spec, StringComparison.OrdinalIgnoreCase)));
            var why = other == null ? "가져온 목록에 없음 (이름·주소·주소:포트로 정확히 지정)"
                : other.Status == ImportStatus.Existing && !updateExisting ? "이미 목록에 있음 (갱신하려면 update_existing=true)"
                : other.Status == ImportStatus.Existing ? "클라우드 동기화 호스트라 바꿀 수 없음"
                : $"{other.Status} 줄이라 등록할 수 없음";
            sb.AppendLine($"- {spec}: {why}");
        }
        if (unmatched.Count > 50) sb.AppendLine($"… 외 {unmatched.Count - 50}개");
        return sb.ToString().TrimEnd();
    }

    // ───────────── 결과 표 ─────────────

    private static string Cell(string? s, int max = 80)
    {
        var t = (s ?? "").Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
        return t.Length > max ? t[..(max - 1)] + "…" : t;
    }

    /// <summary>상태별 개수 한 줄</summary>
    public static string StatusCounts(IReadOnlyList<ImportRow> rows) =>
        $"New {rows.Count(r => r.Status == ImportStatus.New)} · Existing {rows.Count(r => r.Status == ImportStatus.Existing)} · " +
        $"Duplicate {rows.Count(r => r.Status == ImportStatus.Duplicate)} · Invalid {rows.Count(r => r.Status == ImportStatus.Invalid)}";

    /// <summary>
    /// 미리보기 결과 (dry run). sourceLabel 은 출처 설명, selected 는 apply=true 로 부르면 등록될 줄.
    /// 줄은 maxRows 까지만 보여 주고 나머지는 개수만.
    /// </summary>
    public static string FormatPreview(string sourceLabel, IReadOnlyList<ImportRow> rows, IReadOnlyCollection<ImportRow> selected,
        IReadOnlyList<string> warnings, bool updateExisting, int maxRows = MaxPreviewRows)
    {
        var picked = new HashSet<ImportRow>(selected, ReferenceEqualityComparer.Instance);
        var sb = new StringBuilder();
        sb.AppendLine($"미리보기 (아직 등록하지 않음) — 출처: {sourceLabel}");
        sb.AppendLine($"읽은 서버 {rows.Count}대 · {StatusCounts(rows)}");
        var add = selected.Count(r => r.Status == ImportStatus.New);
        var upd = selected.Count(r => r.Status == ImportStatus.Existing);
        sb.AppendLine($"apply=true 로 부르면: 추가 {add}개 · 갱신 {upd}개" +
                      (updateExisting ? "" : " (Existing 은 update_existing=true 일 때만 이름·그룹·사용자명 갱신)"));
        AppendWarnings(sb, warnings);
        if (rows.Count > 0)
        {
            sb.AppendLine("* = 등록 대상");
            sb.AppendLine("sel | 이름 | 주소:포트 | 종류 | 그룹 | 사용자 | 상태 | 메모");
            foreach (var r in rows.Take(maxRows))
                sb.AppendLine($"{(picked.Contains(r) ? "*" : " ")} | {Cell(r.NameText)} | {Cell(r.AddressText)}:{r.PortText} | {r.ProtocolText} | " +
                              $"{Cell(r.GroupText)} | {Cell(r.UserText)} | {r.Status} | {Cell(r.DetailText, 160)}");
            if (rows.Count > maxRows) sb.AppendLine($"… 외 {rows.Count - maxRows}줄 (select 로 이름·주소를 지정하면 그 줄만 등록)");
        }
        sb.Append(add + upd > 0
            ? "등록하려면 사용자에게 위 내용을 확인받은 뒤 같은 인자에 apply=true 를 더해 다시 호출하세요 (일부만이면 select 에 이름·주소 목록). 앱이 사용자 승인을 받습니다."
            : "등록할 줄이 없습니다.");
        return sb.ToString();
    }

    private static void AppendWarnings(StringBuilder sb, IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0) return;
        sb.AppendLine($"경고 {warnings.Count}개:");
        foreach (var w in warnings.Take(30)) sb.AppendLine("- " + Cell(w, 300));
        if (warnings.Count > 30) sb.AppendLine($"… 외 {warnings.Count - 30}개");
    }

    /// <summary>등록 결과</summary>
    public static string FormatApplied(ImportApplyResult r, IReadOnlyList<string> parserWarnings, int changedWhileWaiting)
    {
        var sb = new StringBuilder($"가져오기 완료: 추가 {r.Added}개 · 갱신 {r.Updated}개 · 건너뜀 {r.Skipped}개 (건너뜀 = 고르지 않음·중복·오류·바뀐 값 없음 포함)\n");
        if (changedWhileWaiting > 0)
            sb.AppendLine($"승인을 기다리는 동안 목록이 바뀌어 {changedWhileWaiting}개는 등록·갱신하지 않았습니다 (이미 같은 호스트가 생겼거나 갱신 대상이 지워짐).");
        AppendWarnings(sb, r.Warnings.Concat(parserWarnings).ToList());
        return sb.ToString().TrimEnd();
    }

    /// <summary>승인 창 대상 목록: "+ 이름  (SSH 주소:포트)" / "~ 이름 ..." 을 max 개까지, 나머지는 "… 외 N개"</summary>
    public static List<string> ApprovalTargets(IReadOnlyList<ImportRow> selected, int max = MaxApprovalTargets)
    {
        var lines = selected.Take(max).Select(r =>
            $"{(r.Status == ImportStatus.Existing ? "~" : "+")} {r.NameText}  ({r.ProtocolText} {r.AddressText}:{r.PortText})" +
            (r.GroupText.Length > 0 ? $"  [{r.GroupText}]" : "")).ToList();
        if (selected.Count > max) lines.Add(L.F("… 외 {0}개", selected.Count - max));
        return lines;
    }

    // ───────────── 경로 검사 ─────────────

    /// <summary>
    /// 가져오기로 읽을 로컬 파일 경로 검사 — 결과(미리보기 표 · 경고)에 파일 내용 일부가 나오므로
    /// Nubis 데이터 폴더(계정 · Vault · MCP 토큰)와 개인키는 읽지 않는다. ~/.ssh 는 ssh_config 출처만 허용.
    /// 장치 경로 · 이 PC 를 가리키는 UNC · 대체 데이터 스트림도 거부. 문제 없으면 null.
    /// </summary>
    public static string? BlockedPathReason(string path, string dataDir, string? rootDir, string sshDir, bool allowSshDir)
    {
        var trimmed = path.Trim();
        if (trimmed.StartsWith(@"\\?\", StringComparison.Ordinal) || trimmed.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            trimmed.StartsWith(@"\??\", StringComparison.Ordinal) || trimmed.StartsWith("//", StringComparison.Ordinal))
            return "장치 경로는 쓸 수 없습니다. 드라이브 경로(C:\\...)를 쓰세요.";
        if (McpHelpers.IsLocalOrAdminUnc(trimmed)) return "이 PC 를 가리키는 네트워크 경로나 관리 공유($)는 쓸 수 없습니다.";
        if (trimmed.Length > 2 && trimmed.IndexOf(':', 2) >= 0) return "잘못된 경로입니다.";
        // 네트워크 경로(UNC)는 Claude 가 정한 서버에 이 PC 의 Windows 자격 증명(NTLM)으로 접속하게 되므로 읽지 않는다.
        // "/\srv\share" · "\/srv" 처럼 / 와 \ 를 섞어도 Windows 는 UNC 로 해석하므로 두 글자 모두 구분자인지로 본다
        if (IsUncLike(trimmed)) return "네트워크 경로(UNC)의 파일은 읽을 수 없습니다. 이 PC 로 복사한 뒤 드라이브 경로(C:\\...)를 쓰세요.";
        string full;
        try { full = System.IO.Path.GetFullPath(trimmed); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return "잘못된 경로입니다."; }
        // 정규화 결과로 한 번 더 (파일을 여는 FinalPath 보다 먼저 — 여는 순간 접속이 일어남)
        if (IsUncLike(full)) return "네트워크 경로(UNC)의 파일은 읽을 수 없습니다. 이 PC 로 복사한 뒤 드라이브 경로(C:\\...)를 쓰세요.";
        var final = McpHelpers.FinalPath(full);
        // 로컬 심볼릭 링크 · 정션이 네트워크 공유를 가리키는 경우
        if (IsUncLike(final)) return "네트워크 경로로 이어지는 링크는 읽을 수 없습니다.";
        if (McpHelpers.IsLocalOrAdminUnc(final)) return "이 PC 를 가리키는 네트워크 경로로 이어지는 링크는 쓸 수 없습니다.";

        static bool Under(string p, string dir)
        {
            var d = dir.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            return p.StartsWith(d, StringComparison.OrdinalIgnoreCase) || p.Equals(d.TrimEnd(System.IO.Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }
        static bool UnderAny(string full, string final, string dir)
        {
            string d;
            try { d = System.IO.Path.GetFullPath(dir); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
            var dFinal = McpHelpers.FinalPath(d);
            return Under(full, d) || Under(final, d) || Under(full, dFinal) || Under(final, dFinal);
        }

        if (UnderAny(full, final, dataDir) || (rootDir != null && UnderAny(full, final, rootDir)))
            return "Nubis 데이터 폴더(계정·Vault·설정)의 파일은 읽을 수 없습니다.";
        foreach (var p in new[] { full, final })
        {
            var name = System.IO.Path.GetFileName(p).ToLowerInvariant();
            if ((name.StartsWith("id_", StringComparison.Ordinal) && !name.EndsWith(".pub", StringComparison.Ordinal)) ||
                name.EndsWith(".ppk", StringComparison.Ordinal) || name.EndsWith(".pem", StringComparison.Ordinal) ||
                name.EndsWith(".key", StringComparison.Ordinal) || name.EndsWith(".pfx", StringComparison.Ordinal) ||
                name.EndsWith(".p12", StringComparison.Ordinal))
                return "개인키로 보이는 파일은 읽을 수 없습니다.";
        }
        if (!allowSshDir && UnderAny(full, final, sshDir)) return "~/.ssh 폴더의 파일은 source=ssh_config 로만 읽을 수 있습니다.";
        return null;
    }

    /// <summary>
    /// Include(ssh_config) · group_vars(ansible) 처럼 파서가 따라 읽는 파일 검사. BlockedPathReason 에 더해 네트워크 경로(UNC)도 막는다 —
    /// Claude 가 넘긴 text 의 "Include \\서버\공유\x" 로 이 PC 가 외부 SMB 서버에 Windows 자격 증명(NTLM)으로 접속하지 않게.
    /// path 인자와 달리 사용자가 경로를 직접 보지 못하는 곳이라 더 엄격하게 본다. 문제 없으면 null
    /// </summary>
    public static string? BlockedFollowPathReason(string path, string dataDir, string? rootDir, string sshDir, bool allowSshDir)
    {
        var t = path.Trim();
        if (IsUncLike(t)) return "네트워크 경로는 따라 읽지 않습니다.";
        return BlockedPathReason(t, dataDir, rootDir, sshDir, allowSshDir);
    }

    /// <summary>앞 두 글자가 모두 경로 구분자(\ 또는 /) — Windows 가 UNC · 장치 경로로 해석하는 형태</summary>
    internal static bool IsUncLike(string path) =>
        path.Length >= 2 && path[0] is '\\' or '/' && path[1] is '\\' or '/';

    /// <summary>개인키 내용이 들어 있으면 true (파일 이름과 무관하게 내용으로 한 번 더 막는다)</summary>
    public static bool LooksLikePrivateKey(string text) =>
        text.Contains("PRIVATE KEY-----", StringComparison.Ordinal) || text.Contains("PuTTY-User-Key-File", StringComparison.Ordinal);
}
