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

    internal static MessageBoxResult Show(Window? owner, string message, string title, MessageBoxButton buttons,
        MessageBoxImage icon, string? detail)
    {
        var dlg = new MessageDialog();
        if (owner is { IsLoaded: true, IsVisible: true }) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;

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

        switch (buttons)
        {
            case MessageBoxButton.YesNo:
            case MessageBoxButton.YesNoCancel:
                dlg.SetButtons(L.T("예"), MessageBoxResult.Yes, L.T("아니요"), MessageBoxResult.No);
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

        if (owner is { WindowState: WindowState.Minimized }) owner.WindowState = WindowState.Normal;
        dlg.ShowDialog();
        // 창을 그냥 닫으면(Esc) 부정 응답
        return dlg.Result != MessageBoxResult.None ? dlg.Result
            : buttons == MessageBoxButton.OK ? MessageBoxResult.OK : dlg._secondaryResult;
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

    public static MessageBoxResult Show(string message, string title = "Nubis",
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None) =>
        MessageDialog.Show(Application.Current?.MainWindow, message, title, buttons, icon, null);
}
