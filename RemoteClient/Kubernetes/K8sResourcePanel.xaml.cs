using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Nubis;

/// <summary>
/// 클러스터 탭 왼쪽 리소스 패널: 네임스페이스 · 종류(파드 · 디플로이먼트 · 서비스 · 노드) 목록과 우클릭 동작(.Menu.cs).
/// 셸·로그는 새 탭으로, describe·YAML 은 오른쪽 kubectl 터미널에 실행한다 (이벤트로 탭에 맡긴다).
/// 자동 새로고침 타이머는 패널이 보이고 창이 최소화되지 않았고 자동 새로고침이 켜져 있을 때만 돈다
/// </summary>
public partial class K8sResourcePanel : UserControl
{
    private readonly K8sCluster _cluster;
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private ObservableCollection<K8sItemRow> _rows = new();
    private List<K8sItem> _items = new();
    private K8sKind _kind = K8sKind.Pods;
    private bool _busy, _auto = true, _loadedOnce, _settingNamespaces, _attached, _shutdown;
    private string? _namespace;   // null = 모든 네임스페이스
    private CancellationTokenSource? _loadCts;   // 지금 목록을 받는 kubectl (종류·네임스페이스를 바꾸면 취소)
    private bool _reloadPending, _pendingLoud;
    private DateTime _lastLoadUtc = DateTime.MinValue;
    private string? _pausedKey;   // 목록이 너무 커서 자동 새로고침을 멈춘 (종류, 네임스페이스)
    private Window? _window;      // StateChanged 구독 중인 창 (Unloaded · Shutdown 에서 해제)
    private bool _nsBusy, _nsLoaded;
    private DateTime _nsTriedUtc = DateTime.MinValue;
    private List<string> _nsNames = new();

    /// <summary>파드 셸 (파드, 컨테이너)</summary>
    public event Action<K8sItem, string?>? ShellRequested;
    /// <summary>파드 로그 (파드, 컨테이너, 이전 컨테이너)</summary>
    public event Action<K8sItem, string?, bool>? LogsRequested;
    /// <summary>포트 포워딩 열기 (파드 · 디플로이먼트 · 서비스)</summary>
    public event Action<K8sItem>? PortForwardRequested;
    /// <summary>kubectl 터미널에 입력할 명령 (Enter 포함하지 않음)</summary>
    public event Action<string>? TerminalCommand;
    public event Action? CloseRequested;

    public K8sResourcePanel(K8sCluster cluster)
    {
        InitializeComponent();
        _cluster = cluster;
        ClusterTitle.Text = cluster.Name;
        ClusterTitle.ToolTip = cluster.Endpoint.Length > 0 ? $"{cluster.Name}\n{cluster.Endpoint}" : cluster.Name;
        _namespace = cluster.NamespaceOrDefault;
        ItemList.ItemsSource = _rows;
        UpdateHeaders();
        UpdateAutoButton();
        _timer.Tick += (_, _) => OnTimerTick();
        Loaded += (_, _) =>
        {
            if (_shutdown) return;
            _attached = true;
            HookWindow(Window.GetWindow(this));
            if (!_loadedOnce)
            {
                _loadedOnce = true;
                _ = LoadNamespacesAsync();
                _ = LoadItemsAsync(quiet: false);
            }
            UpdateTimer();
        };
        Unloaded += (_, _) =>
        {
            _attached = false;
            HookWindow(null);
            UpdateTimer();
        };
        IsVisibleChanged += (_, _) => UpdateTimer();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.F5) { Refresh(); e.Handled = true; } };
    }

    /// <summary>탭을 닫을 때 진행 중인 kubectl 을 멈추고 창 이벤트 구독을 푼다</summary>
    public void Shutdown()
    {
        _shutdown = true;
        _timer.Stop();
        HookWindow(null);
        try { _cts.Cancel(); } catch { }
    }

    private Task<Kubectl.Result> RunAsync(IReadOnlyList<string> args, CancellationToken? ct = null) =>
        Kubectl.RunAsync(_cluster, args, TimeSpan.FromSeconds(30), ct ?? _cts.Token, maxOutput: 8_000_000);

    // ----- 자동 새로고침 타이머 -----

    private void HookWindow(Window? w)
    {
        if (ReferenceEquals(_window, w)) return;
        if (_window != null) _window.StateChanged -= OnWindowStateChanged;
        _window = w;
        if (w != null) w.StateChanged += OnWindowStateChanged;
    }

    private void OnWindowStateChanged(object? sender, EventArgs e) => UpdateTimer();

    private static string ViewKey(K8sKind kind, string? ns) => kind + "|" + (K8sResources.Namespaced(kind) ? ns ?? "*" : "");

    /// <summary>타이머를 돌릴 조건이 바뀔 때마다 부른다 (가려지면 멈추고, 다시 보이면 오래된 목록을 바로 갱신)</summary>
    private void UpdateTimer()
    {
        var run = !_shutdown && _auto && _attached && IsVisible && _window?.WindowState != WindowState.Minimized
                  && _pausedKey != ViewKey(_kind, _namespace);
        if (run == _timer.IsEnabled) return;
        if (!run) { _timer.Stop(); return; }
        _timer.Start();
        if (_loadedOnce && !_busy && DateTime.UtcNow - _lastLoadUtc >= _timer.Interval) _ = LoadItemsAsync(quiet: true);
    }

    private void OnTimerTick()
    {
        if (_busy) return;
        _ = LoadItemsAsync(quiet: true);
        // 네임스페이스 목록을 못 받았으면 1분에 한 번 다시
        if (!_nsLoaded && DateTime.UtcNow - _nsTriedUtc >= TimeSpan.FromMinutes(1)) _ = LoadNamespacesAsync();
    }

    // ----- 불러오기 -----

    /// <summary>F5 · 새로고침 버튼: 네임스페이스와 목록을 다시 읽는다 (목록이 커서 멈춘 범위도 이번엔 다시 시도)</summary>
    private void Refresh()
    {
        _ = LoadNamespacesAsync();
        _ = LoadItemsAsync(quiet: false);
    }

    private async Task LoadNamespacesAsync()
    {
        if (_nsBusy || _shutdown) return;
        _nsBusy = true;
        _nsTriedUtc = DateTime.UtcNow;
        try
        {
            var r = await RunAsync(new[] { "get", "namespaces", "-o", "json", "--request-timeout=20s" });
            if (r.ExitCode != 0 || r.TimedOut || r.Truncated) { NamespacesFailed(); return; }   // 권한이 없으면 기본 네임스페이스만
            SetNamespaces(K8sResources.ParseNamespaces(r.Output));
            _nsLoaded = true;
        }
        catch (OperationCanceledException) { }
        catch { NamespacesFailed(); }
        finally { _nsBusy = false; }
    }

    private void NamespacesFailed()
    {
        _nsLoaded = false;   // 다음 새로고침 때 다시
        if (NamespaceBox.ItemsSource == null) SetNamespaces(new List<string>());
    }

    private void SetNamespaces(List<string> names)
    {
        var current = _namespace;
        if (current != null && !names.Contains(current)) names.Insert(0, current);
        if (NamespaceBox.ItemsSource != null && names.SequenceEqual(_nsNames, StringComparer.Ordinal)) return;   // 같으면 그대로 (펼친 목록을 닫지 않게)
        _settingNamespaces = true;
        try
        {
            _nsNames = names;
            var items = new List<string> { L.T("모든 네임스페이스") };
            items.AddRange(names);
            NamespaceBox.ItemsSource = items;
            NamespaceBox.SelectedIndex = current == null ? 0 : items.IndexOf(current);
        }
        finally { _settingNamespaces = false; }
    }

    private async Task LoadItemsAsync(bool quiet)
    {
        if (_shutdown) return;
        // 불러오는 중이면 끝난 뒤 한 번 더 (변경 동작 뒤 갱신도 건너뛰지 않게)
        if (_busy)
        {
            _reloadPending = true;
            if (!quiet) _pendingLoud = true;
            return;
        }
        _busy = true;
        var kind = _kind;
        var ns = _namespace;
        using var req = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _loadCts = req;
        if (!quiet) SetStatus(L.T("불러오는 중..."));
        var sw = Stopwatch.StartNew();
        var canceled = false;
        try
        {
            var r = await RunAsync(K8sResources.ListArgs(kind, ns), req.Token);
            if (kind != _kind || ns != _namespace) return;   // 그 사이 종류·네임스페이스를 바꿨다
            if (r.TimedOut || r.ExitCode != 0) { ShowError(K8sResources.ErrorText(r)); return; }
            if (r.Truncated)
            {
                // 잘린 JSON 은 읽을 수 없다 — 안내하고 같은 범위의 자동 새로고침은 멈춘다 (범위를 바꾸거나 F5 로 다시)
                _pausedKey = ViewKey(kind, ns);
                ShowError(K8sResources.Namespaced(kind) && ns == null
                    ? L.T("목록이 너무 커서 받지 못했습니다 — 네임스페이스를 골라 범위를 줄이세요. 자동 새로고침을 멈췄습니다.")
                    : L.T("목록이 너무 커서 받지 못했습니다 — 터미널에서 kubectl 로 확인하세요. 자동 새로고침을 멈췄습니다."), "Warning");
                return;
            }
            // 큰 JSON(최대 8MB) 해석은 UI 스레드 밖에서. 그 사이 범위를 바꿨거나 닫았으면 버린다
            var output = r.Output;
            var parsed = await Task.Run(() => K8sResources.Parse(kind, output, DateTime.UtcNow), req.Token);
            req.Token.ThrowIfCancellationRequested();
            if (_shutdown || kind != _kind || ns != _namespace) return;
            _items = parsed;
            if (_pausedKey == ViewKey(kind, ns)) _pausedKey = null;
            _lastLoadUtc = DateTime.UtcNow;
            ApplyFilter(rebuild: false);
            var bad = _items.Count(i => !i.Healthy);
            SetStatus(L.F("{0}개 · {1:HH:mm:ss} 갱신", _items.Count, DateTime.Now) + (bad > 0 ? " · " + L.F("확인 필요 {0}개", bad) : ""));
        }
        catch (OperationCanceledException) { canceled = true; }
        catch (JsonException) { ShowError(L.T("kubectl 출력을 읽지 못했습니다.")); }
        catch (Exception ex) { ShowError(ex.Message); }
        finally
        {
            if (ReferenceEquals(_loadCts, req)) _loadCts = null;
            _busy = false;
            // 오래 걸린 큰 목록은 자동 새로고침 간격을 늘린다 (끝나자마자 다시 받느라 CPU 를 계속 쓰지 않게)
            if (!canceled) _timer.Interval = K8sResources.NextRefreshInterval(sw.Elapsed);
            UpdateTimer();
            if (_reloadPending && !_cts.IsCancellationRequested)
            {
                var loud = _pendingLoud;
                _reloadPending = _pendingLoud = false;
                _ = LoadItemsAsync(quiet: !loud);
            }
        }
    }

    /// <summary>종류·네임스페이스를 바꾸면 진행 중인 목록 kubectl 을 멈추고 새 범위로 다시 받는다</summary>
    private void ReloadForNewView()
    {
        try { _loadCts?.Cancel(); } catch { }
        _items = new List<K8sItem>();
        ApplyFilter(rebuild: true);
        EmptyText.Visibility = Visibility.Collapsed;
        _ = LoadItemsAsync(quiet: false);
        UpdateTimer();   // 범위가 바뀌면 멈췄던 자동 새로고침도 다시 (불러오는 중이라 여기서 또 받지는 않는다)
    }

    private void SetStatus(string text)
    {
        StatusText.Text = text;
        StatusText.ToolTip = null;
        StatusText.Foreground = (Brush)FindResource("TextTertiary");
    }

    private void ShowError(string message, string brushKey = "Danger")
    {
        if (string.IsNullOrWhiteSpace(message)) message = L.T("알 수 없는 오류가 발생했습니다.");
        StatusText.Text = message;
        StatusText.Foreground = (Brush)FindResource(brushKey);
        StatusText.ToolTip = message;
        if (_items.Count == 0)
        {
            EmptyText.Text = message;
            EmptyText.Visibility = Visibility.Visible;
        }
    }

    // ----- 목록 표시 -----

    private readonly record struct Wanted(K8sItem Item, string DisplayName, Brush Brush);

    /// <summary>
    /// 목록을 화면에 맞춘다. 새로고침은 바뀐 행만 고쳐 포커스·선택·툴팁·스크롤을 지키고,
    /// 추가·삭제가 많거나 rebuild(검색어·범위 변경)면 새로 만든 뒤 선택·포커스(새로고침이면 스크롤도)를 되돌린다
    /// </summary>
    private void ApplyFilter(bool rebuild)
    {
        var q = SearchBox.Text.Trim();
        var all = _namespace == null;
        var ok = (Brush)FindResource("Success");
        var bad = (Brush)FindResource("Warning");
        var wanted = _items
            .Where(i => q.Length == 0 || i.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || i.Namespace.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Select(i => new Wanted(i, all && i.Namespace.Length > 0 ? $"{i.Namespace}/{i.Name}" : i.Name, i.Healthy ? ok : bad))
            .ToList();
        var hadFocus = ItemList.IsKeyboardFocusWithin;
        if (rebuild || K8sRowSync.ShouldRebuild(_rows.Select(r => r.Key), wanted.Select(w => K8sRowSync.KeyOf(w.Item))))
        {
            var selectedKey = (ItemList.SelectedItem as K8sItemRow)?.Key;
            var scroll = rebuild ? null : FindScrollViewer(ItemList);
            var offset = scroll?.VerticalOffset ?? 0;
            _rows = new ObservableCollection<K8sItemRow>(wanted.Select(w => new K8sItemRow(w.Item, w.DisplayName, w.Brush)));
            ItemList.ItemsSource = _rows;
            if (selectedKey != null) ItemList.SelectedItem = _rows.FirstOrDefault(r => r.Key == selectedKey);
            if (scroll != null && offset > 0) { ItemList.UpdateLayout(); scroll.ScrollToVerticalOffset(offset); }
        }
        else
        {
            K8sRowSync.Apply(_rows, wanted, r => r.Key, w => K8sRowSync.KeyOf(w.Item),
                w => new K8sItemRow(w.Item, w.DisplayName, w.Brush), (r, w) => r.Update(w.Item, w.DisplayName, w.Brush));
        }
        // 포커스가 목록에 있었는데 그 행이 사라졌으면 선택 행(없으면 목록)으로
        if (hadFocus && !ItemList.IsKeyboardFocusWithin)
        {
            ItemList.UpdateLayout();
            if (ItemList.SelectedItem != null && ItemList.ItemContainerGenerator.ContainerFromItem(ItemList.SelectedItem) is ListBoxItem li) li.Focus();
            else ItemList.Focus();
        }
        EmptyText.Text = _items.Count == 0 ? L.T("항목이 없습니다.") : L.T("찾는 이름이 없습니다.");
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject d)
    {
        if (d is ScrollViewer sv) return sv;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(d, i)) is { } found) return found;
        return null;
    }

    private void UpdateHeaders()
    {
        (H1.Text, H2.Text, H3.Text, H4.Text) = _kind switch
        {
            K8sKind.Pods => ("Ready", L.T("상태"), L.T("재시작"), L.T("나이")),
            K8sKind.Deployments => ("Ready", L.T("상태"), L.T("가용"), L.T("나이")),
            K8sKind.Services => (L.T("유형"), "Cluster IP", L.T("포트"), L.T("나이")),
            _ => (L.T("역할"), L.T("상태"), L.T("버전"), L.T("나이")),
        };
    }

    private void UpdateAutoButton()
    {
        AutoButton.Opacity = _auto ? 1 : 0.45;
        AutoButton.ToolTip = _auto ? L.T("자동 새로고침 켜짐 — 누르면 끔") : L.T("자동 새로고침 꺼짐 — 누르면 켬");
    }

    // ----- 화면 동작 -----

    private void OnRefresh(object sender, RoutedEventArgs e) => Refresh();

    private void OnToggleAuto(object sender, RoutedEventArgs e)
    {
        _auto = !_auto;
        UpdateAutoButton();
        UpdateTimer();
    }

    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private void OnKindChanged(object sender, RoutedEventArgs e)
    {
        if (ItemList == null) return;   // InitializeComponent 중
        _kind = KindDeployments.IsChecked == true ? K8sKind.Deployments
            : KindServices.IsChecked == true ? K8sKind.Services
            : KindNodes.IsChecked == true ? K8sKind.Nodes : K8sKind.Pods;
        NamespaceBox.IsEnabled = K8sResources.Namespaced(_kind);
        UpdateHeaders();
        ReloadForNewView();
    }

    private void OnNamespaceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingNamespaces || NamespaceBox.SelectedItem is not string s) return;
        var ns = NamespaceBox.SelectedIndex == 0 ? null : s;
        if (ns == _namespace) return;
        _namespace = ns;
        ReloadForNewView();
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (ItemList != null) ApplyFilter(rebuild: true);
    }
}
