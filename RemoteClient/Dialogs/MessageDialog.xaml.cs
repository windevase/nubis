using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Nubis;

/// <summary>앱 테마에 맞춘 메시지 대화상자 (Windows 기본 MessageBox 대체)</summary>
public partial class MessageDialog : Window
{
    private MessageBoxResult _primaryResult = MessageBoxResult.OK;
    private MessageBoxResult _secondaryResult = MessageBoxResult.Cancel;

    public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;

    private MessageDialog() => InitializeComponent();

    /// <summary>
    /// 버튼 문구를 직접 정하는 선택 창. 0 = primary, 1 = secondary, 2 = extra, -1 = 닫기(Esc).
    /// primary 가 Enter 기본 버튼이다.
    /// </summary>
    internal static int Choose(Window? owner, string message, string title, MessageBoxImage icon, string? detail,
        string primary, string secondary, string? extra)
    {
        var dlg = Create(owner, message, title, icon, detail);
        dlg.SetButtons(primary, MessageBoxResult.Yes, secondary, MessageBoxResult.No);
        if (extra != null)
        {
            dlg.ExtraButton.Content = extra;
            dlg.ExtraButton.Visibility = Visibility.Visible;
        }
        // Esc 는 어느 버튼도 아닌 "닫기"
        dlg.SecondaryButton.IsCancel = false;
        dlg.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { dlg.Result = MessageBoxResult.None; dlg.DialogResult = false; } };
        dlg.PrimaryButton.IsDefault = true;
        dlg.Loaded += (_, _) => dlg.PrimaryButton.Focus();
        Dialog.RestoreIfMinimized(owner);
        dlg.ShowDialog();
        return dlg._extraChosen ? 2 : dlg.Result switch { MessageBoxResult.Yes => 0, MessageBoxResult.No => 1, _ => -1 };
    }

    private bool _extraChosen;
    private MessageBoxResult _extraResult = MessageBoxResult.None;
    private void OnExtra(object sender, RoutedEventArgs e) { _extraChosen = true; Result = _extraResult; DialogResult = false; }

    internal static MessageBoxResult Show(Window? owner, string message, string title, MessageBoxButton buttons,
        MessageBoxImage icon, string? detail)
    {
        var dlg = Create(owner, message, title, icon, detail);
        ConfigureButtons(dlg, buttons, icon);

        Dialog.RestoreIfMinimized(owner);
        dlg.ShowDialog();
        // 창을 그냥 닫으면(Esc) 부정 응답 (YesNoCancel 은 취소)
        return dlg.Result != MessageBoxResult.None ? dlg.Result
            : buttons == MessageBoxButton.OK ? MessageBoxResult.OK
            : buttons == MessageBoxButton.YesNoCancel ? MessageBoxResult.Cancel
            : dlg._secondaryResult;
    }

    private static MessageDialog Create(Window? owner, string message, string title, MessageBoxImage icon, string? detail)
    {
        var dlg = new MessageDialog();
        if (owner is { IsLoaded: true, IsVisible: true }) dlg.Owner = owner;
        else
        {
            // 주인 창이 없으면 작업 표시줄에 보이게 하고 앞으로 — 다른 창 뒤에 숨어 앱이 멈춘 것처럼 보이는 일 방지
            dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dlg.ShowInTaskbar = true;
            dlg.Loaded += (_, _) => { dlg.Activate(); dlg.Topmost = true; dlg.Topmost = false; };
        }
        // Ctrl+C: 제목·내용·상세를 클립보드로 (Windows 메시지 상자와 같음). 상세 칸에서 글자를 고른 경우는 그 선택을 복사
        dlg.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.C || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            if (dlg.DetailBox.IsKeyboardFocusWithin && dlg.DetailBox.SelectionLength > 0) return;
            var text = dlg.TitleText.Text + "\r\n\r\n" + dlg.MessageText.Text +
                       (dlg.DetailBox.Visibility == Visibility.Visible ? "\r\n\r\n" + dlg.DetailBox.Text : "");
            try { Clipboard.SetText(text); } catch { }
            e.Handled = true;
        };

        dlg.TitleText.Text = title;
        dlg.Title = title;
        dlg.MessageText.Text = message;
        if (!string.IsNullOrEmpty(detail))
        {
            dlg.DetailBox.Text = detail;
            dlg.DetailBox.Visibility = Visibility.Visible;
        }

        // 아이콘: 원형 배지 + Fluent 글리프
        var (glyph, color) = icon switch
        {
            MessageBoxImage.Question => ("", Color.FromRgb(0x4F, 0x8C, 0xFF)),
            MessageBoxImage.Warning => ("", Color.FromRgb(0xF5, 0xB7, 0x40)),
            MessageBoxImage.Error => ("", Color.FromRgb(0xF0, 0x61, 0x6D)),
            MessageBoxImage.Information => ("", Color.FromRgb(0x20, 0xC5, 0xA0)),
            _ => ((string?)null, default(Color)),
        };
        if (glyph != null)
        {
            dlg.IconBadge.Visibility = Visibility.Visible;
            dlg.IconBadge.Background = new SolidColorBrush(Color.FromArgb(0x33, color.R, color.G, color.B));
            dlg.IconGlyph.Text = glyph;
            dlg.IconGlyph.Foreground = new SolidColorBrush(color);
            if (icon is MessageBoxImage.Warning or MessageBoxImage.Error) dlg.Frame.BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, color.R, color.G, color.B));
        }
        return dlg;
    }

    private static void ConfigureButtons(MessageDialog dlg, MessageBoxButton buttons, MessageBoxImage icon)
    {
        switch (buttons)
        {
            case MessageBoxButton.YesNo:
                dlg.SetButtons(L.T("예"), MessageBoxResult.Yes, L.T("아니요"), MessageBoxResult.No);
                break;
            case MessageBoxButton.YesNoCancel:
                // 세 번째 버튼(취소)이 Esc. "아니요" 는 Esc 로 눌리지 않게 한다
                dlg.SetButtons(L.T("예"), MessageBoxResult.Yes, L.T("아니요"), MessageBoxResult.No);
                dlg.SecondaryButton.IsCancel = false;
                dlg.ExtraButton.Content = L.T("취소");
                dlg.ExtraButton.Visibility = Visibility.Visible;
                dlg.ExtraButton.IsCancel = true;
                dlg._extraResult = MessageBoxResult.Cancel;
                break;
            case MessageBoxButton.OKCancel:
                dlg.SetButtons(L.T("확인"), MessageBoxResult.OK, L.T("취소"), MessageBoxResult.Cancel);
                break;
            default:
                dlg.SetButtons(L.T("확인"), MessageBoxResult.OK, null, MessageBoxResult.None);
                break;
        }

        // 경고성 질문은 Enter 로 바로 승인되지 않게 (버튼을 직접 눌러야 함), 그 외에는 Enter = 기본 버튼
        dlg.PrimaryButton.IsDefault = !(icon == MessageBoxImage.Warning && buttons != MessageBoxButton.OK);
        dlg.Loaded += (_, _) => (dlg.PrimaryButton.IsDefault ? dlg.PrimaryButton : (UIElement)dlg.SecondaryButton).Focus();
    }

    private void SetButtons(string primary, MessageBoxResult primaryResult, string? secondary, MessageBoxResult secondaryResult)
    {
        PrimaryButton.Content = primary;
        _primaryResult = primaryResult;
        _secondaryResult = secondaryResult;
        if (secondary != null)
        {
            SecondaryButton.Content = secondary;
            SecondaryButton.Visibility = Visibility.Visible;
            SecondaryButton.IsCancel = true;
        }
        else PrimaryButton.IsCancel = true;
    }

    private void OnPrimary(object sender, RoutedEventArgs e) { Result = _primaryResult; DialogResult = true; }
    private void OnSecondary(object sender, RoutedEventArgs e) { Result = _secondaryResult; DialogResult = false; }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}

/// <summary>MessageBox.Show 와 같은 인자 순서의 앱 스타일 대화상자</summary>
public static class Dialog
{
    public static MessageBoxResult Show(Window? owner, string message, string title = "Nubis",
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None, string? detail = null) =>
        MessageDialog.Show(owner, message, title, buttons, icon, detail);

    /// <summary>버튼 문구를 직접 정하는 선택 창. 0 = primary, 1 = secondary, 2 = extra, -1 = 닫기(Esc)</summary>
    public static int Choose(Window? owner, string message, string title, string primary, string secondary, string? extra = null,
        MessageBoxImage icon = MessageBoxImage.Question, string? detail = null) =>
        MessageDialog.Choose(owner, message, title, icon, detail, primary, secondary, extra);

    public static MessageBoxResult Show(string message, string title = "Nubis",
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None) =>
        MessageDialog.Show(Application.Current?.MainWindow, message, title, buttons, icon, null);

    /// <summary>최소화된 창을 이전 상태로 되돌린다 (최대화였으면 최대화로 — WindowState = Normal 은 최대화를 잃는다)</summary>
    public static void RestoreIfMinimized(Window? w)
    {
        if (w is not { WindowState: WindowState.Minimized }) return;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
        if (hwnd == System.IntPtr.Zero || !ShowWindow(hwnd, 9 /* SW_RESTORE */)) w.WindowState = WindowState.Normal;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(System.IntPtr hWnd, int nCmdShow);
}
