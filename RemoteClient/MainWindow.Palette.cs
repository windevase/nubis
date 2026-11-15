using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace Nubis;

public partial class MainWindow
{
    // =================== 명령 팔레트 (Ctrl+K): 항목 모으기 · 실행 ===================
    // 화면 · 검색은 Dialogs\CommandPalette(.xaml.cs · Index.cs). 여기는 기존 메서드를 부르기만 한다 (로직을 새로 만들지 않음)

    /// <summary>팔레트 열기 (단축키에서 부른다)</summary>
    private void ShowCommandPalette() => CommandPalette.Show(this);

    /// <summary>
    /// 팔레트를 열 때 한 번: 호스트(목록에 보이는 것과 같은 기준) · 열린 탭 · 앱 동작.
    /// 동작 대리자는 이 창을 붙잡지만 팔레트가 닫히면 함께 버려진다
    /// </summary>
    internal List<PaletteItem> BuildPaletteItems()
    {
        var items = new List<PaletteItem>(_hosts.Count + _tabs.Count + 12);
        var profiles = new Dictionary<Guid, CloudProfile>(_profiles.Count);
        foreach (var p in _profiles) profiles[p.Id] = p;

        // 열린 탭 (지금 탭은 빼지 않는다 — 이름으로 찾아 돌아오는 용도)
        foreach (var t in _tabs)
        {
            items.Add(new PaletteItem
            {
                Kind = PaletteKind.Tab,
                Title = t.Title,
                Subtitle = t.Host is { } th ? th.EffectiveAddress : "",
                Badge = t.Status == SessionStatus.Disconnected ? L.T("열린 탭 · 끊김") : L.T("열린 탭"),
                Glyph = t.Glyph,
                Protocol = t.Protocol == Protocol.Local ? (Protocol?)null : t.Protocol,
                Addresses = t.Host is { } h2 ? new[] { h2.Address, h2.PrivateIp, h2.PublicIp } : null,
                Group = t.Host?.Group,
                Extra = t.Protocol == Protocol.Local ? "powershell local" : t.Host?.Username,
                Target = t,
            });
        }

        // 호스트: "중지된 서버 제외" 프로필의 중지된 서버는 목록(RefreshHosts)처럼 뺀다
        foreach (var h in _hosts)
        {
            var profile = h.CloudProfileId is { } pid && profiles.TryGetValue(pid, out var pp) ? pp : null;
            if (h.IsCloud && h.IsStopped && profile is { IncludeStopped: false }) continue;
            var group = profile?.GroupName ?? h.Group;
            var addr = h.UsePublicIp && h.HasPublicIp ? h.PublicIp! : h.PrimaryAddress;
            var user = VaultStore.Get(h.VaultId) is { } v && !string.IsNullOrWhiteSpace(v.Username) ? v.Username : h.Username;
            items.Add(new PaletteItem
            {
                Kind = PaletteKind.Host,
                Title = h.DisplayName,
                Subtitle = string.IsNullOrWhiteSpace(user) ? addr : $"{user}@{addr}",
                Badge = h.IsStopped ? $"{group} · {h.CloudState?.ToLowerInvariant()}" : group,
                Glyph = h.Protocol == Protocol.Rdp ? "" : "",
                Protocol = h.Protocol,
                Addresses = new[] { h.Address, h.PrivateIp, h.PublicIp },
                Group = string.IsNullOrWhiteSpace(h.Group) || h.Group == group ? group : $"{group} {h.Group}",
                Extra = string.Join(' ', user, h.ProtocolText, h.CloudInstanceId, h.CloudRegion, h.Os, h.Purpose, h.TagsText),
                LastUsed = h.LastConnected,
                Favorite = h.Favorite,
                HasPublicIp = h.HasPublicIp,
                Target = h,
            });
        }

        // 쿠버네티스 클러스터 — 고르면 클러스터 탭
        foreach (var c in K8sStore.All())
        {
            var cluster = c;
            items.Add(new PaletteItem
            {
                Kind = PaletteKind.Action,
                Title = c.Name,
                Subtitle = c.Endpoint,
                Badge = "Kubernetes",
                Glyph = "",
                Extra = string.Join(' ', "kubernetes k8s kubectl cluster 클러스터", c.ContextName, c.Region, c.Group),
                LastUsed = c.LastConnected,
                Target = (Action)(() => { if (K8sStore.Get(cluster.Id) is { } cur) OpenK8sTab(cur); }),
            });
        }

        // 앱 동작 — Extra 에 한국어 원문 · 영어 낱말을 넣어 어느 언어로 쳐도 찾게
        void Act(string title, string glyph, string shortcut, string keywords, Action run) => items.Add(new PaletteItem
        {
            Kind = PaletteKind.Action,
            Title = title,
            Badge = shortcut,
            Glyph = glyph,
            Extra = keywords,
            Target = run,
        });
        Act(L.T("새 호스트"), "", "Ctrl+N", "새 호스트 추가 new host add", () => { SelectHome(); OpenEditor(null); });
        Act(L.T("호스트 가져오기"), "", "", "호스트 가져오기 import excel csv ssh config", () =>
        {
            SelectHome();
            NavHosts.IsChecked = true;
            OnImportHosts(this, new RoutedEventArgs());
        });
        Act(L.T("새 PowerShell 탭"), "", "Ctrl+Shift+T", "새 PowerShell 탭 local terminal powershell", OpenLocalTerminal);
        if (CanReopenClosedTab)
            Act(L.T("닫은 탭 다시 열기"), "", "Ctrl+Shift+R", "닫은 탭 다시 열기 reopen closed tab", ReopenClosedTab);
        Act(ClaudeColumn.Width.Value > 0 ? L.T("Claude 패널 닫기") : L.T("Claude 패널 열기"), "", "Ctrl+Shift+A",
            "Claude 패널 열기 닫기 claude panel toggle ai", () => ToggleClaude());
        if (_profiles.Count > 0 && !_syncingAll)
            Act(L.T("모든 클라우드 동기화"), "", "", "모든 클라우드 동기화 sync all cloud refresh", () => _ = SyncAllAsync());
        Act(L.T("Cloud 페이지"), "", "", "클라우드 cloud page aws tencent gcp azure", () => ShowPage(NavCloud));
        Act(L.T("Kubernetes 페이지"), "", "", "kubernetes k8s kubectl 클러스터 cluster kubeconfig", () => ShowPage(NavK8s));
        Act(L.T("Vault 페이지"), "", "", "vault 계정 비밀번호 키 account password key", () => ShowPage(NavVault));
        Act(L.T("Known Hosts 페이지"), "", "", "known hosts 호스트 키 host key fingerprint", () => ShowPage(NavKnown));
        Act(L.T("설정 페이지"), "", "", "설정 settings preferences options", () => ShowPage(NavSettings));
        return items;
    }

    /// <summary>홈으로 가서 사이드바 페이지 하나를 고른다 (OnNavChanged 가 화면을 바꾼다)</summary>
    private void ShowPage(System.Windows.Controls.RadioButton nav)
    {
        SelectHome();
        nav.IsChecked = true;
    }

    /// <summary>
    /// 아무것도 실행하지 않고 닫혔을 때(Esc · 바깥 클릭): 보고 있던 탭의 터미널 · RDP 에 포커스를 돌려준다
    /// (WebView2 · ActiveX 는 HWND 라 WPF 가 창 활성화만으로 포커스를 되돌려 주지 않는다)
    /// </summary>
    internal void OnPaletteDismissed()
    {
        // 활성화는 팔레트가 닫힌 뒤에 넘어오므로 한 박자 늦게 본다. 다른 앱으로 갔으면(비활성) 아무것도 하지 않는다
        Dispatcher.BeginInvoke(() =>
        {
            if (IsActive && _tabs.FirstOrDefault(t => t.IsSelected) is { } cur) cur.Session.FocusSession();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>팔레트에서 고른 항목 실행. 팔레트가 떠 있던 동안 지워진 호스트 · 닫힌 탭은 무시한다</summary>
    internal void ExecutePaletteItem(PaletteItem item, PaletteModifier mod)
    {
        switch (item.Target)
        {
            case HostEntry h:
                if (!_hosts.Contains(h)) return;
                if (mod == PaletteModifier.Edit) { SelectHome(); OpenEditor(h); }
                else if (mod == PaletteModifier.PublicIp && h.HasPublicIp) Connect(h, usePublic: true);
                else Connect(h);
                break;
            case SessionTab t:
                if (!_tabs.Contains(t)) return;
                if (mod == PaletteModifier.Edit && t.Host != null && _hosts.Contains(t.Host)) { SelectHome(); OpenEditor(t.Host); }
                else SelectTab(t);
                break;
            case Action run:
                run();
                break;
        }
    }
}
