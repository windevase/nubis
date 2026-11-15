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
        var all = _hosts.ToHashSet();
        _selected.RemoveWhere(h => !all.Contains(h));
        return OrderByScreen(_selected, VisibleHosts());
    }

    /// <summary>화면 순서대로 (화면에 없는 호스트는 뒤로). 위치는 사전으로 한 번만 계산 (IndexOf 반복은 O(n²))</summary>
    internal static List<HostEntry> OrderByScreen(IEnumerable<HostEntry> hosts, IReadOnlyList<HostEntry> order)
    {
        var index = new Dictionary<HostEntry, int>(order.Count);
        for (var i = 0; i < order.Count; i++) index.TryAdd(order[i], i);
        return hosts.OrderBy(h => index.TryGetValue(h, out var i) ? i : int.MaxValue).ToList();
    }

    // 범위의 시작 카드가 화면에서 몇 번째였는지 (ScreenOrder 순번) — 즐겨찾기·최근 접속과 원래 그룹 중 클릭한 쪽
    private int _selectAnchorIndex = -1;

    /// <summary>카드 클릭. screenIndex = 클릭한 카드의 ScreenOrder 순번 (모르면 -1). 처리했으면 true (더블 클릭 연결 등 기본 동작 생략)</summary>
    private bool HandleCardSelectClick(HostEntry h, int screenIndex = -1)
    {
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Shift) && _selectAnchor != null)
        {
            var order = ScreenOrder();
            if (SelectionRange(order, _selectAnchor, _selectAnchorIndex, h, screenIndex) is not { } range) { SetSelected(h, true); }
            else
            {
                if (!mods.HasFlag(ModifierKeys.Control)) { foreach (var x in _selected) x.IsSelected = false; _selected.Clear(); }
                for (var i = range.From; i <= range.To; i++) SetSelected(order[i], true);
            }
            UpdateSelectionBar();
            return true;
        }
        if (mods.HasFlag(ModifierKeys.Control))
        {
            SetSelected(h, !h.IsSelected);
            (_selectAnchor, _selectAnchorIndex) = (h, screenIndex);
            UpdateSelectionBar();
            return true;
        }
        // 그냥 클릭: 선택 해제 후 이 카드를 범위의 시작점으로
        (_selectAnchor, _selectAnchorIndex) = (h, screenIndex);
        if (_selected.Count > 0) ClearSelection();
        return false;
    }

    /// <summary>
    /// Shift+클릭 범위 (order 의 순번, 양 끝 포함). 같은 호스트가 즐겨찾기·최근 접속과 원래 그룹에 두 번 있을 수 있어
    /// 클릭한 위치(힌트)를 쓰고, 힌트가 맞지 않으면 끝 카드는 첫 위치, 시작 카드는 끝 카드에 가장 가까운 위치로 고른다.
    /// 둘 중 하나라도 화면에 없으면 null
    /// </summary>
    internal static (int From, int To)? SelectionRange(IReadOnlyList<HostEntry> order, HostEntry anchor, int anchorHint, HostEntry target, int targetHint)
    {
        static bool At(IReadOnlyList<HostEntry> o, int i, HostEntry h) => i >= 0 && i < o.Count && ReferenceEquals(o[i], h);
        var t = targetHint;
        if (!At(order, t, target))
        {
            t = -1;
            for (var i = 0; i < order.Count && t < 0; i++) if (ReferenceEquals(order[i], target)) t = i;
        }
        if (t < 0) return null;
        var a = anchorHint;
        if (!At(order, a, anchor))
        {
            a = -1;
            for (var i = 0; i < order.Count; i++)
                if (ReferenceEquals(order[i], anchor) && (a < 0 || Math.Abs(i - t) < Math.Abs(a - t))) a = i;
        }
        if (a < 0) return null;
        return (Math.Min(a, t), Math.Max(a, t));
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
        if (VaultPickerDialog.Pick(this, current, proto, L.F("고른 호스트 {0}개가 이 계정으로 로그인합니다.", targets.Count), allowClear: true) is not { } id) return;
        // "지정 해제": 고른 호스트의 Vault 지정을 비운다 (숨겨 둔 직접 적은 값 → 기본 계정 순으로 다시 쓰인다)
        if (id == VaultPickerDialog.Cleared) { ApplyToSelected(new BulkEdit { SetVault = true, VaultId = null }, targets); return; }
        if (VaultStore.Get(id) == null) return;

        // 직접 고른 Vault 계정은 호스트에 적힌 값보다 우선 (적힌 값은 숨겨 두고 해제하면 다시 쓴다) — 지울지 묻지 않는다
        ApplyToSelected(new BulkEdit { SetVault = true, VaultId = id }, targets);
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
        var changed = edit.Apply(hosts);
        if (changed == 0) { FlashHostStatus(L.T("바뀐 호스트가 없습니다 (이미 같은 값)"), ToastKind.Info); return; }
        SaveHosts();
        RefreshHosts();
        if (VaultPage.Visibility == Visibility.Visible) RefreshVault();
        FlashHostStatus(L.F("호스트 {0}개를 바꿨습니다", changed));
    }

    private void DeleteSelected()
    {
        var hosts = SelectedHosts();
        if (hosts.Count == 0) return;
        var cloud = hosts.Count(h => h.IsCloud);
        var msg = L.F("호스트 {0}개를 삭제할까요?", hosts.Count);
        if (cloud > 0) msg += "\n" + L.F("이 중 클라우드 호스트 {0}개는 목록에서만 지우고, 다음 동기화에서도 다시 나타나지 않도록 숨깁니다.", cloud);
        // 선택은 검색어를 바꿔도 유지되므로, 지금 검색·필터로 보이지 않는 호스트도 함께 지워진다는 것을 알린다
        var shown = _groups.SelectMany(g => g.Hosts).ToHashSet();
        var hidden = hosts.Count(h => !shown.Contains(h));
        if (hidden > 0) msg += "\n" + L.F("이 중 {0}개는 지금 검색·필터 때문에 목록에 보이지 않는 호스트입니다.", hidden);
        msg += JumpDependentsNote(hosts);
        if (Dialog.Show(this, msg, L.T("삭제 확인"), MessageBoxButton.YesNo, MessageBoxImage.Question,
                string.Join("\n", hosts.Take(15).Select(h => h.DisplayName)) + (hosts.Count > 15 ? "\n..." : "")) != MessageBoxResult.Yes) return;
        ClearSelection();
        RemoveHosts(hosts);
        FlashHostStatus(L.F("호스트 {0}개를 삭제했습니다", hosts.Count));
    }

    /// <summary>
    /// 홈 화면 단축키: Ctrl+N 새 호스트 (검색창에서도), Ctrl+A 전체 선택, Esc 선택 해제, Delete 선택 삭제 (입력 칸에 포커스가 없을 때).
    /// 선택한 카드가 없으면 키보드로 고른 카드에 방향키 · Enter · F2 · Delete (HandleCardKey)
    /// </summary>
    private bool HandleSelectionKey(KeyEventArgs e)
    {
        if (HomeView.Visibility != Visibility.Visible || HostsPage.Visibility != Visibility.Visible ||
            EditorLayer.Visibility == Visibility.Visible) return false;
        if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control) { OpenEditor(null); return true; }
        if (Keyboard.FocusedElement is TextBox or PasswordBox) return false;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (ctrl && e.Key == Key.A) { SelectAllVisible(); return true; }
        if (_selected.Count > 0)
        {
            if (e.Key == Key.Escape) { ClearSelection(); return true; }
            if (e.Key == Key.Delete) { DeleteSelected(); return true; }
        }
        return HandleCardKey(e);
    }
}
