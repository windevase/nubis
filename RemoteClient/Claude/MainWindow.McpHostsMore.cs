using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace Nubis;

// MCP 도구: 호스트 일괄 편집 · 그룹 · 복제 · 여러 대 접속 — UI 에서 손으로 하는 동작을 Claude 도 할 수 있게 (BuildTools 에서 등록).
// 보안 원칙:
// - 값을 바꾸는 도구(일괄 편집 · 그룹 기본값 · 그룹 이름 · 복제)는 항상 승인. 접힘 상태는 화면 상태라 승인 없이 감사 로그만
// - 여러 대 접속은 UI "모두 연결" 과 같이 8개를 넘으면 승인, 호스트마다 감사 로그 한 줄
// - 비밀번호는 인자로 받지 않고 결과·로그에도 넣지 않는다 (있음/없음만)
public partial class MainWindow
{
    /// <summary>UI "모두 연결" 이 확인을 묻는 기준과 같다 (ConnectMany)</summary>
    private const int ConnectApprovalThreshold = 8;

    private IEnumerable<McpTool> HostMoreTools()
    {
        yield return new McpTool("bulk_update_hosts",
            "여러 호스트를 한 번에 바꾼다 (UI 의 일괄 편집과 같은 규칙). hosts(이름·IP·id·'group:이름' 목록) 와/또는 group 으로 대상을 지정하고, 넘긴 항목만 바꾼다. " +
            "group 변경은 직접 등록한 호스트에만 적용(클라우드 호스트는 프로필 그룹이라 건너뜀), use_public_ip 는 공인 IP 가 있는 호스트에만. " +
            "clear_own_credentials=true 는 호스트에 직접 적은 사용자명·비밀번호·키 파일을 지운다 (되돌릴 수 없음). 비밀번호는 넣을 수 없다. 항상 사용자 승인.",
            Schema(new
            {
                hosts = new { type = "array", items = new { type = "string" }, description = "대상 호스트 (이름·IP·id 또는 'group:그룹이름')" },
                group = new { type = "string", description = "대상 그룹 전체 (list_groups 의 그룹 이름, '[AWS] prod' 등)" },
                set_group = new { type = "string", description = "바꿀 그룹 이름 (빈 문자열 = 그룹 없음). 직접 등록한 호스트만" },
                vault = new { type = "string", description = "Vault 계정 이름 또는 id (list_vault). \"none\" = 지정 해제 (기본 계정 따르기)" },
                username = new { type = "string", description = "사용자명 (빈 문자열 = 지우기)" },
                port = new { type = "integer" },
                use_public_ip = new { type = "boolean" },
                favorite = new { type = "boolean" },
                clear_own_credentials = new { type = "boolean", description = "직접 적은 사용자명·비밀번호·키 파일 지우기 (되돌릴 수 없음)" },
            }),
            a => OnUiAsync(() => BulkUpdateHostsFromClaude(a)));

        yield return new McpTool("set_group_defaults",
            "그룹(클라우드 프로필 그룹 또는 직접 등록한 그룹)의 SSH/RDP Vault 계정과 공인 IP 접속 여부를 한 번에 바꾼다 (UI 의 그룹 우클릭 메뉴와 같음). " +
            "클라우드 그룹은 프로필 기본값으로 저장되어 앞으로 동기화로 추가되는 서버에도 적용된다. ssh_vault/rdp_vault 에 \"none\" = 지정 해제. " +
            "클라우드 그룹에서 다른 Vault 계정을 직접 골라 둔 호스트는 기본적으로 그대로 두고(override_host_vault=true 면 해제), " +
            "직접 적은 사용자명·비밀번호·키는 기본적으로 유지한다(clear_own_credentials=true 면 지움, 되돌릴 수 없음, 클라우드 그룹만). 항상 사용자 승인.",
            Schema(new
            {
                group = new { type = "string", description = "그룹 이름 (list_groups)" },
                ssh_vault = new { type = "string", description = "SSH 호스트에 쓸 Vault 계정 이름·id, \"none\" = 해제" },
                rdp_vault = new { type = "string", description = "RDP 호스트에 쓸 Vault 계정 이름·id, \"none\" = 해제" },
                use_public_ip = new { type = "boolean", description = "공인 IP 가 있는 호스트를 공인(true)/사설(false) IP 접속으로" },
                override_host_vault = new { type = "boolean", description = "클라우드 그룹: 다른 Vault 계정을 직접 골라 둔 호스트의 지정도 해제 (기본 false)" },
                clear_own_credentials = new { type = "boolean", description = "클라우드 그룹: 직접 적은 사용자명·비밀번호·키 지우기 (기본 false, 되돌릴 수 없음)" },
            }, "group"),
            a => OnUiAsync(() => SetGroupDefaultsFromClaude(a)));

        yield return new McpTool("rename_group",
            "직접 등록한 그룹의 이름을 바꾼다 (클라우드 프로필 그룹은 불가 — 프로필 설정에서). 같은 이름의 그룹이 이미 있으면 합쳐진다. 항상 사용자 승인.",
            Schema(new
            {
                group = new { type = "string", description = "지금 그룹 이름" },
                new_name = new { type = "string" },
            }, "group", "new_name"),
            a => OnUiAsync(() => RenameGroupFromClaude(a)));

        yield return new McpTool("set_group_collapsed",
            "호스트 목록에서 그룹을 접거나 편다 (화면 상태만, 승인 없음). group 은 그룹 이름, 'favorites', 'recent' 또는 'all'(화면에 보이는 모든 그룹).",
            Schema(new
            {
                group = new { type = "string" },
                collapsed = new { type = "boolean" },
            }, "group", "collapsed"),
            a => OnUi(() => SetGroupCollapsedFromClaude(a)));

        yield return new McpTool("duplicate_host",
            "호스트 1개를 복제해 새 호스트로 저장한다 (UI 의 복제와 같음). 원본의 계정 설정(사용자명 · Vault 계정 · 저장된 비밀번호)이 복사본에도 그대로 복사된다. " +
            "클라우드 호스트의 복사본은 직접 관리하는 호스트가 되며 원본이 실제로 쓰던 계정을 옮긴다. label·group 으로 이름·그룹을 지정할 수 있다. 항상 사용자 승인.",
            Schema(new
            {
                host = new { type = "string", description = "원본 호스트 (이름·IP·id, 정확히 1개)" },
                label = new { type = "string", description = "복사본 이름 (생략 시 '<원본> (복사본)')" },
                group = new { type = "string", description = "복사본 그룹 (생략 시 원본 그룹, 클라우드 원본은 프로필 이름. 빈 문자열 = 그룹 없음)" },
            }, "host"),
            a => OnUiAsync(() => DuplicateHostFromClaude(a)));

        yield return new McpTool("connect_hosts",
            "여러 호스트에 세션 탭을 한 번에 연다 (UI 의 '모두 연결'). hosts 와/또는 group 으로 지정. 중지된 서버는 건너뛴다. " +
            $"{ConnectApprovalThreshold}개를 넘으면 사용자 승인. address 로 공인/사설 IP 를 고를 수 있다(public 이면 공인 IP 가 없는 호스트는 건너뜀).",
            Schema(new
            {
                hosts = new { type = "array", items = new { type = "string" }, description = "호스트 이름·IP·id 또는 'group:그룹이름'" },
                group = new { type = "string", description = "그룹 전체" },
                address = new { type = "string", @enum = new[] { "default", "private", "public" } },
            }),
            a => OnUiAsync(() => ConnectHostsFromClaude(a)));
    }

    // =================== 공통: 대상 해석 ===================

    private static string HostTarget(HostEntry h) =>
        $"{McpHelpers.VisibleControlChars(h.DisplayName)}  ({h.ProtocolText} {h.Address}:{h.Port})";

    /// <summary>보이지 않는 제어·양방향 문자(U+202E 등)를 거부한 문자열 인자 (앞뒤 공백 제거)</summary>
    private static string VisibleArg(JsonElement a, string name)
    {
        var v = Arg(a, name).Trim();
        if (McpHelpers.HasInvisibleChars(v)) throw new McpToolException($"{name} 에 보이지 않는 제어 문자·양방향 문자가 있습니다: {McpHelpers.VisibleControlChars(v)}");
        return v;
    }

    /// <summary>hosts(호스트 또는 'group:이름') + group 인자를 합친 대상 (중복 제거, 입력 순서)</summary>
    private List<HostEntry> ResolveHostTargets(JsonElement a)
    {
        var list = new List<HostEntry>();
        foreach (var spec in ListArg(a, "hosts"))
            list.AddRange(spec.TrimStart().StartsWith("group:", StringComparison.OrdinalIgnoreCase)
                ? GroupMembers(ResolveGroupSpec(spec))
                : new List<HostEntry> { ResolveOneHost(spec) });
        if (Arg(a, "group").Trim().Length > 0) list.AddRange(GroupMembers(ResolveGroupSpec(Arg(a, "group"))));
        var targets = list.DistinctBy(h => h.Id).ToList();
        if (targets.Count == 0) throw new McpToolException("대상 호스트가 없습니다. hosts 또는 group 을 지정하세요.");
        return targets;
    }

    // =================== bulk_update_hosts ===================

    private async Task<string> BulkUpdateHostsFromClaude(JsonElement a)
    {
        var targets = ResolveHostTargets(a);
        var edit = new BulkEdit();
        // 감사 로그·결과용(한국어 원문)과 승인 창용(번역)
        var lines = new List<string>();
        var shown = new List<string>();
        void Add(string ko, string ui) { lines.Add(ko); shown.Add(ui); }

        VaultEntry? vault = null;
        if (Has(a, "set_group"))
        {
            edit.Group = VisibleArg(a, "set_group");
            Add($"그룹: {(edit.Group.Length == 0 ? "(그룹 없음)" : edit.Group)}", L.F("그룹: {0}", edit.Group.Length == 0 ? L.T("(그룹 없음)") : edit.Group));
        }
        if (Has(a, "vault"))
        {
            var spec = Arg(a, "vault").Trim();
            vault = spec.Length == 0 || spec.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : ResolveVault(spec);
            edit.SetVault = true;
            edit.VaultId = vault?.Id;
            Add($"Vault 계정: {vault?.Name ?? "지정 해제 (기본 계정 따르기)"}",
                vault != null ? L.F("Vault 계정: {0}", vault.Name) : L.T("Vault 계정: 지정 해제 (기본 계정 따르기)"));
        }
        if (Has(a, "username"))
        {
            edit.Username = VisibleArg(a, "username");
            Add($"사용자명: {(edit.Username.Length == 0 ? "(지움)" : edit.Username)}", L.F("사용자명: {0}", edit.Username.Length == 0 ? L.T("(지움)") : edit.Username));
        }
        if (Has(a, "port"))
        {
            var port = IntArg(a, "port") ?? 0;
            if (port is < 1 or > 65535) throw new McpToolException("port 는 1~65535 입니다.");
            edit.Port = port;
            Add($"포트: {port}", L.F("포트: {0}", port));
        }
        if (Has(a, "use_public_ip"))
        {
            edit.UsePublicIp = BoolArg(a, "use_public_ip") ?? throw new McpToolException("use_public_ip 는 true/false 입니다.");
            Add(edit.UsePublicIp.Value ? "접속 IP: 공인" : "접속 IP: 사설", edit.UsePublicIp.Value ? L.T("접속 IP: 공인") : L.T("접속 IP: 사설"));
        }
        if (Has(a, "favorite"))
        {
            edit.Favorite = BoolArg(a, "favorite") ?? throw new McpToolException("favorite 은 true/false 입니다.");
            Add(edit.Favorite.Value ? "즐겨찾기: 추가" : "즐겨찾기: 해제", edit.Favorite.Value ? L.T("즐겨찾기: 추가") : L.T("즐겨찾기: 해제"));
        }
        if (BoolArg(a, "clear_own_credentials") == true)
        {
            edit.ClearOwnCredentials = true;
            Add("직접 적은 사용자명·비밀번호·키 파일: 지움", L.T("직접 적은 사용자명·비밀번호·키 파일: 지움"));
        }
        if (edit.IsEmpty) throw new McpToolException("바꿀 항목이 없습니다 (set_group · vault · username · port · use_public_ip · favorite · clear_own_credentials).");

        // 사본에 먼저 적용해 실제로 바뀔 호스트만 고른다 (원본은 승인 후에만)
        var changing = targets.Where(h => edit.Apply(new[] { h.Clone() }) > 0).ToList();
        if (changing.Count == 0) return "모든 대상이 이미 요청한 값과 같아 바뀐 호스트가 없습니다.";

        var notesKo = new List<string>();
        var notesUi = new List<string>();
        var cloudSkip = edit.Group != null ? targets.Count(h => h.IsCloud) : 0;
        if (cloudSkip > 0)
        {
            notesKo.Add($"클라우드 호스트 {cloudSkip}개는 프로필 그룹이라 그룹을 바꾸지 않습니다.");
            notesUi.Add(L.F("클라우드 호스트 {0}개는 프로필 그룹이라 그룹을 바꾸지 않습니다.", cloudSkip));
        }
        var noPublic = edit.UsePublicIp != null ? targets.Count(h => !h.HasPublicIp) : 0;
        if (noPublic > 0)
        {
            notesKo.Add($"공인 IP 가 없는 {noPublic}개는 접속 IP 를 바꾸지 않습니다.");
            notesUi.Add(L.F("공인 IP 가 없는 {0}개는 접속 IP 를 바꾸지 않습니다.", noPublic));
        }
        if (vault != null || edit.Port != null)
        {
            notesKo.Add("Vault 계정·포트가 바뀌면 저장된 계정이 쓰이는 서버가 달라집니다.");
            notesUi.Add(L.T("Vault 계정·포트가 바뀌면 저장된 계정이 쓰이는 서버가 달라집니다."));
        }
        if (edit.ClearOwnCredentials)
        {
            var own = changing.Where(h => h.Protocol != Protocol.Local && BulkEdit.HasOwnCredentials(h)).ToList();
            var pw = own.Count(h => !string.IsNullOrEmpty(h.ProtectedPassword));
            // 사본 기준으로 적용 후에도 Vault 계정 지정이 없는 호스트 (기본 계정이 없으면 접속할 때 계정을 묻는다)
            var noVault = own.Count(h => { var c = h.Clone(); edit.Apply(new[] { c }); return c.VaultId == null; });
            notesKo.Add($"⚠ 호스트 {own.Count}개에 직접 적은 사용자명·비밀번호·키 파일을 지웁니다 (저장된 비밀번호 {pw}개). 지운 비밀번호는 되돌릴 수 없습니다." +
                        (noVault > 0 ? $" 이 중 {noVault}개는 Vault 계정 지정이 없어, 기본 계정이 없으면 접속할 때 계정을 묻습니다." : ""));
            notesUi.Add(L.F("⚠ 호스트 {0}개에 직접 적은 사용자명·비밀번호·키 파일을 지웁니다 (저장된 비밀번호 {1}개). 지운 비밀번호는 되돌릴 수 없습니다.", own.Count, pw) +
                        (noVault > 0 ? " " + L.F("이 중 {0}개는 Vault 계정 지정이 없어, 기본 계정이 없으면 접속할 때 계정을 묻습니다.", noVault) : ""));
        }

        var command = string.Join("\n", lines);
        var labels = changing.Select(HostTarget).ToList();
        var desc = L.F("호스트 {0}개의 설정을 아래와 같이 바꿉니다.", changing.Count) + string.Concat(notesUi.Select(n => "\n" + n));
        if (!await AskApprovalAsync("bulk_update_hosts",
                edit.ClearOwnCredentials ? L.F("Claude 가 호스트 {0}개 일괄 편집을 요청합니다 (되돌릴 수 없는 항목 포함)", changing.Count)
                                         : L.F("Claude 가 호스트 {0}개 일괄 편집을 요청합니다", changing.Count),
                desc, labels, string.Join("\n", shown)))
        {
            AuditLog.Write("bulk_update_hosts", labels, command, "거부");
            throw new McpToolException("사용자가 일괄 편집을 거부했습니다.");
        }
        // 승인 창을 기다리는 동안 지워진 호스트 · Vault 계정은 반영하지 않는다
        if (vault != null && VaultStore.Get(vault.Id) == null)
        {
            AuditLog.Write("bulk_update_hosts", labels, command, "승인 후 취소 (대기 중 Vault 계정이 지워짐)");
            throw new McpToolException("승인을 기다리는 동안 Vault 계정이 지워져 바꾸지 않았습니다.");
        }
        var present = changing.Where(_hosts.Contains).ToList();
        var changed = edit.Apply(present);
        if (changed > 0)
        {
            SaveHosts();
            RefreshHosts();
            if (VaultPage.Visibility == Visibility.Visible) RefreshVault();
        }
        var gone = changing.Count - present.Count;
        AuditLog.Write("bulk_update_hosts", labels, command, gone == 0 ? $"승인·변경 {changed}개" : $"승인·변경 {changed}개 (대기 중 지워짐 {gone}개)");
        return $"호스트 {changed}개를 바꿨습니다:\n{command}" + string.Concat(notesKo.Select(n => "\n" + n)) +
               (gone > 0 ? $"\n승인을 기다리는 동안 목록에서 지워진 {gone}개는 건너뛰었습니다." : "") +
               "\n대상:\n" + string.Join("\n", present.Take(50).Select(HostTarget)) + (present.Count > 50 ? $"\n... 외 {present.Count - 50}개" : "");
    }

    // =================== duplicate_host ===================

    private async Task<string> DuplicateHostFromClaude(JsonElement a)
    {
        var src = ResolveOneHost(Arg(a, "host"));
        string? label = null, group = null;
        if (Has(a, "label")) { label = VisibleArg(a, "label"); if (label.Length == 0) label = null; }
        if (Has(a, "group")) group = VisibleArg(a, "group");
        var (copy, note) = MakeDuplicate(src, label, group);
        // group="" 은 "그룹 없음" — 클라우드 원본은 DetachFromCloud 가 빈 그룹을 프로필 이름으로 채우므로 다시 비운다
        if (group is { Length: 0 }) copy.Group = "";

        // 비밀번호는 있음/없음만 (값·암호문은 결과·로그·승인 창에 넣지 않는다)
        var hasPw = !string.IsNullOrEmpty(copy.ProtectedPassword);
        var hasKey = !string.IsNullOrWhiteSpace(copy.KeyPath);
        var summary = HostSummary(copy) + $"\n저장된 비밀번호: {(hasPw ? "복사됨" : "없음")} · 키 파일: {(hasKey ? "복사됨" : "없음")}";
        var shown = HostSummary(copy) + "\n" + (hasPw ? L.T("저장된 비밀번호: 복사됨") : L.T("저장된 비밀번호: 없음")) + " · " +
                    (hasKey ? L.T("키 파일: 복사됨") : L.T("키 파일: 없음"));
        var srcLabel = HostTarget(src);
        var desc = L.F("'{0}' 를 복제해 새 호스트로 저장합니다. 원본의 계정(사용자명 · Vault 계정 지정 · 저장된 비밀번호)이 복사본에도 그대로 복사됩니다.", McpHelpers.VisibleControlChars(src.DisplayName)) +
                   (src.IsCloud ? "\n" + L.T("클라우드 호스트의 복사본은 직접 관리하는 호스트가 되며, 원본이 실제로 쓰던 계정(프로필 기본 계정 포함)을 복사본에 옮깁니다.") : "") +
                   "\n" + L.T("나중에 복사본의 주소를 바꾸면 이 계정이 다른 서버에 쓰입니다.");
        if (!await AskApprovalAsync("duplicate_host", L.T("Claude 가 호스트 복제를 요청합니다"), desc, new[] { srcLabel }, shown))
        {
            AuditLog.Write("duplicate_host", new[] { srcLabel }, summary, "거부");
            throw new McpToolException("사용자가 호스트 복제를 거부했습니다.");
        }
        if (!_hosts.Contains(src))
        {
            AuditLog.Write("duplicate_host", new[] { srcLabel }, summary, "승인 후 취소 (대기 중 원본이 지워짐)");
            throw new McpToolException("승인을 기다리는 동안 원본 호스트가 지워져 복제하지 않았습니다.");
        }
        _hosts.Add(copy);
        SaveHosts();
        RefreshHosts();
        AuditLog.Write("duplicate_host", new[] { srcLabel }, summary, $"승인·복제 ({copy.Id.ToString()[..8]})");
        return $"복제했습니다: {copy.Id.ToString()[..8]} | {summary}" + (note != null ? $"\n참고: {note}" : "");
    }

    // =================== connect_hosts ===================

    private async Task<string> ConnectHostsFromClaude(JsonElement a)
    {
        var all = ResolveHostTargets(a);
        var mode = Arg(a, "address");
        bool? usePublic = mode switch { "public" => true, "private" => false, "" or "default" => null, _ => throw new McpToolException("address 는 default · private · public 입니다.") };
        var stopped = all.Where(h => h.IsStopped).ToList();
        var noPublic = usePublic == true ? all.Where(h => !h.IsStopped && !h.HasPublicIp).ToList() : new List<HostEntry>();
        var targets = all.Where(h => !h.IsStopped && !noPublic.Contains(h)).ToList();
        var skipped = (stopped.Count > 0 ? $"\n중지된 서버 {stopped.Count}개는 건너뜀: {string.Join(", ", stopped.Take(20).Select(h => h.DisplayName))}" : "") +
                      (noPublic.Count > 0 ? $"\n공인 IP 가 없는 {noPublic.Count}개는 건너뜀: {string.Join(", ", noPublic.Take(20).Select(h => h.DisplayName))}" : "");
        if (targets.Count == 0) return "접속할 호스트가 없습니다." + skipped;

        var labels = targets.Select(HostTarget).ToList();
        var cmd = "(탭 열기" + (usePublic != null ? $", {mode} 주소" : "") + ")";
        var approved = targets.Count > ConnectApprovalThreshold;
        if (approved && !await AskApprovalAsync("connect_hosts", L.F("Claude 가 호스트 {0}개에 접속을 요청합니다", targets.Count),
                L.T("호스트마다 세션 탭을 열고 저장된 계정으로 로그인합니다."), labels, L.T("(탭 열기)")))
        {
            AuditLog.Write("connect_hosts", labels, cmd, "거부");
            throw new McpToolException("사용자가 여러 호스트 접속을 거부했습니다.");
        }
        var lines = new List<string>();
        foreach (var h in targets)
        {
            var label = HostTarget(h);
            if (!_hosts.Contains(h)) { lines.Add($"{h.DisplayName}: 대기 중 목록에서 지워져 건너뜀"); continue; }
            // 승인을 기다리는 동안 동기화로 중지됐거나 공인 IP 가 없어졌으면 건너뛴다 (사용자가 본 대상과 달라지지 않게)
            if (h.IsStopped) { lines.Add($"{h.DisplayName}: 대기 중 중지된 서버가 되어 건너뜀"); continue; }
            if (usePublic == true && !h.HasPublicIp) { lines.Add($"{h.DisplayName}: 대기 중 공인 IP 가 없어져 건너뜀"); continue; }
            try
            {
                var tab = Connect(h, usePublic);
                AuditLog.Write("connect_hosts", new[] { label }, $"(탭 열기: session {tab.Id}{(usePublic != null ? $", {mode} 주소" : "")})", approved ? "승인·실행" : "실행");
                lines.Add($"session_id={tab.Id} | {h.DisplayName} ({h.ProtocolText} {tab.Host?.Address ?? h.Address}:{tab.Host?.Port ?? h.Port})");
            }
            catch (Exception ex) when (ex is not McpToolException)
            {
                AuditLog.Write("connect_hosts", new[] { label }, cmd, "실패: " + ex.GetType().Name);
                lines.Add($"{h.DisplayName}: 탭을 열지 못함 ({ex.Message})");
            }
        }
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        return $"세션 탭 {lines.Count(l => l.StartsWith("session_id="))}개를 열었습니다. 연결 결과는 잠시 후 list_sessions 로 확인하세요.\n" +
               string.Join("\n", lines) + skipped;
    }
}
