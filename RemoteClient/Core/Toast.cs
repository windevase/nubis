using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Nubis;

/// <summary>토스트 종류 (아이콘 · 강조 색)</summary>
public enum ToastKind { Info, Success, Warning, Error }

/// <summary>
/// 메인 창 오른쪽 아래에 잠깐 뜨는 알림. 잠깐 보여 줄 상태 문구(저장됨 · 복사됨 · 동기화 완료 등)에 쓴다.
///   Toast.Show(L.T("복사했습니다"), ToastKind.Success);
/// · 어느 스레드에서 불러도 된다 (UI 스레드로 넘김). 메인 창이 붙기 전(로그인 화면 등)이나 닫힌 뒤에는 무시된다.
/// · 최대 3개까지 쌓이고 넘치면 가장 오래된 것부터 닫힌다. 클릭하면 바로 닫히고, 마우스를 올려 두면 시간이 멈춘다.
/// · 앱이 비활성(다른 앱으로 전환) · 최소화되면 숨겼다가 돌아오면 다시 보여 준다 (그동안 시간도 멈춤).
///
/// 구현: 토스트마다 작은 Popup(별도 최상위 창). 터미널(WebView2) · RDP(ActiveX) 는 HWND 라 창 안 WPF 오버레이가 그 위에 그려지지 않기 때문.
/// Popup 은 AllowsTransparency 를 쓰지 않는다(레이어드 창 = 소프트웨어 렌더링) — 대신 Windows 11 은 DWM 으로 모서리를 둥글게 한다.
/// 타이머는 떠 있는 토스트에만 있고 닫히면 멈춘 뒤 버린다. 창 이벤트 구독은 창이 닫힐 때 모두 해제한다.
/// 문구는 호출하는 쪽이 L.T/L.F 로 번역해 넘긴다.
/// </summary>
public static class Toast
{
    private const double ToastWidth = 340;
    private static readonly Color DwmBorder = Color.FromRgb(0x3A, 0x3E, 0x48);
    private const double EdgeMargin = 16;
    private const double Gap = 8;
    private const int MaxVisible = 3;

    private static Window? _owner;
    private static FrameworkElement? _anchor;
    private static bool _hidden;
    private static readonly List<Item> Items = new();

    private sealed class Item
    {
        public required Popup Popup;
        public required Border Body;
        public required DispatcherTimer Timer;
        public bool Closing;
    }

    /// <summary>메인 창이 만들어질 때 한 번. anchor = 토스트를 띄울 기준 영역 (오른쪽 아래 기준)</summary>
    public static void Attach(Window owner, FrameworkElement anchor)
    {
        if (_owner != null) Detach();
        _owner = owner;
        _anchor = anchor;
        owner.LocationChanged += OnOwnerMoved;
        owner.SizeChanged += OnOwnerMoved;
        owner.StateChanged += OnOwnerStateChanged;
        owner.Closed += OnOwnerClosed;
        // 앱 단위 활성/비활성 — 메인 창이 아니라 대화상자에서 다른 앱으로 넘어가도 숨긴다
        // (Application 은 앱 수명 동안 살아 있고 Detach 에서 해제한다)
        if (Application.Current is { } app)
        {
            app.Activated += OnAppActivated;
            app.Deactivated += OnAppDeactivated;
        }
    }

    /// <summary>토스트 띄우기 (ms = 보여 줄 시간, 최소 1초)</summary>
    public static void Show(string text, ToastKind kind = ToastKind.Info, int ms = 4000)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted) return;
        if (!dispatcher.CheckAccess()) { dispatcher.BeginInvoke(() => ShowCore(text, kind, ms)); return; }
        ShowCore(text, kind, ms);
    }

    /// <summary>떠 있는 토스트를 모두 닫는다</summary>
    public static void ClearAll()
    {
        foreach (var it in Items.ToArray()) Remove(it);
    }

    private static void ShowCore(string text, ToastKind kind, int ms)
    {
        if (_owner == null || _anchor == null || !_owner.IsLoaded) return;
        while (Items.Count >= MaxVisible) Remove(Items[0]);
        // 다른 앱을 쓰는 중에 생긴 알림(백그라운드 전송 완료 등)은 그 앱 위에 띄우지 않고, 돌아왔을 때 보여 준다
        if (!_hidden && !AnyAppWindowActive()) SetHidden(true);

        var body = Build(text, kind);
        var popup = new Popup
        {
            Child = body,
            AllowsTransparency = false,
            Placement = PlacementMode.Relative,
            PlacementTarget = _anchor,
            StaysOpen = true,
            Focusable = false,
            PopupAnimation = PopupAnimation.None,
        };
        var timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(Math.Max(1000, ms)) };
        var item = new Item { Popup = popup, Body = body, Timer = timer };
        timer.Tick += (_, _) => Dismiss(item);
        body.MouseLeftButtonUp += (_, _) => Dismiss(item);
        body.MouseEnter += (_, _) => item.Timer.Stop();
        body.MouseLeave += (_, _) => { if (!item.Closing && !_hidden) item.Timer.Start(); };
        body.SizeChanged += (_, _) => Reposition();
        popup.Opened += (_, _) =>
        {
            if (PresentationSource.FromVisual(body) is HwndSource src)
                WindowBackdrop.RoundCorners(src.Handle, DwmBorder);
        };

        Items.Add(item);
        if (!_hidden)
        {
            popup.IsOpen = true;
            timer.Start();
        }
        Reposition();
        Anim.FadeIn(body, Anim.NormalMs, offsetY: 0);   // AllowsTransparency 가 없어 위치 이동은 잘린다 — 페이드만
        // 화면 읽기 프로그램에 알림
        if (UIElementAutomationPeer.CreatePeerForElement(body) is { } peer)
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private static Border Build(string text, ToastKind kind)
    {
        var (glyph, colorKey) = kind switch
        {
            ToastKind.Success => ("", "Success"),
            ToastKind.Warning => ("", "Warning"),
            ToastKind.Error => ("", "Danger"),
            _ => ("", "Accent"),
        };
        var res = Application.Current.Resources;
        var accent = res[colorKey] as Brush ?? Brushes.White;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = res["IconFont"] as FontFamily,
            FontSize = 15,
            Foreground = accent,
            Margin = new Thickness(0, 1, 12, 0),
            VerticalAlignment = VerticalAlignment.Top,
        });
        var msg = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = res["UiFont"] as FontFamily,
            FontSize = 13,
            Foreground = res["Text"] as Brush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(msg, 1);
        grid.Children.Add(msg);

        var body = new Border
        {
            Width = ToastWidth,
            Background = res["OverlayRaisedBg"] as Brush,
            // Windows 11 은 DWM 이 테두리를 그린다 (RoundCorners). 그 밖에는 WPF 머리카락 테두리
            BorderBrush = WindowBackdrop.IsWindows11 ? null : res["StrokeStrong"] as Brush,
            BorderThickness = new Thickness(WindowBackdrop.IsWindows11 ? 0 : 1),
            Padding = new Thickness(14, 12, 16, 12),
            Cursor = Cursors.Hand,
            Child = grid,
        };
        TextOptions.SetTextFormattingMode(body, TextFormattingMode.Display);
        AutomationProperties.SetLiveSetting(body, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(body, text);
        return body;
    }

    /// <summary>닫기: 짧게 사라진 뒤 팝업을 닫고 목록에서 뺀다</summary>
    private static void Dismiss(Item it)
    {
        if (it.Closing) return;
        it.Closing = true;
        it.Timer.Stop();
        if (!it.Popup.IsOpen) { Remove(it); return; }
        Anim.FadeOut(it.Body, () => Remove(it));
    }

    /// <summary>즉시 닫고 참조를 모두 끊는다 (타이머 · 팝업 · 목록)</summary>
    private static void Remove(Item it)
    {
        it.Closing = true;
        it.Timer.Stop();
        it.Body.BeginAnimation(UIElement.OpacityProperty, null);
        it.Popup.IsOpen = false;
        it.Popup.Child = null;
        it.Popup.PlacementTarget = null;
        if (Items.Remove(it)) Reposition();
    }

    /// <summary>
    /// 아래에서 위로 쌓기 (가장 최근 것이 맨 아래). 창이 움직이면 Popup 이 따라오지 않으므로 오프셋을 다시 넣어 위치를 갱신한다
    /// </summary>
    private static void Reposition()
    {
        if (_anchor == null) return;
        var x = Math.Max(0, _anchor.ActualWidth - ToastWidth - EdgeMargin);
        var y = _anchor.ActualHeight - EdgeMargin;
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            var it = Items[i];
            var h = it.Body.ActualHeight > 0 ? it.Body.ActualHeight : 48;
            y -= h;
            // 같은 값을 다시 넣으면 Popup 이 위치를 다시 계산하지 않는다 — 살짝 바꿨다가 되돌린다
            it.Popup.HorizontalOffset = x + 0.01;
            it.Popup.HorizontalOffset = x;
            it.Popup.VerticalOffset = Math.Max(0, y);
            y -= Gap;
        }
    }

    private static void SetHidden(bool hidden)
    {
        if (_hidden == hidden) return;
        _hidden = hidden;
        foreach (var it in Items)
        {
            if (it.Closing) continue;
            if (hidden) it.Timer.Stop(); else it.Timer.Start();
            it.Popup.IsOpen = !hidden;
        }
        if (!hidden) Reposition();
    }

    private static void OnOwnerMoved(object? sender, EventArgs e) { if (Items.Count > 0) Reposition(); }

    private static void OnOwnerStateChanged(object? sender, EventArgs e) =>
        SetHidden(_owner?.WindowState == WindowState.Minimized || (_owner?.IsActive == false && !AnyAppWindowActive()));

    private static void OnAppActivated(object? sender, EventArgs e) => SetHidden(_owner?.WindowState == WindowState.Minimized);

    /// <summary>
    /// 다른 앱으로 전환하면 숨긴다 (Popup 은 항상 위 창이라 다른 앱 위에 떠 있게 된다).
    /// 이 앱 창끼리(메인 ↔ 대화상자) 오가는 것은 Application.Deactivated 가 오지 않으므로 그대로 보인다
    /// </summary>
    private static void OnAppDeactivated(object? sender, EventArgs e) => SetHidden(true);

    private static bool AnyAppWindowActive()
    {
        if (Application.Current == null) return false;
        foreach (Window w in Application.Current.Windows)
            if (w.IsActive) return true;
        return false;
    }

    private static void OnOwnerClosed(object? sender, EventArgs e) => Detach();

    private static void Detach()
    {
        ClearAll();
        if (_owner != null)
        {
            _owner.LocationChanged -= OnOwnerMoved;
            _owner.SizeChanged -= OnOwnerMoved;
            _owner.StateChanged -= OnOwnerStateChanged;
            _owner.Closed -= OnOwnerClosed;
        }
        if (Application.Current is { } app)
        {
            app.Activated -= OnAppActivated;
            app.Deactivated -= OnAppDeactivated;
        }
        _owner = null;
        _anchor = null;
        _hidden = false;
    }
}
