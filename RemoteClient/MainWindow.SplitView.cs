using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace Nubis;

public partial class MainWindow
{
    // =================== 나란히 보기 (탭 두 개를 한 화면에) ===================
    // 탭은 탭 줄에 그대로 두고, 짝(_split)인 두 탭 중 하나를 고르면 둘이 SessionsHost 의 0열 · 2열에 함께 보인다.
    // 세션 뷰는 같은 부모 안에서 열만 바꾸므로 WebView2 · RDP(HwndHost)를 다시 붙이지 않는다.
    // 고른 탭(IsSelected) = 키보드 포커스가 있는 쪽 — Claude 현재 화면 · 단축키 · MCP(session_id 생략)가 이 탭을 가리킨다.

    private SplitPair<SessionTab>? _split;
    /// <summary>오른쪽 영역 너비 비율 (끌어서 바꾼 값을 나란히 보기를 잠시 숨겼다 다시 보일 때 되살린다)</summary>
    private double _splitRatio = 0.5;
    private bool _splitShown;

    /// <summary>
    /// 고른 탭을 보이게 한다 (짝이면 두 탭을 나란히). 키보드 포커스는 옮기지 않는다 — SelectTab 은 이 뒤에 포커스를 준다.
    /// 캡처 · RDP 입력용 탭 전환(ShowTabWithoutFocus)도 이것을 쓴다
    /// </summary>
    private void ShowSessionLayout(SessionTab selected)
    {
        var (left, right) = SplitLayout.Visible(selected, _split);
        foreach (var t in _tabs)
        {
            t.IsSelected = t == selected;
            var shown = t == left || t == right;
            t.IsBeside = shown && t != selected;
            var col = t == right ? 2 : 0;
            if (Grid.GetColumn(t.View) != col) Grid.SetColumn(t.View, col);
            t.View.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        }
        ApplySplitColumns(left, right, selected);
        HomeView.Visibility = Visibility.Collapsed;
        SessionsHost.Visibility = Visibility.Visible;
        HomeTab.Background = Brushes.Transparent;
        UpdateActiveContext();
    }

    private void ApplySplitColumns(SessionTab? left, SessionTab? right, SessionTab? selected)
    {
        var split = left != null && right != null;
        if (_splitShown && !split)
            _splitRatio = SplitLayout.RightRatio(SplitLeftColumn.ActualWidth, SplitRightColumn.ActualWidth);
        if (split && !_splitShown)
        {
            SplitLeftColumn.Width = new GridLength(1 - _splitRatio, GridUnitType.Star);
            SplitRightColumn.Width = new GridLength(_splitRatio, GridUnitType.Star);
            SplitRightColumn.MinWidth = 120;
        }
        else if (!split && _splitShown)
        {
            SplitLeftColumn.Width = new GridLength(1, GridUnitType.Star);
            SplitRightColumn.MinWidth = 0;
            SplitRightColumn.Width = new GridLength(0);
        }
        _splitShown = split;
        SetSplitFocusHook(split);
        var vis = split ? Visibility.Visible : Visibility.Collapsed;
        SplitSplitter.Visibility = SplitLeftHeader.Visibility = SplitRightHeader.Visibility = vis;
        if (!split)
        {
            // 머리줄 제목 바인딩이 탭(→ 세션 뷰)을 붙잡지 않게 푼다
            BindingOperations.ClearBinding(SplitLeftTitle, TextBlock.TextProperty);
            BindingOperations.ClearBinding(SplitRightTitle, TextBlock.TextProperty);
            return;
        }
        SetSplitHeader(SplitLeftHeader, SplitLeftTitle, left!, left == selected);
        SetSplitHeader(SplitRightHeader, SplitRightTitle, right!, right == selected);
    }

    /// <summary>영역 머리줄: 탭 제목 + 포커스가 있는 쪽은 강조색 밑줄</summary>
    private void SetSplitHeader(Border header, TextBlock title, SessionTab tab, bool focused)
    {
        if (BindingOperations.GetBinding(title, TextBlock.TextProperty)?.Source != tab)
            title.SetBinding(TextBlock.TextProperty, new Binding(nameof(SessionTab.Title)) { Source = tab, Mode = BindingMode.OneWay });
        header.BorderBrush = (Brush)FindResource(focused ? "Accent" : "Stroke");
        title.Foreground = (Brush)FindResource(focused ? "TextPrimary" : "TextSecondary");
        header.Tag = tab;
    }

    /// <summary>나란히 보는 탭 쪽에 WPF 키보드 포커스가 들어가면(SFTP 패널 등) 그 탭을 고른 탭으로 — 화면 배치는 그대로</summary>
    private void OnSessionViewFocusWithin(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true) return;
        FocusBesideTab(_tabs.FirstOrDefault(t => ReferenceEquals(t.View, sender)));
    }

    private void FocusBesideTab(SessionTab? tab)
    {
        if (tab == null || _split == null || tab.IsSelected || !tab.IsBeside || SessionsHost.Visibility != Visibility.Visible) return;
        ShowSessionLayout(tab);
    }

    // 터미널(WebView2) · RDP(ActiveX) 를 클릭하면 Win32 포커스만 그 자식 창으로 가고 WPF 포커스 이벤트는 오지 않는다 →
    // 나란히 보는 동안만 포커스 WinEvent 를 받아, 포커스를 얻은 창이 어느 쪽 세션 뷰의 HwndHost 안인지 본다
    private WinEventProc? _splitFocusProc;
    private IntPtr _splitFocusHook;

    private void SetSplitFocusHook(bool on)
    {
        if (on && _splitFocusHook == IntPtr.Zero)
        {
            _splitFocusProc ??= OnSplitWinFocus;
            // 프로세스 밖 훅: 이 스레드의 메시지 루프로 전달된다 (WebView2 창은 다른 프로세스라 pid 로 거르지 않는다)
            _splitFocusHook = SetWinEventHook(EventObjectFocus, EventObjectFocus, IntPtr.Zero, _splitFocusProc, 0, 0, WinEventOutOfContext);
        }
        else if (!on && _splitFocusHook != IntPtr.Zero)
        {
            UnhookWinEvent(_splitFocusHook);
            _splitFocusHook = IntPtr.Zero;
        }
    }

    private void OnSplitWinFocus(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero || _split == null || !_splitShown) return;
        foreach (var t in new[] { _split.Left, _split.Right })
            if (!t.IsSelected && t.IsBeside && ContainsWindow(t.View, hwnd)) { FocusBesideTab(t); return; }
    }

    /// <summary>hwnd 가 view 안의 HwndHost(WebView2 · WindowsFormsHost) 창이거나 그 자식인지</summary>
    private static bool ContainsWindow(DependencyObject view, IntPtr hwnd)
    {
        if (view is System.Windows.Interop.HwndHost host && host.Handle != IntPtr.Zero && (host.Handle == hwnd || IsChild(host.Handle, hwnd)))
            return true;
        var n = VisualTreeHelper.GetChildrenCount(view);
        for (var i = 0; i < n; i++)
            if (ContainsWindow(VisualTreeHelper.GetChild(view, i), hwnd)) return true;
        return false;
    }

    private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    private const uint EventObjectFocus = 0x8005, WinEventOutOfContext = 0;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventProc proc, uint pid, uint thread, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsChild(IntPtr parent, IntPtr child);

    private void OnSplitHeaderClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SessionTab tab && _tabs.Contains(tab)) SelectTab(tab);
    }

    private void OnSplitSwap(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        SwapSplit();
    }

    private void OnSplitClose(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        CloseSplit();
    }

    /// <summary>anchor(왼쪽) 오른쪽에 beside 를 나란히 보고 anchor 에 포커스</summary>
    private void SplitWith(SessionTab anchor, SessionTab beside)
    {
        if (!SplitLayout.CanPair(_tabs, anchor, beside)) return;
        _split = new SplitPair<SessionTab>(anchor, beside);
        // 새 짝은 반반으로 — 열을 다시 잡도록 지금 보이는 나란히 보기를 한 번 내린다
        if (_splitShown) ApplySplitColumns(null, null, null);
        _splitRatio = 0.5;
        SelectTab(anchor);
    }

    private void SwapSplit()
    {
        if (_split == null) return;
        _split = _split.Swapped();
        // 두 탭이 자리를 바꾸므로 너비도 함께 바꾼다 (끌어 둔 비율 유지)
        var ratio = 1 - (_splitShown ? SplitLayout.RightRatio(SplitLeftColumn.ActualWidth, SplitRightColumn.ActualWidth) : _splitRatio);
        if (_splitShown) ApplySplitColumns(null, null, null);   // 열을 다시 잡도록 한 번 내린다
        _splitRatio = ratio;
        if (_tabs.FirstOrDefault(t => t.IsSelected) is { } cur && _split.Contains(cur))
        {
            ShowSessionLayout(cur);
            // 머리줄 버튼이 가져간 키보드 포커스를 고른 탭에 돌려준다
            Dispatcher.BeginInvoke(() => { if (cur.IsSelected) cur.Session.FocusSession(); }, System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    /// <summary>나란히 보기를 푼다 — 고른 탭(포커스가 있던 쪽)만 남는다</summary>
    private void CloseSplit()
    {
        if (_split == null) return;
        _split = null;
        if (_tabs.FirstOrDefault(t => t.IsSelected) is { } cur) SelectTab(cur);
    }

    /// <summary>탭 우클릭 메뉴의 나란히 보기 항목</summary>
    private void AddSplitMenuItems(ContextMenu menu, SessionTab tab)
    {
        if (_split != null && _split.Contains(tab))
        {
            var swap = new MenuItem { Header = L.T("좌우 바꾸기") };
            swap.Click += (_, _) => SwapSplit();
            menu.Items.Add(swap);
            var off = new MenuItem { Header = L.T("나란히 보기 해제") };
            off.Click += (_, _) => CloseSplit();
            menu.Items.Add(off);
            return;
        }
        var candidates = SplitLayout.Candidates(_tabs, tab);
        var parent = new MenuItem { Header = L.T("오른쪽에 나란히 보기"), IsEnabled = candidates.Count > 0 };
        foreach (var other in candidates)
        {
            var item = new MenuItem
            {
                // 밑줄을 액세스 키로 먹지 않게 (MenuText)
                Header = MenuText(other.Title) + (other.Status == SessionStatus.Disconnected ? L.T("  (끊김)") : ""),
                FontWeight = other.IsSelected ? FontWeights.SemiBold : FontWeights.Normal,
            };
            // 메뉴가 떠 있는 동안 닫혔을 수 있다 — CanPair 가 다시 본다
            item.Click += (_, _) => SplitWith(tab, other);
            parent.Items.Add(item);
        }
        menu.Items.Add(parent);
    }
}
