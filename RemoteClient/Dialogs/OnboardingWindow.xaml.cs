using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Nubis;

/// <summary>시작하기 안내에서 고른 항목</summary>
public enum OnboardingTopic { Hosts, Cloud, Vault, Login, Claude }

/// <summary>
/// 첫 실행 안내 (계정별 프로필마다 한 번, Settings 에서 다시 열기). 카드마다 "직접 하기"(해당 화면으로) 와
/// "Claude 에게 묻기"(Claude 패널을 열고 안내 질문으로 시작) 가 있다. 고른 동작은 Result 로 돌려주고 메인 창이 실행한다.
/// </summary>
public partial class OnboardingWindow : Window
{
    /// <summary>(항목, Claude 에게 물을지). 그냥 닫으면 null</summary>
    public (OnboardingTopic Topic, bool AskClaude)? Result { get; private set; }

    private OnboardingWindow(bool claudeInstalled)
    {
        InitializeComponent();
        AddCard(OnboardingTopic.Hosts, "", L.T("서버 직접 추가"),
            L.T("SSH / RDP 서버를 주소·포트·계정으로 등록합니다. 검색창에 user@host 를 입력해 바로 접속할 수도 있습니다."), L.T("새 호스트"));
        AddCard(OnboardingTopic.Cloud, "", L.T("클라우드 서버 가져오기"),
            L.T("AWS · Tencent · GCP · Azure 의 서버를 이 PC 의 CLI 로그인으로 그룹별로 가져옵니다."), L.T("Cloud 열기"));
        AddCard(OnboardingTopic.Vault, "", L.T("Vault 계정"),
            L.T("여러 서버가 함께 쓰는 사용자명·비밀번호·SSH 키를 한 번 저장해 두고 골라 씁니다."), L.T("Vault 열기"));
        AddCard(OnboardingTopic.Login, "", L.T("시작 화면 로그인 (SSO)"),
            L.T("GitHub · Google · Microsoft 계정으로 로그인해야 앱이 열리게 하고, 계정마다 데이터를 나눕니다."), L.T("설정 열기"));
        AddCard(OnboardingTopic.Claude, "", L.T("Claude 패널"),
            L.T("\"web-01 접속해줘\", \"디스크 확인해줘\", \"테마 바꿔줘\" 처럼 말로 서버와 앱을 다룹니다. 명령 실행은 항상 승인을 거칩니다."),
            L.T("Claude 패널 열기"), askLabel: L.T("기능 둘러보기"));

        ClaudeStatusText.Text = claudeInstalled
            ? L.T("Claude Code CLI 가 설치되어 있습니다. 패널을 처음 열면 /login 으로 로그인하고 작업 폴더를 신뢰하세요.")
            : L.T("Claude Code CLI(claude) 가 없습니다. 설치: npm install -g @anthropic-ai/claude-code — 설치 전에도 각 카드의 왼쪽 버튼으로 모든 기능을 쓸 수 있습니다.");
    }

    /// <summary>메인 창 위에 띄우고 고른 동작을 돌려준다</summary>
    public static (OnboardingTopic Topic, bool AskClaude)? Show(Window owner, bool claudeInstalled)
    {
        var w = new OnboardingWindow(claudeInstalled) { Owner = owner };
        w.ShowDialog();
        return w.Result;
    }

    private void AddCard(OnboardingTopic topic, string glyph, string title, string text, string doLabel, string? askLabel = null)
    {
        var res = Application.Current.Resources;
        var doBtn = new Button { Style = (Style)res["GhostButton"], Content = doLabel, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 5, 12, 5), FontSize = 12 };
        doBtn.Click += (_, _) => { Result = (topic, false); DialogResult = true; };
        var askBtn = new Button { Style = (Style)res["PrimaryButton"], Content = askLabel ?? L.T("Claude 에게 묻기"), Padding = new Thickness(12, 5, 12, 5), FontSize = 12 };
        askBtn.Click += (_, _) => { Result = (topic, true); DialogResult = true; };

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new TextBlock { Text = glyph, FontFamily = (FontFamily)res["IconFont"], FontSize = 16, Foreground = (Brush)res["Accent"], VerticalAlignment = VerticalAlignment.Center });
        head.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(doBtn);
        buttons.Children.Add(askBtn);

        var body = new StackPanel();
        body.Children.Add(head);
        body.Children.Add(new TextBlock { Text = text, FontSize = 12, Foreground = (Brush)res["SubText"], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), MinHeight = 48 });
        body.Children.Add(buttons);

        Cards.Children.Add(new Border
        {
            Background = (Brush)res["CardBg"], CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 14, 16, 14),
            Margin = new Thickness(0, 0, Cards.Children.Count % 2 == 0 ? 6 : 0, 10), Child = body,
        });
    }

    private void OnClose(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
