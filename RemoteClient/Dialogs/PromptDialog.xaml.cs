using System.Windows;
using System.Windows.Input;

namespace Nubis;

/// <summary>한 줄 입력 대화상자 (새 폴더, 이름 변경 등)</summary>
public partial class PromptDialog : Window
{
    private PromptDialog() => InitializeComponent();

    public static string? Ask(Window? owner, string title, string label, string initial = "")
    {
        var dlg = new PromptDialog();
        if (owner != null && owner.IsLoaded) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dlg.TitleText.Text = title;
        dlg.LabelText.Text = label;
        dlg.InputBox.Text = initial;
        dlg.Loaded += (_, _) =>
        {
            dlg.InputBox.Focus();
            // 파일 이름은 확장자 앞까지만 선택
            var dot = initial.LastIndexOf('.');
            dlg.InputBox.Select(0, dot > 0 ? dot : initial.Length);
        };
        return dlg.ShowDialog() == true ? dlg.InputBox.Text.Trim() : null;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(InputBox.Text)) { InputBox.Focus(); return; }
        DialogResult = true;
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
