using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Nubis;

public partial class MainWindow
{
    // =================== 탭 줄: 휠 스크롤 · 넘친 탭 목록 · 우클릭 메뉴 · 끌어서 순서 바꾸기 ===================

    private void OnTabsWheel(object sender, MouseWheelEventArgs e)
    {
        TabsScroll.ScrollToHorizontalOffset(TabsScroll.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private void OnTabsScrollChanged(object sender, ScrollChangedEventArgs e) =>
        TabListButton.Visibility = TabsScroll.ScrollableWidth > 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnTabListClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = TabListButton, Placement = PlacementMode.Bottom };
        for (var i = 0; i < _tabs.Count; i++)
        {
            var t = _tabs[i];
            var item = new MenuItem
            {
                // 밑줄을 액세스 키로 먹지 않게 (MenuText — "web_prod" 가 "webprod" 로 보이던 문제)
                Header = MenuText(t.Title) +(t.Status == SessionStatus.Disconnected ? L.T("  (끊김)") : ""),
                // 바로 가는 단축키 (Ctrl+1~8 = 그 번호, Ctrl+9 = 마지막 탭 — SelectTabNumber)
                InputGestureText = i < 8 ? $"Ctrl+{i + 1}" : i == _tabs.Count - 1 ? "Ctrl+9" : "",
                FontWeight = t.IsSelected ? FontWeights.SemiBold : FontWeights.Normal,
            };
            item.Click += (_, _) => { if (_tabs.Contains(t)) SelectTab(t); };
            menu.Items.Add(item);
        }
        // 닫힌 뒤 항목 람다(탭 → 세션 뷰)를 붙잡지 않게 비운다 (클릭 처리는 그보다 먼저 — OnTabMenuClosed 와 같은 방식)
        menu.Closed += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            menu.Items.Clear();
            menu.ClearValue(ContextMenu.PlacementTargetProperty);
        }, DispatcherPriority.ApplicationIdle);
        menu.IsOpen = true;
    }

    // ---------- 탭 우클릭 메뉴 ----------

    private void OnTabMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu { PlacementTarget: FrameworkElement { DataContext: SessionTab tab } } menu) return;
        menu.Items.Clear();
        menu.Closed -= OnTabMenuClosed;
        menu.Closed += OnTabMenuClosed;
        void Add(string header, Action act, bool enabled = true, string gesture = "")
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled, InputGestureText = gesture };
            item.Click += (_, _) => act();
            menu.Items.Add(item);
        }
        var disconnected = _tabs.Where(t => t.Status == SessionStatus.Disconnected).ToList();
        var idx = _tabs.IndexOf(tab);
        Add(L.T("다시 연결"), () => { if (!_tabs.Contains(tab)) return; SelectTab(tab); tab.Session.Reconnect(); }, tab.Status == SessionStatus.Disconnected);
        if (tab.Host != null) Add(L.T("같은 호스트로 새 탭"), () => Connect(tab.Host, TabUsePublic(tab)));
        if (tab.Protocol == Protocol.Local) Add(L.T("새 PowerShell 탭"), OpenLocalTerminal, gesture: "Ctrl+Shift+T");
        if (tab.View is RdpSessionView rdp)
            Add(L.T("전체 화면"), () => { if (!_tabs.Contains(tab)) return; SelectTab(tab); rdp.EnterFullScreen(); }, rdp.IsConnected);
        // Claude 에게 준 화면 조작 허용(10분)을 바로 끝낸다 — 허용 중일 때만 보인다
        if (tab.View is RdpSessionView && HasRdpControl(tab))
            Add(L.T("Claude 화면 조작 허용 끝내기"), () => RevokeRdpControl(tab));
        // Claude 가 지금 보내는 입력(긴 글자 입력 등)을 바로 멈춘다 — 허용 여부와 무관하게 입력 중일 때만 보인다
        if (tab.View is RdpSessionView && IsRdpInputRunning(tab))
            Add(L.T("Claude 입력 멈추기"), () => StopRdpInput(tab));
        Add(L.T("탭 이름 바꾸기..."), () =>
        {
            // 공백만 넣으면 탭 제목이 비어 탭을 알아볼 수 없다 — 앞뒤 공백을 떼고 빈 이름은 무시
            if (PromptDialog.Ask(this, L.T("탭 이름 바꾸기"), L.T("새 이름"), tab.Title)?.Trim() is { Length: > 0 } name && _tabs.Contains(tab))
            {
                tab.Title = name;
                // Claude 에게 알려 주는 현재 탭 정보(active-session.txt)도 새 이름으로 — 나란히 보이는 다른 쪽 탭 이름도 들어 있다
                if (tab.IsSelected || tab.IsBeside) UpdateActiveContext();
            }
        });
        if (tab.Host != null && _hosts.Contains(tab.Host)) Add(L.T("호스트 편집"), () => { SelectHome(); OpenEditor(tab.Host); });
        menu.Items.Add(new Separator());
        AddSplitMenuItems(menu, tab);   // 오른쪽에 나란히 보기 · 좌우 바꾸기 · 해제 (MainWindow.SplitView.cs)
        menu.Items.Add(new Separator());
        Add(L.T("닫기"), () => CloseTabs(new[] { tab }), gesture: "Ctrl+Shift+W");
        Add(L.T("다른 탭 모두 닫기"), () => CloseTabs(_tabs.Where(t => t != tab).ToList()), _tabs.Count > 1);
        // 위치는 누를 때 다시 본다 — 메뉴가 떠 있는 동안 다른 탭이 닫혔을 수 있다
        Add(L.T("오른쪽 탭 모두 닫기"), () =>
        {
            var at = _tabs.IndexOf(tab);
            if (at >= 0) CloseTabs(_tabs.Skip(at + 1).ToList());
        }, idx >= 0 && idx < _tabs.Count - 1);
        Add(L.T("끊긴 탭 모두 다시 연결"), () => ReconnectAll(disconnected), disconnected.Count > 0);
        Add(L.T("끊긴 탭 모두 닫기"), () => CloseTabs(disconnected), disconnected.Count > 0);
        menu.Items.Add(new Separator());
        Add(L.T("닫은 탭 다시 열기"), ReopenClosedTab, CanReopenClosedTab, "Ctrl+Shift+R");
    }

    /// <summary>
    /// 탭 메뉴(TabMenu)는 창 리소스 하나를 모든 탭이 함께 쓴다 → 닫힌 뒤에도 항목의 람다(마지막 탭·끊긴 탭 목록)와
    /// PlacementTarget(탭 항목 → 탭 → 세션 뷰)이 남아, 메뉴로 닫은 탭이 다음에 메뉴를 열 때까지 해제되지 않는다.
    /// 항목 클릭은 메뉴가 닫히고 그려진 뒤(Render 우선순위) 실행되므로, 그보다 낮은 ApplicationIdle 에서 비운다.
    /// </summary>
    private void OnTabMenuClosed(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (menu.IsOpen) return;   // 그 사이 다시 열렸으면 새 항목을 지우지 않는다
            menu.Items.Clear();
            menu.ClearValue(ContextMenu.PlacementTargetProperty);
        }, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// 끊긴 탭 여러 개 다시 연결. RDP 는 계정 입력 창을 탭마다 차례로 띄우므로, 사용자가 한 번 취소하면
    /// 나머지 탭은 묻지 않고 멈춘다 (취소해도 다음 창이 계속 뜨던 문제).
    /// </summary>
    private void ReconnectAll(IReadOnlyList<SessionTab> tabs)
    {
        foreach (var t in tabs.ToList())
        {
            // 앞 탭의 계정 입력 창(중첩 메시지 루프)이 떠 있는 동안 닫혔거나 이미 다시 연결된 탭은 건너뛴다
            if (!_tabs.Contains(t) || t.Status != SessionStatus.Disconnected) continue;
            t.Session.Reconnect();
            if (t.View is RdpSessionView { LastConnectCancelled: true }) break;
        }
    }

    // ---------- 탭 끌어서 순서 바꾸기 ----------

    private Point _tabDragStart;
    private SessionTab? _tabDragging;

    private void OnTabDragStart(object sender, MouseButtonEventArgs e)
    {
        // 탭의 닫기 버튼을 누른 채 조금 움직이면 끌기가 시작돼 닫기 클릭이 사라지던 문제 — 버튼 위에서는 끌기를 준비하지 않는다
        if (IsInsideButton(e.OriginalSource as DependencyObject, sender as DependencyObject)) { _tabDragging = null; return; }
        _tabDragStart = e.GetPosition(this);
        _tabDragging = (sender as FrameworkElement)?.DataContext as SessionTab;
    }

    /// <summary>source 에서 root(탭 항목)까지 올라가며 버튼이 있는지</summary>
    private static bool IsInsideButton(DependencyObject? source, DependencyObject? root)
    {
        for (var d = source; d != null && !ReferenceEquals(d, root); d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is ButtonBase) return true;
        return false;
    }

    /// <summary>버튼을 떼면 끌기 준비를 푼다 (그냥 클릭한 탭이 나중에 다른 곳에서 누른 채 지나갈 때 끌려가지 않게)</summary>
    private void OnTabDragEnd(object sender, MouseButtonEventArgs e) => _tabDragging = null;

    private void OnTabDragMove(object sender, MouseEventArgs e)
    {
        if (_tabDragging == null || e.LeftButton != MouseButtonState.Pressed) return;
        // 누른 탭 위에서 움직일 때만 끌기 시작
        if (!ReferenceEquals((sender as FrameworkElement)?.DataContext, _tabDragging)) { _tabDragging = null; return; }
        var d = e.GetPosition(this) - _tabDragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance * 2) return;
        var tab = _tabDragging;
        _tabDragging = null;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(SessionTab), tab), DragDropEffects.Move);
    }

    private void OnTabDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(SessionTab)) ? DragDropEffects.Move : DragDropEffects.None;
        if (e.Data.GetData(typeof(SessionTab)) is SessionTab dragged && (sender as FrameworkElement)?.DataContext is SessionTab over && dragged != over)
        {
            var from = _tabs.IndexOf(dragged);
            var to = _tabs.IndexOf(over);
            // 폭이 다른 탭끼리 바꾸면 자리를 바꾼 직후 커서가 다시 상대 탭 위에 놓여 왔다 갔다 하던 문제 —
            // 바꾼 뒤에도 커서가 끄는 탭 위에 있게 될 만큼 들어왔을 때만 옮긴다
            var overBox = TabsList.ItemContainerGenerator.ContainerFromItem(over) as FrameworkElement;
            var dragBox = TabsList.ItemContainerGenerator.ContainerFromItem(dragged) as FrameworkElement;
            if (from >= 0 && to >= 0 &&
                (overBox == null || dragBox == null ||
                 ShouldReorderTab(from, to, e.GetPosition(overBox).X, overBox.ActualWidth, dragBox.ActualWidth)))
                MoveTab(dragged, to);
        }
        e.Handled = true;
    }

    /// <summary>탭을 to 자리(옮긴 뒤의 인덱스)로 옮긴다 — 끌어서 옮기기와 MCP move_session 공용. 옮겼으면 true</summary>
    internal bool MoveTab(SessionTab tab, int to)
    {
        var from = _tabs.IndexOf(tab);
        if (from < 0 || to < 0 || to >= _tabs.Count || from == to) return false;
        _tabs.Move(from, to);
        return true;
    }

    /// <summary>
    /// 끄는 탭(폭 draggedWidth)을 over 탭(폭 overWidth, 커서 x 는 over 기준) 자리로 옮길지.
    /// 오른쪽으로 옮기면 over 탭이 왼쪽으로 draggedWidth 만큼 밀리므로 x ≥ overWidth − draggedWidth 여야
    /// 옮긴 뒤 커서가 끄는 탭 위에 남는다 (왼쪽도 대칭). 탭 절반은 넘어야 옮겨 살짝 걸친 것만으로 바뀌지 않게 한다.
    /// </summary>
    internal static bool ShouldReorderTab(int from, int to, double x, double overWidth, double draggedWidth)
    {
        if (from == to || overWidth <= 0) return false;
        return from < to
            ? x >= Math.Max(overWidth / 2, overWidth - draggedWidth)
            : x <= Math.Min(overWidth / 2, draggedWidth);
    }

    private void OnTabDrop(object sender, DragEventArgs e) => e.Handled = true;
}
