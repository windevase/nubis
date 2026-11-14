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
    private readonly List<CheckBox?> _remember = [];

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

            // "이 답 기억하기": 화면에 보이는 질문은 그대로, 가려진 질문은 빈 답일 때만 (OTP 코드·비밀번호는 기억하지 않음)
            CheckBox? check = null;
            if (f.CanRemember)
            {
                check = new CheckBox
                {
                    Content = f.Secret ? L.T("빈 답을 기억하고 다음부터 묻지 않기") : L.T("이 답을 기억하고 다음부터 묻지 않기"),
                    Margin = new Thickness(0, 8, 0, 0),
                };
                if (input is PasswordBox pb)
                    pb.PasswordChanged += (_, _) => { check.IsEnabled = pb.Password.Length == 0; if (!check.IsEnabled) check.IsChecked = false; };
                FieldsPanel.Children.Add(check);
            }
            _remember.Add(check);
        }
        Loaded += (_, _) => _inputs.FirstOrDefault()?.Focus();
    }

    /// <summary>UI 스레드에서 호출. 취소하면 null. "기억하기" 고른 질문은 req.Remember 에 표시한다</summary>
    public static string[]? Ask(Window? owner, string target, AuthPromptRequest req)
    {
        var dlg = new AuthPromptDialog(target, req);
        if (owner != null && owner.IsLoaded) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (dlg.ShowDialog() != true) return null;
        for (var i = 0; i < dlg._remember.Count && i < req.Remember.Length; i++)
            req.Remember[i] = dlg._remember[i] is { IsChecked: true, IsEnabled: true };
        return dlg._inputs.Select(c => c is PasswordBox p ? p.Password : ((TextBox)c).Text).ToArray();
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
