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

    /// <param name="reloadLogin">
    /// 로그인 입력란도 저장된 값으로 다시 채울지. 페이지를 열 때만 true — Claude 가 다른 설정을 바꿔 다시 그릴 때
    /// 사용자가 입력하던 Client ID 등을 지우지 않게 한다
    /// </param>
    private void RefreshSettingsPage(bool reloadLogin = false)
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
        // 입력 중인 칸(키보드 포커스)은 덮어쓰지 않는다
        SetUnlessEditing(SftpEditorBox, s.SftpEditor);
        if (reloadLogin) LoadLoginSettings();
        (SettingsStore.Global.Language == L.English ? LangEnglish : LangKorean).IsChecked = true;
        SetUnlessEditing(DefSshPortBox, HostEntry.PreferredPort(Protocol.Ssh).ToString());
        SetUnlessEditing(DefRdpPortBox, HostEntry.PreferredPort(Protocol.Rdp).ToString());
        FontSizeText.Text = $"{s.FontSize}px";
        RefreshPreferences();

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
        // 목록에 없는 글꼴(다른 글꼴... · Claude 가 바꾼 값)이면 모두 해제 — 예전 선택이 그대로 남아 보이지 않게
        foreach (var rb in FontPanel.Children.OfType<Border>().Select(b => (RadioButton)b.Child))
        {
            var on = string.Equals((string)rb.Tag, s.FontFamily, StringComparison.OrdinalIgnoreCase);
            if (rb.IsChecked != on) rb.IsChecked = on;
        }
    }

    private static void SetUnlessEditing(TextBox box, string value)
    {
        if (!box.IsKeyboardFocusWithin) box.Text = value;
    }

    /// <summary>
    /// 포커스를 잃을 때 저장하는 입력란을 지금 저장 (창을 닫을 때 — 포커스가 그 칸에 있으면 LostFocus 가 오지 않을 수 있다).
    /// 값이 그대로면 아무것도 하지 않는다
    /// </summary>
    private void CommitSettingsInputs()
    {
        if (SettingsPage.Visibility != Visibility.Visible) return;
        try
        {
            OnSftpEditorChanged(SftpEditorBox, new RoutedEventArgs());
            OnCloudSyncIntervalChanged(CloudSyncIntervalBox, new RoutedEventArgs());
            OnDefaultPortChanged(DefSshPortBox, new RoutedEventArgs());
        }
        catch { }
    }

    /// <summary>설정 입력란에서 Enter = 바로 저장 (포커스를 옮기지 않아도)</summary>
    private void OnSettingsBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        e.Handled = true;
        if (box == DefSshPortBox || box == DefRdpPortBox) OnDefaultPortChanged(box, e);
        else if (box == SftpEditorBox) OnSftpEditorChanged(box, e);
        else if (box == CloudSyncIntervalBox) OnCloudSyncIntervalChanged(box, e);
        box.SelectAll();
    }

    private void OnThemeClick(object sender, MouseButtonEventArgs e) => SelectTheme(sender);

    /// <summary>테마 카드는 Tab 으로 옮겨 와 Enter · Space 로 고를 수 있다</summary>
    private void OnThemeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space)) return;
        e.Handled = true;
        SelectTheme(sender);
    }

    private void SelectTheme(object sender)
    {
        if ((sender as FrameworkElement)?.DataContext is not ThemeItem item || _themeItems == null) return;
        foreach (var it in _themeItems) it.IsSelected = it == item;
        SettingsStore.Update(s => s.TerminalTheme = item.Theme.Id);
    }

    /// <summary>Settings 위쪽 바로가기: Tag 에 적힌 이름의 제목이 맨 위에 오게 스크롤</summary>
    private void OnSettingsJump(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string name || FindName(name) is not FrameworkElement target) return;
        if (SettingsPage.Content is not Visual content) return;
        _settingsJumpPicked = SettingsJumpBar.Children.IndexOf((UIElement)sender);
        try
        {
            var y = target.TransformToAncestor(content).Transform(new Point(0, 0)).Y;
            SettingsPage.ScrollToVerticalOffset(Math.Max(0, y - 8));
        }
        catch (InvalidOperationException) { target.BringIntoView(); }
        // 이미 그 위치라 스크롤이 일어나지 않아도 강조는 맞춘다
        Dispatcher.BeginInvoke(UpdateSettingsJumpHighlight, DispatcherPriority.Loaded);
    }

    private void OnSettingsScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // 안쪽 입력란(TextBox 의 ScrollViewer)에서 올라온 이벤트는 무시
        if (ReferenceEquals(e.OriginalSource, SettingsPage)) UpdateSettingsJumpHighlight();
    }

    /// <summary>스크롤 위치에 맞춰 지금 보고 있는 섹션의 바로가기 버튼을 강조</summary>
    private void UpdateSettingsJumpHighlight()
    {
        if (SettingsJumpBar == null || SettingsPage.Visibility != Visibility.Visible || SettingsPage.Content is not Visual content) return;
        var buttons = SettingsJumpBar.Children.OfType<Button>().ToList();
        var tops = new List<double>();
        foreach (var b in buttons)
        {
            double y;
            try { y = b.Tag is string name && FindName(name) is FrameworkElement t ? t.TransformToAncestor(content).Transform(new Point(0, 0)).Y : double.NaN; }
            catch (InvalidOperationException) { y = double.NaN; }
            tops.Add(y);
        }
        var current = CurrentSettingsSection(tops, SettingsPage.VerticalOffset, SettingsPage.ScrollableHeight,
            SettingsPage.ViewportHeight, _settingsJumpPicked);
        if (current != _settingsJumpPicked) _settingsJumpPicked = -1;   // 고른 섹션이 화면 밖으로 나가면 평소 기준으로
        // 지금 섹션 칩 = 옅은 강조 채움 + 강조 글자 (Segment 선택 모양과 같은 계열). 나머지는 스타일 값으로 되돌린다
        var fill = (Brush)FindResource("AccentSubtle");
        var text = (Brush)FindResource("AccentText");
        for (var i = 0; i < buttons.Count; i++)
        {
            if (i == current)
            {
                buttons[i].Background = fill;
                buttons[i].BorderBrush = fill;
                buttons[i].Foreground = text;
            }
            else
            {
                buttons[i].ClearValue(BackgroundProperty);
                buttons[i].ClearValue(BorderBrushProperty);
                buttons[i].ClearValue(ForegroundProperty);
            }
        }
    }

    /// <summary>바로가기로 마지막에 고른 섹션 번호 — 페이지 끝 근처 섹션은 맨 위까지 올라오지 못하므로 화면에 보이는 동안 그 버튼을 강조</summary>
    private int _settingsJumpPicked = -1;

    /// <summary>
    /// 지금 보고 있는 섹션 번호 (UI 와 무관, 테스트용). tops = 각 섹션 제목의 세로 위치(못 구하면 NaN).
    /// 바로가기로 고른 섹션(picked)의 제목이 화면 안에 있으면 그 섹션. 아니면 제목이 화면 위쪽(offset + 24)을 지난 마지막 섹션,
    /// 맨 아래까지 내렸으면 마지막 섹션. 없으면 -1
    /// </summary>
    internal static int CurrentSettingsSection(IReadOnlyList<double> tops, double offset, double scrollable,
        double viewport = double.PositiveInfinity, int picked = -1)
    {
        if (tops.Count == 0) return -1;
        if (picked >= 0 && picked < tops.Count && !double.IsNaN(tops[picked]) &&
            tops[picked] >= offset - 1 && tops[picked] < offset + viewport)
            return picked;
        if (scrollable > 0 && offset >= scrollable - 1)
        {
            for (var i = tops.Count - 1; i >= 0; i--) if (!double.IsNaN(tops[i])) return i;
            return -1;
        }
        var current = -1;
        for (var i = 0; i < tops.Count; i++)
            if (!double.IsNaN(tops[i]) && tops[i] <= offset + 24) current = i;
        // 맨 위에서는 첫 섹션
        if (current < 0) for (var i = 0; i < tops.Count; i++) if (!double.IsNaN(tops[i])) return i;
        return current;
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
        var (oldSsh, oldRdp) = (HostEntry.PreferredPort(Protocol.Ssh), HostEntry.PreferredPort(Protocol.Rdp));
        if (oldSsh == ssh && oldRdp == rdp) return;

        // 클라우드 호스트 포트도 함께 바뀌는 경우 조용히 바꾸지 않고 몇 개인지 알리고 고르게 한다
        var moveHosts = true;
        var affected = CloudHostsOnDefaultPorts(oldSsh, oldRdp, ssh != oldSsh, rdp != oldRdp).Count();
        if (affected > 0)
        {
            var pick = Dialog.Choose(this,
                L.F("포트를 따로 지정하지 않은 클라우드 프로필의 호스트 {0}개가 이전 기본 포트를 쓰고 있습니다. 이 호스트들의 포트도 새 기본 포트로 바꿀까요?", affected),
                L.T("기본 포트"), L.T("함께 바꾸기"), L.T("기본 포트만 바꾸기"));
            if (pick < 0)
            {
                // 닫기(Esc) = 취소: 입력란을 지금 값으로 되돌린다
                DefSshPortBox.Text = oldSsh.ToString();
                DefRdpPortBox.Text = oldRdp.ToString();
                return;
            }
            moveHosts = pick == 0;
        }
        ApplyDefaultPorts(ssh, rdp, moveHosts);
    }

    /// <summary>포트를 따로 지정하지 않은 클라우드 프로필의 호스트 중 이전 기본 포트를 쓰는 것 (바꿀 프로토콜만)</summary>
    private IEnumerable<HostEntry> CloudHostsOnDefaultPorts(int oldSsh, int oldRdp, bool ssh, bool rdp) =>
        from p in _profiles
        from h in _hosts
        where h.CloudProfileId == p.Id &&
              ((ssh && p.SshPort == null && h.Protocol == Protocol.Ssh && h.Port == oldSsh) ||
               (rdp && p.RdpPort == null && h.Protocol == Protocol.Rdp && h.Port == oldRdp))
        select h;

    /// <summary>
    /// 기본 포트 변경. moveHosts 면 포트를 따로 지정하지 않은 클라우드 프로필의 호스트 중 이전 기본 포트를 쓰던 것도 함께 바꾼다.
    /// 바뀐 호스트 수를 돌려준다.
    /// </summary>
    private int ApplyDefaultPorts(int ssh, int rdp, bool moveHosts = true)
    {
        var (oldSsh, oldRdp) = (HostEntry.PreferredPort(Protocol.Ssh), HostEntry.PreferredPort(Protocol.Rdp));
        if (oldSsh == ssh && oldRdp == rdp) return 0;
        // 바꿀 호스트는 기본 포트를 바꾸기 전에 고른다 (사용자가 호스트별로 바꾼 포트는 유지)
        var targets = moveHosts ? CloudHostsOnDefaultPorts(oldSsh, oldRdp, ssh != oldSsh, rdp != oldRdp).ToList() : new List<HostEntry>();
        SettingsStore.Update(x => { x.DefaultSshPort = ssh; x.DefaultRdpPort = rdp; });
        foreach (var h in targets) h.Port = h.Protocol == Protocol.Ssh ? ssh : rdp;
        if (targets.Count > 0) { SaveHosts(); RefreshHosts(); }
        return targets.Count;
    }

    // ----- 언어 -----

    private void OnLanguageClick(object sender, RoutedEventArgs e)
    {
        var lang = LangEnglish.IsChecked == true ? L.English : L.Korean;
        if (lang == (SettingsStore.Global.Language ?? L.Korean)) return;
        SettingsStore.UpdateGlobal(s => s.Language = lang);
        // 새 언어로 안내 (아직 앱은 이전 언어로 떠 있음)
        var msg = lang == L.English
            ? "Language changed to English. Restart Nubis now to apply it?"
            : "언어를 한국어로 바꿨습니다. 지금 Nubis 를 다시 시작해 적용할까요?";
        if (Dialog.Show(this, msg, "Nubis", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) App.Restart();
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
            ? L.F("현재: {0} 로그인 사용 중", LoginConfig.ProviderLabel(c.Provider)) + (st != null && st.Account.Length > 0 ? L.F(" — 마지막 로그인 {0} ({1:yyyy-MM-dd HH:mm})", st.Account, st.LastSuccessUtc.ToLocalTime()) : "") +
              "\n" + L.T("설정을 잘못해 들어갈 수 없으면 %APPDATA%\\Nubis\\settings.json 의 \"Login\" → \"Provider\" 를 \"None\" 으로 바꾸세요.")
            : L.T("현재: 사용 안 함");
        if (CurrentProfileKey is { } key)
            LoginStatusText.Text += "\n" + (key == c.OwnerKey ? L.F("프로필: {0} (소유자)", key) : L.F("프로필: {0} — 로그인 설정은 소유자 계정만 바꿀 수 있습니다", key));
        // 소유자가 아니면 저장 버튼만이 아니라 입력란·방식 선택도 막는다 (고쳐도 저장되지 않는 칸을 열어 두지 않게)
        var editable = CanEditLogin;
        LoginSaveButton.IsEnabled = editable;
        // 흐리게 보이는 것은 입력란·세그먼트의 IsEnabled 모양(App.xaml)이 맡는다 — 패널 Opacity 까지 낮추면 겹쳐서 읽기 어렵다
        LoginFields.IsEnabled = editable;
        foreach (var rb in new[] { LoginNone, LoginGitHub, LoginGoogle, LoginMicrosoft }) rb.IsEnabled = editable;
        // 로그아웃은 시작 화면 로그인을 쓰는 동안만 — 소유자가 아니어도 자기 로그인 기록은 지울 수 있다
        LoginSignOutButton.Visibility = c.Enabled ? Visibility.Visible : Visibility.Collapsed;
        UpdateLoginUi();
    }

    /// <summary>
    /// 로그아웃: 로그인 기록(login.json)을 지우고 다시 시작한다. 다음 실행은 로그인 유지 시간과 상관없이 온라인 로그인이 필요하고,
    /// 기록이 없으므로 오프라인 진입도 막힌다
    /// </summary>
    private void OnLoginSignOut(object sender, RoutedEventArgs e)
    {
        if (!SettingsStore.Global.Login.Enabled) { LoadLoginSettings(); return; }
        if (Dialog.Show(this, L.T("로그아웃할까요?\n다음 실행 때 다시 온라인으로 로그인해야 합니다. 로그인 기록이 지워지므로 인터넷이 없을 때의 오프라인 진입도 다시 로그인하기 전까지는 할 수 없습니다.\n\n열린 탭을 모두 닫고 Nubis 를 다시 시작합니다."),
                L.T("로그아웃"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (LoginState.SignOut()) App.Restart();
        else Dialog.Show(this, L.T("로그인 기록(login.json)을 지우지 못해 로그아웃하지 못했습니다. 다른 프로그램이 파일을 쓰고 있지 않은지 확인한 뒤 다시 시도하세요."),
                L.T("로그아웃"), MessageBoxButton.OK, MessageBoxImage.Warning);
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
        LoginSaveButton.Content = p == LoginProviderKind.None ? L.T("저장 (로그인 끄기)") : L.T("테스트 로그인 후 저장");
        LoginAllowedLabel.Text = p == LoginProviderKind.GitHub
            ? L.T("허용 계정 (GitHub 로그인 이름, 쉼표로 여러 개 — 예: octocat)")
            : L.T("허용 계정 (이메일, 쉼표로 여러 개. @회사.com 은 그 도메인 전체)");
        LoginAllowedBox.Tag = p == LoginProviderKind.GitHub ? "octocat" : "me@example.com, @example.com";
        LoginHelpText.Text = p switch
        {
            LoginProviderKind.GitHub => L.T("등록: github.com/settings/developers → OAuth Apps → New OAuth App (Homepage·Callback URL 은 아무 주소, 예: http://localhost) → 만든 뒤 'Enable Device Flow' 체크 → Client ID 를 아래에. Client Secret 은 필요 없습니다."),
            LoginProviderKind.Google => L.T("등록: Google Cloud 콘솔 → API 및 서비스 → 사용자 인증 정보 → OAuth 클라이언트 ID 만들기 → 유형 'TV 및 입력 제한 기기' → Client ID 와 Secret 을 아래에 (이 유형의 Secret 은 기기에 넣어 쓰는 값입니다)."),
            LoginProviderKind.Microsoft => L.T("등록: Entra 관리 센터 → 앱 등록 → 새 등록 → 인증 → '공용 클라이언트 흐름 허용' 을 예로 → 애플리케이션(클라이언트) ID 를 아래에. Secret 은 필요 없습니다."),
            _ => "",
        };
    }

    private void OnLoginSave(object sender, RoutedEventArgs e)
    {
        if (!CanEditLogin) { LoginStatusText.Text = L.T("로그인 설정은 소유자 계정만 바꿀 수 있습니다."); return; }
        var current = SettingsStore.Global.Login;
        var draft = current.Clone();
        draft.Provider = SelectedLoginProvider;
        if (draft.Provider == LoginProviderKind.None)
        {
            if (current.Enabled && Dialog.Show(this, L.T("시작 화면 로그인을 끌까요?\n앞으로는 로그인 없이 Nubis 가 열립니다 (소유자 프로필의 데이터를 그대로 씁니다)."), L.T("시작 화면 로그인"),
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
        var who = LoginWindow.Test(this, draft, out var orgMember);
        if (who == null) { LoginStatusText.Text = L.T("테스트 로그인에 성공하지 못해 저장하지 않았습니다."); return; }
        // 소유자 = 로그인을 설정한 계정. 소유자가 로그인 방식을 바꾸면 다음 실행 때 프로필을 새 이름으로 옮긴다
        var firstSetup = draft.OwnerKey.Length == 0;
        if (firstSetup) draft.OwnerKey = who.ProfileKey;
        else if (draft.OwnerKey != who.ProfileKey)
        {
            // 다른 계정으로 테스트 로그인하면 소유자와 프로필 폴더가 그 계정으로 넘어간다 — 조용히 넘기지 않고 확인
            var targetHasData = Profiles.HasUserData(Profiles.Dir(who.ProfileKey));
            var ask = L.F("테스트 로그인한 계정({0})이 지금 소유자 프로필({1})과 다릅니다.\n저장하면 소유자가 이 계정으로 바뀌고, 다음 실행 때 지금 프로필의 호스트·Vault·설정을 이 계정의 프로필({2})로 옮깁니다.",
                          who.Account, draft.OwnerKey, who.ProfileKey) +
                      (targetHasData ? "\n\n" + L.T("⚠ 이 계정의 프로필에 이미 데이터가 있어 자동으로 합치지 않습니다 (지금 프로필 데이터는 예전 폴더에 남습니다).") : "") +
                      "\n\n" + L.T("소유자를 바꿀까요?");
            if (Dialog.Show(this, ask, L.T("시작 화면 로그인"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                LoginStatusText.Text = L.T("소유자를 바꾸지 않아 저장하지 않았습니다. 소유자 계정으로 테스트 로그인하세요.");
                return;
            }
            draft.PreviousOwnerKey = draft.OwnerKey;
            draft.OwnerKey = who.ProfileKey;
        }
        SettingsStore.UpdateGlobal(s => s.Login = draft);
        // 로그인 기록은 설정을 실제로 저장할 때만 남긴다 (테스트 로그인 자체는 기록하지 않음).
        // 저장한 설정·소유자가 된 계정 기준으로 새로 써서, 예전 설정(다른 앱·테넌트·계정)으로 남은 기록이 쓰이지 않게 한다
        try { LoginState.Save(who, draft, orgMember); } catch { LoginState.Clear(); }
        LoginSecretBox.Clear();
        AuditLog.Write("login-settings", new[] { "시작 화면 로그인" }, $"{LoginConfig.ProviderLabel(draft.Provider)} · 허용 {string.Join(", ", draft.AllowedAccounts)}" +
                       (draft.GitHubOrg.Length > 0 ? $" · Org {draft.GitHubOrg}" : ""), "테스트 로그인 성공·저장");
        LoadLoginSettings();
        LoginStatusText.Text = L.F("저장했습니다. {0} 로 로그인을 확인했습니다. 다음 실행부터 로그인 화면이 나옵니다.", who.Account) + "\n" + LoginStatusText.Text;
        var msg = L.T("저장했습니다. 다음 실행부터 로그인 화면이 나오고, 계정마다 호스트·Vault·설정이 따로 저장됩니다.") + "\n" +
                  (firstSetup || !AppPaths.UsingProfile ? L.F("지금 데이터는 {0} (소유자) 의 프로필로 옮겨집니다.", who.Account) + "\n" : "") +
                  "\n" + L.T("지금 다시 시작할까요?");
        if (Dialog.Show(this, msg, L.T("시작 화면 로그인"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) App.Restart();
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
        if (code == null) { Dialog.Show(this, L.T("VS Code(Code.exe)를 찾지 못했습니다. '찾기' 로 직접 지정하세요."), L.T("파일 편집기")); return; }
        SftpEditorBox.Text = code;
        OnSftpEditorChanged(sender, e);
    }

    private void OnSftpEditorBrowse(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = L.T("편집기 프로그램 선택"), Filter = L.T("프로그램 (*.exe;*.cmd;*.bat)|*.exe;*.cmd;*.bat|모든 파일|*.*") };
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
            // 지우면 다음 접속 때 그 서버의 키를 처음 보는 키로 받아들인다 — 그 사이 키가 바뀌었어도 경고가 없으므로 확인
            if (Dialog.Show(this, L.F("'{0}' 의 저장된 호스트키를 지울까요?\n다음 접속 때 서버가 보내는 키를 새로 신뢰합니다. 서버를 다시 설치해 키가 바뀐 경우에만 지우세요.", kv.Key),
                    L.T("호스트키 삭제"), MessageBoxButton.YesNo, MessageBoxImage.Warning, kv.Value) != MessageBoxResult.Yes) return;
            KnownHostStore.Remove(kv.Key);
            RefreshKnownHosts();
        }
    }
}
