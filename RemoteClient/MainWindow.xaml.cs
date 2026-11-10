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

public partial class MainWindow : Window
{
    private readonly List<HostEntry> _hosts;
    private readonly List<CloudProfile> _profiles;
    private readonly List<CloudSection> _sections;
    private readonly ObservableCollection<SessionTab> _tabs = new();
    private readonly HashSet<string> _collapsedLocal = new();
    private readonly HashSet<string> _expandedCloud = new();
    private HostEntry? _editing;

    private static readonly Regex QuickRegex = new(
        @"^(?:(?<proto>ssh|rdp)\s+)?(?:(?<user>[^@\s]+)@)?(?<host>[A-Za-z0-9._\-]+)(?::(?<port>\d{1,5}))?$",
        RegexOptions.IgnoreCase);

    public MainWindow()
    {
        InitializeComponent();
        _hosts = HostStore.Load();
        _profiles = CloudStore.Load();
        _sections = CloudProviders.All.Select(pr => new CloudSection
        {
            Provider = pr.Kind,
            Profiles = new ObservableCollection<CloudProfile>(
                _profiles.Where(p => p.Provider == pr.Kind).OrderBy(p => p.GroupName, StringComparer.CurrentCultureIgnoreCase)),
        }).ToList();
        CloudSections.ItemsSource = _sections;
        UpdateProfileCounts();
        TabsList.ItemsSource = _tabs;
        FooterText.Text = $"v{typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}";
        FooterText.ToolTip = $"데이터 폴더: {AppPaths.DataDir}";

        StateChanged += (_, _) => ApplyWindowState();
        Closing += OnWindowClosing;
        PreviewKeyDown += OnWindowKeyDown;

        RefreshHosts();
        SelectHome();
        Loaded += (_, _) =>
        {
            // 설정 파일을 읽지 못한 경우 (원본은 .corrupt 사본으로 보관됨)
            if (SafeFile.LoadWarnings.Count > 0)
                Dialog.Show(this, string.Join("\n\n", SafeFile.LoadWarnings), "설정 파일을 읽지 못했습니다",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            ConnectFromArgs();
        };
        XtermView.Hotkey += OnTerminalHotkey;
    }

    /// <summary>RemoteClient.exe --connect "호스트이름" [--connect ...] : 시작하면서 저장된 호스트로 바로 연결</summary>
    private void ConnectFromArgs()
    {
        var args = Environment.GetCommandLineArgs();
        // --claude : 시작하면서 Claude 패널 열기
        if (args.Any(a => a.Equals("--claude", StringComparison.OrdinalIgnoreCase))) ToggleClaude(true);
        for (var i = 1; i < args.Length - 1; i++)
        {
            if (!args[i].Equals("--connect", StringComparison.OrdinalIgnoreCase)) continue;
            var key = args[i + 1];
            var h = _hosts.FirstOrDefault(x => x.Id.ToString().Equals(key, StringComparison.OrdinalIgnoreCase))
                    ?? _hosts.FirstOrDefault(x => x.DisplayName.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (h != null) Connect(h);
            else Dialog.Show(this, $"'{key}' 호스트를 찾을 수 없습니다.", "Nubis");
        }
    }

    // =================== 사이드바 ===================

    private void OnNavChanged(object sender, RoutedEventArgs e)
    {
        if (HostsPage == null || SettingsPage == null || CloudPage == null || VaultPage == null) return;   // InitializeComponent 중
        var known = NavKnown.IsChecked == true;
        var settings = NavSettings.IsChecked == true;
        var cloud = NavCloud.IsChecked == true;
        var vault = NavVault.IsChecked == true;
        HostsPage.Visibility = !known && !settings && !cloud && !vault ? Visibility.Visible : Visibility.Collapsed;
        KnownPage.Visibility = known ? Visibility.Visible : Visibility.Collapsed;
        CloudPage.Visibility = cloud ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        VaultPage.Visibility = vault ? Visibility.Visible : Visibility.Collapsed;
        if (known) RefreshKnownHosts();
        if (settings) RefreshSettingsPage();
        if (vault) RefreshVault();
    }

    // =================== 창 ===================

    private void ApplyWindowState()
    {
        if (WindowState == WindowState.Maximized)
        {
            // WindowChrome 최대화 시 화면 밖으로 넘치는 테두리만큼 안쪽 여백
            var t = SystemParameters.WindowResizeBorderThickness;
            RootGrid.Margin = new Thickness(t.Left + 4, t.Top + 4, t.Right + 4, t.Bottom + 4);
            MaxButton.Content = "";
        }
        else
        {
            RootGrid.Margin = new Thickness(0);
            MaxButton.Content = "";
        }
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseWindow(object sender, RoutedEventArgs e) => Close();

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        var live = _tabs.Count(t => t.Status != SessionStatus.Disconnected);
        if (live > 0 && Dialog.Show(this, $"연결 중인 세션 {live}개가 종료됩니다. 닫을까요?", "Nubis",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        foreach (var t in _tabs.ToList())
        {
            try { t.Session.CloseSession(); } catch { }
        }
        ShutdownClaude();
    }
}
