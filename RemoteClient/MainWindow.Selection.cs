using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Nubis;

public partial class MainWindow
{
    // =================== 카드 여러 개 선택 · 일괄 작업 ===================
    // Ctrl+클릭 = 하나씩 추가/빼기, Shift+클릭 = 범위, Ctrl+A = 보이는 호스트 전체, Esc = 선택 해제.
    // 검색어를 바꿔도 선택은 유지된다 (검색 → 고르기를 여러 번 해서 모을 수 있게).

    private readonly HashSet<HostEntry> _selected = new();
    private HostEntry? _selectAnchor;

    private void SetSelected(HostEntry h, bool on)
    {
        if (on ? _selected.Add(h) : _selected.Remove(h)) h.IsSelected = on;
    }

    private void ClearSelection()
    {
        foreach (var h in _selected) h.IsSelected = false;
        _selected.Clear();
        UpdateSelectionBar();
    }

    /// <summary>선택한 호스트 (화면 순서, 목록에서 사라진 호스트는 뺀다)</summary>
    private List<HostEntry> SelectedHosts()
    {
        _selected.RemoveWhere(h => !_hosts.Contains(h));
        var order = VisibleHosts();
        return _selected.OrderBy(h => order.IndexOf(h) is var i and >= 0 ? i : int.MaxValue).ToList();
    }

    /// <summary>카드 클릭. 처리했으면 true (더블 클릭 연결 등 기본 동작 생략)</summary>
    private bool HandleCardSelectClick(HostEntry h)
    {
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Shift) && _selectAnchor != null)
        {
            var order = VisibleHosts();
            int a = order.IndexOf(_selectAnchor), b = order.IndexOf(h);
            if (a < 0 || b < 0) { SetSelected(h, true); }
            else
            {
                if (!mods.HasFlag(ModifierKeys.Control)) { foreach (var x in _selected) x.IsSelected = false; _selected.Clear(); }
                for (var i = Math.Min(a, b); i <= Math.Max(a, b); i++) SetSelected(order[i], true);
            }
            UpdateSelectionBar();
            return true;
        }
        if (mods.HasFlag(ModifierKeys.Control))
        {
            SetSelected(h, !h.IsSelected);
            _selectAnchor = h;
            UpdateSelectionBar();
            return true;
        }
        // 그냥 클릭: 선택 해제 후 이 카드를 범위의 시작점으로
        _selectAnchor = h;
        if (_selected.Count > 0) ClearSelection();
        return false;
    }

    private void SelectAllVisible()
    {
        foreach (var h in VisibleHosts()) SetSelected(h, true);
        UpdateSelectionBar();
    }

    private void SelectGroup(HostGroup g)
    {
        foreach (var h in g.Hosts) SetSelected(h, true);
        UpdateSelectionBar();
    }

    private void UpdateSelectionBar()
    {
        var n = _selected.Count;
        SelectionBar.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        HostToolbar.Visibility = n > 0 ? Visibility.Hidden : Visibility.Visible;
        SelectionText.Text = L.F("{0}개 선택됨", n);
    }

    // ----- 선택 막대 버튼 -----
    private void OnSelectionConnect(object sender, RoutedEventArgs e) => ConnectMany(SelectedHosts(), L.T("선택한 호스트"));
    private void OnSelectionEdit(object sender, RoutedEventArgs e) => BulkEditSelected();
    private void OnSelectionDelete(object sender, RoutedEventArgs e) => DeleteSelected();
    private void OnSelectionClear(object sender, RoutedEventArgs e) => ClearSelection();
    private void OnSelectionMore(object sender, RoutedEventArgs e)
    {
        var menu = BuildSelectionMenu();
        menu.PlacementTarget = (UIElement)sender;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>선택한 카드를 우클릭하면 카드 메뉴 대신 선택 메뉴. 선택 밖의 카드를 우클릭하면 선택을 풀고 그 카드 메뉴</summary>
    private void OnCardContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (HostOf(sender) is not { } h || _selected.Count == 0) return;
        if (!h.IsSelected) { ClearSelection(); return; }
        e.Handled = true;
        var menu = BuildSelectionMenu();
        menu.PlacementTarget = (UIElement)sender;
        menu.IsOpen = true;
    }

    private ContextMenu BuildSelectionMenu()
    {
        var hosts = SelectedHosts();
        var menu = new ContextMenu();
        void Add(string header, Action act, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => act();
            menu.Items.Add(item);
        }
        var running = hosts.Count(h => !h.IsStopped);
        var withPublic = hosts.Count(h => h.HasPublicIp);
        var remote = hosts.Count(h => h.Protocol != Protocol.Local);

        Add(L.F("모두 연결 ({0}개)", running), () => ConnectMany(hosts, L.T("선택한 호스트")), running > 0);
        menu.Items.Add(new Separator());
        Add(L.T("일괄 편집..."), BulkEditSelected);
        Add(L.F("Vault 계정 지정 ({0}개)...", remote), () => SetSelectedVault(hosts), remote > 0);
        if (withPublic > 0)
        {
            Add(L.F("공인 IP로 접속 ({0}개)", withPublic), () => ApplyToSelected(new BulkEdit { UsePublicIp = true }), hosts.Any(h => h.HasPublicIp && !h.UsePublicIp));
            Add(L.F("사설 IP로 접속 ({0}개)", withPublic), () => ApplyToSelected(new BulkEdit { UsePublicIp = false }), hosts.Any(h => h.UsePublicIp));
        }
        var allFav = hosts.All(h => h.Favorite);
        Add(allFav ? L.T("즐겨찾기 해제") : L.T("즐겨찾기에 추가"), () => ApplyToSelected(new BulkEdit { Favorite = !allFav }));
        menu.Items.Add(new Separator());
        Add(L.F("삭제 ({0}개)...", hosts.Count), DeleteSelected);
        Add(L.T("선택 해제"), ClearSelection);
        return menu;
    }

    private void ConnectMany(IReadOnlyList<HostEntry> hosts, string name)
    {
        var targets = hosts.Where(h => !h.IsStopped).ToList();
        if (targets.Count == 0) return;
        if (targets.Count > 8 && Dialog.Show(this, L.F("'{0}' 의 호스트 {1}개에 모두 연결할까요?", name, targets.Count), L.T("모두 연결"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        foreach (var h in targets) Connect(h);
    }

    private void SetSelectedVault(IReadOnlyList<HostEntry> hosts)
    {
        var targets = hosts.Where(h => h.Protocol != Protocol.Local).ToList();
        if (targets.Count == 0) return;
        var proto = targets.All(h => h.Protocol == Protocol.Rdp) ? Protocol.Rdp : Protocol.Ssh;
        var current = targets.Select(h => h.VaultId).Distinct().Count() == 1 ? targets[0].VaultId : null;
        if (VaultPickerDialog.Pick(this, current, proto, L.F("고른 호스트 {0}개가 이 계정으로 로그인합니다.", targets.Count)) is not { } id
            || VaultStore.Get(id) is not { } v) return;

        // 호스트에 직접 적은 값은 Vault 보다 우선 — 지울지 묻는다 (그룹 일괄 지정과 같은 창)
        if (AskClearOwnCredentials(targets, v) is not { } clearOwn) return;
        ApplyToSelected(new BulkEdit { SetVault = true, VaultId = id, ClearOwnCredentials = clearOwn }, targets);
    }

    private void BulkEditSelected()
    {
        var hosts = SelectedHosts();
        if (hosts.Count == 0) return;
        if (BulkEditDialog.Ask(this, hosts, KnownGroupNames()) is not { } edit) return;
        ApplyToSelected(edit, hosts);
    }

    private void ApplyToSelected(BulkEdit edit, IReadOnlyList<HostEntry>? hosts = null)
    {
        hosts ??= SelectedHosts();
        if (edit.Apply(hosts) == 0) return;
        SaveHosts();
        RefreshHosts();
        if (VaultPage.Visibility == Visibility.Visible) RefreshVault();
    }

    private void DeleteSelected()
    {
        var hosts = SelectedHosts();
        if (hosts.Count == 0) return;
        var cloud = hosts.Count(h => h.IsCloud);
        var msg = L.F("호스트 {0}개를 삭제할까요?", hosts.Count);
        if (cloud > 0) msg += "\n" + L.F("이 중 클라우드 호스트 {0}개는 목록에서만 지우고, 다음 동기화에서도 다시 나타나지 않도록 숨깁니다.", cloud);
        if (Dialog.Show(this, msg, L.T("삭제 확인"), MessageBoxButton.YesNo, MessageBoxImage.Question,
                string.Join("\n", hosts.Take(15).Select(h => h.DisplayName)) + (hosts.Count > 15 ? "\n..." : "")) != MessageBoxResult.Yes) return;
        ClearSelection();
        RemoveHosts(hosts);
    }

    /// <summary>홈 화면 단축키: Ctrl+A 전체 선택, Esc 선택 해제, Delete 선택 삭제 (입력 칸에 포커스가 없을 때)</summary>
    private bool HandleSelectionKey(KeyEventArgs e)
    {
        if (HomeView.Visibility != Visibility.Visible || HostsPage.Visibility != Visibility.Visible ||
            EditorLayer.Visibility == Visibility.Visible || Keyboard.FocusedElement is TextBox or PasswordBox) return false;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (ctrl && e.Key == Key.A) { SelectAllVisible(); return true; }
        if (_selected.Count == 0) return false;
        if (e.Key == Key.Escape) { ClearSelection(); return true; }
        if (e.Key == Key.Delete) { DeleteSelected(); return true; }
        return false;
    }
}
