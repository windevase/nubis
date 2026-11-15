using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace Nubis;

// MCP 도구: 클라우드 프로필 조회 · 설정 · 제거 · 동기화 취소 — UI 에서 손으로 하는 동작을 Claude 도 할 수 있게 (BuildTools 에서 등록)
public partial class MainWindow
{
    private IEnumerable<McpTool> CloudProfileTools()
    {
        yield return new McpTool("cloud_list_profiles",
            "클라우드 프로필 목록과 설정을 반환한다: id, 클라우드, 프로필 이름, 표시 이름, 서버 수(실행 중), 마지막 동기화, 동기화 중 여부, " +
            "include_stopped, use_public_ip, ssh/rdp 포트(비어 있으면 설정의 기본 포트), 기본 사용자명, 기본 비밀번호 설정 여부(set/unset 만), " +
            "SSH/RDP 기본 Vault 계정 이름, 목록에서 지운(숨긴) 서버 수. 비밀번호 값은 포함하지 않는다.",
            Schema(new { }),
            _ => OnUi(ListCloudProfilesText));

        yield return new McpTool("cloud_update_profile",
            "클라우드 프로필 설정을 바꾼다 (Cloud 화면의 프로필 설정 · 그룹 우클릭 Vault 지정과 같은 동작, 사용자 승인 필요). 넘긴 항목만 바뀐다. " +
            "use_public_ip 를 바꾸면 기본값을 따르던 서버에 적용되고, override_host_address=true 면 호스트별로 따로 고른 서버도 새 기본값으로 바꾼다. " +
            "ssh_port/rdp_port 를 바꾸면 이전 기본 포트를 쓰던 호스트도 함께 바뀐다 (빈 문자열 또는 'default' = 설정의 기본 포트). " +
            "include_stopped 를 켜면 바로 동기화한다 (조회 API 만 사용). " +
            "ssh_vault/rdp_vault 는 Vault 계정 이름 또는 id ('none' 또는 빈 문자열 = 해제, 해제하면 그 프로토콜 호스트에 직접 고른 계정도 함께 해제 — UI 와 같음). " +
            "지정할 때 다른 Vault 계정을 직접 골라 둔 호스트는 기본으로 유지하고, vault_override_hosts=true 면 그 지정을 해제한다. " +
            "vault_clear_own_credentials=true 면 호스트에 직접 적은 사용자명·비밀번호·키 경로를 지워 Vault 계정만 쓰게 한다 (기본 false). " +
            "unhide_removed=true 면 목록에서 지운 서버를 다음 동기화부터 다시 표시한다. 기본 비밀번호(default_password)는 MCP 로 바꿀 수 없다.",
            Schema(new
            {
                profile = new { type = "string", description = "프로필 id(앞 8자 이상)·표시 이름·프로필 이름 또는 '[AWS] 이름' (cloud_list_profiles 참고)" },
                display_name = new { type = "string", description = "그룹 표시 이름 (빈 문자열 = 프로필 이름 사용)" },
                default_username = new { type = "string" },
                ssh_port = new { type = new[] { "integer", "string" }, description = "1~65535, 빈 문자열 또는 'default' = 설정의 기본 포트" },
                rdp_port = new { type = new[] { "integer", "string" }, description = "1~65535, 빈 문자열 또는 'default' = 설정의 기본 포트" },
                use_public_ip = new { type = "boolean", description = "기본 접속 주소: true = 공인 IP, false = 사설 IP" },
                override_host_address = new { type = "boolean", description = "use_public_ip 변경 시 호스트별로 따로 고른 서버도 바꿀지 (기본 false)" },
                include_stopped = new { type = "boolean", description = "중지된 서버도 가져오기 (켜면 바로 동기화)" },
                ssh_vault = new { type = "string", description = "SSH(Linux) 기본 Vault 계정 이름/id, 'none' = 해제" },
                rdp_vault = new { type = "string", description = "RDP(Windows) 기본 Vault 계정 이름/id, 'none' = 해제" },
                vault_override_hosts = new { type = "boolean", description = "다른 Vault 계정을 직접 골라 둔 호스트의 지정도 해제 (기본 false = 유지)" },
                vault_clear_own_credentials = new { type = "boolean", description = "호스트에 직접 적은 사용자명·비밀번호·키 경로를 지움 (기본 false)" },
                unhide_removed = new { type = "boolean", description = "목록에서 지운 서버 다시 표시 (다음 동기화부터)" },
            }, "profile"),
            a => OnUiAsync(() => UpdateCloudProfileFromClaude(a)));

        yield return new McpTool("cloud_remove_profile",
            "클라우드 프로필과 그 프로필로 동기화된 호스트를 Nubis 에서 제거한다 (사용자 승인 필요, 클라우드의 서버에는 영향 없음). " +
            "다른 그룹 호스트가 이 프로필의 서버를 점프 호스트로 쓰고 있으면 승인 창과 결과에 알린다 (제거 후 그 호스트들은 점프 호스트를 다시 지정해야 접속 가능).",
            Schema(new { profile = new { type = "string", description = "프로필 id(앞 8자 이상)·표시 이름·프로필 이름" } }, "profile"),
            a => OnUiAsync(() => RemoveCloudProfileFromClaude(a)));

        yield return new McpTool("cloud_sync_cancel",
            "진행 중인 클라우드 동기화를 취소한다 (Cloud 화면의 동기화 취소 버튼과 같음 — 이번 조회 결과는 버리고 호스트 목록은 바꾸지 않는다). " +
            "profile 은 프로필 이름 또는 'all'(진행 중인 동기화 전부).",
            Schema(new { profile = new { type = "string" } }, "profile"),
            a => OnUi(() => CancelCloudSyncFromClaude(a)));
    }

    // =================== 프로필 찾기 · 표시 ===================

    private static string CloudProfileLabel(CloudProfile p) => $"[{p.ProviderText}] {McpHelpers.VisibleControlChars(p.GroupName)}";

    /// <summary>id 앞부분(8자 이상) · 표시 이름 · 프로필 이름 · "[클라우드] 이름" 으로 프로필 하나를 찾는다 (정확히 일치 우선, 없으면 부분 일치)</summary>
    private CloudProfile ResolveCloudProfile(string spec)
    {
        spec = spec.Trim();
        if (spec.Length == 0) throw new McpToolException("profile 이 필요합니다. cloud_list_profiles 로 확인하세요.");
        var hit = CloudProfileEdit.Match(_profiles, spec);
        return hit.Count switch
        {
            1 => hit[0],
            0 => throw new McpToolException($"'{spec}' 와 일치하는 클라우드 프로필이 없습니다. cloud_list_profiles 로 확인하세요."),
            _ => throw new McpToolException($"'{spec}' 가 프로필 {hit.Count}개와 일치합니다. id 로 지정하세요:\n" +
                                            string.Join("\n", hit.Select(p => $"{p.Id.ToString()[..8]} | {CloudProfileLabel(p)} ({McpHelpers.VisibleControlChars(p.Name)})"))),
        };
    }

    private static string ProfileVaultName(Guid? id) => id == null ? "없음" : VaultStore.Get(id)?.Name ?? "(삭제된 계정)";

    private string ListCloudProfilesText()
    {
        if (_profiles.Count == 0) return "클라우드 프로필이 없습니다. discover_sources · cloud_add_profile 로 추가할 수 있습니다.";
        var counts = _hosts.Where(h => h.CloudProfileId != null).GroupBy(h => h.CloudProfileId!.Value)
            .ToDictionary(g => g.Key, g => (total: g.Count(), running: g.Count(h => !h.IsStopped)));
        var sb = new StringBuilder($"클라우드 프로필 {_profiles.Count}개\n");
        foreach (var p in _profiles.OrderBy(p => p.Provider).ThenBy(p => p.GroupName, StringComparer.CurrentCultureIgnoreCase))
        {
            var c = counts.GetValueOrDefault(p.Id);
            sb.AppendLine($"- id {p.Id.ToString()[..8]} | {p.ProviderText} | 프로필 {McpHelpers.VisibleControlChars(p.Name)} | 표시 이름 {(p.DisplayName.Length > 0 ? McpHelpers.VisibleControlChars(p.DisplayName) : "(프로필 이름)")}");
            sb.AppendLine($"  서버 {c.total}대 (실행 {c.running}) | 마지막 동기화 {(p.LastSync is { } at ? at.ToString("yyyy-MM-dd HH:mm") : "없음")}" +
                          (p.IsSyncing ? " | 동기화 중" : "") + (p.LastResult.Length > 0 ? $" | 결과 {McpHelpers.VisibleControlChars(p.LastResult)}" : ""));
            sb.AppendLine($"  include_stopped {(p.IncludeStopped ? "true" : "false")} | use_public_ip {(p.UsePublicIp ? "true" : "false")}" +
                          $" | ssh_port {(p.SshPort?.ToString() ?? $"기본({p.EffectiveSshPort})")} | rdp_port {(p.RdpPort?.ToString() ?? $"기본({p.EffectiveRdpPort})")}");
            sb.AppendLine($"  기본 사용자명 {(p.DefaultUsername.Length > 0 ? McpHelpers.VisibleControlChars(p.DefaultUsername) : "-")}" +
                          $" | 기본 비밀번호 {(string.IsNullOrEmpty(p.ProtectedDefaultPassword) ? "unset" : "set")}" +
                          $" | ssh_vault {ProfileVaultName(p.SshVaultId)} | rdp_vault {ProfileVaultName(p.RdpVaultId)} | 숨긴 서버 {p.HiddenInstances.Count}개");
            if (p.StatusText.Length > 0) sb.AppendLine($"  상태 {McpHelpers.VisibleControlChars(p.StatusText)}");
        }
        return sb.ToString();
    }

    // =================== 프로필 설정 변경 ===================

    /// <summary>포트 인자: 숫자 또는 숫자 문자열, 빈 문자열·"default" = null (설정의 기본 포트)</summary>
    private static int? ProfilePortArg(JsonElement a, string name)
    {
        var v = a.GetProperty(name);
        var text = v.ValueKind switch
        {
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.String => v.GetString() ?? "",
            _ => throw new McpToolException($"{name} 은 1~65535 숫자 또는 빈 문자열('default')입니다."),
        };
        if (text.Trim().Equals("default", StringComparison.OrdinalIgnoreCase)) text = "";
        if (!ProfileDialog.TryPort(text, out var port)) throw new McpToolException($"{name} 은 1~65535 숫자 또는 빈 문자열('default')입니다.");
        return port;
    }

    /// <summary>Vault 인자: (지정했는지, 계정 — null 이면 해제)</summary>
    private (bool set, VaultEntry? vault) ProfileVaultArg(JsonElement a, string name)
    {
        if (!Has(a, name)) return (false, null);
        var spec = Arg(a, name).Trim();
        return (true, spec.Length == 0 || spec.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : ResolveVault(spec));
    }

    private async Task<string> UpdateCloudProfileFromClaude(JsonElement a)
    {
        // 비밀번호는 MCP 인자로 받지 않는다 (Claude 대화·로그에 남지 않게) — 사용자가 프로필 설정 창에서 직접 입력
        if (a.ValueKind == JsonValueKind.Object && a.TryGetProperty("default_password", out _))
            throw new McpToolException("default_password 는 MCP 로 바꿀 수 없습니다. 사용자에게 Cloud 화면의 프로필 설정에서 직접 입력해 달라고 안내하세요.");
        var p = ResolveCloudProfile(Arg(a, "profile"));
        // 프로필 설정 창이 열려 있으면 창의 OK 가 이 변경을 덮어쓰므로 바꾸지 않는다
        if (_editingProfile == p) throw new McpToolException("사용자가 이 프로필의 설정 창을 열어 두었습니다. 창을 닫은 뒤 다시 요청하세요.");
        var cur = CloudProfileSettings.From(p);
        var next = cur;
        var changes = new List<string>();
        void Note(string label, object? from, object? to)
        {
            if (!Equals(from, to)) changes.Add($"{label}: {McpHelpers.VisibleControlChars(from?.ToString() ?? "")} → {McpHelpers.VisibleControlChars(to?.ToString() ?? "")}");
        }
        string Visible(string field)
        {
            var v = Arg(a, field).Trim();
            if (McpHelpers.HasInvisibleChars(v)) throw new McpToolException($"{field} 에 보이지 않는 제어 문자·양방향 문자가 있습니다: {McpHelpers.VisibleControlChars(v)}");
            return v;
        }
        string PortText(int? port, Protocol proto) => port?.ToString() ?? $"기본({HostEntry.PreferredPort(proto)})";

        if (Has(a, "display_name")) { var v = Visible("display_name"); Note("표시 이름", cur.DisplayName, v); next = next with { DisplayName = v }; }
        if (Has(a, "default_username")) { var v = Visible("default_username"); Note("기본 사용자명", cur.DefaultUsername, v); next = next with { DefaultUsername = v }; }
        if (Has(a, "ssh_port")) { var v = ProfilePortArg(a, "ssh_port"); Note("SSH 포트", PortText(cur.SshPort, Protocol.Ssh), PortText(v, Protocol.Ssh)); next = next with { SshPort = v }; }
        if (Has(a, "rdp_port")) { var v = ProfilePortArg(a, "rdp_port"); Note("RDP 포트", PortText(cur.RdpPort, Protocol.Rdp), PortText(v, Protocol.Rdp)); next = next with { RdpPort = v }; }
        if (Has(a, "use_public_ip"))
        {
            var v = BoolArg(a, "use_public_ip") ?? throw new McpToolException("use_public_ip 는 true/false 입니다.");
            Note("기본 접속 주소", cur.UsePublicIp ? "공인 IP" : "사설 IP", v ? "공인 IP" : "사설 IP");
            next = next with { UsePublicIp = v };
        }
        if (Has(a, "include_stopped"))
        {
            var v = BoolArg(a, "include_stopped") ?? throw new McpToolException("include_stopped 는 true/false 입니다.");
            Note("중지된 서버 포함", cur.IncludeStopped, v);
            next = next with { IncludeStopped = v };
        }
        var unhide = BoolArg(a, "unhide_removed") == true && p.HiddenInstances.Count > 0;
        if (unhide) changes.Add($"목록에서 지운 서버 {p.HiddenInstances.Count}개 다시 표시 (다음 동기화부터)");

        var (sshSet, sshVault) = ProfileVaultArg(a, "ssh_vault");
        var (rdpSet, rdpVault) = ProfileVaultArg(a, "rdp_vault");
        var overrideHosts = BoolArg(a, "vault_override_hosts") == true;
        var clearOwn = BoolArg(a, "vault_clear_own_credentials") == true;
        var overrideAddress = BoolArg(a, "override_host_address") == true;

        // ----- 호스트에 미칠 영향 (승인 창 · 감사 로그에 함께 보여 준다) -----
        var members = _hosts.Where(h => h.CloudProfileId == p.Id).ToList();
        // 프로필 기본 계정이 이미 같아도 호스트 지정 해제 · 직접 적은 값 삭제가 남아 있으면 반영한다 (UI 에서 같은 계정을 다시 골라도 반영되는 것과 같음)
        if (sshSet && CloudProfileEdit.VaultNoop(members, p, Protocol.Ssh, sshVault?.Id, overrideHosts, clearOwn)) sshSet = false;
        if (rdpSet && CloudProfileEdit.VaultNoop(members, p, Protocol.Rdp, rdpVault?.Id, overrideHosts, clearOwn)) rdpSet = false;
        if (cur.UsePublicIp != next.UsePublicIp)
        {
            var withPublic = members.Count(h => h.HasPublicIp);
            var custom = CloudProfileEdit.CustomAddressCount(members, p, cur.UsePublicIp);
            changes.Add($"  → 공인 IP 가 있는 서버 {withPublic}개 중 기본값을 따르던 {withPublic - custom}개의 접속 주소를 바꿈" +
                        (custom > 0 ? overrideAddress ? $", 호스트별로 따로 고른 {custom}개도 바꿈" : $", 호스트별로 따로 고른 {custom}개는 유지" : ""));
        }
        var newSsh = next.SshPort ?? HostEntry.PreferredPort(Protocol.Ssh);
        var newRdp = next.RdpPort ?? HostEntry.PreferredPort(Protocol.Rdp);
        if (newSsh != p.EffectiveSshPort)
            changes.Add($"  → 포트 {p.EffectiveSshPort} 를 쓰던 SSH 호스트 {members.Count(h => h.Protocol == Protocol.Ssh && h.Port == p.EffectiveSshPort)}개를 {newSsh} 로");
        if (newRdp != p.EffectiveRdpPort)
            changes.Add($"  → 포트 {p.EffectiveRdpPort} 를 쓰던 RDP 호스트 {members.Count(h => h.Protocol == Protocol.Rdp && h.Port == p.EffectiveRdpPort)}개를 {newRdp} 로");
        if (!cur.IncludeStopped && next.IncludeStopped) changes.Add("  → 승인하면 바로 동기화 (조회 API 만 사용)");
        void VaultNote(Protocol proto, bool set, VaultEntry? v)
        {
            if (!set) return;
            var name = proto == Protocol.Rdp ? "RDP" : "SSH";
            changes.Add($"{name} 기본 Vault 계정: {ProfileVaultName(proto == Protocol.Rdp ? p.RdpVaultId : p.SshVaultId)} → {v?.Name ?? "없음"}");
            var targets = members.Where(h => h.Protocol == proto).ToList();
            if (v == null)
            {
                var picked = targets.Count(h => h.VaultId != null);
                changes.Add($"  → {name} 호스트 {targets.Count}개" + (picked > 0 ? $", 호스트에 직접 고른 Vault 계정 {picked}개도 해제" : ""));
                return;
            }
            var keep = GroupVaultKeep(targets, v.Id, isCloud: true, overrideHosts);
            var others = HostsWithOtherVault(targets, v.Id).Count;
            var own = targets.Count(h => !keep.Contains(h) && BulkEdit.HasOwnCredentials(h));
            changes.Add($"  → {name} 호스트 {targets.Count}개가 이 계정을 따름" +
                        (others > 0 ? overrideHosts ? $", 다른 계정을 직접 고른 {others}개의 지정도 해제" : $", 다른 계정을 직접 고른 {others}개는 유지" : "") +
                        (own > 0 ? clearOwn ? $", 직접 적은 사용자명·비밀번호·키 {own}개 삭제" : $", 직접 적은 사용자명·비밀번호·키가 있는 {own}개는 그 값이 우선" : ""));
        }
        VaultNote(Protocol.Ssh, sshSet, sshVault);
        VaultNote(Protocol.Rdp, rdpSet, rdpVault);

        var settingsChanged = next != cur || unhide;
        if (!settingsChanged && !sshSet && !rdpSet) return "바뀐 항목이 없습니다.";

        var label = CloudProfileLabel(p);
        var text = string.Join("\n", changes);
        var otherBefore = (ssh: p.SshVaultId, rdp: p.RdpVaultId, hidden: p.HiddenInstances.Count);
        if (!await AskApprovalAsync("cloud_update_profile", L.T("Claude 가 클라우드 프로필 설정 변경을 요청합니다"),
                L.F("'{0}' 프로필의 설정을 바꿉니다. 이 프로필 서버들의 접속 주소·포트·로그인 계정이 함께 바뀔 수 있으니 아래 내용을 확인하세요.", label),
                new[] { label }, text))
        {
            AuditLog.Write("cloud_update_profile", new[] { label }, text, "거부");
            throw new McpToolException("사용자가 프로필 설정 변경을 거부했습니다.");
        }
        // 승인을 기다리는 동안 프로필이 제거됐거나 다른 경로(프로필 설정 창 · 그룹 메뉴)로 바뀌었으면 사용자가 본 내용과 달라지므로 반영하지 않는다
        if (!_profiles.Contains(p))
        {
            AuditLog.Write("cloud_update_profile", new[] { label }, text, "승인 후 취소 (대기 중 프로필이 제거됨)");
            throw new McpToolException("승인을 기다리는 동안 프로필이 제거되어 바꾸지 않았습니다.");
        }
        if (_editingProfile == p)
        {
            AuditLog.Write("cloud_update_profile", new[] { label }, text, "승인 후 취소 (프로필 설정 창이 열려 있음)");
            throw new McpToolException("승인을 기다리는 동안 사용자가 이 프로필의 설정 창을 열어 바꾸지 않았습니다. 창을 닫은 뒤 다시 요청하세요.");
        }
        if (CloudProfileSettings.From(p) != cur || (p.SshVaultId, p.RdpVaultId, p.HiddenInstances.Count) != otherBefore || (sshSet && sshVault != null && VaultStore.Get(sshVault.Id) == null) ||
            (rdpSet && rdpVault != null && VaultStore.Get(rdpVault.Id) == null))
        {
            AuditLog.Write("cloud_update_profile", new[] { label }, text, "승인 후 취소 (대기 중 프로필 설정 또는 Vault 계정이 바뀜)");
            throw new McpToolException("승인을 기다리는 동안 프로필 설정 또는 Vault 계정이 바뀌어 반영하지 않았습니다. cloud_list_profiles 로 확인한 뒤 다시 요청하세요.");
        }

        // UI 프로필 설정(EditProfile)과 같은 순서: 값 반영 → 호스트 주소·포트 반영 → 저장 · (중지 서버 포함을 켰으면) 동기화
        var before = CloudProfileEdit.Snapshot(p);
        CloudProfileEdit.ApplySettings(p, next, unhide);
        CloudProfileEdit.ApplyCascade(_hosts, p, before, overrideAddress);
        // 그룹 우클릭 Vault 지정 · 해제와 같은 반영 (MainWindow.Groups.cs 공용 함수)
        void ApplyVault(Protocol proto, bool set, VaultEntry? v)
        {
            if (!set) return;
            var targets = _hosts.Where(h => h.CloudProfileId == p.Id && h.Protocol == proto).ToList();
            if (v == null) ClearGroupVaultOn(targets, p, proto);
            else ApplyGroupVaultTo(targets, p, proto, v.Id, clearOwn, GroupVaultKeep(targets, v.Id, isCloud: true, overrideHosts));
        }
        ApplyVault(Protocol.Ssh, sshSet, sshVault);
        ApplyVault(Protocol.Rdp, rdpSet, rdpVault);
        var syncing = FinishProfileEdit(p, before);
        if ((sshSet || rdpSet) && VaultPage.Visibility == Visibility.Visible) RefreshVault();

        AuditLog.Write("cloud_update_profile", new[] { label }, text, "승인·변경" + (syncing ? " · 동기화 시작" : ""));
        return $"바꿨습니다: {label}\n{text}" +
               (syncing ? "\n중지된 서버를 가져오도록 동기화를 시작했습니다. cloud_sync 로 같은 프로필을 부르면 끝날 때까지 기다려 결과를 볼 수 있습니다." : "") +
               (!cur.IncludeStopped && next.IncludeStopped && !syncing ? "\nhosts.json 을 읽지 못한 상태라 자동 동기화하지 않았습니다." : "");
    }

    // =================== 프로필 제거 ===================

    private async Task<string> RemoveCloudProfileFromClaude(JsonElement a)
    {
        var p = ResolveCloudProfile(Arg(a, "profile"));
        var label = CloudProfileLabel(p);
        var count = _hosts.Count(h => h.CloudProfileId == p.Id);
        var dependents = CloudProfileEdit.JumpDependents(_hosts, p);
        var depNames = dependents.Select(h => McpHelpers.VisibleControlChars(h.DisplayName)).ToList();
        // 승인 창 문구는 UI 프로필 제거 확인 창과 같은 문구 (번역 공용)
        var description = L.F("[{0}] {1} 프로필과 동기화된 호스트 {2}개를 제거할까요?\n(클라우드의 서버에는 아무 영향이 없습니다)",
            p.ProviderText, McpHelpers.VisibleControlChars(p.GroupName), count);
        if (dependents.Count > 0)
            description += "\n\n" + L.F("주의: 이 프로필의 서버를 점프 호스트로 쓰는 다른 호스트 {0}개가 있습니다 — 제거하면 점프 호스트를 다시 지정해야 접속할 수 있습니다: {1}",
                dependents.Count, NameList(depNames));
        var command = $"프로필 제거: {label} ({McpHelpers.VisibleControlChars(p.Name)}) — 동기화된 호스트 {count}개를 Nubis 목록에서 제거" +
                      (dependents.Count > 0 ? $"\n이 프로필 서버를 점프 호스트로 쓰는 다른 호스트 {dependents.Count}개: {string.Join(", ", depNames.Take(30))}" +
                                              (depNames.Count > 30 ? $" 외 {depNames.Count - 30}개" : "") : "");
        if (!await AskApprovalAsync("cloud_remove_profile", L.T("Claude 가 클라우드 프로필 제거를 요청합니다"), description, new[] { label }, command))
        {
            AuditLog.Write("cloud_remove_profile", new[] { label }, command, "거부");
            throw new McpToolException("사용자가 프로필 제거를 거부했습니다.");
        }
        if (!_profiles.Contains(p))
        {
            AuditLog.Write("cloud_remove_profile", new[] { label }, command, "승인 (대기 중 이미 제거됨)");
            return $"승인을 기다리는 동안 이미 제거된 프로필입니다: {label}";
        }
        // 승인을 기다리는 동안 동기화 · 호스트 편집으로 점프 호스트 관계가 바뀌었을 수 있어 결과 안내는 지금 기준으로 다시 센다
        dependents = CloudProfileEdit.JumpDependents(_hosts, p);
        depNames = dependents.Select(h => McpHelpers.VisibleControlChars(h.DisplayName)).ToList();
        var removed = RemoveProfile(p);
        AuditLog.Write("cloud_remove_profile", new[] { label }, command, $"승인·제거 (호스트 {removed}개)");
        return $"제거했습니다: {label} ({McpHelpers.VisibleControlChars(p.Name)}) — 호스트 {removed}개" +
               (dependents.Count > 0 ? $"\n주의: 이 프로필 서버를 점프 호스트로 쓰던 호스트 {dependents.Count}개는 점프 호스트를 다시 지정해야 접속할 수 있습니다 (update_host 또는 호스트 편집): " +
                                       string.Join(", ", depNames.Take(30)) + (depNames.Count > 30 ? $" 외 {depNames.Count - 30}개" : "") : "");
    }

    // =================== 동기화 취소 ===================

    /// <summary>
    /// UI 의 동기화 취소 버튼(SyncOrCancelAsync)과 같다 — 조회 결과를 버릴 뿐 호스트·프로필은 바꾸지 않으므로 승인 없이 실행하고 감사 로그만 남긴다
    /// </summary>
    private string CancelCloudSyncFromClaude(JsonElement a)
    {
        var spec = Arg(a, "profile").Trim();
        var targets = spec.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? _profiles.Where(p => p.IsSyncing).ToList()
            : new List<CloudProfile> { ResolveCloudProfile(spec) };
        var cancelled = new List<string>();
        var idle = new List<string>();
        foreach (var p in targets)
        {
            if (_syncCancels.TryGetValue(p, out var cts)) { cts.Cancel(); cancelled.Add(CloudProfileLabel(p)); }
            else idle.Add(CloudProfileLabel(p));
        }
        if (cancelled.Count == 0)
            return spec.Equals("all", StringComparison.OrdinalIgnoreCase) ? "진행 중인 동기화가 없습니다." : $"{string.Join(", ", idle)} 은 동기화 중이 아닙니다.";
        AuditLog.Write("cloud_sync_cancel", cancelled, "sync cancel", "취소 요청");
        return $"동기화 취소를 요청했습니다 (이번 조회 결과는 반영하지 않음): {string.Join(", ", cancelled)}" +
               (idle.Count > 0 ? $"\n동기화 중이 아님: {string.Join(", ", idle)}" : "");
    }
}

/// <summary>클라우드 프로필 설정 창(ProfileDialog)에서 바꾸는 값 (기본 비밀번호 제외 — MCP 로는 바꾸지 않는다)</summary>
internal sealed record CloudProfileSettings(string DisplayName, string DefaultUsername, int? SshPort, int? RdpPort, bool UsePublicIp, bool IncludeStopped)
{
    public static CloudProfileSettings From(CloudProfile p) => new(p.DisplayName, p.DefaultUsername, p.SshPort, p.RdpPort, p.UsePublicIp, p.IncludeStopped);
}

/// <summary>
/// 클라우드 프로필 설정 변경 · 제거의 판단과 반영 (UI 프로필 설정 · 프로필 제거와 MCP cloud_update_profile · cloud_remove_profile 공용, UI 와 무관 — 테스트 대상)
/// </summary>
internal static class CloudProfileEdit
{
    /// <summary>설정을 바꾸기 전 값 (호스트 반영 판단용)</summary>
    public readonly record struct Before(int SshPort, int RdpPort, bool UsePublicIp, bool IncludeStopped);

    public static Before Snapshot(CloudProfile p) => new(p.EffectiveSshPort, p.EffectiveRdpPort, p.UsePublicIp, p.IncludeStopped);

    public static void ApplySettings(CloudProfile p, CloudProfileSettings s, bool unhideRemoved)
    {
        p.DisplayName = s.DisplayName;
        p.DefaultUsername = s.DefaultUsername;
        p.SshPort = s.SshPort;
        p.RdpPort = s.RdpPort;
        p.UsePublicIp = s.UsePublicIp;
        p.IncludeStopped = s.IncludeStopped;
        if (unhideRemoved) p.HiddenInstances.Clear();
    }

    /// <summary>공인 IP 가 있으면서 호스트별로 이전 기본값과 다른 접속 주소를 고른 서버 수 (기본값을 바꿀 때 함께 바꿀지 물을 대상)</summary>
    public static int CustomAddressCount(IEnumerable<HostEntry> hosts, CloudProfile p, bool oldPublic) =>
        hosts.Count(h => h.CloudProfileId == p.Id && h.HasPublicIp && h.UsePublicIp != oldPublic);

    /// <summary>
    /// 바뀐 기본 접속 주소 · 포트를 프로필 호스트에 반영. 주소: 기본값을 따르던 서버(overrideCustom 이면 따로 고른 서버도),
    /// 포트: 이전 기본 포트를 쓰던 호스트만. 바꾼 (주소, 포트) 호스트 수를 돌려준다
    /// </summary>
    public static (int address, int port) ApplyCascade(IEnumerable<HostEntry> hosts, CloudProfile p, Before before, bool overrideCustom)
    {
        int address = 0, port = 0;
        var mine = hosts.Where(h => h.CloudProfileId == p.Id).ToList();
        if (before.UsePublicIp != p.UsePublicIp)
            foreach (var h in mine.Where(h => h.HasPublicIp && (overrideCustom || h.UsePublicIp == before.UsePublicIp)))
            {
                if (h.UsePublicIp != p.UsePublicIp) address++;
                h.UsePublicIp = p.UsePublicIp;
            }
        foreach (var h in mine)
        {
            if (h.Protocol == Protocol.Ssh && h.Port == before.SshPort && h.Port != p.EffectiveSshPort) { h.Port = p.EffectiveSshPort; port++; }
            if (h.Protocol == Protocol.Rdp && h.Port == before.RdpPort && h.Port != p.EffectiveRdpPort) { h.Port = p.EffectiveRdpPort; port++; }
        }
        return (address, port);
    }

    /// <summary>
    /// MCP Vault 지정이 아무것도 바꾸지 않는가: 프로필 기본 계정이 이미 vaultId 이고, 해제(null)면 그 프로토콜 호스트에 직접 고른 계정이 없고,
    /// 지정이면 유지 대상이 아닌 호스트에 지울 지정 · (clearOwn 이면) 직접 적은 값이 없을 때 (MainWindow.GroupVaultKeep 과 같은 기준)
    /// </summary>
    public static bool VaultNoop(IEnumerable<HostEntry> hosts, CloudProfile p, Protocol proto, Guid? vaultId, bool overrideHosts, bool clearOwn)
    {
        if ((proto == Protocol.Rdp ? p.RdpVaultId : p.SshVaultId) != vaultId) return false;
        var targets = hosts.Where(h => h.CloudProfileId == p.Id && h.Protocol == proto).ToList();
        if (vaultId is not { } id) return targets.All(h => h.VaultId == null);
        var keep = MainWindow.GroupVaultKeep(targets, id, isCloud: true, overrideHosts);
        return targets.Where(h => !keep.Contains(h)).All(h => h.VaultId == null && !(clearOwn && BulkEdit.HasOwnCredentials(h)));
    }

    /// <summary>"중지된 서버 포함" 을 켰으면 바로 동기화해야 한다 (끄는 쪽은 기존 호스트를 지우지 않음)</summary>
    public static bool NeedsSyncAfter(Before before, CloudProfile p) => !before.IncludeStopped && p.IncludeStopped;

    /// <summary>다른 프로필·그룹의 호스트 중 이 프로필의 서버를 점프 호스트로 쓰는 호스트 (프로필을 지우면 접속하지 못함)</summary>
    public static List<HostEntry> JumpDependents(IReadOnlyCollection<HostEntry> hosts, CloudProfile p)
    {
        var ids = hosts.Where(h => h.CloudProfileId == p.Id).Select(h => h.Id).ToHashSet();
        return hosts.Where(h => h.CloudProfileId != p.Id && h.JumpHostId is { } j && ids.Contains(j)).ToList();
    }

    /// <summary>MCP 프로필 지정: 정확히 일치(id 앞 8자 이상 · 표시 이름 · 프로필 이름 · "[클라우드] 이름")가 있으면 그것, 없으면 이름 부분 일치</summary>
    public static List<CloudProfile> Match(IEnumerable<CloudProfile> profiles, string spec)
    {
        var all = profiles.ToList();
        bool Exact(CloudProfile p) =>
            (spec.Length >= 8 && p.Id.ToString().StartsWith(spec, StringComparison.OrdinalIgnoreCase)) ||
            new[] { p.GroupName, p.Name, $"[{p.ProviderText}] {p.GroupName}" }.Any(n => n.Equals(spec, StringComparison.OrdinalIgnoreCase));
        var hit = all.Where(Exact).ToList();
        if (hit.Count > 0) return hit;
        return all.Where(p => new[] { p.GroupName, p.Name }.Any(n => n.Contains(spec, StringComparison.OrdinalIgnoreCase))).ToList();
    }
}
