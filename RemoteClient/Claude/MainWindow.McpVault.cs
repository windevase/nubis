using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace Nubis;

// MCP 도구: Vault · Known Hosts (비밀 값은 다루지 않음) — UI 에서 손으로 하는 동작을 Claude 도 할 수 있게 (BuildTools 에서 등록)
// 보안 원칙:
// - 비밀번호·키·키 암호는 어떤 도구로도 넣거나 읽지 않는다 (공개키는 비밀이 아니라 돌려준다 — 감사 로그는 남김)
// - 계정 정보(이름·사용자명·메모) 변경도 승인 — 사용자명이 바뀌면 그 계정으로 로그인하는 서버의 계정이 바뀜
// - 기본 계정 지정·계정 삭제·호스트키 삭제는 항상 승인 (어느 서버에 어떤 계정이 가는지, 어떤 키를 신뢰하는지가 바뀜)
// - 거부(REJECTED)로 표시된 호스트키는 지우지 않는다 — 나쁜 키를 막으려고 남겨 둔 것
public partial class MainWindow
{
    private IEnumerable<McpTool> VaultTools()
    {
        yield return new McpTool("vault_set_default",
            "설정의 기본 Vault 계정(SSH 또는 RDP)을 정하거나 해제한다. 계정을 따로 정하지 않은 모든 해당 프로토콜 호스트가 이 계정으로 로그인하므로 " +
            "앱이 사용자 승인을 받는다. account 는 Vault 계정 이름(정확히) 또는 id(8자 이상), \"none\" = 기본 계정 해제.",
            Schema(new
            {
                protocol = new { type = "string", @enum = new[] { "ssh", "rdp" } },
                account = new { type = "string", description = "Vault 계정 이름 또는 id (list_vault). \"none\" = 해제" },
            }, "protocol", "account"),
            a => OnUiAsync(() => VaultSetDefaultFromClaude(Arg(a, "protocol"), Arg(a, "account"))));

        yield return new McpTool("vault_get_public_key",
            "SSH 키 Vault 계정의 공개키 한 줄(서버 ~/.ssh/authorized_keys 에 넣는 값)을 돌려준다. 공개키는 비밀이 아니다. 개인키·키 암호·비밀번호는 절대 돌려주지 않는다.",
            Schema(new { account = new { type = "string", description = "Vault 계정 이름 또는 id" } }, "account"),
            a => OnUi(() =>
            {
                // 부분 일치로 엉뚱한 계정의 공개키를 서버에 넣게 하지 않도록 이름 정확히 · id 로만
                var v = ResolveVaultStrict(Arg(a, "account"));
                if (!v.HasKey || string.IsNullOrEmpty(v.PublicKey))
                    throw new McpToolException($"Vault 계정 '{McpHelpers.VisibleControlChars(v.Name)}' 에는 SSH 키(공개키)가 없습니다.");
                // 저장값이 "형식 base64(공개키 blob)" 한 줄이 아니면 돌려주지 않는다 (손상·예전 형식 값에 개인키 등이 섞여 나가지 않게)
                var pub = SafePublicKeyLine(v.PublicKey)
                          ?? throw new McpToolException($"Vault 계정 '{McpHelpers.VisibleControlChars(v.Name)}' 의 저장된 공개키 형식이 올바르지 않습니다. 사용자에게 Vault 에서 키를 다시 넣어 달라고 안내하세요.");
                var line = $"{pub} {PublicKeyComment(v.Name)}";
                AuditLog.Write("vault_get_public_key", new[] { McpHelpers.VisibleControlChars(v.Name) }, "(공개키 조회)", "조회");
                return line;
            }));

        yield return new McpTool("vault_delete",
            "Vault 계정을 지운다. 이 계정을 쓰는 호스트(직접 연결·기본 계정으로 사용), 클라우드 프로필 기본 계정, 설정의 기본 계정 연결이 함께 해제된다. " +
            "앱이 영향받는 목록을 보여 주며 사용자 승인을 받는다. account 는 이름(정확히) 또는 id(8자 이상).",
            Schema(new { account = new { type = "string", description = "Vault 계정 이름 또는 id" } }, "account"),
            a => OnUiAsync(() => VaultDeleteFromClaude(Arg(a, "account"))));

        yield return new McpTool("vault_update",
            "Vault 계정의 비밀이 아닌 값(이름 · 사용자명 · 메모)을 바꾼다. 바꿀 항목만 넘긴다 (빈 문자열 = 사용자명·메모 지우기, 이름은 비울 수 없음). " +
            "사용자명을 바꾸면 이 계정으로 로그인하는 서버의 로그인 계정이 바뀌므로 앱이 영향받는 호스트 수와 함께 사용자 승인을 받는다 (이름·메모만 바꿔도 승인). " +
            "비밀번호·SSH 키·키 암호는 넣거나 지울 수 없다 — open_app_page vault_edit:<계정> 으로 창을 열어 사용자가 직접 하게 하세요. " +
            "account 는 이름(정확히) 또는 id(8자 이상).",
            Schema(new
            {
                account = new { type = "string", description = "Vault 계정 이름 또는 id (list_vault)" },
                name = new { type = "string", description = "새 이름 (다른 계정과 겹치면 안 됨)" },
                username = new { type = "string", description = "새 로그인 사용자명 (빈 문자열 = 지움)" },
                note = new { type = "string", description = "새 메모 (빈 문자열 = 지움)" },
            }, "account"),
            a => OnUiAsync(() => VaultUpdateFromClaude(a)));

        yield return new McpTool("list_known_hosts",
            "저장된 SSH 호스트키 목록 (host:port | 키 형식 | 지문 | 상태). 상태 rejected = 사용자가 거부한 키(이 키를 보내는 서버에는 접속하지 않음).",
            Schema(new { }),
            _ => OnUi(() =>
            {
                var all = KnownHostStore.Load();
                AuditLog.Write("list_known_hosts", new[] { "known_hosts" }, $"(호스트키 {all.Count}개 조회)", "조회");
                var sb = new StringBuilder();
                if (KnownHostStore.LoadFailed) sb.AppendLine("주의: known_hosts.json 을 읽지 못했습니다. 아래는 이번 실행에서 신뢰한 키뿐입니다.");
                if (all.Count == 0) return sb.Append("저장된 호스트키가 없습니다.").ToString();
                sb.AppendLine("host:port | 키 형식 | 지문 | 상태");
                foreach (var kv in all.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var (type, fp, rejected) = SplitKnownHost(kv.Value);
                    sb.AppendLine($"{McpHelpers.VisibleControlChars(kv.Key)} | {(type.Length > 0 ? McpHelpers.VisibleControlChars(type) : "-")} | " +
                                  $"{McpHelpers.VisibleControlChars(fp)} | {(rejected ? "rejected" : "trusted")}");
                }
                return sb.ToString();
            }));

        yield return new McpTool("delete_known_host",
            "저장된 SSH 호스트키 1개를 지운다 (서버를 다시 설치해 키가 바뀐 경우). 지우면 다음 접속 때 서버가 보내는 키를 확인 없이 새로 신뢰하므로 " +
            "앱이 경고와 함께 사용자 승인을 받는다. 거부(rejected)로 표시된 키는 지울 수 없다. host 는 list_known_hosts 의 host:port.",
            Schema(new { host = new { type = "string", description = "host:port (port 를 생략하면 그 호스트의 항목이 하나일 때만)" } }, "host"),
            a => OnUiAsync(() => DeleteKnownHostFromClaude(Arg(a, "host"))));
    }

    /// <summary>vault_update 로 바꿀 값 (null = 그대로)</summary>
    internal sealed record VaultEdit(string? Name, string? Username, string? Note);

    /// <summary>vault_update 검사 결과: 실제로 바뀌는 값만 (그대로인 항목은 null). Error 가 있으면 바꾸지 않는다</summary>
    internal sealed record VaultEditPlan(string? Name, string? Username, string? Note, string? Error)
    {
        public bool Empty => Error == null && Name == null && Username == null && Note == null;
    }

    internal const int VaultNameMax = 128, VaultUsernameMax = 256, VaultNoteMax = 1000;

    /// <summary>
    /// vault_update 검사 (UI 무관 — VaultEntryDialog 저장과 같은 규칙): 앞뒤 공백 제거, 이름은 비울 수 없고 이름을 바꿀 때만 중복 검사
    /// (예전부터 같은 이름이던 계정도 그대로 둘 수 있게 — 대소문자만 바꾸는 것은 중복 아님). 보이지 않는 제어·양방향 문자는
    /// 목록·승인 창에서 다른 계정처럼 꾸밀 수 있어 거부. 메모는 Vault 화면의 한 줄 입력란과 같게 줄 바꿈 불가
    /// </summary>
    internal static VaultEditPlan PlanVaultEdit(VaultEntry cur, VaultEdit edit, Func<string, Guid, bool> nameTaken)
    {
        string? Check(string? v, string field, int max, out string? error)
        {
            error = null;
            if (v == null) return null;
            v = v.Trim();
            if (McpHelpers.HasInvisibleChars(v)) error = $"{field} 에 보이지 않는 제어 문자·양방향 문자가 있습니다: {McpHelpers.VisibleControlChars(v)}";
            else if (v.Length > max) error = $"{field} 는 {max}자 이하여야 합니다.";
            return v;
        }
        var name = Check(edit.Name, "name", VaultNameMax, out var e1);
        var user = Check(edit.Username, "username", VaultUsernameMax, out var e2);
        var note = Check(edit.Note, "note", VaultNoteMax, out var e3);
        if ((e1 ?? e2 ?? e3) is { } err) return new(null, null, null, err);
        if (name != null && name.Length == 0) return new(null, null, null, "name 은 비울 수 없습니다.");
        if (name != null && name == cur.Name) name = null;
        if (name != null && !string.Equals(name, cur.Name.Trim(), StringComparison.CurrentCultureIgnoreCase) && nameTaken(name, cur.Id))
            return new(null, null, null, $"같은 이름의 Vault 계정 '{McpHelpers.VisibleControlChars(name)}' 이(가) 이미 있습니다. 다른 이름을 쓰세요.");
        if (user != null && user == cur.Username) user = null;
        if (note != null && note == cur.Note) note = null;
        return new(name, user, note, null);
    }

    /// <summary>
    /// 접속할 때 실제로 쓰는 사용자명 (VaultStore.Apply · HostEntry.WithDefaults 와 같은 우선순위).
    /// preferVault = 호스트에 직접 고른 계정(계정 값 우선, 비었으면 호스트 값), 아니면 기본 계정(호스트 값 우선). 그래도 비면 프로필 기본 사용자명
    /// </summary>
    internal static string EffectiveUsername(bool preferVault, string? hostUser, string? vaultUser, string? profileUser)
    {
        var u = preferVault
            ? (!string.IsNullOrWhiteSpace(vaultUser) ? vaultUser : hostUser)
            : (!string.IsNullOrWhiteSpace(hostUser) ? hostUser : vaultUser);
        return string.IsNullOrEmpty(u) ? profileUser ?? "" : u!;
    }

    /// <summary>Vault 계정의 사용자명을 바꾸면 접속 사용자명이 실제로 바뀌는 호스트 (이 계정을 직접 고르거나 기본 계정으로 쓰는 호스트 중)</summary>
    private List<HostEntry> HostsWithUsernameChange(Guid id, string oldUser, string newUser)
    {
        var profiles = _profiles.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
        var list = new List<HostEntry>();
        foreach (var h in _hosts)
        {
            var p = h.CloudProfileId is { } pid ? profiles.GetValueOrDefault(pid) : null;
            var (vid, source) = h.ResolveVault(p);
            if (vid != id) continue;
            var prefer = source == "host";
            if (EffectiveUsername(prefer, h.Username, oldUser, p?.DefaultUsername) != EffectiveUsername(prefer, h.Username, newUser, p?.DefaultUsername))
                list.Add(h);
        }
        return list;
    }

    private async Task<string> VaultUpdateFromClaude(System.Text.Json.JsonElement a)
    {
        var v = ResolveVaultStrict(Arg(a, "account"));
        // 문자열이 아닌 값(숫자 등)을 빈 문자열로 받아 사용자명·메모를 몰래 지우지 않게 — 넘겼으면 문자열이어야 한다
        string? Str(string field)
        {
            if (!Has(a, field)) return null;
            return a.GetProperty(field).ValueKind == System.Text.Json.JsonValueKind.String
                ? Arg(a, field)
                : throw new McpToolException($"{field} 는 문자열이어야 합니다.");
        }
        var edit = new VaultEdit(Str("name"), Str("username"), Str("note"));
        if (edit is { Name: null, Username: null, Note: null })
            throw new McpToolException("바꿀 항목(name · username · note)을 하나 이상 넘기세요. 비밀번호·키는 open_app_page vault_edit:<계정> 으로 사용자가 직접 바꿉니다.");
        var plan = PlanVaultEdit(v, edit, VaultStore.NameTaken);
        if (plan.Error != null) throw new McpToolException(plan.Error);
        var shown = McpHelpers.VisibleControlChars(v.Name);
        if (plan.Empty) return $"Vault 계정 '{shown}' 은 이미 그 값입니다. 바꾼 것이 없습니다.";

        var affected = plan.Username != null ? HostsWithUsernameChange(v.Id, v.Username, plan.Username) : new List<HostEntry>();
        static string Q(string s) => $"'{McpHelpers.VisibleControlChars(s)}'";
        static string Short(string s) => s.Length > 60 ? s[..60] + "…" : s;
        // 승인 창의 명령 칸·감사 로그: 언어와 무관한 표기
        var parts = new List<string>();
        if (plan.Name != null) parts.Add($"name {Q(v.Name)} → {Q(plan.Name)}");
        if (plan.Username != null) parts.Add($"username {Q(v.Username)} → {Q(plan.Username)} (hosts {affected.Count})");
        if (plan.Note != null) parts.Add($"note {Q(Short(v.Note))} → {Q(Short(plan.Note))}");
        var command = $"vault_update {shown}: {string.Join(", ", parts)}";
        // 승인 창(사용자 화면)은 번역
        var empty = L.T("(없음)");
        string Ui(string s) => s.Length == 0 ? empty : McpHelpers.VisibleControlChars(Short(s));
        var targets = new List<string> { L.F("Vault 계정 '{0}' ({1})", shown, v.MethodsText) };
        if (plan.Name != null) targets.Add(L.F("이름: {0} → {1}", Ui(v.Name), Ui(plan.Name)));
        if (plan.Username != null) targets.Add(L.F("사용자명: {0} → {1}", Ui(v.Username), Ui(plan.Username)));
        if (plan.Note != null) targets.Add(L.F("메모: {0} → {1}", Ui(v.Note), Ui(plan.Note)));
        targets.AddRange(affected.Take(20).Select(h => "  " + McpHelpers.VisibleControlChars(h.DisplayName)));
        if (affected.Count > 20) targets.Add("  " + L.F("외 {0}개", affected.Count - 20));
        var desc = L.F("Vault 계정 '{0}' 의 정보를 바꿉니다. 비밀번호·SSH 키는 바뀌지 않습니다.", shown);
        if (plan.Username != null)
            desc += "\n" + L.F("사용자명을 바꾸면 이 계정으로 로그인하는 호스트 {0}개의 로그인 계정이 바뀝니다:", affected.Count);
        var auditTargets = new[] { $"Vault {shown}" };
        if (!await AskApprovalAsync("vault_update", L.T("Claude 가 Vault 계정 정보 변경을 요청합니다"), desc, targets, command))
        {
            AuditLog.Write("vault_update", auditTargets, command, "거부");
            throw new McpToolException("사용자가 Vault 계정 정보 변경을 거부했습니다.");
        }
        // 승인을 기다리는 동안 계정이 지워졌거나 (Vault 화면에서) 고쳐졌으면 승인한 것과 다른 상태에 덮어쓰지 않는다
        var now = VaultStore.Get(v.Id);
        if (now == null)
        {
            AuditLog.Write("vault_update", auditTargets, command, "승인 후 취소 (대기 중 계정이 지워짐)");
            throw new McpToolException("승인을 기다리는 동안 Vault 계정이 지워져 바꾸지 않았습니다.");
        }
        if (now.Name != v.Name || now.Username != v.Username || now.Note != v.Note || now.Updated != v.Updated)
        {
            AuditLog.Write("vault_update", auditTargets, command, "승인 후 취소 (대기 중 계정이 바뀜)");
            throw new McpToolException("승인을 기다리는 동안 Vault 계정이 바뀌어 저장하지 않았습니다. list_vault 로 다시 확인하세요.");
        }
        if (plan.Name != null && !string.Equals(plan.Name, now.Name.Trim(), StringComparison.CurrentCultureIgnoreCase) && VaultStore.NameTaken(plan.Name, now.Id))
        {
            AuditLog.Write("vault_update", auditTargets, command, "승인 후 취소 (대기 중 같은 이름의 계정이 생김)");
            throw new McpToolException($"승인을 기다리는 동안 같은 이름의 Vault 계정 '{McpHelpers.VisibleControlChars(plan.Name)}' 이(가) 생겨 바꾸지 않았습니다.");
        }
        // Vault 화면 수정 창과 같게 사본을 고쳐 Upsert 로 바꿔 넣는다 (접속 중인 다른 스레드가 반쯤 바뀐 값을 읽지 않게, 실패하면 원본 유지)
        var copy = now.Clone();
        if (plan.Name != null) copy.Name = plan.Name;
        if (plan.Username != null) copy.Username = plan.Username;
        if (plan.Note != null) copy.Note = plan.Note;
        try { VaultStore.Upsert(copy); }
        catch (Exception ex)
        {
            RefreshVault();
            AuditLog.Write("vault_update", auditTargets, command, "승인·실패: " + ex.Message);
            throw new McpToolException("Vault 계정을 저장하지 못했습니다: " + ex.Message);
        }
        RefreshHosts();   // 카드의 사용자명·툴팁이 Vault 값을 보여 준다
        RefreshVault();
        if (SettingsPage.Visibility == Visibility.Visible) RefreshPreferences();   // 기본 계정 선택 칸의 이름
        AuditLog.Write("vault_update", auditTargets, command, "승인·변경");
        return $"Vault 계정을 바꿨습니다: {command}." + (plan.Username != null ? $" 로그인 사용자명이 바뀐 호스트 {affected.Count}개." : "");
    }

    /// <summary>저장값 "[REJECTED ]형식 SHA256:..." → (형식, 지문, 거부 여부). 형식이 없는 값은 전체가 지문</summary>
    internal static (string type, string fingerprint, bool rejected) SplitKnownHost(string value)
    {
        var rejected = KnownHostStore.IsRejected(value);
        var body = rejected ? value["REJECTED ".Length..] : value;
        var sp = body.IndexOf(' ');
        return sp > 0 ? (body[..sp], body[(sp + 1)..], rejected) : ("", body, rejected);
    }

    /// <summary>
    /// 공개키 한 줄 검증: "형식 base64" 두 칸, base64 가 SSH 공개키 blob 이고 blob 안의 형식 이름이 앞 칸과 같을 때만 그 줄, 아니면 null.
    /// 개인키(PEM·OpenSSH)·여러 줄 값·제어 문자가 섞인 값은 통과하지 못한다
    /// </summary>
    internal static string? SafePublicKeyLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Trim().Split(' ');
        if (parts.Length != 2) return null;
        var (type, b64) = (parts[0], parts[1]);
        if (type.Length is 0 or > 64 || !type.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '.' or '@' or '_')) return null;
        if (b64.Length is < 16 or > 16384 || !b64.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/' or '=')) return null;
        byte[] blob;
        try { blob = Convert.FromBase64String(b64); }
        catch (FormatException) { return null; }
        return VaultStore.BlobType(blob) == type ? $"{type} {b64}" : null;
    }

    /// <summary>공개키 줄 끝의 주석: 계정 이름에서 글자·숫자·-._@ 만 남기고 나머지(공백·제어·서식 문자)는 '_' (authorized_keys 한 줄이 깨지지 않게)</summary>
    internal static string PublicKeyComment(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name.Trim())
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '.' or '_' or '@' ? c : '_');
        var s = sb.ToString();
        return s.Length == 0 ? "nubis" : s.Length > 64 ? s[..64] : s;
    }

    /// <summary>바꾸거나 지우는 도구용: 이름이 정확히 같거나 id(8자 이상 앞부분)가 맞는 계정 1개 (부분 일치로 엉뚱한 계정을 고르지 않게)</summary>
    private static VaultEntry ResolveVaultStrict(string spec)
    {
        spec = spec.Trim();
        if (spec.Length == 0) throw new McpToolException("account 를 지정하세요 (list_vault 로 확인).");
        var all = VaultStore.All();
        var hit = all.Where(v => v.Name.Trim().Equals(spec, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hit.Count == 0 && spec.Length >= 8) hit = all.Where(v => v.Id.ToString().StartsWith(spec, StringComparison.OrdinalIgnoreCase)).ToList();
        return hit.Count switch
        {
            1 => hit[0],
            0 => throw new McpToolException($"Vault 계정 '{spec}' 을 찾을 수 없습니다. 이름을 정확히 쓰거나 id 로 지정하세요 (list_vault)."),
            _ => throw new McpToolException($"Vault 계정 '{spec}' 이 {hit.Count}개와 일치합니다. id 로 지정하세요: " +
                                            string.Join(", ", hit.Select(v => $"{v.Name} ({v.Id.ToString()[..8]})"))),
        };
    }

    /// <summary>기본 계정을 바꾸면 영향받는(설정의 기본 계정을 쓰게 되는) 호스트 — HostEntry.ResolveVault 의 1~4단계를 건너뛰는 호스트</summary>
    private List<HostEntry> HostsOnGlobalDefault(Protocol proto)
    {
        var profiles = _profiles.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
        return _hosts.Where(h =>
        {
            if (h.Protocol != proto) return false;
            if (h.VaultId != null && VaultStore.Get(h.VaultId) != null) return false;
            if (!string.IsNullOrEmpty(h.ProtectedPassword) || !string.IsNullOrWhiteSpace(h.KeyPath)) return false;
            var p = h.CloudProfileId is { } pid ? profiles.GetValueOrDefault(pid) : null;
            var fromProfile = proto == Protocol.Rdp ? p?.RdpVaultId : p?.SshVaultId;
            if (fromProfile != null && VaultStore.Get(fromProfile) != null) return false;
            return p == null || (string.IsNullOrEmpty(p.DefaultUsername) && string.IsNullOrEmpty(p.ProtectedDefaultPassword));
        }).ToList();
    }

    private async Task<string> VaultSetDefaultFromClaude(string protocol, string account)
    {
        var rdp = protocol.Trim().ToLowerInvariant() switch
        {
            "rdp" => true,
            "ssh" => false,
            _ => throw new McpToolException("protocol 은 ssh 또는 rdp 여야 합니다."),
        };
        var clear = account.Trim().Equals("none", StringComparison.OrdinalIgnoreCase);
        var target = clear ? null : ResolveVaultStrict(account);
        var cur = rdp ? SettingsStore.Current.DefaultRdpVaultId : SettingsStore.Current.DefaultSshVaultId;
        if (cur == target?.Id) return clear ? "이미 기본 계정이 지정되어 있지 않습니다." : $"'{target!.Name}' 은 이미 {(rdp ? "RDP" : "SSH")} 기본 계정입니다.";

        var proto = rdp ? "RDP" : "SSH";
        // 승인 창의 명령 칸에도 보이므로 언어와 무관한 표기로 (감사 로그도 같은 값)
        var oldName = VaultStore.Get(cur)?.Name ?? (cur != null ? "(deleted)" : "(none)");
        var newName = target?.Name ?? "(none)";
        var command = $"{(rdp ? nameof(AppSettings.DefaultRdpVaultId) : nameof(AppSettings.DefaultSshVaultId))}:{McpHelpers.VisibleControlChars(oldName)} → {McpHelpers.VisibleControlChars(newName)}";
        var affected = HostsOnGlobalDefault(rdp ? Protocol.Rdp : Protocol.Ssh);
        var targets = new List<string> { L.F("{0} 기본 계정: {1} → {2}", proto,
            McpHelpers.VisibleControlChars(VaultStore.Get(cur)?.Name ?? (cur != null ? L.T("(삭제된 계정)") : L.T("(없음)"))),
            McpHelpers.VisibleControlChars(target?.Name ?? L.T("(없음)"))) };
        targets.AddRange(affected.Take(20).Select(h => "  " + McpHelpers.VisibleControlChars(h.DisplayName)));
        if (affected.Count > 20) targets.Add("  " + L.F("외 {0}개", affected.Count - 20));
        var desc = (rdp
                       ? L.T("계정을 따로 정하지 않은 모든 RDP(Windows) 호스트가 이 계정으로 로그인합니다.")
                       : L.T("계정을 따로 정하지 않은 모든 SSH(Linux) 호스트가 이 계정으로 로그인합니다.")) +
                   "\n" + L.F("영향받는 호스트: {0}개", affected.Count);
        if (!await AskApprovalAsync("vault_set_default", L.T("Claude 가 기본 Vault 계정 변경을 요청합니다"), desc, targets, command))
        {
            AuditLog.Write("vault_set_default", new[] { $"{proto} 기본 계정" }, command, "거부");
            throw new McpToolException("사용자가 기본 계정 변경을 거부했습니다.");
        }
        // 승인을 기다리는 동안 계정이 지워졌으면 지운 ID 를 기본값으로 넣지 않는다
        if (target != null && VaultStore.Get(target.Id) == null)
        {
            AuditLog.Write("vault_set_default", new[] { $"{proto} 기본 계정" }, command, "승인 후 취소 (대기 중 계정이 지워짐)");
            throw new McpToolException("승인을 기다리는 동안 Vault 계정이 지워져 바꾸지 않았습니다.");
        }
        var id = target?.Id;
        SettingsStore.Update(x => { if (rdp) x.DefaultRdpVaultId = id; else x.DefaultSshVaultId = id; });
        if (SettingsPage.Visibility == Visibility.Visible) RefreshPreferences();
        RefreshVault();
        RefreshHosts();
        AuditLog.Write("vault_set_default", new[] { $"{proto} 기본 계정" }, command, "승인·변경");
        return $"{proto} 기본 Vault 계정을 바꿨습니다: {command} (영향받는 호스트 {affected.Count}개).";
    }

    private async Task<string> VaultDeleteFromClaude(string account)
    {
        var v = ResolveVaultStrict(account);
        var refs = FindVaultRefs(v.Id);
        var hosts = refs.AllHosts;
        var name = McpHelpers.VisibleControlChars(v.Name);

        // 승인 창(사용자 화면)은 번역, 감사 로그는 한국어 원문
        var targets = new List<string> { L.F("Vault 계정 '{0}' ({1})", name, v.MethodsText) };
        if (refs.SshDefault) targets.Add(L.T("설정: SSH 기본 계정 해제"));
        if (refs.RdpDefault) targets.Add(L.T("설정: RDP 기본 계정 해제"));
        targets.AddRange(refs.Profiles.Select(p => L.F("클라우드 프로필 기본 계정 해제: [{0}] {1}", p.ProviderText, McpHelpers.VisibleControlChars(p.GroupName))));
        targets.AddRange(hosts.Take(20).Select(h => L.F("호스트 연결 해제: {0}", McpHelpers.VisibleControlChars(h.DisplayName))));
        if (hosts.Count > 20) targets.Add("  " + L.F("외 {0}개", hosts.Count - 20));
        var desc = L.F("Vault 계정 '{0}' 를 삭제합니다.", name) + (hosts.Count > 0
            ? "\n" + L.F("이 계정을 쓰는 호스트 {0}개는 연결이 해제되어, 호스트에 직접 적은 값이 없으면 다음 접속 때 계정을 묻습니다:", hosts.Count)
            : "");
        // 승인 창의 명령 칸에도 보이므로 언어와 무관한 표기로 (감사 로그도 같은 값)
        var command = $"vault_delete {name} (hosts {hosts.Count}, profiles {refs.Profiles.Count}" +
                      $"{(refs.SshDefault ? ", DefaultSshVaultId" : "")}{(refs.RdpDefault ? ", DefaultRdpVaultId" : "")})";
        var auditTargets = new[] { $"Vault {name}" };
        if (!await AskApprovalAsync("vault_delete", L.T("Claude 가 Vault 계정 삭제를 요청합니다"), desc, targets, command))
        {
            AuditLog.Write("vault_delete", auditTargets, command, "거부");
            throw new McpToolException("사용자가 Vault 계정 삭제를 거부했습니다.");
        }
        if (VaultStore.Get(v.Id) == null)
        {
            AuditLog.Write("vault_delete", auditTargets, command, "승인 (대기 중 이미 지워짐)");
            return $"Vault 계정 '{v.Name}' 은 이미 지워져 있습니다.";
        }
        try { DeleteVaultAndRefs(v.Id); }
        catch (Exception ex)
        {
            AuditLog.Write("vault_delete", auditTargets, command, "승인·실패: " + ex.Message);
            throw new McpToolException("Vault 계정을 지우지 못했습니다: " + ex.Message);
        }
        AuditLog.Write("vault_delete", auditTargets, command, "승인·삭제");
        return $"Vault 계정 '{v.Name}' 을 지웠습니다. 연결 해제: 호스트 {hosts.Count}개, 클라우드 프로필 {refs.Profiles.Count}개" +
               $"{(refs.SshDefault ? ", SSH 기본 계정" : "")}{(refs.RdpDefault ? ", RDP 기본 계정" : "")}.";
    }

    private async Task<string> DeleteKnownHostFromClaude(string spec)
    {
        spec = spec.Trim();
        if (spec.Length == 0) throw new McpToolException("host 를 지정하세요 (list_known_hosts 의 host:port).");
        var all = KnownHostStore.Load();
        var hit = all.Keys.Where(k => k.Equals(spec, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hit.Count == 0) hit = all.Keys.Where(k => k.StartsWith(spec + ":", StringComparison.OrdinalIgnoreCase)).ToList();
        var key = hit.Count switch
        {
            1 => hit[0],
            0 => throw new McpToolException($"'{spec}' 의 저장된 호스트키가 없습니다. list_known_hosts 로 확인하세요."),
            _ => throw new McpToolException($"'{spec}' 와 일치하는 항목이 {hit.Count}개입니다. host:port 로 지정하세요: {string.Join(", ", hit)}"),
        };
        var value = all[key];
        if (KnownHostStore.IsRejected(value))
            throw new McpToolException($"'{key}' 는 사용자가 거부한 호스트키입니다. 바뀐(의심스러운) 키를 막으려고 남겨 둔 것이라 Claude 는 지울 수 없습니다. " +
                                       "정말 지워야 하면 사용자가 Known Hosts 화면에서 직접 지우도록 안내하세요.");
        var shownKey = McpHelpers.VisibleControlChars(key);
        var shownValue = McpHelpers.VisibleControlChars(value);
        var command = $"delete_known_host {shownKey} ({shownValue})";
        if (!await AskApprovalAsync("delete_known_host", L.T("Claude 가 저장된 호스트키 삭제를 요청합니다"),
                L.F("'{0}' 의 저장된 호스트키를 지울까요?\n다음 접속 때 서버가 보내는 키를 새로 신뢰합니다. 서버를 다시 설치해 키가 바뀐 경우에만 지우세요.", shownKey),
                new[] { $"{shownKey}  {shownValue}" }, command))
        {
            AuditLog.Write("delete_known_host", new[] { key }, command, "거부");
            throw new McpToolException("사용자가 호스트키 삭제를 거부했습니다.");
        }
        // 승인을 기다리는 동안 키가 바뀌었거나 거부로 표시됐으면 지우지 않는다 (승인한 것과 다른 값을 지우지 않게)
        var now = KnownHostStore.Get(key);
        if (now == null)
        {
            AuditLog.Write("delete_known_host", new[] { key }, command, "승인 (대기 중 이미 지워짐)");
            return $"'{key}' 의 호스트키는 이미 지워져 있습니다.";
        }
        if (now != value)
        {
            AuditLog.Write("delete_known_host", new[] { key }, command, "승인 후 취소 (대기 중 저장값이 바뀜)");
            throw new McpToolException("승인을 기다리는 동안 저장된 호스트키가 바뀌어 지우지 않았습니다. list_known_hosts 로 다시 확인하세요.");
        }
        try { KnownHostStore.Remove(key); }
        catch (Exception ex)
        {
            AuditLog.Write("delete_known_host", new[] { key }, command, "승인·실패: " + ex.Message);
            throw new McpToolException("호스트키를 지우지 못했습니다: " + ex.Message);
        }
        RefreshKnownHosts();
        AuditLog.Write("delete_known_host", new[] { key }, command, "승인·삭제");
        return $"'{key}' 의 저장된 호스트키를 지웠습니다. 다음 접속 때 서버가 보내는 키를 새로 저장합니다.";
    }

    /// <summary>
    /// update_settings 로 설정이 바뀐 뒤 화면·타이머를 맞춘다 (Settings 화면에서 바꿀 때와 같게):
    /// 최근 접속 표시는 호스트 목록을 다시 그리고, 자동 동기화 설정은 타이머를 다시 건다
    /// </summary>
    private void AfterClaudeSettingsChange(IReadOnlyCollection<SettingsCatalog.Change> plan)
    {
        if (plan.Any(p => p.Def.Key == nameof(AppSettings.ShowRecent))) RefreshHosts();
        if (plan.Any(p => SettingsCatalog.CloudSyncKeys.Contains(p.Def.Key))) StartCloudAutoSync();
    }
}
