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
        InstructionScroll.Visibility = string.IsNullOrWhiteSpace(req.Instruction) ? Visibility.Collapsed : Visibility.Visible;

        foreach (var f in req.Fields)
        {
            FieldsPanel.Children.Add(new TextBlock { Text = f.Label, Style = (Style)FindResource("FieldLabel"), TextWrapping = TextWrapping.Wrap });
            Control input = f.Secret ? new PasswordBox() : new TextBox();
            // 화면 낭독기가 칸마다 서버의 질문 문구를 읽게 (위 TextBlock 은 칸과 연결되어 있지 않다)
            System.Windows.Automation.AutomationProperties.SetName(input, f.Label);
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
        _secret = req.Fields.Select(f => f.Secret).ToList();
        // 창은 SSH 연결 스레드의 요청으로 뜬다 — 다른 창이 앞에 있어도 입력이 바로 이 창으로 가게 활성화하고 첫 칸에 키보드 포커스
        ContentRendered += (_, _) =>
        {
            Activate();
            if (_inputs.FirstOrDefault() is { } first) { first.Focus(); Keyboard.Focus(first); }
        };
    }

    private readonly List<bool> _secret = [];
    // 빈 값으로 보내기를 한 번 확인받은 칸 (빈 OTP 를 보내 인증 시도 횟수를 낭비하지 않게)
    private readonly HashSet<int> _emptyConfirmed = [];

    private static string ValueOf(Control c) => c is PasswordBox p ? p.Password : ((TextBox)c).Text;

    /// <summary>UI 스레드에서 호출. 취소하면 null. "기억하기" 고른 질문은 req.Remember 에 표시한다</summary>
    public static string[]? Ask(Window? owner, string target, AuthPromptRequest req)
    {
        var dlg = new AuthPromptDialog(target, req);
        if (owner != null && owner.IsLoaded) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (dlg.ShowDialog() != true) return null;
        for (var i = 0; i < dlg._remember.Count && i < req.Remember.Length; i++)
            req.Remember[i] = dlg._remember[i] is { IsChecked: true, IsEnabled: true };
        return dlg._inputs.Select(ValueOf).ToArray();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        // 가려진 칸(OTP·비밀번호)이 비어 있으면 한 번은 그 칸으로 보낸다 — 여러 칸에서 첫 칸 Enter 로 바로 보내지거나
        // 빈 OTP 가 나가 서버의 인증 시도 횟수를 쓰지 않게. 한 번 더 확인을 누르면 빈 값 그대로 보낸다 ("Press enter" 질문).
        // 빈 칸이 여러 개여도(비밀번호 변경의 두 칸 등) 확인은 한 번만 — 처음 보는 빈 칸을 모두 확인한 것으로 두고 첫 칸으로 보낸다
        var firstNew = -1;
        for (var i = 0; i < _inputs.Count; i++)
        {
            if (!_secret[i] || ValueOf(_inputs[i]).Length > 0) continue;
            if (_remember[i] is { IsChecked: true }) continue;   // "빈 답을 기억" 을 고름 — 빈 값이 의도
            if (_emptyConfirmed.Add(i) && firstNew < 0) firstNew = i;
        }
        if (firstNew >= 0)
        {
            HintText.Text = L.T("입력하지 않은 칸이 있습니다. 빈 값으로 보내려면 확인을 한 번 더 누르세요.");
            HintText.Visibility = Visibility.Visible;
            _inputs[firstNew].Focus();
            return;
        }
        DialogResult = true;
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
