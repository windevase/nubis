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
        // SSH 접속 때 배너로 판별한 서버 OS 를 저장된 호스트에 기억 (읽기 전용 자동 승인 판단에 씀)
        SshServerOs.Detected += (id, kind) => Dispatcher.BeginInvoke(() =>
        {
            var h = _hosts.FirstOrDefault(x => x.Id == id);
            if (h == null || h.SshBannerOs == kind) return;
            h.SshBannerOs = kind;   // Windows 로고는 HostEntry 가 화면에 알린다
            SaveHosts();
        });
        // 접속해 알아낸 Linux 배포판 (OS 로고). 판별하는 사이에 주소를 바꿨으면 버린다 — 다른 서버의 값일 수 있음
        SshServerOs.DistroDetected += (id, address, distro, fromOsRelease) => Dispatcher.BeginInvoke(() =>
        {
            var h = _hosts.FirstOrDefault(x => x.Id == id);
            if (!ServerOsKind.ShouldStore(h?.OsDistro, distro, fromOsRelease) || h == null ||
                !h.AddressCandidates().Contains(address, StringComparer.OrdinalIgnoreCase)) return;
            h.OsDistro = distro;
            SaveHosts();
        });
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

        // Windows 11: Mica 배경 + 기본 캡션 버튼 (실패하면 단색 배경 · 직접 만든 버튼 그대로)
        SourceInitialized += (_, _) => { if (WindowBackdrop.Apply(this, nativeCaptionButtons: true)) UseNativeCaptionButtons(); };
        Toast.Attach(this, RootGrid);

        StateChanged += (_, _) => ApplyWindowState();
        // 저장된 최대화 상태로 시작하면 StateChanged 가 오지 않을 수 있다 — 여백·최대화 버튼 모양을 지금 맞춘다
        ApplyWindowState();
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
                Dialog.Show(this, string.Join("\n\n", warnings), L.T("데이터 파일을 읽지 못했습니다"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            ConnectFromArgs(Environment.GetCommandLineArgs());
            ShowOnboardingIfFirstRun();
            StartCloudAutoSync(startup: true);
            StartUpdateCheck();
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
        Dialog.RestoreIfMinimized(this);
        Activate();
        // 다른 창 뒤에 있으면 앞으로 (Activate 만으로는 작업 표시줄만 깜빡일 수 있다)
        Topmost = true;
        Topmost = false;
        if (args.Length > 1) ConnectFromArgs(args);
    }

    // =================== 사이드바 ===================

    private void OnNavChanged(object sender, RoutedEventArgs e)
    {
        if (HostsPage == null || SettingsPage == null || SettingsView == null || CloudPage == null || VaultPage == null || K8sPage == null) return;   // InitializeComponent 중
        var known = NavKnown.IsChecked == true;
        var settings = NavSettings.IsChecked == true;
        var cloud = NavCloud.IsChecked == true;
        var vault = NavVault.IsChecked == true;
        var k8s = NavK8s.IsChecked == true;
        HostsPage.Visibility = !known && !settings && !cloud && !vault && !k8s ? Visibility.Visible : Visibility.Collapsed;
        K8sPage.Visibility = k8s ? Visibility.Visible : Visibility.Collapsed;
        if (k8s) RefreshK8s();
        KnownPage.Visibility = known ? Visibility.Visible : Visibility.Collapsed;
        CloudPage.Visibility = cloud ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = SettingsPage.Visibility;   // 고정 머리글(제목 · 섹션 바로가기)과 스크롤 영역을 함께
        VaultPage.Visibility = vault ? Visibility.Visible : Visibility.Collapsed;
        if (known) RefreshKnownHosts();
        if (settings) RefreshSettingsPage(reloadLogin: true);
        if (vault) RefreshVault();
    }

    // =================== 창 ===================

    /// <summary>최소화하기 직전이 최대화였는지 — 최소화한 채 닫아도 다음 실행 때 최대화로 연다</summary>
    private bool _maximizedBeforeMinimize;

    private void ApplyWindowState()
    {
        if (WindowState != WindowState.Minimized) _maximizedBeforeMinimize = WindowState == WindowState.Maximized;
        // 아이콘만 있는 버튼 — 툴팁과 화면 읽기 프로그램용 이름
        var tip = WindowState == WindowState.Maximized ? L.T("이전 크기로") : L.T("최대화");
        MaxButton.ToolTip = tip;
        System.Windows.Automation.AutomationProperties.SetName(MaxButton, tip);
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

    /// <summary>Mica 를 켜면 유리 프레임 위에 DWM 이 기본 캡션 버튼을 그린다 — 직접 만든 버튼은 숨기고 그 폭만큼 오른쪽 자리를 비운다</summary>
    private FrameworkElement? _captionSpacer;

    private void UseNativeCaptionButtons()
    {
        if (MaxButton.Parent is not Panel bar) return;
        foreach (var b in bar.Children.OfType<Button>().Where(b => b.Style == (Style)FindResource("TitleButton")).ToList())
            b.Visibility = Visibility.Collapsed;
        _captionSpacer = new FrameworkElement { Width = 138 };   // 실제 폭은 창이 뜬 뒤 DWM 에 물어 맞춘다 (100% 기준 기본값)
        bar.Children.Add(_captionSpacer);
        void Fit() { if (_captionSpacer != null && WindowBackdrop.CaptionButtonsWidth(this) is { } w) _captionSpacer.Width = Math.Ceiling(w) + 4; }
        Loaded += (_, _) => Fit();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(Fit, DispatcherPriority.Loaded);
        // 최대화/복원 때 캡션 버튼 영역이 달라질 수 있다 (최대화 시 테두리 · 버튼 높이)
        StateChanged += (_, _) => { if (WindowState != WindowState.Minimized) Dispatcher.BeginInvoke(Fit, DispatcherPriority.Loaded); };
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseWindow(object sender, RoutedEventArgs e) => Close();

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        // SFTP 로 편집 중인 파일이 있으면 설정과 상관없이 묻는다 (닫은 뒤 저장분은 올라가지 않음) — 이 확인이 세션 확인을 대신한다
        if (ConfirmCloseWithSftpEdits(_tabs, closingApp: true) is { } ok)
        {
            if (!ok) { e.Cancel = true; return; }
        }
        // 로컬 PowerShell 은 세지 않고, 탭 닫기 확인을 끈 설정(ConfirmCloseTab)도 따른다. 클러스터 탭의 실행 중인 포트 포워딩도 알린다
        else if (SettingsStore.Current.ConfirmCloseTab &&
            CloseAppPrompt(LiveRemoteSessionCount(_tabs.Select(t => (t.Protocol, t.Status))),
                _tabs.Select(t => t.View).OfType<K8sSessionView>().Sum(v => v.Forwards.Count(f => f.IsRunning))) is { } prompt &&
            Dialog.Show(this, prompt, "Nubis", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        SaveWindowState();
        ShutdownSplitView();   // 나란히 보기 포커스 훅 해제 (MainWindow.SplitView.cs)
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
        var r = new Rect(b[0], b[1], b[2], b[3]);
        // 제목 표시줄이 실제 모니터 작업 영역 안에 있어야 끌어 옮길 수 있다
        // (가상 화면 사각형만 보면 크기가 다른 모니터 사이의 빈 공간에 창이 열릴 수 있다)
        if (!TitleBarOnSomeMonitor(new Rect(r.X + 40, r.Y, Math.Max(1, r.Width - 80), 40))) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        (Left, Top, Width, Height) = (r.X, r.Y, r.Width, r.Height);
        if (st.WindowMaximized) WindowState = WindowState.Maximized;
    }

    /// <summary>제목 표시줄(DIP)이 어느 모니터의 작업 영역과 충분히(가로 100 · 세로 20 DIP 이상) 겹치는지</summary>
    private static bool TitleBarOnSomeMonitor(Rect titleDip)
    {
        try
        {
            // WinForms Screen 은 물리 픽셀 — 시스템 DPI 배율(가상 화면 픽셀 ÷ DIP)로 DIP 로 바꿔 비교
            var vs = System.Windows.Forms.SystemInformation.VirtualScreen;
            var scale = SystemParameters.VirtualScreenWidth > 0 ? vs.Width / SystemParameters.VirtualScreenWidth : 1.0;
            if (scale <= 0 || double.IsNaN(scale)) scale = 1.0;
            foreach (var sc in System.Windows.Forms.Screen.AllScreens)
            {
                var wa = sc.WorkingArea;
                var area = new Rect(wa.X / scale, wa.Y / scale, wa.Width / scale, wa.Height / scale);
                var hit = Rect.Intersect(area, titleDip);
                if (!hit.IsEmpty && hit.Width >= Math.Min(100, titleDip.Width) && hit.Height >= 20) return true;
            }
            return false;
        }
        catch
        {
            // 모니터 정보를 못 읽으면 예전처럼 가상 화면 기준
            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            return screen.IntersectsWith(titleDip);
        }
    }

    private void SaveWindowState()
    {
        CommitSettingsInputs();
        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        var claude = ClaudeColumn.ActualWidth > 0 ? ClaudeColumn.ActualWidth : _claudeWidth;
        // 최소화한 채 닫으면 WindowState 는 Minimized — 최소화 직전 상태(최대화 여부)를 저장
        var maximized = WindowState == WindowState.Maximized || (WindowState == WindowState.Minimized && _maximizedBeforeMinimize);
        SettingsStore.UpdateState(s =>
        {
            if (!r.IsEmpty && r.Width > 0) s.WindowBounds = new[] { r.X, r.Y, r.Width, r.Height };
            s.WindowMaximized = maximized;
            s.ClaudePanelWidth = Math.Round(claude);
        });
    }
}
