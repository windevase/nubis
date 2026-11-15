using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Nubis;

// MCP 그룹 도구 구현: set_group_defaults · rename_group · set_group_collapsed 와 그룹 지정 해석 (등록은 MainWindow.McpHostsMore.cs 의 HostMoreTools)
public partial class MainWindow
{
    // =================== 공통: 그룹 지정 해석 ===================

    /// <summary>
    /// 그룹 지정 해석: 직접 등록한 그룹 이름(대소문자 무시, 'Hosts' = 그룹 없음) → 클라우드 그룹 "[AWS] 이름" → 프로필 그룹 이름 · 프로필 이름.
    /// 화면 목록(_groups)이 검색으로 그룹을 숨겨도 찾을 수 있게 그룹을 새로 만든다 (GroupMembers 는 _hosts 기준)
    /// </summary>
    private HostGroup ResolveGroupSpec(string spec)
    {
        spec = spec.Trim();
        if (spec.StartsWith("group:", StringComparison.OrdinalIgnoreCase)) spec = spec[6..].Trim();
        if (spec.Length == 0) throw new McpToolException("group 이 필요합니다.");

        var canon = CanonicalGroupNames(_hosts);
        if (canon.TryGetValue(spec, out var local) && local.Length > 0) return LocalGroup(local);
        if (spec.Equals("Hosts", StringComparison.OrdinalIgnoreCase) && canon.ContainsKey("")) return LocalGroup("");

        var hit = _profiles.Where(p => $"[{p.ProviderText}] {p.GroupName}".Equals(spec, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hit.Count == 0) hit = _profiles.Where(p => p.GroupName.Equals(spec, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hit.Count == 0) hit = _profiles.Where(p => p.Name.Equals(spec, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hit.Count == 1)
        {
            var key = "C:" + hit[0].Id;
            return _groups.FirstOrDefault(g => g.Key == key) ?? new HostGroup { Key = key, Name = hit[0].GroupName, Profile = hit[0] };
        }
        if (hit.Count > 1)
            throw new McpToolException($"'{spec}' 가 클라우드 그룹 {hit.Count}개와 일치합니다. '[공급자] 이름' 으로 지정하세요: " +
                                       string.Join(", ", hit.Select(p => $"[{p.ProviderText}] {p.GroupName}")));
        throw new McpToolException($"'{spec}' 그룹을 찾을 수 없습니다. list_groups 로 확인하세요.");
    }

    private HostGroup LocalGroup(string name)
    {
        var key = "L:" + name;
        return _groups.FirstOrDefault(g => g.Key == key) ?? new HostGroup { Key = key, Name = name.Length == 0 ? "Hosts" : name, GroupName = name };
    }

    // =================== set_group_defaults ===================

    private async Task<string> SetGroupDefaultsFromClaude(JsonElement a)
    {
        var g = ResolveGroupSpec(Arg(a, "group"));
        var clearOwn = BoolArg(a, "clear_own_credentials") == true;
        var overrideHostVault = BoolArg(a, "override_host_vault") == true;
        if (!g.IsCloud && (clearOwn || overrideHostVault))
            throw new McpToolException("clear_own_credentials · override_host_vault 는 클라우드 그룹에만 씁니다 (직접 등록한 그룹은 고른 Vault 계정이 호스트에 적힌 값보다 우선). 직접 적은 값을 지우려면 bulk_update_hosts 의 clear_own_credentials 를 쓰세요.");

        var members = GroupMembers(g);
        // (프로토콜, 지정할 Vault — null 이면 해제)
        var vaultPlan = new List<(Protocol Proto, VaultEntry? Vault)>();
        foreach (var (arg, proto) in new[] { ("ssh_vault", Protocol.Ssh), ("rdp_vault", Protocol.Rdp) })
        {
            if (!Has(a, arg)) continue;
            var spec = Arg(a, arg).Trim();
            var v = spec.Length == 0 || spec.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : ResolveVault(spec);
            // 직접 등록한 그룹에 그 프로토콜 호스트가 없으면 바꿀 것이 없다 (클라우드는 프로필 기본값이 앞으로 추가될 서버에 쓰임)
            if (!g.IsCloud && !members.Any(h => h.Protocol == proto))
                throw new McpToolException($"'{g.Name}' 에 {(proto == Protocol.Rdp ? "RDP" : "SSH")} 호스트가 없습니다.");
            vaultPlan.Add((proto, v));
        }
        bool? usePublic = Has(a, "use_public_ip") ? BoolArg(a, "use_public_ip") ?? throw new McpToolException("use_public_ip 는 true/false 입니다.") : null;
        if (vaultPlan.Count == 0 && usePublic == null) throw new McpToolException("바꿀 항목이 없습니다 (ssh_vault · rdp_vault · use_public_ip).");
        if (usePublic != null && !g.IsCloud && !members.Any(h => h.HasPublicIp))
            throw new McpToolException($"'{g.Name}' 에 공인 IP 가 있는 호스트가 없습니다.");

        var lines = new List<string>();
        var shown = new List<string>();
        var warn = new List<string>();
        foreach (var (proto, v) in vaultPlan)
        {
            var name = proto == Protocol.Rdp ? "RDP" : "SSH";
            var targets = members.Where(h => h.Protocol == proto).ToList();
            if (v == null)
            {
                lines.Add($"{name} Vault 계정: 지정 해제 (호스트 {targets.Count}개{(g.IsCloud ? " + 프로필 기본 계정" : "")})");
                shown.Add(g.IsCloud ? L.F("{0} Vault 계정: 지정 해제 (호스트 {1}개 + 프로필 기본 계정)", name, targets.Count)
                                    : L.F("{0} Vault 계정: 지정 해제 (호스트 {1}개)", name, targets.Count));
                continue;
            }
            lines.Add($"{name} Vault 계정: {v.Name} (호스트 {targets.Count}개{(g.IsCloud ? ", 프로필 기본 계정" : "")})");
            shown.Add(g.IsCloud ? L.F("{0} Vault 계정: {1} (호스트 {2}개, 프로필 기본 계정)", name, v.Name, targets.Count)
                                : L.F("{0} Vault 계정: {1} (호스트 {2}개)", name, v.Name, targets.Count));
            if (!g.IsCloud) continue;
            var keep = GroupVaultKeep(targets, v.Id, isCloud: true, overrideHostVault);
            var other = HostsWithOtherVault(targets, v.Id);
            if (other.Count > 0)
            {
                lines.Add(overrideHostVault ? $"  다른 Vault 계정을 직접 고른 {other.Count}개: 지정 해제" : $"  다른 Vault 계정을 직접 고른 {other.Count}개: 그대로 유지");
                shown.Add(overrideHostVault ? "  " + L.F("다른 Vault 계정을 직접 고른 {0}개: 지정 해제", other.Count)
                                            : "  " + L.F("다른 Vault 계정을 직접 고른 {0}개: 그대로 유지", other.Count));
            }
            var own = targets.Where(h => !keep.Contains(h) && BulkEdit.HasOwnCredentials(h)).ToList();
            if (own.Count > 0)
            {
                lines.Add(clearOwn ? $"  직접 적은 사용자명·비밀번호·키가 있는 {own.Count}개: 지움" : $"  직접 적은 사용자명·비밀번호·키가 있는 {own.Count}개: 유지 (그 값이 우선)");
                shown.Add(clearOwn ? "  " + L.F("직접 적은 사용자명·비밀번호·키가 있는 {0}개: 지움", own.Count)
                                   : "  " + L.F("직접 적은 사용자명·비밀번호·키가 있는 {0}개: 유지 (그 값이 우선)", own.Count));
                if (clearOwn)
                    warn.Add(L.F("⚠ {0} 호스트 {1}개에 직접 적은 사용자명·비밀번호·키 파일을 지웁니다. 지운 비밀번호는 되돌릴 수 없습니다.", name, own.Count));
            }
        }
        if (usePublic is { } up)
        {
            var n = members.Count(h => h.HasPublicIp);
            var tail = g.IsCloud ? ", 프로필 기본값" : "";
            lines.Add($"접속 IP: {(up ? "공인" : "사설")} (공인 IP 가 있는 호스트 {n}개{tail})");
            shown.Add(g.IsCloud ? (up ? L.F("접속 IP: 공인 (공인 IP 가 있는 호스트 {0}개, 프로필 기본값)", n) : L.F("접속 IP: 사설 (공인 IP 가 있는 호스트 {0}개, 프로필 기본값)", n))
                                : (up ? L.F("접속 IP: 공인 (공인 IP 가 있는 호스트 {0}개)", n) : L.F("접속 IP: 사설 (공인 IP 가 있는 호스트 {0}개)", n)));
        }

        var groupLabel = g.IsCloud ? $"[{g.Profile!.ProviderText}] {g.Name}" : g.Name;
        var command = string.Join("\n", lines);
        var desc = (g.IsCloud
                       ? L.F("'{0}' 그룹의 기본값을 바꿉니다. 클라우드 프로필에 저장되어 앞으로 동기화로 추가되는 서버에도 적용됩니다.", groupLabel)
                       : L.F("'{0}' 그룹의 호스트를 바꿉니다.", groupLabel)) +
                   L.T("\nVault 계정이 바뀌면 이 그룹의 서버에 접속할 때 쓰이는 계정이 달라집니다.") +
                   string.Concat(warn.Select(w => "\n" + w));
        if (!await AskApprovalAsync("set_group_defaults", L.T("Claude 가 그룹 기본값 변경을 요청합니다"), desc, new[] { groupLabel }, string.Join("\n", shown)))
        {
            AuditLog.Write("set_group_defaults", new[] { groupLabel }, command, "거부");
            throw new McpToolException("사용자가 그룹 기본값 변경을 거부했습니다.");
        }
        // 승인 창을 기다리는 동안 프로필이 지워졌거나 Vault 계정이 지워졌으면 반영하지 않는다
        if (g.Profile != null && !_profiles.Contains(g.Profile))
        {
            AuditLog.Write("set_group_defaults", new[] { groupLabel }, command, "승인 후 취소 (대기 중 프로필이 지워짐)");
            throw new McpToolException("승인을 기다리는 동안 클라우드 프로필이 지워져 바꾸지 않았습니다.");
        }
        if (vaultPlan.Any(p => p.Vault != null && VaultStore.Get(p.Vault.Id) == null))
        {
            AuditLog.Write("set_group_defaults", new[] { groupLabel }, command, "승인 후 취소 (대기 중 Vault 계정이 지워짐)");
            throw new McpToolException("승인을 기다리는 동안 Vault 계정이 지워져 바꾸지 않았습니다.");
        }

        var result = new List<string>();
        foreach (var (proto, v) in vaultPlan)
        {
            var name = proto == Protocol.Rdp ? "RDP" : "SSH";
            if (v == null) result.Add($"{name} Vault 계정 지정 해제: 호스트 {ClearGroupVaultCore(g, proto)}개" + (g.IsCloud ? " + 프로필 기본 계정" : ""));
            else result.Add($"{name} Vault 계정 '{v.Name}' 지정: 호스트 {ApplyGroupVaultCore(g, proto, v.Id, clearOwn, overrideHostVault)}개" + (g.IsCloud ? " + 프로필 기본 계정" : ""));
        }
        if (usePublic is { } u) result.Add($"{(u ? "공인" : "사설")} IP 접속: 호스트 {SetGroupPublicIpCore(g, u)}개" + (g.IsCloud ? " + 프로필 기본값" : ""));
        AuditLog.Write("set_group_defaults", new[] { groupLabel }, command, "승인·변경");
        return $"'{groupLabel}' 그룹을 바꿨습니다:\n" + string.Join("\n", result);
    }

    // =================== rename_group ===================

    private async Task<string> RenameGroupFromClaude(JsonElement a)
    {
        var g = ResolveGroupSpec(Arg(a, "group"));
        if (!g.IsLocal || g.GroupName.Length == 0)
            throw new McpToolException("직접 등록한 그룹만 이름을 바꿀 수 있습니다 (클라우드 그룹은 프로필 설정, 'Hosts' 는 그룹 없음).");
        var name = VisibleArg(a, "new_name");
        if (name.Length == 0) throw new McpToolException("new_name 이 필요합니다.");
        if (name.Length > 200) throw new McpToolException("new_name 이 너무 깁니다 (최대 200자).");
        if (name == g.GroupName) return "지금 이름과 같아 바꾸지 않았습니다.";
        var oldName = g.GroupName;
        var count = GroupMembers(g).Count;
        // 대소문자만 다른 같은 그룹이 아니라 다른 기존 그룹과 이름이 같으면 합쳐진다
        var merge = CanonicalGroupNames(_hosts).TryGetValue(name, out var existing) && !existing.Equals(oldName, StringComparison.CurrentCultureIgnoreCase)
            ? existing : null;
        var command = $"그룹 이름: {oldName} → {name} (호스트 {count}개)" + (merge != null ? $"\n기존 그룹 '{merge}' 와 합쳐집니다." : "");
        var shown = L.F("그룹 이름: {0} → {1} (호스트 {2}개)", oldName, name, count) + (merge != null ? "\n" + L.F("기존 그룹 '{0}' 와 합쳐집니다.", merge) : "");
        if (!await AskApprovalAsync("rename_group", L.T("Claude 가 그룹 이름 변경을 요청합니다"),
                L.T("직접 등록한 그룹의 이름을 바꿉니다. 서버에는 영향이 없습니다."), new[] { oldName }, shown))
        {
            AuditLog.Write("rename_group", new[] { oldName }, command, "거부");
            throw new McpToolException("사용자가 그룹 이름 변경을 거부했습니다.");
        }
        var n = RenameGroupCore(g, name);
        AuditLog.Write("rename_group", new[] { oldName }, command, $"승인·변경 {n}개");
        return n == 0 ? $"승인을 기다리는 동안 '{oldName}' 그룹의 호스트가 없어져 바뀐 것이 없습니다." : $"그룹 이름을 바꿨습니다: {oldName} → {name} (호스트 {n}개)" + (merge != null ? $", 기존 그룹 '{merge}' 와 합쳐짐" : "");
    }

    // =================== set_group_collapsed ===================

    private string SetGroupCollapsedFromClaude(JsonElement a)
    {
        var collapsed = BoolArg(a, "collapsed") ?? throw new McpToolException("collapsed 는 true/false 입니다.");
        var spec = Arg(a, "group").Trim();
        var verb = collapsed ? "접기" : "펼치기";
        if (spec.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            SetAllGroups(collapsed);
            AuditLog.Write("set_group_collapsed", new[] { "(모든 그룹)" }, verb, "실행");
            return $"화면의 모든 그룹({_groups.Count}개)을 {(collapsed ? "접었습니다" : "폈습니다")}.";
        }
        HostGroup g;
        if (spec.Equals("favorites", StringComparison.OrdinalIgnoreCase) || spec == "즐겨찾기")
            g = _groups.FirstOrDefault(x => x.Key == FavoritesKey) ?? new HostGroup { Key = FavoritesKey, Name = "즐겨찾기" };
        else if (spec.Equals("recent", StringComparison.OrdinalIgnoreCase) || spec == "최근 접속")
            g = _groups.FirstOrDefault(x => x.Key == RecentKey) ?? new HostGroup { Key = RecentKey, Name = "최근 접속" };
        else g = ResolveGroupSpec(spec);
        SetGroupCollapsed(g, collapsed);
        RebuildRows();
        SaveGroupState();
        var label = g.IsCloud ? $"[{g.Profile!.ProviderText}] {g.Name}" : g.Name;
        AuditLog.Write("set_group_collapsed", new[] { label }, verb, "실행");
        return $"'{label}' 그룹을 {(collapsed ? "접었습니다" : "폈습니다")}." +
               (SearchBox.Text.Trim().Length > 0 ? " (지금 검색 중이라 검색을 지울 때까지 모든 그룹이 펼쳐져 보입니다)" : "");
    }
}
