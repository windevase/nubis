using System;
using System.Windows;
using System.Windows.Threading;

namespace Nubis;

/// <summary>
/// 첫 실행 안내(시작하기)와 Claude 안내 연결:
/// - 계정별 프로필마다 처음 한 번 시작하기 창을 띄운다 (Settings 에서 다시 열기)
/// - "직접 하기" 는 해당 화면을 열고, "Claude 에게 묻기" 는 Claude 패널을 열며 안내 질문으로 시작한다
/// - Claude 는 MCP open_app_page 로 같은 화면을 띄우며 안내한다 (OpenAppPage)
/// </summary>
public partial class MainWindow
{
    /// <summary>Claude 패널을 새로 띄울 때 첫 질문으로 넘길 문장 (한 번 쓰고 비움)</summary>
    private string? _pendingClaudePrompt;

    /// <summary>처음 실행이면 시작하기 창 (--connect 로 바로 접속하는 실행은 건너뜀)</summary>
    private void ShowOnboardingIfFirstRun()
    {
        if (SettingsStore.Current.OnboardingShown) return;
        if (Environment.GetCommandLineArgs() is { Length: > 1 } args && Array.Exists(args, a => a.Equals("--connect", StringComparison.OrdinalIgnoreCase))) return;
        SettingsStore.Update(s => s.OnboardingShown = true);
        ShowOnboarding();
    }

    private void ShowOnboarding()
    {
        if (OnboardingWindow.Show(this, FindClaude() != null) is not { } pick) return;
        if (pick.AskClaude) AskClaude(GuidePrompt(pick.Topic));
        else OpenTopic(pick.Topic);
    }

    private void OnShowOnboarding(object sender, RoutedEventArgs e) => ShowOnboarding();

    private void OpenTopic(OnboardingTopic topic)
    {
        switch (topic)
        {
            case OnboardingTopic.Hosts: OpenAppPage("new_host"); break;
            case OnboardingTopic.Cloud: OpenAppPage("cloud"); break;
            case OnboardingTopic.Vault: OpenAppPage("vault"); break;
            case OnboardingTopic.Login: OpenAppPage("settings_login"); break;
            case OnboardingTopic.Claude: ToggleClaude(true); break;
        }
    }

    /// <summary>안내 질문 (사용자 언어로 — Claude 도 그 언어로 답한다)</summary>
    private static string GuidePrompt(OnboardingTopic topic) => topic switch
    {
        OnboardingTopic.Hosts => L.T("Nubis 에 서버를 처음 추가하려고 해. get_setup_status 로 지금 상태를 보고, 직접 추가하는 방법과 네가 add_host 로 추가해 주는 방법을 알려줘. 필요한 화면은 open_app_page 로 열어 줘."),
        OnboardingTopic.Cloud => L.T("Nubis 로 클라우드 서버를 가져오고 싶어. get_setup_status 로 이 PC 에 어떤 클라우드 CLI 로그인이 있는지 보고, 프로필을 추가해 동기화하는 과정을 단계별로 안내해줘. Cloud 화면은 open_app_page 로 열어 줘."),
        OnboardingTopic.Vault => L.T("Nubis Vault 에 서버 계정을 저장하는 방법을 알려줘. 비밀번호와 SSH 키를 어떻게 넣고, 호스트에 어떻게 연결하는지 단계별로. Vault 화면은 open_app_page 로 열어 줘."),
        OnboardingTopic.Login => L.T("Nubis 시작 화면 로그인(SSO) 을 설정하고 싶어. get_setup_status 로 현재 상태를 보고, GitHub · Google · Microsoft 중 하나를 골라 OAuth 앱 등록부터 Client ID 입력, 테스트 로그인까지 한 단계씩 안내해줘. 설정 화면은 open_app_page 로 열어 줘. 값 입력과 저장은 내가 직접 할게."),
        _ => L.T("Nubis 의 Claude 패널로 무엇을 할 수 있는지 get_setup_status 로 내 상태를 보고 예시와 함께 짧게 소개해줘. 위험한 작업은 어떻게 승인되는지도 알려줘."),
    };

    /// <summary>Claude 패널을 열고 질문을 넘긴다 (처음 띄우면 첫 질문으로, 이미 떠 있으면 입력창에 입력)</summary>
    private void AskClaude(string prompt)
    {
        if (_claudeView == null)
        {
            _pendingClaudePrompt = prompt;
            ToggleClaude(true);
            return;
        }
        ToggleClaude(true);
        // CLI 가 입력을 받을 준비가 된 상태 — 질문을 입력하고 보낸다
        Dispatcher.BeginInvoke(() => _claudeView?.SendText(prompt + "\r"), DispatcherPriority.Input);
    }

    /// <summary>cmd.exe / 명령줄에 그대로 넣어도 안전한 첫 질문 (따옴표·셸 특수 문자 제거)</summary>
    public static string SafePromptArg(string prompt)
    {
        var sb = new System.Text.StringBuilder(prompt.Length);
        foreach (var ch in prompt)
            sb.Append(ch is '"' or '&' or '|' or '<' or '>' or '^' or '%' or '!' or '\r' or '\n' or '`' ? ' ' : ch);
        return sb.ToString().Trim();
    }

    /// <summary>
    /// 앱 화면 열기 (시작하기 버튼 · MCP open_app_page). 알 수 없는 이름이면 false.
    /// hosts · new_host · cloud · vault · known_hosts · settings · settings_language · settings_login · settings_sftp · settings_claude · onboarding
    /// </summary>
    internal bool OpenAppPage(string page)
    {
        SelectHome();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        FrameworkElement? scrollTo = null;
        switch (page.Trim().ToLowerInvariant())
        {
            case "hosts": NavHosts.IsChecked = true; break;
            case "new_host": NavHosts.IsChecked = true; OpenEditor(null); break;
            case "cloud": NavCloud.IsChecked = true; break;
            case "vault": NavVault.IsChecked = true; break;
            case "known_hosts": NavKnown.IsChecked = true; break;
            case "settings": NavSettings.IsChecked = true; break;
            case "settings_language": NavSettings.IsChecked = true; scrollTo = LangKorean; break;
            case "settings_login": NavSettings.IsChecked = true; scrollTo = LoginNone; break;
            case "settings_sftp": NavSettings.IsChecked = true; scrollTo = SftpAutoOpenBox; break;
            case "settings_claude": NavSettings.IsChecked = true; scrollTo = AutoReadOnlyBox; break;
            case "onboarding": Dispatcher.BeginInvoke(ShowOnboarding, DispatcherPriority.Background); break;
            default: return false;
        }
        if (scrollTo != null)
            Dispatcher.BeginInvoke(() => scrollTo.BringIntoView(new Rect(0, -40, 10, 400)), DispatcherPriority.Loaded);
        return true;
    }
}
