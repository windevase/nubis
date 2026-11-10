using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace Nubis;

/// <summary>
/// 앱 시작 로그인 화면 (Device Flow). 시작할 때는 로그인에 성공해 허용된 계정이어야 메인 창이 열린다.
/// Settings 의 "테스트 로그인" 에서도 같은 창을 쓴다 (설정을 저장하기 전에 실제로 들어갈 수 있는지 확인).
/// </summary>
public partial class LoginWindow : Window
{
    private readonly LoginConfig _cfg;
    private readonly bool _test;
    private CancellationTokenSource? _cts;
    private DeviceCodeInfo? _code;
    private readonly LoginState? _state;

    /// <summary>허용된 계정으로 로그인했으면 그 계정</summary>
    public LoginIdentity? Result { get; private set; }

    private LoginWindow(LoginConfig cfg, bool test)
    {
        InitializeComponent();
        _cfg = cfg;
        _test = test;
        _state = test ? null : LoginState.Load();
        var name = LoginConfig.ProviderLabel(cfg.Provider);
        SubTitle.Text = test ? "로그인 설정 테스트" : "Cloud & AI Remote";
        TitleText.Text = test ? $"{name} 로그인 테스트" : $"{name} 로그인이 필요합니다";
        InfoText.Text = (test ? "이 설정으로 실제로 들어갈 수 있는지 확인합니다. 성공해야 설정이 저장됩니다.\n" : "") +
                        $"허용된 계정으로 로그인하면 Nubis 가 열립니다. 비밀번호는 브라우저({name})에서만 입력하며, Nubis 는 받지 않습니다.";
        LoginButton.Content = $"{(cfg.Provider == LoginProviderKind.Microsoft ? "Microsoft" : name)}로 로그인";
        CancelButton.Content = test ? "취소" : "종료";
        Closed += (_, _) => _cts?.Cancel();
    }

    /// <summary>앱 시작: 로그인이 필요 없거나(사용 안 함·유지 시간 안) 허용된 계정으로 로그인하면 true</summary>
    public static bool EnsureAtStartup()
    {
        var cfg = SettingsStore.Current.Login;
        if (!cfg.Enabled) return true;
        var state = LoginState.Load();
        if (state != null && state.Within(cfg, TimeSpan.FromHours(cfg.RememberHours), DateTime.UtcNow)) return true;
        var w = new LoginWindow(cfg, test: false);
        return w.ShowDialog() == true;
    }

    /// <summary>Settings 의 테스트 로그인: 허용된 계정으로 로그인하면 그 계정, 아니면 null</summary>
    public static LoginIdentity? Test(Window owner, LoginConfig cfg)
    {
        var w = new LoginWindow(cfg, test: true) { Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        return w.ShowDialog() == true ? w.Result : null;
    }

    private void ShowError(string? text)
    {
        ErrorText.Text = text ?? "";
        ErrorText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnLogin(object sender, RoutedEventArgs e)
    {
        ShowError(null);
        OfflineButton.Visibility = Visibility.Collapsed;
        LoginButton.IsEnabled = false;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var flow = new DeviceFlow(_cfg);
        try
        {
            _code = await flow.StartAsync(ct);
            CodeBox.Text = _code.UserCode;
            UrlText.Text = _code.VerificationUri;
            CodePanel.Visibility = Visibility.Visible;
            LoginButton.Visibility = Visibility.Collapsed;
            CancelButton.Content = "취소";
            try { Clipboard.SetText(_code.UserCode); } catch { }
            OpenBrowser();

            var (who, orgMember) = await flow.WaitAsync(_code, ct);
            if (!_cfg.IsAllowed(who, orgMember))
                throw new LoginException($"허용되지 않은 계정입니다: {who.Account}" +
                                         (_cfg.GitHubOrg.Trim().Length > 0 && _cfg.Provider == LoginProviderKind.GitHub ? $" (Organization '{_cfg.GitHubOrg}' 멤버가 아님)" : ""));
            Result = who;
            try { LoginState.Save(who, _cfg); } catch { }
            AuditLog.Write("login", new[] { $"{LoginConfig.ProviderLabel(who.Provider)} {who.Account}" }, _test ? "테스트 로그인" : "앱 시작 로그인", "성공");
            DialogResult = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { ResetToStart(null); }
        catch (LoginException ex) { Fail(ex.Message); }
        catch (HttpRequestException ex) { Fail($"로그인 서버에 연결할 수 없습니다: {ex.Message}", offline: true); }
        catch (TaskCanceledException) { Fail("로그인 서버 응답이 없습니다 (시간 초과).", offline: true); }
        catch (Exception ex) { Fail($"로그인 실패: {ex.Message}"); }
    }

    private void Fail(string message, bool offline = false)
    {
        AuditLog.Write("login", new[] { LoginConfig.ProviderLabel(_cfg.Provider) }, _test ? "테스트 로그인" : "앱 시작 로그인", "실패: " + message);
        ResetToStart(message);
        // 인터넷이 안 될 때: 최근 로그인 기록이 허용 기간 안이면 들어갈 수 있게 (테스트 로그인에는 없음)
        if (offline && !_test && _cfg.OfflineDays > 0 && _state != null &&
            _state.Within(_cfg, TimeSpan.FromDays(_cfg.OfflineDays), DateTime.UtcNow))
        {
            OfflineButton.Content = $"오프라인으로 들어가기 (마지막 로그인 {_state.LastSuccessUtc.ToLocalTime():MM-dd HH:mm}, {_state.Account})";
            OfflineButton.Visibility = Visibility.Visible;
        }
    }

    private void ResetToStart(string? error)
    {
        CodePanel.Visibility = Visibility.Collapsed;
        LoginButton.Visibility = Visibility.Visible;
        LoginButton.IsEnabled = true;
        CancelButton.Content = _test ? "취소" : "종료";
        ShowError(error);
    }

    private void OnOffline(object sender, RoutedEventArgs e)
    {
        AuditLog.Write("login", new[] { $"{LoginConfig.ProviderLabel(_cfg.Provider)} {_state?.Account}" }, "앱 시작 로그인", "오프라인 허용으로 진입");
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (CodePanel.Visibility == Visibility.Visible) { _cts?.Cancel(); return; }   // 진행 중이면 취소만
        DialogResult = false;
    }

    private void OnCopyCode(object sender, RoutedEventArgs e)
    {
        try { if (_code != null) Clipboard.SetText(_code.UserCode); } catch { }
    }

    private void OnOpenBrowser(object sender, RoutedEventArgs e) => OpenBrowser();

    private void OpenBrowser()
    {
        if (_code == null) return;
        // 로그인 제공자 주소만 연다 (응답에 다른 주소가 와도 열지 않음)
        if (!Uri.TryCreate(_code.VerificationUri, UriKind.Absolute, out var u) || !IsTrustedLoginUrl(u))
        {
            ShowError($"브라우저에서 직접 여세요: {_code.VerificationUri}");
            return;
        }
        try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); }
        catch { ShowError($"브라우저를 열지 못했습니다. 직접 여세요: {u.AbsoluteUri}"); }
    }

    /// <summary>https 이고 로그인 제공자 도메인(또는 그 하위 도메인)인지</summary>
    public static bool IsTrustedLoginUrl(Uri u)
    {
        if (u.Scheme != Uri.UriSchemeHttps) return false;
        foreach (var d in new[] { "github.com", "google.com", "microsoft.com", "microsoftonline.com" })
            if (u.Host.Equals(d, StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not System.Windows.Controls.TextBox) DragMove();
    }
}
