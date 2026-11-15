using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Nubis;

public partial class MainWindow
{
    // =================== 그룹 우클릭 메뉴 (일괄 작업) ===================

    private static HostGroup? GroupOf(ContextMenu menu) =>
        menu.PlacementTarget is FrameworkElement { DataContext: HostGroup g } ? g : null;

    private CloudProfile? ProfileOf(HostEntry h) =>
        h.CloudProfileId is { } id ? _profiles.FirstOrDefault(p => p.Id == id) : null;

    /// <summary>메뉴를 열 때마다 그룹에 맞게 항목을 만든다 (SSH/RDP 호스트 수, 공인 IP 유무, 클라우드 여부)</summary>
    private void OnGroupMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu || GroupOf(menu) is not { } g) return;
        menu.Items.Clear();
        void Add(string header, Action act, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => act();
            menu.Items.Add(item);
        }
        void Sep() { if (menu.Items.Count > 0 && menu.Items[^1] is not Separator) menu.Items.Add(new Separator()); }

        // 연결·선택은 화면에 보이는 호스트, 계정·공인 IP 일괄 변경은 그룹 전체 (프로필 기본값도 프로필 전체에 적용되므로)
        var hosts = g.Hosts;
        var all = GroupMembers(g);
        var ssh = all.Count(h => h.Protocol == Protocol.Ssh);
        var rdp = all.Count(h => h.Protocol == Protocol.Rdp);
        var running = hosts.Count(h => !h.IsStopped);

        Add(L.F("모두 연결 ({0}개)", running), () => ConnectGroup(g), running > 0);
        Add(L.F("호스트 {0}개 모두 선택 (일괄 편집)", hosts.Count), () => SelectGroup(g), hosts.Count > 0);
        Sep();
        if (ssh > 0) Add(L.F("SSH 호스트 {0}개에 Vault 계정 지정...", ssh), () => ApplyGroupVault(g, Protocol.Ssh));
        if (rdp > 0) Add(L.F("RDP 호스트 {0}개에 Vault 계정 지정...", rdp), () => ApplyGroupVault(g, Protocol.Rdp));
        // 해제도 SSH · RDP 따로 (한쪽만 해제하려다 다른 쪽 기본 계정까지 지우지 않게)
        foreach (var proto in new[] { Protocol.Ssh, Protocol.Rdp })
        {
            var profileVault = proto == Protocol.Rdp ? g.Profile?.RdpVaultId : g.Profile?.SshVaultId;
            if (all.Any(h => h.Protocol == proto && h.VaultId != null) || profileVault != null)
                Add(L.F("{0} 호스트 Vault 계정 지정 해제 (기본 계정 따르기)", proto == Protocol.Rdp ? "RDP" : "SSH"), () => ClearGroupVault(g, proto));
        }
        Sep();
        var withPublic = all.Count(h => h.HasPublicIp);
        if (withPublic > 0)
        {
            Add(L.F("공인 IP로 접속하도록 일괄 변경 ({0}개)", withPublic), () => SetGroupPublicIp(g, true), all.Any(h => h.HasPublicIp && !h.UsePublicIp));
            Add(L.F("사설 IP로 접속하도록 일괄 변경 ({0}개)", withPublic), () => SetGroupPublicIp(g, false), all.Any(h => h.UsePublicIp));
        }
        Sep();
        if (g.IsLocal && g.GroupName.Length > 0) Add(L.T("그룹 이름 바꾸기..."), () => RenameGroup(g));
        if (g.Profile is { } p)
        {
            // 동기화 중이면 같은 자리에서 취소 (헤더 버튼과 같은 SyncOrCancelAsync)
            Add(p.IsSyncing ? L.T("동기화 취소") : L.T("동기화"), () => _ = SyncOrCancelAsync(p));
            Add(L.T("CLI 로그인 (이 프로필)"), () => RunProviderLogin(p));
            Add(L.T("프로필 설정..."), () => EditProfile(p));
        }
        Sep();
        Add(L.T("모든 그룹 펼치기"), () => SetAllGroups(false));
        Add(L.T("모든 그룹 접기"), () => SetAllGroups(true));
        if (menu.Items.Count > 0 && menu.Items[^1] is Separator last) menu.Items.Remove(last);
    }

    private void ConnectGroup(HostGroup g) => ConnectMany(g.Hosts, g.Name);

    /// <summary>그룹 전체 호스트 (검색어·"중지된 서버 제외" 로 화면에서 숨겨진 호스트 포함). 즐겨찾기·최근 접속은 보이는 그대로</summary>
    private List<HostEntry> GroupMembers(HostGroup g) => GroupMembers(_hosts, g);

    internal static List<HostEntry> GroupMembers(IEnumerable<HostEntry> all, HostGroup g)
    {
        if (g.Profile is { } p) return all.Where(h => h.CloudProfileId == p.Id).ToList();
        if (g.IsVirtual) return g.Hosts.ToList();
        // 직접 등록한 그룹: RefreshHosts 와 같은 기준 (앞뒤 공백 무시, 대소문자만 다른 이름은 같은 그룹)
        return all.Where(h => !h.IsCloud && string.Equals(string.IsNullOrWhiteSpace(h.Group) ? "" : h.Group.Trim(), g.GroupName,
            StringComparison.CurrentCultureIgnoreCase)).ToList();
    }

    /// <summary>
    /// 그룹의 SSH(또는 RDP) 호스트에 Vault 계정을 한 번에 지정.
    /// 클라우드 그룹은 프로필의 기본 계정으로 저장해, 앞으로 동기화로 추가되는 서버도 그 계정을 쓴다.
    /// </summary>
    private void ApplyGroupVault(HostGroup g, Protocol proto)
    {
        var targets = GroupMembers(g).Where(h => h.Protocol == proto).ToList();
        if (targets.Count == 0) return;
        var current = g.Profile is { } pr ? (proto == Protocol.Rdp ? pr.RdpVaultId : pr.SshVaultId) : targets.Select(h => h.VaultId).FirstOrDefault(id => id != null);
        var sub = g.IsCloud
            ? L.F("'{0}' 의 {1} 서버 {2}개가 이 계정으로 로그인합니다. 앞으로 동기화로 추가되는 서버에도 적용됩니다.", g.Name, proto == Protocol.Rdp ? "RDP" : "SSH", targets.Count)
            : L.F("'{0}' 의 {1} 호스트 {2}개가 이 계정으로 로그인합니다.", g.Name, proto == Protocol.Rdp ? "RDP" : "SSH", targets.Count);
        if (VaultPickerDialog.Pick(this, current, proto, sub, allowClear: true) is not { } id) return;
        // "지정 해제": 우클릭 메뉴의 "Vault 계정 지정 해제" 와 같다 (고르기 창에서 이미 눌렀으므로 다시 묻지 않음)
        if (id == VaultPickerDialog.Cleared) { ClearGroupVault(g, proto, confirm: false); return; }
        if (VaultStore.Get(id) is not { } v) return;

        // 일반 그룹은 호스트에 직접 지정 → Vault 계정이 우선이라 묻지 않는다.
        // 클라우드 그룹은 프로필 기본 계정으로 저장 → 기본 계정은 호스트에 적힌 값보다 뒤라 지울지 묻는다
        var clearOwn = false;
        var overrideHostVault = false;
        if (g.IsCloud)
        {
            // 호스트마다 다른 Vault 계정을 직접 골라 둔 경우 — 예전에는 묻지 않고 지정을 지워 프로필 기본 계정으로 바꿨다
            var picked = HostsWithOtherVault(targets, id);
            if (picked.Count > 0)
            {
                var choice = Dialog.Choose(this,
                    L.F("이 중 {0}개 호스트에는 다른 Vault 계정을 직접 골라 두었습니다 (그 계정이 프로필 기본 계정보다 우선).\n그 지정을 해제하고 '{1}' 계정을 쓸까요?", picked.Count, v.Name),
                    L.T("Vault 계정 지정"), L.T("해제하고 이 계정 사용"), L.T("직접 고른 계정 유지"), L.T("취소"),
                    MessageBoxImage.Question, string.Join("\n", picked.Take(15).Select(h => h.DisplayName)) + (picked.Count > 15 ? "\n..." : ""));
                if (choice is not (0 or 1)) return;
                overrideHostVault = choice == 0;
            }
            var keep = GroupVaultKeep(targets, id, isCloud: true, overrideHostVault);
            // 직접 고른 계정을 유지하는 호스트는 Vault 계정이 우선이라 직접 적은 값을 물을 필요가 없다
            if (AskClearOwnCredentials(targets.Where(h => !keep.Contains(h)).ToList(), v) is not { } answer) return;
            clearOwn = answer;
        }

        ApplyGroupVaultCore(g, proto, id, clearOwn, overrideHostVault);
        FlashHostStatus(L.F("'{0}' 에 Vault 계정 '{1}' 을 지정했습니다", g.Name, v.Name));
    }

    /// <summary>
    /// 그룹 Vault 계정 지정의 실제 반영 (우클릭 메뉴와 MCP set_group_defaults 공통, 묻는 창 없음).
    /// clearOwn: 직접 적은 사용자명·비밀번호·키를 지울지 (클라우드 그룹만, 유지하는 호스트 제외).
    /// overrideHostVault: 클라우드 그룹에서 다른 Vault 계정을 직접 골라 둔 호스트의 지정도 해제할지. 바꾼 호스트 수를 돌려준다
    /// </summary>
    private int ApplyGroupVaultCore(HostGroup g, Protocol proto, Guid id, bool clearOwn, bool overrideHostVault)
    {
        var targets = GroupMembers(g).Where(h => h.Protocol == proto).ToList();
        var keep = GroupVaultKeep(targets, id, g.IsCloud, overrideHostVault);
        var n = ApplyGroupVaultTo(targets, g.Profile, proto, id, g.IsCloud && clearOwn, keep);
        if (g.Profile != null) SaveProfiles();
        SaveHosts();
        RefreshHosts();
        if (VaultPage.Visibility == Visibility.Visible) RefreshVault();
        return n;
    }

    /// <summary>클라우드 그룹에 지정할 계정(id)과 다른, 지금 있는 Vault 계정을 직접 골라 둔 호스트</summary>
    internal static List<HostEntry> HostsWithOtherVault(IEnumerable<HostEntry> targets, Guid id) =>
        targets.Where(h => h.VaultId != null && h.VaultId != id && VaultStore.Get(h.VaultId) != null).ToList();

    /// <summary>
    /// 그룹 Vault 지정에서 호스트의 지정을 그대로 둘 호스트. 일반 그룹은 없음.
    /// 클라우드 그룹: overrideHostVault 가 아니면 다른 계정을 직접 골라 둔 호스트,
    /// 그리고 이미 이 계정을 직접 골라 두고 숨겨 둔 값(직접 적은 사용자명·비밀번호·키)이 있는 호스트 —
    /// 지정을 비우면 숨겨 둔 값이 프로필 기본 계정보다 우선이 되어 로그인 계정이 바뀐다 (지금과 같은 계정으로 로그인)
    /// </summary>
    internal static HashSet<HostEntry> GroupVaultKeep(IReadOnlyList<HostEntry> targets, Guid id, bool isCloud, bool overrideHostVault)
    {
        var keep = new HashSet<HostEntry>();
        if (!isCloud) return keep;
        if (!overrideHostVault) keep.UnionWith(HostsWithOtherVault(targets, id));
        keep.UnionWith(targets.Where(h => h.VaultId == id && BulkEdit.HasOwnCredentials(h)));
        return keep;
    }

    /// <summary>그룹 Vault 지정을 호스트·프로필 값에 반영 (저장은 호출한 쪽). 클라우드 그룹(profile 있음)은 호스트를 비워 프로필 기본 계정을 따르게</summary>
    internal static int ApplyGroupVaultTo(IReadOnlyList<HostEntry> targets, CloudProfile? profile, Protocol proto, Guid id, bool clearOwn, IReadOnlySet<HostEntry> keep)
    {
        var n = 0;
        foreach (var h in targets)
        {
            if (keep.Contains(h)) continue;
            if (clearOwn) BulkEdit.ClearCredentials(h);
            h.VaultId = profile != null ? null : id;
            n++;
        }
        if (profile != null)
        {
            if (proto == Protocol.Rdp) profile.RdpVaultId = id; else profile.SshVaultId = id;
        }
        return n;
    }

    /// <summary>
    /// 기본 Vault 계정(클라우드 프로필)을 지정할 호스트 중 직접 적은 계정(그 값이 기본 계정보다 우선)이 있으면 지울지 묻는다.
    /// 지우면 true, 유지하면 false, 취소면 null. 해당 호스트가 없으면 묻지 않고 false
    /// </summary>
    private bool? AskClearOwnCredentials(IReadOnlyList<HostEntry> targets, VaultEntry v)
    {
        var own = targets.Where(BulkEdit.HasOwnCredentials).ToList();
        if (own.Count == 0) return false;
        var choice = Dialog.Choose(this,
            L.F("이 중 {0}개 호스트에는 사용자명·비밀번호·키 파일을 직접 적어 두어 그 값이 Vault 보다 우선합니다.\n직접 적은 값을 지우고 '{1}' 계정만 쓸까요?", own.Count, v.Name),
            L.T("Vault 계정 지정"), L.T("지우고 Vault 계정 사용"), L.T("직접 적은 값 유지"), L.T("취소"),
            MessageBoxImage.Question, string.Join("\n", own.Take(15).Select(h => h.DisplayName)) + (own.Count > 15 ? "\n..." : ""));
        return choice switch { 0 => true, 1 => false, _ => null };
    }

    /// <summary>그룹의 SSH(또는 RDP) 호스트에 지정한 Vault 계정과, 클라우드 그룹이면 프로필의 같은 프로토콜 기본 계정을 해제</summary>
    private void ClearGroupVault(HostGroup g, Protocol proto, bool confirm = true)
    {
        var name = proto == Protocol.Rdp ? "RDP" : "SSH";
        if (confirm && Dialog.Show(this, L.F("'{0}' 의 {1} 호스트에 지정한 Vault 계정을 해제할까요?\n해제한 호스트는 직접 적은 값 → 프로필 기본 계정 → 설정의 기본 Vault 계정 순으로 씁니다.", g.Name, name),
                L.T("Vault 계정 해제"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        ClearGroupVaultCore(g, proto);
        FlashHostStatus(L.F("'{0}' 의 {1} Vault 계정 지정을 해제했습니다", g.Name, name));
    }

    /// <summary>그룹 Vault 해제의 실제 반영 (우클릭 메뉴 · MCP 공통, 묻는 창 없음). 지정이 있던 호스트 수</summary>
    private int ClearGroupVaultCore(HostGroup g, Protocol proto)
    {
        var n = ClearGroupVaultOn(GroupMembers(g), g.Profile, proto);
        if (g.Profile != null) SaveProfiles();
        SaveHosts();
        RefreshHosts();
        if (VaultPage.Visibility == Visibility.Visible) RefreshVault();
        return n;
    }

    internal static int ClearGroupVaultOn(IEnumerable<HostEntry> members, CloudProfile? profile, Protocol proto)
    {
        var n = 0;
        foreach (var h in members.Where(h => h.Protocol == proto))
        {
            if (h.VaultId != null) n++;
            h.VaultId = null;
        }
        if (profile != null)
        {
            if (proto == Protocol.Rdp) profile.RdpVaultId = null; else profile.SshVaultId = null;
        }
        return n;
    }

    /// <summary>그룹의 공인 IP 가 있는 호스트 전체를 공인/사설 IP 접속으로. 클라우드 그룹은 프로필 기본값도 바꿔 새 서버에도 적용</summary>
    private void SetGroupPublicIp(HostGroup g, bool usePublic)
    {
        var count = SetGroupPublicIpCore(g, usePublic);
        FlashHostStatus(usePublic ? L.F("호스트 {0}개를 공인 IP 접속으로 바꿨습니다", count)
                                  : L.F("호스트 {0}개를 사설 IP 접속으로 바꿨습니다", count));
    }

    /// <summary>그룹 공인/사설 IP 접속 일괄 변경의 실제 반영 (우클릭 메뉴 · MCP 공통). 대상(공인 IP 가 있는) 호스트 수</summary>
    private int SetGroupPublicIpCore(HostGroup g, bool usePublic)
    {
        var n = SetGroupPublicIpOn(GroupMembers(g), g.Profile, usePublic);
        if (g.Profile != null) SaveProfiles();
        SaveHosts();
        RefreshHosts();
        return n;
    }

    internal static int SetGroupPublicIpOn(IEnumerable<HostEntry> members, CloudProfile? profile, bool usePublic)
    {
        var targets = members.Where(h => h.HasPublicIp).ToList();
        foreach (var h in targets) h.UsePublicIp = usePublic;
        if (profile != null) profile.UsePublicIp = usePublic;
        return targets.Count;
    }

    private void RenameGroup(HostGroup g)
    {
        if (PromptDialog.Ask(this, L.T("그룹 이름 바꾸기"), L.F("'{0}' 그룹의 새 이름", g.GroupName), g.GroupName)?.Trim() is not { Length: > 0 } name
            || name == g.GroupName) return;
        RenameGroupCore(g, name);
    }

    /// <summary>직접 등록한 그룹 이름 바꾸기의 실제 반영 (우클릭 메뉴 · MCP 공통). 바꾼 호스트 수</summary>
    private int RenameGroupCore(HostGroup g, string name)
    {
        var n = RenameLocalGroup(_hosts, _collapsedLocal, g.GroupName, g.Key, name);
        SaveGroupState();
        SaveHosts();
        RefreshHosts();
        return n;
    }

    internal static int RenameLocalGroup(IReadOnlyCollection<HostEntry> all, HashSet<string> collapsed, string oldName, string oldKey, string newName)
    {
        var n = 0;
        foreach (var h in all.Where(h => !h.IsCloud && string.Equals(h.Group?.Trim(), oldName, StringComparison.CurrentCultureIgnoreCase)))
        {
            h.Group = newName;
            n++;
        }
        // 접힘 상태는 새 이름의 키로 (이미 있는 그룹과 합쳐졌으면 그 그룹의 표기로)
        if (collapsed.Remove(oldKey) && CanonicalGroupNames(all).TryGetValue(newName, out var canon)) collapsed.Add("L:" + canon);
        return n;
    }

    private void SetAllGroups(bool collapsed)
    {
        foreach (var g in _groups) SetGroupCollapsed(g, collapsed);
        RebuildRows();
        SaveGroupState();
    }

    /// <summary>
    /// 직접 등록한 호스트들이 쓰는 그룹 이름 (편집 패널·일괄 편집의 그룹 목록). 대소문자만 다른 이름은 그룹 머리글과 같은 표기 하나로
    /// (예전에는 목록 순서상 처음 나온 표기라 머리글은 "Web" 인데 고르기 메뉴는 "web" 으로 보였다)
    /// </summary>
    private List<string> KnownGroupNames() => KnownGroupNames(_hosts);

    internal static List<string> KnownGroupNames(IEnumerable<HostEntry> hosts) =>
        CanonicalGroupNames(hosts).Values
            .Where(n => n.Length > 0)
            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
}
