using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Nubis;

public partial class MainWindow
{
    // =================== 설정 ===================

    private static readonly string[] FontCandidates =
        { "Cascadia Mono", "Cascadia Code", "D2Coding", "JetBrains Mono", "Fira Code", "NanumGothicCoding", "Consolas", "Lucida Console" };

    private List<ThemeItem>? _themeItems;

    private void RefreshSettingsPage()
    {
        var s = SettingsStore.Current;

        _themeItems ??= TerminalThemes.All.Select(t => new ThemeItem { Theme = t }).ToList();
        foreach (var it in _themeItems) it.IsSelected = it.Theme.Id == TerminalThemes.Get(s.TerminalTheme).Id;
        ThemeList.ItemsSource ??= _themeItems;

        AutoContrastBox.IsChecked = s.AutoContrast;
        HlIpBox.IsChecked = s.HighlightIp;
        HlNumBox.IsChecked = s.HighlightNumbers;
        HlKwBox.IsChecked = s.HighlightKeywords;
        GpuBox.IsChecked = s.TerminalGpu;
        RdpCertBox.IsChecked = s.RdpCertWarning;
        SshKeyPromptBox.IsChecked = s.SshHostKeyPrompt;
        AutoReadOnlyBox.IsChecked = s.AutoApproveReadOnly;
        SftpAutoOpenBox.IsChecked = s.SftpAutoOpen;
        SftpFollowBox.IsChecked = s.SftpFollowTerminal;
        SftpEditorBox.Text = s.SftpEditor;
        LoadLoginSettings();
        DefSshPortBox.Text = HostEntry.PreferredPort(Protocol.Ssh).ToString();
        DefRdpPortBox.Text = HostEntry.PreferredPort(Protocol.Rdp).ToString();
        FontSizeText.Text = $"{s.FontSize}px";

        if (FontPanel.Children.Count == 0)
        {
            var installed = Fonts.SystemFontFamilies.Select(f => f.Source).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var name in FontCandidates.Where(installed.Contains))
            {
                var rb = new RadioButton
                {
                    Content = name,
                    Tag = name,
                    GroupName = "termfont",
                    Style = (Style)FindResource("Segment"),
                    FontFamily = new FontFamily(name),
                    FontWeight = FontWeights.Normal,
                    MinWidth = 150,
                    Padding = new Thickness(14, 0, 14, 0),
                };
                rb.Checked += (_, _) => SettingsStore.Update(x => x.FontFamily = name);
                FontPanel.Children.Add(new Border
                {
                    Background = (Brush)FindResource("InputBg"),
                    BorderBrush = (Brush)FindResource("Line"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(3),
                    Margin = new Thickness(0, 0, 8, 8),
                    Child = rb,
                });
            }
        }
        foreach (var rb in FontPanel.Children.OfType<Border>().Select(b => (RadioButton)b.Child))
            if ((string)rb.Tag == s.FontFamily && rb.IsChecked != true) rb.IsChecked = true;
    }

    private void OnThemeClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ThemeItem item || _themeItems == null) return;
        foreach (var it in _themeItems) it.IsSelected = it == item;
        SettingsStore.Update(s => s.TerminalTheme = item.Theme.Id);
    }

    private void OnAutoContrastClick(object sender, RoutedEventArgs e) =>
        SettingsStore.Update(s => s.AutoContrast = AutoContrastBox.IsChecked == true);

    private void OnHighlightClick(object sender, RoutedEventArgs e) =>
        SettingsStore.Update(s =>
        {
            s.HighlightIp = HlIpBox.IsChecked == true;
            s.HighlightNumbers = HlNumBox.IsChecked == true;
            s.HighlightKeywords = HlKwBox.IsChecked == true;
        });

    private void OnSshKeyPromptClick(object sender, RoutedEventArgs e) =>
        SettingsStore.Update(s => s.SshHostKeyPrompt = SshKeyPromptBox.IsChecked == true);

    /// <summary>기본 포트 입력란: 1~65535 숫자만 저장하고, 비우거나 잘못 넣으면 표준 포트로 되돌린다</summary>
    private void OnDefaultPortChanged(object sender, RoutedEventArgs e)
    {
        var ssh = int.TryParse(DefSshPortBox.Text.Trim(), out var a) && a is >= 1 and <= 65535 ? a : HostEntry.DefaultPort(Protocol.Ssh);
        var rdp = int.TryParse(DefRdpPortBox.Text.Trim(), out var b) && b is >= 1 and <= 65535 ? b : HostEntry.DefaultPort(Protocol.Rdp);
        DefSshPortBox.Text = ssh.ToString();
        DefRdpPortBox.Text = rdp.ToString();
        ApplyDefaultPorts(ssh, rdp);
    }

    /// <summary>
    /// 기본 포트 변경. 포트를 따로 지정하지 않은 클라우드 프로필의 호스트 중 이전 기본 포트를 쓰던 것도 함께 바꾼다.
    /// 바뀐 호스트 수를 돌려준다.
    /// </summary>
    private int ApplyDefaultPorts(int ssh, int rdp)
    {
        var (oldSsh, oldRdp) = (HostEntry.PreferredPort(Protocol.Ssh), HostEntry.PreferredPort(Protocol.Rdp));
        if (oldSsh == ssh && oldRdp == rdp) return 0;
        SettingsStore.Update(x => { x.DefaultSshPort = ssh; x.DefaultRdpPort = rdp; });

        // 포트를 따로 지정하지 않은 클라우드 프로필의 호스트 중, 이전 기본 포트를 쓰던 것도 함께 변경
        // (사용자가 호스트별로 바꾼 포트는 유지)
        var changed = 0;
        foreach (var p in _profiles)
            foreach (var h in _hosts.Where(h => h.CloudProfileId == p.Id))
            {
                if (p.SshPort == null && h.Protocol == Protocol.Ssh && h.Port == oldSsh) { h.Port = ssh; changed++; }
                if (p.RdpPort == null && h.Protocol == Protocol.Rdp && h.Port == oldRdp) { h.Port = rdp; changed++; }
            }
        if (changed > 0) { SaveHosts(); RefreshHosts(); }
        return changed;
    }

    // ----- 시작 화면 로그인 -----

    /// <summary>지금 쓰는 프로필 이름 (로그인을 쓰지 않아 공통 폴더면 null)</summary>
    private static string? CurrentProfileKey => AppPaths.UsingProfile ? Path.GetFileName(AppPaths.DataDir) : null;

    /// <summary>로그인 설정은 소유자만 바꿀 수 있다 (다른 계정이 로그인을 꺼서 소유자 데이터를 보는 우회 방지)</summary>
    private static bool CanEditLogin => CurrentProfileKey == null || CurrentProfileKey == SettingsStore.Global.Login.OwnerKey;

    private void LoadLoginSettings()
    {
        var c = SettingsStore.Global.Login;
        (c.Provider switch
        {
            LoginProviderKind.GitHub => LoginGitHub,
            LoginProviderKind.Google => LoginGoogle,
            LoginProviderKind.Microsoft => LoginMicrosoft,
            _ => LoginNone,
        }).IsChecked = true;
        LoginClientIdBox.Text = c.ClientId;
        LoginSecretBox.Clear();
        LoginTenantBox.Text = c.Tenant;
        LoginAllowedBox.Text = string.Join(", ", c.AllowedAccounts);
        LoginOrgBox.Text = c.GitHubOrg;
        LoginRememberBox.Text = c.RememberHours.ToString();
        LoginOfflineBox.Text = c.OfflineDays.ToString();
        var st = LoginState.Load();
        LoginStatusText.Text = c.Enabled
            ? $"현재: {LoginConfig.ProviderLabel(c.Provider)} 로그인 사용 중" + (st != null && st.Account.Length > 0 ? $" — 마지막 로그인 {st.Account} ({st.LastSuccessUtc.ToLocalTime():yyyy-MM-dd HH:mm})" : "") +
              "\n설정을 잘못해 들어갈 수 없으면 %APPDATA%\\Nubis\\settings.json 의 \"Login\" → \"Provider\" 를 \"None\" 으로 바꾸세요."
            : "현재: 사용 안 함";
        if (CurrentProfileKey is { } key)
            LoginStatusText.Text += $"\n프로필: {key}" + (key == c.OwnerKey ? " (소유자)" : " — 로그인 설정은 소유자 계정만 바꿀 수 있습니다");
        LoginSaveButton.IsEnabled = CanEditLogin;
        UpdateLoginUi();
    }

    private LoginProviderKind SelectedLoginProvider =>
        LoginGitHub.IsChecked == true ? LoginProviderKind.GitHub
        : LoginGoogle.IsChecked == true ? LoginProviderKind.Google
        : LoginMicrosoft.IsChecked == true ? LoginProviderKind.Microsoft
        : LoginProviderKind.None;

    private void OnLoginProviderChanged(object sender, RoutedEventArgs e) => UpdateLoginUi();

    private void UpdateLoginUi()
    {
        if (LoginFields == null || LoginOrgPanel == null) return;   // InitializeComponent 중
        var p = SelectedLoginProvider;
        LoginFields.Visibility = p == LoginProviderKind.None ? Visibility.Collapsed : Visibility.Visible;
        LoginSecretPanel.Visibility = p == LoginProviderKind.Google ? Visibility.Visible : Visibility.Collapsed;
        LoginTenantPanel.Visibility = p == LoginProviderKind.Microsoft ? Visibility.Visible : Visibility.Collapsed;
        LoginOrgPanel.Visibility = p == LoginProviderKind.GitHub ? Visibility.Visible : Visibility.Collapsed;
        LoginSaveButton.Content = p == LoginProviderKind.None ? "저장 (로그인 끄기)" : "테스트 로그인 후 저장";
        LoginAllowedLabel.Text = p == LoginProviderKind.GitHub
            ? "허용 계정 (GitHub 로그인 이름, 쉼표로 여러 개 — 예: octocat)"
            : "허용 계정 (이메일, 쉼표로 여러 개. @회사.com 은 그 도메인 전체)";
        LoginAllowedBox.Tag = p == LoginProviderKind.GitHub ? "octocat" : "me@example.com, @example.com";
        LoginHelpText.Text = p switch
        {
            LoginProviderKind.GitHub => "등록: github.com/settings/developers → OAuth Apps → New OAuth App (Homepage·Callback URL 은 아무 주소, 예: http://localhost) → 만든 뒤 'Enable Device Flow' 체크 → Client ID 를 아래에. Client Secret 은 필요 없습니다.",
            LoginProviderKind.Google => "등록: Google Cloud 콘솔 → API 및 서비스 → 사용자 인증 정보 → OAuth 클라이언트 ID 만들기 → 유형 'TV 및 입력 제한 기기' → Client ID 와 Secret 을 아래에 (이 유형의 Secret 은 기기에 넣어 쓰는 값입니다).",
            LoginProviderKind.Microsoft => "등록: Entra 관리 센터 → 앱 등록 → 새 등록 → 인증 → '공용 클라이언트 흐름 허용' 을 예로 → 애플리케이션(클라이언트) ID 를 아래에. Secret 은 필요 없습니다.",
            _ => "",
        };
    }

    private void OnLoginSave(object sender, RoutedEventArgs e)
    {
        if (!CanEditLogin) { LoginStatusText.Text = "로그인 설정은 소유자 계정만 바꿀 수 있습니다."; return; }
        var current = SettingsStore.Global.Login;
        var draft = current.Clone();
        draft.Provider = SelectedLoginProvider;
        if (draft.Provider == LoginProviderKind.None)
        {
            if (current.Enabled && Dialog.Show(this, "시작 화면 로그인을 끌까요?\n앞으로는 로그인 없이 Nubis 가 열립니다 (소유자 프로필의 데이터를 그대로 씁니다).", "시작 화면 로그인",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) { LoadLoginSettings(); return; }
            SettingsStore.UpdateGlobal(s => s.Login = draft);
            LoginState.Clear();
            AuditLog.Write("login-settings", new[] { "시작 화면 로그인" }, "사용 안 함", "변경");
            LoadLoginSettings();
            return;
        }

        draft.ClientId = LoginClientIdBox.Text.Trim();
        if (LoginSecretBox.Password.Length > 0) draft.ClientSecret = LoginSecretBox.Password;
        draft.Tenant = LoginTenantBox.Text.Trim().Length > 0 ? LoginTenantBox.Text.Trim() : "organizations";
        draft.AllowedAccounts = LoginConfig.ParseList(LoginAllowedBox.Text);
        draft.GitHubOrg = LoginOrgBox.Text.Trim();
        draft.RememberHours = int.TryParse(LoginRememberBox.Text.Trim(), out var h) ? h : -1;
        draft.OfflineDays = int.TryParse(LoginOfflineBox.Text.Trim(), out var d) ? d : -1;
        if (draft.Validate() is { } problem) { LoginStatusText.Text = "⚠ " + problem; return; }

        // 실제로 로그인해 허용되는지 확인한 뒤에만 저장 (잘못된 설정으로 앱에 못 들어오는 일 방지)
        var who = LoginWindow.Test(this, draft);
        if (who == null) { LoginStatusText.Text = "테스트 로그인에 성공하지 못해 저장하지 않았습니다."; return; }
        // 소유자 = 로그인을 설정한 계정. 소유자가 로그인 방식을 바꾸면 다음 실행 때 프로필을 새 이름으로 옮긴다
        var firstSetup = draft.OwnerKey.Length == 0;
        if (firstSetup) draft.OwnerKey = who.ProfileKey;
        else if (draft.OwnerKey != who.ProfileKey) { draft.PreviousOwnerKey = draft.OwnerKey; draft.OwnerKey = who.ProfileKey; }
        SettingsStore.UpdateGlobal(s => s.Login = draft);
        LoginSecretBox.Clear();
        AuditLog.Write("login-settings", new[] { "시작 화면 로그인" }, $"{LoginConfig.ProviderLabel(draft.Provider)} · 허용 {string.Join(", ", draft.AllowedAccounts)}" +
                       (draft.GitHubOrg.Length > 0 ? $" · Org {draft.GitHubOrg}" : ""), "테스트 로그인 성공·저장");
        LoadLoginSettings();
        LoginStatusText.Text = $"저장했습니다. {who.Account} 로 로그인을 확인했습니다. 다음 실행부터 로그인 화면이 나옵니다.\n" + LoginStatusText.Text;
        var msg = $"저장했습니다. 다음 실행부터 로그인 화면이 나오고, 계정마다 호스트·Vault·설정이 따로 저장됩니다.\n" +
                  (firstSetup || !AppPaths.UsingProfile ? $"지금 데이터는 {who.Account} (소유자) 의 프로필로 옮겨집니다.\n" : "") +
                  "\n지금 다시 시작할까요?";
        if (Dialog.Show(this, msg, "시작 화면 로그인", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) App.Restart();
    }

    private void OnSftpOptionClick(object sender, RoutedEventArgs e) =>
        SettingsStore.Update(s =>
        {
            s.SftpAutoOpen = SftpAutoOpenBox.IsChecked == true;
            s.SftpFollowTerminal = SftpFollowBox.IsChecked == true;
        });

    private void OnSftpEditorChanged(object sender, RoutedEventArgs e)
    {
        var v = SftpEditorBox.Text.Trim().Trim('"');
        if (v != SettingsStore.Current.SftpEditor) SettingsStore.Update(s => s.SftpEditor = v);
    }

    private void OnSftpEditorVsCode(object sender, RoutedEventArgs e)
    {
        var code = SftpPanel.FindVsCode();
        if (code == null) { Dialog.Show(this, "VS Code(Code.exe)를 찾지 못했습니다. '찾기' 로 직접 지정하세요.", "파일 편집기"); return; }
        SftpEditorBox.Text = code;
        OnSftpEditorChanged(sender, e);
    }

    private void OnSftpEditorBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "편집기 프로그램 선택", Filter = "프로그램 (*.exe;*.cmd;*.bat)|*.exe;*.cmd;*.bat|모든 파일|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        SftpEditorBox.Text = dlg.FileName;
        OnSftpEditorChanged(sender, e);
    }

    private void OnAutoReadOnlyClick(object sender, RoutedEventArgs e) =>
        SettingsStore.Update(s => s.AutoApproveReadOnly = AutoReadOnlyBox.IsChecked == true);

    private void OnRdpCertClick(object sender, RoutedEventArgs e) =>
        SettingsStore.Update(s => s.RdpCertWarning = RdpCertBox.IsChecked == true);

    private void OnGpuClick(object sender, RoutedEventArgs e) =>
        SettingsStore.Update(s => s.TerminalGpu = GpuBox.IsChecked == true);

    private void OnFontSmaller(object sender, RoutedEventArgs e) => ChangeFontSize(-1);
    private void OnFontBigger(object sender, RoutedEventArgs e) => ChangeFontSize(+1);

    private void ChangeFontSize(int delta)
    {
        SettingsStore.Update(s => s.FontSize = Math.Clamp(s.FontSize + delta, 9, 28));
        FontSizeText.Text = $"{SettingsStore.Current.FontSize}px";
    }

    private void RefreshKnownHosts() =>
        KnownList.ItemsSource = KnownHostStore.Load().OrderBy(kv => kv.Key).ToList();

    private void OnKnownDelete(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is KeyValuePair<string, string> kv)
        {
            KnownHostStore.Remove(kv.Key);
            RefreshKnownHosts();
        }
    }
}
