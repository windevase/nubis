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

        var hosts = g.Hosts;
        var ssh = hosts.Count(h => h.Protocol == Protocol.Ssh);
        var rdp = hosts.Count(h => h.Protocol == Protocol.Rdp);
        var running = hosts.Count(h => !h.IsStopped);

        Add(L.F("모두 연결 ({0}개)", running), () => ConnectGroup(g), running > 0);
        Add(L.F("호스트 {0}개 모두 선택 (일괄 편집)", hosts.Count), () => SelectGroup(g), hosts.Count > 0);
        Sep();
        if (ssh > 0) Add(L.F("SSH 호스트 {0}개에 Vault 계정 지정...", ssh), () => ApplyGroupVault(g, Protocol.Ssh));
        if (rdp > 0) Add(L.F("RDP 호스트 {0}개에 Vault 계정 지정...", rdp), () => ApplyGroupVault(g, Protocol.Rdp));
        var hasGroupVault = hosts.Any(h => h.VaultId != null) || g.Profile is { SshVaultId: not null } or { RdpVaultId: not null };
        Add(L.T("Vault 계정 지정 해제 (기본 계정 따르기)"), () => ClearGroupVault(g), hasGroupVault);
        Sep();
        var withPublic = hosts.Count(h => h.HasPublicIp);
        if (withPublic > 0)
        {
            Add(L.F("공인 IP로 접속하도록 일괄 변경 ({0}개)", withPublic), () => SetGroupPublicIp(g, true), hosts.Any(h => h.HasPublicIp && !h.UsePublicIp));
            Add(L.F("사설 IP로 접속하도록 일괄 변경 ({0}개)", withPublic), () => SetGroupPublicIp(g, false), hosts.Any(h => h.UsePublicIp));
        }
        Sep();
        if (g.IsLocal && g.GroupName.Length > 0) Add(L.T("그룹 이름 바꾸기..."), () => RenameGroup(g));
        if (g.Profile is { } p)
        {
            Add(L.T("동기화"), () => _ = SyncProfileAsync(p), !p.IsSyncing);
            Add(L.T("CLI 로그인 (이 프로필)"), () => RunProviderLogin(p));
            Add(L.T("프로필 설정..."), () => EditProfile(p));
        }
        Sep();
        Add(L.T("모든 그룹 펼치기"), () => SetAllGroups(false));
        Add(L.T("모든 그룹 접기"), () => SetAllGroups(true));
        if (menu.Items.Count > 0 && menu.Items[^1] is Separator last) menu.Items.Remove(last);
    }

    private void ConnectGroup(HostGroup g) => ConnectMany(g.Hosts, g.Name);

    /// <summary>
    /// 그룹의 SSH(또는 RDP) 호스트에 Vault 계정을 한 번에 지정.
    /// 클라우드 그룹은 프로필의 기본 계정으로 저장해, 앞으로 동기화로 추가되는 서버도 그 계정을 쓴다.
    /// </summary>
    private void ApplyGroupVault(HostGroup g, Protocol proto)
    {
        var targets = g.Hosts.Where(h => h.Protocol == proto).ToList();
        if (targets.Count == 0) return;
        var current = g.Profile is { } pr ? (proto == Protocol.Rdp ? pr.RdpVaultId : pr.SshVaultId) : targets.Select(h => h.VaultId).FirstOrDefault(id => id != null);
        var sub = g.IsCloud
            ? L.F("'{0}' 의 {1} 서버 {2}개가 이 계정으로 로그인합니다. 앞으로 동기화로 추가되는 서버에도 적용됩니다.", g.Name, proto == Protocol.Rdp ? "RDP" : "SSH", targets.Count)
            : L.F("'{0}' 의 {1} 호스트 {2}개가 이 계정으로 로그인합니다.", g.Name, proto == Protocol.Rdp ? "RDP" : "SSH", targets.Count);
        if (VaultPickerDialog.Pick(this, current, proto, sub) is not { } id || VaultStore.Get(id) is not { } v) return;

        if (AskClearOwnCredentials(targets, v) is not { } clearOwn) return;

        foreach (var h in targets)
        {
            if (clearOwn) BulkEdit.ClearCredentials(h);
            // 클라우드 그룹: 호스트는 프로필 기본값을 따르게 비워 둔다
            h.VaultId = g.IsCloud ? null : id;
        }
        if (g.Profile is { } p)
        {
            if (proto == Protocol.Rdp) p.RdpVaultId = id; else p.SshVaultId = id;
            SaveProfiles();
        }
        SaveHosts();
        RefreshHosts();
        if (VaultPage.Visibility == Visibility.Visible) RefreshVault();
    }

    /// <summary>
    /// Vault 계정을 지정할 호스트 중 직접 적은 계정(그 값이 Vault 보다 우선)이 있으면 지울지 묻는다.
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

    private void ClearGroupVault(HostGroup g)
    {
        if (Dialog.Show(this, L.F("'{0}' 의 호스트에 지정한 Vault 계정을 해제할까요?\n해제한 호스트는 직접 적은 값 → 프로필 기본 계정 → 설정의 기본 Vault 계정 순으로 씁니다.", g.Name),
                L.T("Vault 계정 해제"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        foreach (var h in g.Hosts) h.VaultId = null;
        if (g.Profile is { } p)
        {
            p.SshVaultId = null;
            p.RdpVaultId = null;
            SaveProfiles();
        }
        SaveHosts();
        RefreshHosts();
    }

    /// <summary>그룹의 공인 IP 가 있는 호스트 전체를 공인/사설 IP 접속으로. 클라우드 그룹은 프로필 기본값도 바꿔 새 서버에도 적용</summary>
    private void SetGroupPublicIp(HostGroup g, bool usePublic)
    {
        var targets = g.Hosts.Where(h => h.HasPublicIp).ToList();
        foreach (var h in targets) h.UsePublicIp = usePublic;
        if (g.Profile is { } p)
        {
            p.UsePublicIp = usePublic;
            SaveProfiles();
        }
        SaveHosts();
        RefreshHosts();
    }

    private void RenameGroup(HostGroup g)
    {
        if (PromptDialog.Ask(this, L.T("그룹 이름 바꾸기"), L.F("'{0}' 그룹의 새 이름", g.GroupName), g.GroupName) is not { Length: > 0 } name) return;
        foreach (var h in _hosts.Where(h => !h.IsCloud && string.Equals(h.Group?.Trim(), g.GroupName, StringComparison.CurrentCultureIgnoreCase)))
            h.Group = name;
        if (_collapsedLocal.Remove(g.Key)) _collapsedLocal.Add("L:" + name);
        SaveGroupState();
        SaveHosts();
        RefreshHosts();
    }

    private void SetAllGroups(bool collapsed)
    {
        foreach (var g in _groups) SetGroupCollapsed(g, collapsed);
        RebuildRows();
        SaveGroupState();
    }

    /// <summary>직접 등록한 호스트들이 쓰는 그룹 이름 (편집 패널의 그룹 목록)</summary>
    private List<string> KnownGroupNames() =>
        _hosts.Where(h => !h.IsCloud && !string.IsNullOrWhiteSpace(h.Group))
            .Select(h => h.Group.Trim())
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
}
