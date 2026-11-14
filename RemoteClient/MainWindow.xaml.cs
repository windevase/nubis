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
        HostEntry.ProfileResolver = id => id == null ? null : _profiles.FirstOrDefault(p => p.Id == id);
        _sections = CloudProviders.All.Select(pr => new CloudSection
        {
            Provider = pr.Kind,
            Profiles = new ObservableCollection<CloudProfile>(
                _profiles.Where(p => p.Provider == pr.Kind).OrderBy(p => p.GroupName, StringComparer.CurrentCultureIgnoreCase)),
        }).ToList();
        CloudSections.ItemsSource = _sections;
        UpdateProfileCounts();
        TabsList.ItemsSource = _tabs;
        // 지난번 화면 상태: 그룹 접힘, Claude 패널 폭, 창 위치·크기
        var st = SettingsStore.Current;
        foreach (var k in st.CollapsedGroups) _collapsedLocal.Add(k);
        foreach (var k in st.ExpandedCloudGroups) _expandedCloud.Add(k);
        if (st.ClaudePanelWidth is >= 320 and <= 3000) _claudeWidth = st.ClaudePanelWidth;
        RestoreWindowBounds(st);
        FooterText.Text = $"v{typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}";
        FooterText.ToolTip = L.F("데이터 폴더: {0}", AppPaths.DataDir);

        StateChanged += (_, _) => ApplyWindowState();
        Closing += OnWindowClosing;
        PreviewKeyDown += OnWindowKeyDown;

        RefreshHosts();
        SelectHome();
        // known_hosts.json 을 미리 읽어 본다 — 손상됐으면 첫 접속 때가 아니라 시작할 때 아래 경고에 함께 보이게
        try { KnownHostStore.Load(); } catch { }
        Loaded += (_, _) =>
        {
            // 설정 파일을 읽지 못한 경우 (원본은 .corrupt 사본으로 보관됨)
            if (SafeFile.LoadWarnings.Count > 0)
            {
                string[] warnings;
                lock (SafeFile.LoadWarnings) warnings = SafeFile.LoadWarnings.ToArray();
                Dialog.Show(this, string.Join("\n\n", warnings), L.T("설정 파일을 읽지 못했습니다"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            ConnectFromArgs(Environment.GetCommandLineArgs());
            ShowOnboardingIfFirstRun();
            StartCloudAutoSync(startup: true);
        };
        XtermView.Hotkey += OnTerminalHotkey;
    }

    /// <summary>RemoteClient.exe --connect "호스트이름" [--connect ...] : 시작하면서 저장된 호스트로 바로 연결</summary>
    /// <param name="args">args[0] 은 실행 파일 경로 (Environment.GetCommandLineArgs 형식)</param>
    private void ConnectFromArgs(string[] args)
    {
        // --claude : 시작하면서 Claude 패널 열기
        if (args.Any(a => a.Equals("--claude", StringComparison.OrdinalIgnoreCase))) ToggleClaude(true);
        for (var i = 1; i < args.Length - 1; i++)
        {
            if (!args[i].Equals("--connect", StringComparison.OrdinalIgnoreCase)) continue;
            var key = args[i + 1];
            var h = _hosts.FirstOrDefault(x => x.Id.ToString().Equals(key, StringComparison.OrdinalIgnoreCase))
                    ?? _hosts.FirstOrDefault(x => x.DisplayName.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (h != null) Connect(h);
            else Dialog.Show(this, L.F("'{0}' 호스트를 찾을 수 없습니다.", key), "Nubis");
        }
    }

    /// <summary>이미 실행 중일 때 다시 실행한 Nubis 가 넘긴 명령줄: 창을 앞으로 가져오고 --connect · --claude 처리</summary>
    internal void HandleForwardedArgs(string[] args)
    {
        // 최소화돼 있으면 이전 상태(최대화 포함)로 되돌린다
        if (WindowState == WindowState.Minimized)
            ShowWindow(new System.Windows.Interop.WindowInteropHelper(this).Handle, 9 /* SW_RESTORE */);
        Activate();
        // 다른 창 뒤에 있으면 앞으로 (Activate 만으로는 작업 표시줄만 깜빡일 수 있다)
        Topmost = true;
        Topmost = false;
        if (args.Length > 1) ConnectFromArgs(args);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

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
        if (live > 0 && Dialog.Show(this, L.F("연결 중인 세션 {0}개가 종료됩니다. 닫을까요?", live), "Nubis",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        SaveWindowState();
        foreach (var t in _tabs.ToList())
        {
            try { t.Session.CloseSession(); } catch { }
        }
        ShutdownClaude();
    }

    /// <summary>저장된 창 위치가 지금 화면 안에 있으면 그대로 연다 (모니터를 뺐으면 기본 위치)</summary>
    private void RestoreWindowBounds(AppSettings st)
    {
        if (st.WindowBounds is not { Length: 4 } b || b[2] < MinWidth || b[3] < MinHeight) return;
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var r = new Rect(b[0], b[1], b[2], b[3]);
        // 제목 표시줄이 화면 안에 있어야 끌어 옮길 수 있다
        if (!screen.IntersectsWith(new Rect(r.X + 40, r.Y, Math.Max(1, r.Width - 80), 40))) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        (Left, Top, Width, Height) = (r.X, r.Y, r.Width, r.Height);
        if (st.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private void SaveWindowState()
    {
        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        var claude = ClaudeColumn.ActualWidth > 0 ? ClaudeColumn.ActualWidth : _claudeWidth;
        SettingsStore.UpdateState(s =>
        {
            if (!r.IsEmpty && r.Width > 0) s.WindowBounds = new[] { r.X, r.Y, r.Width, r.Height };
            s.WindowMaximized = WindowState == WindowState.Maximized;
            s.ClaudePanelWidth = Math.Round(claude);
        });
    }
}
