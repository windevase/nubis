using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Nubis;

/// <summary>
/// SSH 탭의 포워딩 창 (도구 막대 버튼): 지금 걸린 포워딩 — 상태 · 로컬 주소 복사 · 브라우저로 열기 · 켜기/끄기 · 이 탭에서만 임시 추가.
/// 터미널(WebView2)은 HWND 라 창 안 WPF 오버레이가 그 아래로 숨으므로 명령 팔레트처럼 소유된 별도 창으로 띄운다.
/// AllowsTransparency 를 쓰지 않는다 (모서리 · 테두리는 Windows 11 DWM). 다른 곳을 누르면 닫힌다
/// </summary>
internal sealed class PortForwardWindow : Window
{
    private static PortForwardWindow? _current;
    private static readonly Color DwmBorder = Color.FromRgb(0x3A, 0x3E, 0x48);

    private SshSessionView? _view;
    private readonly Border _frame;
    private readonly StackPanel _rows = new();
    private readonly TextBlock _empty;
    private readonly PortForwardForm _form;
    private bool _closing, _closed;

    /// <summary>열려 있으면 닫고, 아니면 이 탭의 창을 연다</summary>
    public static void Toggle(Window owner, FrameworkElement anchor, SshSessionView view)
    {
        if (_current is { } open)
        {
            var same = ReferenceEquals(open._view, view);
            open.CloseOnce();
            if (same) return;
        }
        var w = new PortForwardWindow(owner, view);
        _current = w;
        w.Place(anchor);
        w.Show();
        w.Activate();
    }

    /// <summary>이 탭의 창이 열려 있으면 목록을 다시 그린다 (상태가 바뀜)</summary>
    public static void RefreshFor(SshSessionView view)
    {
        if (_current is { _closed: false } w && ReferenceEquals(w._view, view)) w.Refresh();
    }

    private PortForwardWindow(Window owner, SshSessionView view)
    {
        _view = view;
        Owner = owner;
        var res = Application.Current.Resources;
        Title = L.T("포트 포워딩");
        Width = 480;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;
        Background = (Brush)res["OverlayBg"];
        Foreground = (Brush)res["Text"];
        FontFamily = (FontFamily)res["UiFont"];
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        var header = new TextBlock { Text = L.F("포트 포워딩 — {0}", view.HostDisplayName), Style = (Style)res["TextBodyStrong"], TextTrimming = TextTrimming.CharacterEllipsis };
        var sub = new TextBlock
        {
            Text = L.T("이 탭의 SSH 연결로 이 PC 의 127.0.0.1 에만 엽니다 (다른 PC 는 쓸 수 없음). 탭을 닫거나 연결이 끊기면 닫히고, 다시 연결되면 다시 열립니다."),
            Style = (Style)res["TextCaption"],
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 12),
        };
        _empty = new TextBlock
        {
            Text = L.T("이 호스트에 저장된 포워딩이 없습니다. 아래에서 이 탭에만 임시로 추가하거나, 호스트 편집의 '포트 포워딩' 에 저장하세요."),
            Style = (Style)res["TextCaption"],
            Foreground = (Brush)res["TextTertiary"],
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
        };
        var addLabel = new TextBlock { Text = L.T("이 탭에서만 임시 추가 (저장하지 않음)"), Style = (Style)res["FieldLabel"], Margin = new Thickness(0, 14, 0, 8) };
        _form = new PortForwardForm(L.T("열기"))
        {
            Submit = rule => _view?.Forwards.AddTemporary(rule).Error,
        };
        var hint = new TextBlock
        {
            Text = L.T("계속 쓸 규칙은 호스트 편집 → 포트 포워딩에 저장하면 연결할 때마다 자동으로 열립니다."),
            Style = (Style)res["TextCaption"],
            Foreground = (Brush)res["TextTertiary"],
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
        };

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(sub);
        stack.Children.Add(_empty);
        stack.Children.Add(new ScrollViewer { Content = _rows, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        stack.Children.Add(addLabel);
        stack.Children.Add(_form);
        stack.Children.Add(hint);
        _frame = new Border
        {
            BorderBrush = (Brush)res["StrokeStrong"],
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 14, 16, 16),
            Child = stack,
        };
        Content = _frame;
        SourceInitialized += (_, _) =>
        {
            if (WindowBackdrop.RoundCorners(new WindowInteropHelper(this).Handle, DwmBorder)) _frame.BorderThickness = new Thickness(0);
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; CloseOnce(); } };
        Refresh();
    }

    /// <summary>버튼 오른쪽에 (화면 좌표 → DIP). 아래가 모자라면 위로 올린다</summary>
    private void Place(FrameworkElement anchor)
    {
        try
        {
            var p = anchor.PointToScreen(new Point(anchor.ActualWidth + 6, 0));
            if (PresentationSource.FromVisual(anchor) is { CompositionTarget: { } ct }) p = ct.TransformFromDevice.Transform(p);
            Left = p.X;
            Top = p.Y;
            var area = SystemParameters.WorkArea;
            Loaded += (_, _) =>
            {
                if (Top + ActualHeight > area.Bottom) Top = Math.Max(area.Top, area.Bottom - ActualHeight - 8);
                if (Left + ActualWidth > area.Right) Left = Math.Max(area.Left, area.Right - ActualWidth - 8);
            };
        }
        catch (InvalidOperationException) { WindowStartupLocation = WindowStartupLocation.CenterOwner; }
    }

    private void Refresh()
    {
        if (_view == null) return;
        _rows.Children.Clear();
        var items = _view.Forwards.Items;
        _empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var f in items) _rows.Children.Add(BuildRow(f));
    }

    private PortForwardRow BuildRow(ActiveForward f)
    {
        var view = _view!;
        var r = f.Rule;
        var row = new PortForwardRow(f.LocalAddress, PortForwards.TargetAddress(r.TargetHost, r.TargetPort), r.Name);
        var temp = f.Temporary ? L.T("임시") + " · " : "";
        switch (f.State)
        {
            case ForwardState.Open:
                var conn = f.Connections > 0 ? L.F("열림 · 연결 {0}번", f.Connections) : L.T("열림");
                row.SetState(PortForwardRowState.Open, temp + (f.Error != null ? conn + " · " + L.F("마지막 연결 실패: {0}", f.Error) : conn));
                break;
            case ForwardState.Waiting:
                row.SetState(PortForwardRowState.Waiting, temp + L.T("SSH 가 연결되면 열립니다"));
                break;
            case ForwardState.Failed:
                row.SetState(PortForwardRowState.Failed, temp + (f.Error ?? L.T("열지 못했습니다")));
                break;
            default:
                row.SetState(PortForwardRowState.Off, temp + L.T("꺼짐"));
                break;
        }
        if (f.State == ForwardState.Open)
        {
            row.AddAction("", L.F("{0} 복사", f.LocalAddress), () =>
            {
                try
                {
                    Clipboard.SetText(f.LocalAddress);
                    Toast.Show(L.F("{0} 을(를) 복사했습니다", f.LocalAddress), ToastKind.Success);
                }
                catch (Exception ex) { Toast.Show(L.F("복사하지 못했습니다: {0}", ex.Message), ToastKind.Error); }
            });
            if (PortForwards.BrowserUrl(r) is { } url)
                row.AddAction("", L.F("브라우저로 열기 ({0})", url), () =>
                {
                    try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
                    catch (Exception ex) { Toast.Show(L.F("브라우저를 열지 못했습니다: {0}", ex.Message), ToastKind.Error); }
                });
        }
        if (f.Temporary)
            row.AddAction("", L.T("이 임시 포워딩 닫기"), () => view.Forwards.Remove(f));
        else
            row.AddToggle(f.Enabled, L.T("이 탭에서 켜기/끄기"), on =>
            {
                if (view.Forwards.SetEnabled(f, on) is { } err)
                {
                    Toast.Show(err, ToastKind.Warning);
                    Refresh();
                }
            });
        return row;
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        CloseOnce();
    }

    private void CloseOnce()
    {
        if (_closing) return;
        _closing = true;
        // Deactivated 처리 중에 바로 Close 하면 WPF 가 예외를 낼 수 있어 한 박자 늦춘다
        Dispatcher.BeginInvoke(() => { if (!_closed) Close(); }, DispatcherPriority.Normal);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _closing = true;
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _closed = true;
        if (ReferenceEquals(_current, this)) _current = null;
        _rows.Children.Clear();
        _form.Submit = null;
        _view = null;   // 닫힌 탭(세션 뷰)을 붙잡지 않게
    }
}
