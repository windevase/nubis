using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Nubis;

/// <summary>서버가 묻는 인증 질문(OTP·비밀번호 변경 등)에 답하는 창. 질문 문구는 서버가 보낸 그대로 보여준다.</summary>
public partial class AuthPromptDialog : Window
{
    private readonly List<Control> _inputs = [];

    private AuthPromptDialog(string target, AuthPromptRequest req)
    {
        InitializeComponent();
        TitleText.Text = req.Title;
        TargetText.Text = target;
        InstructionText.Text = req.Instruction;
        InstructionText.Visibility = string.IsNullOrWhiteSpace(req.Instruction) ? Visibility.Collapsed : Visibility.Visible;

        foreach (var f in req.Fields)
        {
            FieldsPanel.Children.Add(new TextBlock { Text = f.Label, Style = (Style)FindResource("FieldLabel"), TextWrapping = TextWrapping.Wrap });
            Control input = f.Secret ? new PasswordBox() : new TextBox();
            _inputs.Add(input);
            FieldsPanel.Children.Add(input);
        }
        Loaded += (_, _) => _inputs.FirstOrDefault()?.Focus();
    }

    /// <summary>UI 스레드에서 호출. 취소하면 null.</summary>
    public static string[]? Ask(Window? owner, string target, AuthPromptRequest req)
    {
        var dlg = new AuthPromptDialog(target, req);
        if (owner != null && owner.IsLoaded) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (dlg.ShowDialog() != true) return null;
        return dlg._inputs.Select(c => c is PasswordBox p ? p.Password : ((TextBox)c).Text).ToArray();
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
