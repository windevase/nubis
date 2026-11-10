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

    // =================== 호스트 목록 ===================

    private void RefreshHosts()
    {
        var q = SearchBox.Text.Trim();
        IEnumerable<HostEntry> list = _hosts;
        if (q.Length > 0)
        {
            list = list.Where(h =>
                Contains(h.DisplayName, q) || Contains(h.Address, q) || Contains(h.Username, q) ||
                Contains(h.Group, q) || Contains(h.ProtocolText, q));
        }

        var filtered = list.ToList();
        var searching = q.Length > 0;

        // 직접 등록한 호스트: Group 별 (지정된 그룹 먼저, 미지정은 "Hosts")
        var groups = filtered.Where(h => !h.IsCloud)
            .GroupBy(h => string.IsNullOrWhiteSpace(h.Group) ? "" : h.Group.Trim())
            .OrderBy(g => g.Key.Length == 0)
            .ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => new HostGroup
            {
                Key = "L:" + g.Key,
                Name = g.Key.Length == 0 ? "Hosts" : g.Key,
                Hosts = g.OrderBy(h => h.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList(),
                IsCollapsed = !searching && _collapsedLocal.Contains("L:" + g.Key),
            })
            .ToList();

        // 클라우드 프로필 그룹: AWS → Tencent → GCP → Azure, 기본은 접힌 상태 (서버가 많으므로)
        foreach (var p in _profiles.OrderBy(p => p.Provider).ThenBy(p => p.GroupName, StringComparer.CurrentCultureIgnoreCase))
        {
            var hosts = filtered.Where(h => h.CloudProfileId == p.Id)
                .OrderBy(h => h.IsStopped)
                .ThenBy(h => h.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            // 검색에 안 걸렸거나, 동기화했는데 서버가 없는 프로필(VM 없는 GCP 프로젝트 등)은 숨김
            if (hosts.Count == 0 && (searching || p.LastSync != null)) continue;
            var key = "C:" + p.Id;
            groups.Add(new HostGroup
            {
                Key = key,
                Name = p.GroupName,
                Profile = p,
                Hosts = hosts,
                IsCollapsed = !searching && !_expandedCloud.Contains(key),
            });
        }

        _groups = groups;
        RebuildRows();
        // 보이는 그룹이 없으면 안내 (검색 중이면 검색 결과 없음)
        EmptyState.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = searching ? "검색 결과가 없습니다" : _profiles.Count > 0 ? "동기화된 서버가 없습니다" : "등록된 호스트가 없습니다";
        EmptyHint.Text = searching
            ? "Enter 를 누르면 입력한 user@host 로 바로 접속합니다."
            : _profiles.Count > 0
                ? "Cloud 페이지에서 프로필을 동기화하거나, NEW HOST 로 서버를 추가하세요."
                : "NEW HOST 로 SSH / RDP 서버를 추가하거나, Cloud 에서 클라우드 계정의 서버를 가져오세요.";
        CountText.Text = q.Length > 0 ? $"{groups.Sum(g => g.Hosts.Count)} / {_hosts.Count}개" : $"{_hosts.Count}개 호스트";
    }

    private List<HostGroup> _groups = new();
    private int _cardsPerRow = 3;
    private const double CardSlotWidth = 302;   // 카드 290 + 간격 12

    /// <summary>그룹 헤더 + 카드 줄(창 폭에 맞춘 개수)로 행 목록 구성. 스크롤 위치는 유지</summary>
    private void RebuildRows()
    {
        var rows = new List<object>();
        foreach (var g in _groups)
        {
            rows.Add(g);
            if (g.IsCollapsed) continue;
            for (var i = 0; i < g.Hosts.Count; i += _cardsPerRow)
                rows.Add(new CardRow { Hosts = g.Hosts.GetRange(i, Math.Min(_cardsPerRow, g.Hosts.Count - i)) });
        }
        var sv = FindScrollViewer(HostRows);
        var offset = sv?.VerticalOffset ?? 0;
        HostRows.ItemsSource = rows;
        if (sv != null && offset > 0) Dispatcher.BeginInvoke(() => sv.ScrollToVerticalOffset(offset), DispatcherPriority.Loaded);
    }

    private void OnHostRowsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 좌우 여백(28+16)과 스크롤바 폭을 빼고 한 줄에 들어가는 카드 수
        var per = Math.Max(1, (int)((e.NewSize.Width - 44 - 12) / CardSlotWidth));
        if (per == _cardsPerRow) return;
        _cardsPerRow = per;
        RebuildRows();
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    private static bool Contains(string? s, string q) => s?.Contains(q, StringComparison.OrdinalIgnoreCase) == true;

    private void SaveHosts()
    {
        try { HostStore.Save(_hosts); }
        catch (Exception ex) { Dialog.Show(this, $"호스트 목록 저장 실패: {ex.Message}", "Nubis"); }
    }

    private DispatcherTimer? _searchTimer;

    /// <summary>입력이 잠깐 멈췄을 때 한 번만 목록 갱신</summary>
    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_searchTimer == null)
        {
            _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
            _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); RefreshHosts(); };
        }
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { QuickConnect(); e.Handled = true; }
        else if (e.Key == Key.Escape) { SearchBox.Clear(); e.Handled = true; }
    }

    private void OnQuickConnect(object sender, RoutedEventArgs e) => QuickConnect();

    /// <summary>검색 결과가 1개면 그 호스트로, 아니면 "user@host:port" / "rdp host" 형식으로 바로 접속</summary>
    private void QuickConnect()
    {
        var text = SearchBox.Text.Trim();
        if (text.Length == 0) return;

        if (_searchTimer?.IsEnabled == true) { _searchTimer.Stop(); RefreshHosts(); }   // 입력 직후 Enter
        var matches = _groups.SelectMany(g => g.Hosts).ToList();
        var m = QuickRegex.Match(text);

        // 1) 이름·주소가 정확히 일치하는 호스트 → 그 호스트로 (10.0.0.1 을 쳤는데 10.0.0.12 로 붙는 일 방지)
        var exact = matches.Where(h => h.DisplayName.Equals(text, StringComparison.OrdinalIgnoreCase) ||
                                       h.Address.Equals(text, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) { Connect(exact[0]); return; }
        // 2) 주소·user@host 형태면 입력한 그대로 접속, 그 외 검색 결과가 하나뿐이면 그 호스트로
        var looksLikeAddress = m.Success && (text.Contains('@') || text.Contains('.') || m.Groups["proto"].Success || m.Groups["port"].Success);
        if (!looksLikeAddress && matches.Count == 1) { Connect(matches[0]); return; }
        if (!m.Success)
        {
            Dialog.Show(this, "user@host[:port] 또는 rdp host[:port] 형식으로 입력하세요.", "빠른 접속");
            return;
        }

        var proto = m.Groups["proto"].Value.Equals("rdp", StringComparison.OrdinalIgnoreCase) ? Protocol.Rdp : Protocol.Ssh;
        var host = new HostEntry
        {
            Protocol = proto,
            Address = m.Groups["host"].Value,
            Username = m.Groups["user"].Value,
            Port = m.Groups["port"].Success ? int.Parse(m.Groups["port"].Value) : HostEntry.PreferredPort(proto),
        };
        if (host.Port is < 1 or > 65535) host.Port = HostEntry.PreferredPort(proto);
        Connect(host);
    }

    /// <summary>카드(또는 공유 우클릭 메뉴의 대상 카드)의 호스트</summary>
    private static HostEntry? HostOf(object sender)
    {
        if (sender is MenuItem { Parent: ContextMenu { PlacementTarget: FrameworkElement target } })
            return target.DataContext as HostEntry;
        return (sender as FrameworkElement)?.DataContext as HostEntry;
    }

    private void OnCardMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && HostOf(sender) is { } h) { Connect(h); e.Handled = true; }
    }

    private void OnEditHost(object sender, RoutedEventArgs e) { if (HostOf(sender) is { } h) OpenEditor(h); }
    private void OnMenuConnect(object sender, RoutedEventArgs e) { if (HostOf(sender) is { } h) Connect(h); }
    private void OnMenuConnectPublic(object sender, RoutedEventArgs e) { if (HostOf(sender) is { } h) Connect(h, usePublic: true); }
    private void OnMenuConnectPrivate(object sender, RoutedEventArgs e) { if (HostOf(sender) is { } h) Connect(h, usePublic: false); }

    /// <summary>공유 우클릭 메뉴: 공인 IP 가 있는 호스트에만 "공인/사설 IP 로 연결" 표시</summary>
    private void OnHostMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu { PlacementTarget: FrameworkElement { DataContext: HostEntry h } } menu) return;
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            item.Visibility = (item.Tag as string) switch
            {
                "public" => h.HasPublicIp && !h.UsePublicIp ? Visibility.Visible : Visibility.Collapsed,
                "private" => h.HasPublicIp && h.UsePublicIp ? Visibility.Visible : Visibility.Collapsed,
                _ => Visibility.Visible,
            };
            if ((item.Tag as string) == "public") item.Header = $"공인 IP로 연결 ({h.PublicIp})";
            if ((item.Tag as string) == "private") item.Header = $"사설 IP로 연결 ({h.PrimaryAddress})";
        }
    }
    private void OnMenuEdit(object sender, RoutedEventArgs e) { if (HostOf(sender) is { } h) OpenEditor(h); }

    private void OnMenuDuplicate(object sender, RoutedEventArgs e)
    {
        if (HostOf(sender) is not { } h) return;
        var copy = h.Clone();
        copy.Id = Guid.NewGuid();
        copy.Label = (string.IsNullOrWhiteSpace(h.Label) ? h.Address : h.Label) + " (복사본)";
        copy.LastConnected = null;
        if (h.IsCloud)
        {
            // 클라우드 호스트의 복사본은 직접 관리하는 호스트로 (같은 인스턴스 ID 를 가진 유령 사본 방지)
            var profile = _profiles.FirstOrDefault(p => p.Id == h.CloudProfileId);
            copy = WithProfileDefaults(copy);
            copy.Group = profile?.GroupName ?? "";
            copy.CloudProfileId = null;
            copy.CloudInstanceId = null;
            copy.CloudState = null;
            copy.CloudRegion = null;
        }
        _hosts.Add(copy);
        SaveHosts();
        RefreshHosts();
        OpenEditor(copy);
    }

    private void OnMenuDelete(object sender, RoutedEventArgs e) { if (HostOf(sender) is { } h) DeleteHost(h); }

    private void DeleteHost(HostEntry h)
    {
        var profile = h.IsCloud ? _profiles.FirstOrDefault(p => p.Id == h.CloudProfileId) : null;
        var msg = profile != null
            ? $"'{h.DisplayName}' 을(를) 목록에서 지울까요?\n클라우드의 서버는 그대로이며, 다음 동기화에서도 다시 나타나지 않도록 숨깁니다.\n(프로필 설정에서 숨김을 해제할 수 있습니다)"
            : $"'{h.DisplayName}' 호스트를 삭제할까요?";
        if (Dialog.Show(this, msg, "삭제 확인", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (profile != null && h.CloudInstanceId != null && !profile.HiddenInstances.Contains(h.CloudInstanceId))
        {
            profile.HiddenInstances.Add(h.CloudInstanceId);
            SaveProfiles();
        }
        _hosts.Remove(h);
        SaveHosts();
        UpdateProfileCounts();
        RefreshHosts();
    }

    private void OnNewHost(object sender, RoutedEventArgs e) => OpenEditor(null);

    // =================== 사이드바 ===================

    private void OnNavChanged(object sender, RoutedEventArgs e)
    {
        if (HostsPage == null || SettingsPage == null || CloudPage == null) return;   // InitializeComponent 중
        var known = NavKnown.IsChecked == true;
        var settings = NavSettings.IsChecked == true;
        var cloud = NavCloud.IsChecked == true;
        HostsPage.Visibility = !known && !settings && !cloud ? Visibility.Visible : Visibility.Collapsed;
        KnownPage.Visibility = known ? Visibility.Visible : Visibility.Collapsed;
        CloudPage.Visibility = cloud ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        if (known) RefreshKnownHosts();
        if (settings) RefreshSettingsPage();
    }

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
        var (oldSsh, oldRdp) = (HostEntry.PreferredPort(Protocol.Ssh), HostEntry.PreferredPort(Protocol.Rdp));
        if (oldSsh == ssh && oldRdp == rdp) return;
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

    // =================== 편집 패널 ===================

    private void OpenEditor(HostEntry? h)
    {
        _editing = h;
        EditorTitle.Text = h == null ? "New Host" : "Edit Host";
        DeleteButton.Visibility = h == null ? Visibility.Collapsed : Visibility.Visible;

        var proto = h?.Protocol ?? Protocol.Ssh;
        EdPort.Text = (h?.Port ?? HostEntry.PreferredPort(proto)).ToString();
        (proto == Protocol.Ssh ? ProtoSsh : ProtoRdp).IsChecked = true;
        UpdateProtoUi();

        EdAddress.Text = h?.PrimaryAddress ?? "";
        EdAddressLabel.Text = h?.IsCloud == true ? "주소 (사설 IP · 동기화로 갱신)" : "주소";
        EdPublicIp.Text = h?.PublicIp ?? "";
        EdUsePublic.IsChecked = h?.UsePublicIp == true;
        EdLabel.Text = h?.Label ?? "";
        EdGroup.Text = h?.Group ?? "";
        EdUser.Text = h?.Username ?? "";
        EdPassword.Password = h?.Password ?? "";
        EdKey.Text = h?.KeyPath ?? "";

        NavHosts.IsChecked = true;
        EditorLayer.Visibility = Visibility.Visible;
        EditorSlide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(390, 0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
        Dispatcher.BeginInvoke(() => EdAddress.Focus(), DispatcherPriority.Input);
    }

    private void CloseEditor()
    {
        var anim = new DoubleAnimation(0, 390, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        anim.Completed += (_, _) => EditorLayer.Visibility = Visibility.Collapsed;
        EditorSlide.BeginAnimation(TranslateTransform.XProperty, anim);
        EdPassword.Clear();
    }

    private void OnProtoChanged(object sender, RoutedEventArgs e)
    {
        if (EdPort == null) return;
        var proto = ProtoRdp.IsChecked == true ? Protocol.Rdp : Protocol.Ssh;
        var other = proto == Protocol.Ssh ? Protocol.Rdp : Protocol.Ssh;
        if (string.IsNullOrWhiteSpace(EdPort.Text) || EdPort.Text == HostEntry.PreferredPort(other).ToString())
            EdPort.Text = HostEntry.PreferredPort(proto).ToString();
        UpdateProtoUi();
    }

    private void UpdateProtoUi() =>
        KeyPanel.Visibility = ProtoSsh.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private void OnBrowseKey(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "SSH 개인키 선택",
            Filter = "모든 파일|*.*|PuTTY 키 (*.ppk)|*.ppk|PEM (*.pem)|*.pem",
            InitialDirectory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh"),
        };
        if (dlg.ShowDialog(this) == true) EdKey.Text = dlg.FileName;
    }

    private HostEntry? SaveEditor()
    {
        var address = EdAddress.Text.Trim();
        if (address.Length == 0) { EdAddress.Focus(); return null; }
        if (!int.TryParse(EdPort.Text.Trim(), out var port) || port is < 1 or > 65535) { EdPort.Focus(); return null; }

        var h = _editing ?? new HostEntry();
        h.Protocol = ProtoRdp.IsChecked == true ? Protocol.Rdp : Protocol.Ssh;
        h.Address = address;
        if (h.PrivateIp != null) h.PrivateIp = address;
        var pub = EdPublicIp.Text.Trim();
        h.PublicIp = pub.Length > 0 ? pub : null;
        h.UsePublicIp = EdUsePublic.IsChecked == true && h.PublicIp != null;
        h.Port = port;
        h.Label = EdLabel.Text.Trim();
        h.Group = EdGroup.Text.Trim();
        h.Username = EdUser.Text.Trim();
        h.Password = EdPassword.Password;
        h.KeyPath = h.Protocol == Protocol.Ssh && EdKey.Text.Trim().Length > 0 ? EdKey.Text.Trim() : null;

        if (_editing == null) _hosts.Add(h);
        SaveHosts();
        RefreshHosts();
        CloseEditor();
        return h;
    }

    private void OnEditorSave(object sender, RoutedEventArgs e) => SaveEditor();

    private void OnEditorSaveConnect(object sender, RoutedEventArgs e)
    {
        if (SaveEditor() is { } h) Connect(h);
    }

    private void OnEditorDelete(object sender, RoutedEventArgs e)
    {
        if (_editing == null) return;
        var before = _hosts.Count;
        DeleteHost(_editing);
        if (_hosts.Count < before) CloseEditor();
    }

    private void OnEditorClose(object sender, RoutedEventArgs e) => CloseEditor();
    private void OnEditorDimClick(object sender, MouseButtonEventArgs e) => CloseEditor();

    // =================== 클라우드 ===================

    private void SaveProfiles()
    {
        try { CloudStore.Save(_profiles); }
        catch (Exception ex) { Dialog.Show(this, $"클라우드 프로필 저장 실패: {ex.Message}", "Nubis"); }
    }

    private void UpdateProfileCounts()
    {
        foreach (var p in _profiles)
        {
            var hosts = _hosts.Where(h => h.CloudProfileId == p.Id).ToList();
            p.HostCount = hosts.Count;
            p.RunningCount = hosts.Count(h => !h.IsStopped);
            p.NotifySummary();
        }
    }

    private CloudSection SectionOf(CloudProfile p) => _sections.First(s => s.Provider == p.Provider);

    private void OnGroupHeaderClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not HostGroup g) return;
        g.IsCollapsed = !g.IsCollapsed;
        var set = g.IsCloud ? _expandedCloud : _collapsedLocal;
        if (g.IsCloud ^ g.IsCollapsed) set.Add(g.Key); else set.Remove(g.Key);
        RebuildRows();
    }

    private async void OnGroupSync(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is HostGroup { Profile: { } p }) await SyncProfileAsync(p);
    }

    private async void OnProfileSync(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CloudProfile p) await SyncProfileAsync(p);
    }

    private async void OnSyncAll(object sender, RoutedEventArgs e)
    {
        if (_profiles.Count == 0) return;
        SyncAllButton.IsEnabled = false;
        SyncAllButton.Content = "동기화 중...";
        try
        {
            using var gate = new System.Threading.SemaphoreSlim(4);
            await Task.WhenAll(_profiles.ToList().Select(async p =>
            {
                await gate.WaitAsync();
                try { await SyncProfileAsync(p); } finally { gate.Release(); }
            }));
        }
        finally
        {
            SyncAllButton.IsEnabled = true;
            SyncAllButton.Content = "전체 동기화";
        }
    }

    /// <summary>프로필의 인스턴스를 조회해 호스트 목록에 반영 (조회는 백그라운드, 반영은 UI 스레드)</summary>
    private async Task SyncProfileAsync(CloudProfile p)
    {
        if (p.IsSyncing) return;
        p.IsSyncing = true;
        p.StatusText = "";
        try
        {
            var provider = CloudProviders.Get(p.Provider);
            var fetched = await Task.Run(() => provider.FetchAsync(p, System.Threading.CancellationToken.None));
            if (!_profiles.Contains(p)) return;   // 조회 중 제거됨
            var (added, updated, removed) = CloudSync.Apply(p, fetched.Items, _hosts, removeMissing: !fetched.IsPartial);
            p.LastResult = added + removed > 0 ? $"추가 {added} · 제거 {removed}" : "변경 없음";
            if (fetched.IsPartial)
                p.StatusText = $"일부 조회 실패 {fetched.Errors.Count}건 — 이번엔 삭제하지 않음: {CloudProviders.FirstLine(fetched.Errors[0])}";
            SaveHosts();
            SaveProfiles();
        }
        catch (Exception ex)
        {
            p.StatusText = "실패: " + CloudProviders.FirstLine(ex.Message);
        }
        finally
        {
            p.IsSyncing = false;
            UpdateProfileCounts();
            RefreshHosts();
        }
    }

    private async void OnAddProfiles(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CloudSection section || section.IsBusy) return;
        var button = (Button)sender;
        section.IsBusy = true;
        section.Message = "";
        List<CloudCandidate> candidates;
        try
        {
            candidates = await Task.Run(() => CloudProviders.Get(section.Provider).DiscoverAsync(System.Threading.CancellationToken.None));
        }
        catch (Exception ex)
        {
            section.Message = $"목록 조회 실패: {CloudProviders.FirstLine(ex.Message)}  (\"로그인\" 버튼으로 CLI 로그인 후 다시 시도)";
            return;
        }
        finally { section.IsBusy = false; }

        var existing = section.Profiles.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fresh = candidates.Where(c => !existing.Contains(c.Name)).ToList();
        if (fresh.Count == 0)
        {
            section.Message = candidates.Count == 0 ? "이 PC에서 찾은 프로필이 없습니다." : "모든 프로필이 이미 추가되어 있습니다.";
            return;
        }

        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var all = new MenuItem { Header = $"모두 추가 ({fresh.Count})", FontWeight = FontWeights.SemiBold };
        all.Click += async (_, _) => await AddProfilesAsync(section, fresh);
        menu.Items.Add(all);
        foreach (var c in fresh)
        {
            var label = c.DisplayName.Equals(c.Name, StringComparison.OrdinalIgnoreCase) ? c.DisplayName : $"{c.DisplayName}   ({c.Name})";
            var item = new MenuItem { Header = label };
            item.Click += async (_, _) => await AddProfilesAsync(section, new List<CloudCandidate> { c });
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private async Task AddProfilesAsync(CloudSection section, List<CloudCandidate> candidates)
    {
        var added = new List<CloudProfile>();
        foreach (var c in candidates)
        {
            var p = new CloudProfile { Provider = section.Provider, Name = c.Name, DisplayName = c.DisplayName, TenantId = c.TenantId };
            _profiles.Add(p);
            section.Profiles.Add(p);
            added.Add(p);
        }
        SaveProfiles();
        RefreshHosts();

        // 추가한 프로필은 바로 동기화
        using var gate = new System.Threading.SemaphoreSlim(4);
        await Task.WhenAll(added.Select(async p =>
        {
            await gate.WaitAsync();
            try { await SyncProfileAsync(p); } finally { gate.Release(); }
        }));
    }

    private void OnProfileSettings(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CloudProfile p) return;
        var (oldSsh, oldRdp, oldPublic) = (p.EffectiveSshPort, p.EffectiveRdpPort, p.UsePublicIp);
        if (!ProfileDialog.Edit(this, p)) return;
        // 기본 접속 주소(사설/공인)를 바꾸면 공인 IP 가 있는 서버 전체에 적용
        if (oldPublic != p.UsePublicIp)
            foreach (var h in _hosts.Where(h => h.CloudProfileId == p.Id && h.HasPublicIp)) h.UsePublicIp = p.UsePublicIp;

        // 기본 포트를 바꾸면 기본값을 쓰던 호스트도 함께 변경
        foreach (var h in _hosts.Where(h => h.CloudProfileId == p.Id))
        {
            if (h.Protocol == Protocol.Ssh && h.Port == oldSsh) h.Port = p.EffectiveSshPort;
            if (h.Protocol == Protocol.Rdp && h.Port == oldRdp) h.Port = p.EffectiveRdpPort;
        }
        SaveProfiles();
        SaveHosts();
        RefreshHosts();
        CloudSections.Items.Refresh();
    }

    private void OnProfileRemove(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CloudProfile p) return;
        var count = _hosts.Count(h => h.CloudProfileId == p.Id);
        if (Dialog.Show(this, $"[{p.ProviderText}] {p.GroupName} 프로필과 동기화된 호스트 {count}개를 제거할까요?\n(클라우드의 서버에는 아무 영향이 없습니다)",
                "프로필 제거", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        _hosts.RemoveAll(h => h.CloudProfileId == p.Id);
        _profiles.Remove(p);
        SectionOf(p).Profiles.Remove(p);
        SaveHosts();
        SaveProfiles();
        RefreshHosts();
    }

    /// <summary>클라우드 인증 파일(폴더) 위치를 직접 선택</summary>
    private void OnChangeCloudPath(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CloudSection section) return;
        var current = CloudPaths.Effective(section.Provider);
        string? chosen;
        if (CloudPaths.IsFolder(section.Provider))
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = $"{section.Title} 인증 폴더 선택 ({(section.Provider == CloudProvider.Tencent ? "*.credential 파일이 있는 폴더" : "az CLI 설정 폴더")})",
                InitialDirectory = Directory.Exists(current) ? current : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            chosen = dlg.ShowDialog(this) == true ? dlg.FolderName : null;
        }
        else
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = $"{section.Title} 인증 파일 선택 ({(section.Provider == CloudProvider.Aws ? "AWS config" : "ADC JSON")})",
                Filter = section.Provider == CloudProvider.Gcp ? "JSON (*.json)|*.json|모든 파일|*.*" : "모든 파일|*.*",
                InitialDirectory = Path.GetDirectoryName(current) is { } d && Directory.Exists(d) ? d : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                FileName = Path.GetFileName(current),
            };
            chosen = dlg.ShowDialog(this) == true ? dlg.FileName : null;
        }
        if (chosen == null) return;
        SetCloudPath(section, chosen);
    }

    private void OnResetCloudPath(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CloudSection section) SetCloudPath(section, null);
    }

    private void SetCloudPath(CloudSection section, string? path)
    {
        SettingsStore.Update(s =>
        {
            if (path == null) s.CloudPaths.Remove(section.Provider.ToString());
            else s.CloudPaths[section.Provider.ToString()] = path;
        });
        CloudPaths.Apply();
        section.NotifyPath();
        section.Message = path == null
            ? "기본 경로로 되돌렸습니다. 프로필 추가·동기화부터 적용됩니다."
            : "경로를 바꿨습니다. 프로필 추가·동기화와 로그인 버튼에 바로 적용됩니다.";
    }

    /// <summary>CLI 로그인 명령을 새 콘솔 창에서 실행 (브라우저 인증은 사용자가 진행)</summary>
    private void OnProviderLogin(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CloudSection section) return;
        var p = section.Profiles.FirstOrDefault();
        if (section.Provider == CloudProvider.Aws && p == null)
        {
            section.Message = "먼저 프로필을 추가하세요. (같은 SSO 포털을 쓰는 프로필은 한 번 로그인으로 모두 사용 가능)";
            return;
        }
        var cmd = CloudProviders.Get(section.Provider).LoginCommand(p);
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/k {cmd}") { UseShellExecute = true });
            section.Message = $"새 창에서 '{cmd}' 를 실행했습니다. 로그인을 마친 뒤 동기화하세요.";
        }
        catch (Exception ex) { section.Message = $"실행 실패: {ex.Message}"; }
    }

    // =================== 세션 / 탭 ===================

    private int _nextSessionId = 1;

    /// <param name="usePublic">null = 호스트 설정대로, true/false = 이번만 공인/사설 IP 로</param>
    private SessionTab Connect(HostEntry saved, bool? usePublic = null)
    {
        // 클라우드 호스트는 비어 있는 계정을 프로필 기본 계정으로 채우고, 사설/공인 중 실제 접속 주소로
        var host = saved.ForConnection(_profiles.FirstOrDefault(p => p.Id == saved.CloudProfileId), usePublic);
        var viaPublic = saved.HasPublicIp && host.Address == saved.PublicIp && saved.PublicIp != saved.PrimaryAddress;
        // 접속 때 입력한 계정 저장 (목록에 있는 호스트만. 빠른 접속은 저장 안 함)
        Action<string, string>? save = _hosts.Contains(saved)
            ? (u, p) => { saved.Username = u; saved.Password = p; SaveHosts(); RefreshHosts(); }
            : null;
        FrameworkElement view = host.Protocol == Protocol.Ssh ? new SshSessionView(host, save) : new RdpSessionView(host, save);
        var title = host.Protocol == Protocol.Ssh && !string.IsNullOrWhiteSpace(host.Username) && string.IsNullOrWhiteSpace(host.Label)
            ? $"{host.Username}@{host.Address}"
            : host.DisplayName;
        if (viaPublic) title += " (공인)";
        var tab = AddTab(new SessionTab { Id = _nextSessionId++, Title = title, Protocol = host.Protocol, View = view, Host = saved });

        if (_hosts.Contains(saved))
        {
            saved.LastConnected = DateTime.Now;
            SaveHosts();
        }
        return tab;
    }

    private HostEntry WithProfileDefaults(HostEntry h) =>
        h.WithDefaults(_profiles.FirstOrDefault(p => p.Id == h.CloudProfileId));

    private SessionTab AddTab(SessionTab tab)
    {
        tab.Session.StatusChanged += s => tab.Status = s;
        _tabs.Add(tab);
        SessionsHost.Children.Add(tab.View);
        SelectTab(tab);
        Dispatcher.BeginInvoke(() => TabsScroll.ScrollToRightEnd(), DispatcherPriority.Loaded);
        return tab;
    }

    /// <summary>로컬 PowerShell 탭 (Ctrl+Shift+T)</summary>
    private void OpenLocalTerminal()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var view = new LocalTerminalView(() => "powershell.exe -NoLogo", home, "PowerShell 이 종료되었습니다.");
        AddTab(new SessionTab { Id = _nextSessionId++, Title = "PowerShell", Protocol = Protocol.Local, View = view });
    }

    private void OnNewLocalTerminal(object sender, RoutedEventArgs e) => OpenLocalTerminal();

    private void CycleTab(int dir)
    {
        if (_tabs.Count == 0) return;
        var cur = _tabs.FirstOrDefault(t => t.IsSelected);
        var i = (cur == null ? -1 : _tabs.IndexOf(cur)) + dir;
        if (cur == null && dir < 0) i = _tabs.Count - 1;
        if (i < 0 || i >= _tabs.Count) SelectHome();
        else SelectTab(_tabs[i]);
    }

    /// <summary>터미널(WebView2)에 포커스가 있을 때 들어오는 앱 단축키</summary>
    private void OnTerminalHotkey(string key) => Dispatcher.BeginInvoke(() =>
    {
        switch (key)
        {
            case "nexttab": CycleTab(+1); break;
            case "prevtab": CycleTab(-1); break;
            case "claude": ToggleClaude(); break;
            case "localterm": OpenLocalTerminal(); break;
        }
    });

    private void SelectTab(SessionTab tab)
    {
        foreach (var t in _tabs)
        {
            t.IsSelected = t == tab;
            t.View.Visibility = t == tab ? Visibility.Visible : Visibility.Collapsed;
        }
        HomeView.Visibility = Visibility.Collapsed;
        SessionsHost.Visibility = Visibility.Visible;
        HomeTab.Background = Brushes.Transparent;
        Dispatcher.BeginInvoke(() => tab.Session.FocusSession(), DispatcherPriority.Input);
    }

    private void SelectHome()
    {
        foreach (var t in _tabs) t.IsSelected = false;
        SessionsHost.Visibility = Visibility.Collapsed;
        HomeView.Visibility = Visibility.Visible;
        HomeTab.Background = (Brush)FindResource("CardBg");
    }

    private void CloseTab(SessionTab tab)
    {
        var idx = _tabs.IndexOf(tab);
        var wasSelected = tab.IsSelected;
        try { tab.Session.CloseSession(); } catch { }
        SessionsHost.Children.Remove(tab.View);
        _tabs.Remove(tab);

        if (!wasSelected) return;
        if (_tabs.Count == 0) SelectHome();
        else SelectTab(_tabs[Math.Min(idx, _tabs.Count - 1)]);
    }

    private void OnHomeTab(object sender, MouseButtonEventArgs e) => SelectHome();

    private void OnTabClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SessionTab t) SelectTab(t);
    }

    private void OnTabMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 휠 클릭으로 탭 닫기
        if (e.ChangedButton == MouseButton.Middle && (sender as FrameworkElement)?.DataContext is SessionTab t)
        {
            CloseTab(t);
            e.Handled = true;
        }
    }

    private void OnTabClose(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SessionTab t) CloseTab(t);
        e.Handled = true;
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        // Ctrl+Tab / Ctrl+Shift+Tab : 탭 전환 (WPF 포커스일 때. 터미널 포커스일 때는 OnTerminalHotkey)
        if (e.Key == Key.Tab && ctrl)
        {
            CycleTab(shift ? -1 : +1);
            e.Handled = true;
        }
        else if (ctrl && shift && e.Key == Key.A) { ToggleClaude(); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.T) { OpenLocalTerminal(); e.Handled = true; }
        else if (e.Key == Key.Escape && EditorLayer.Visibility == Visibility.Visible)
        {
            CloseEditor();
            e.Handled = true;
        }
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
