using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Nubis;

/// <summary>리소스 패널의 행 (상태 색·이름 표시)</summary>
public sealed class K8sItemRow
{
    public required K8sItem Item { get; init; }
    public required string DisplayName { get; init; }
    public required Brush StatusBrush { get; init; }
    public string ToolTipText => Item.ToolTipText;
}

/// <summary>
/// 클러스터 탭 왼쪽 리소스 패널: 네임스페이스 · 종류(파드 · 디플로이먼트 · 서비스 · 노드) 목록과 우클릭 동작.
/// 셸·로그는 새 탭으로, describe·YAML 은 오른쪽 kubectl 터미널에 실행한다 (이벤트로 탭에 맡긴다)
/// </summary>
public partial class K8sResourcePanel : UserControl
{
    private readonly K8sCluster _cluster;
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private List<K8sItem> _items = new();
    private K8sKind _kind = K8sKind.Pods;
    private bool _busy, _auto = true, _loadedOnce, _settingNamespaces;
    private string? _namespace;   // null = 모든 네임스페이스

    /// <summary>파드 셸 (파드, 컨테이너)</summary>
    public event Action<K8sItem, string?>? ShellRequested;
    /// <summary>파드 로그 (파드, 컨테이너, 이전 컨테이너)</summary>
    public event Action<K8sItem, string?, bool>? LogsRequested;
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
        UpdateHeaders();
        UpdateAutoButton();
        _timer.Tick += (_, _) => { if (_auto && IsVisible && !_busy) _ = LoadItemsAsync(quiet: true); };
        Loaded += (_, _) =>
        {
            _timer.Start();
            if (_loadedOnce) return;
            _loadedOnce = true;
            _ = LoadNamespacesAsync();
            _ = LoadItemsAsync(quiet: false);
        };
        Unloaded += (_, _) => _timer.Stop();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.F5) { _ = LoadItemsAsync(quiet: false); e.Handled = true; } };
    }

    /// <summary>탭을 닫을 때 진행 중인 kubectl 을 멈춘다</summary>
    public void Shutdown()
    {
        _timer.Stop();
        try { _cts.Cancel(); } catch { }
    }

    private Task<Kubectl.Result> RunAsync(IReadOnlyList<string> args) =>
        Kubectl.RunAsync(_cluster, args, TimeSpan.FromSeconds(30), _cts.Token, maxOutput: 8_000_000);

    // ----- 불러오기 -----

    private async Task LoadNamespacesAsync()
    {
        try
        {
            var r = await RunAsync(new[] { "get", "namespaces", "-o", "json", "--request-timeout=20s" });
            if (r.ExitCode != 0) { SetNamespaces(new List<string>()); return; }   // 권한이 없으면 기본 네임스페이스만
            SetNamespaces(K8sResources.ParseNamespaces(r.Output));
        }
        catch (OperationCanceledException) { }
        catch { SetNamespaces(new List<string>()); }
    }

    private void SetNamespaces(List<string> names)
    {
        _settingNamespaces = true;
        try
        {
            var current = _namespace;
            if (current != null && !names.Contains(current)) names.Insert(0, current);
            var items = new List<string> { L.T("모든 네임스페이스") };
            items.AddRange(names);
            NamespaceBox.ItemsSource = items;
            NamespaceBox.SelectedIndex = current == null ? 0 : items.IndexOf(current);
        }
        finally { _settingNamespaces = false; }
    }

    private bool _reloadPending;

    private async Task LoadItemsAsync(bool quiet)
    {
        // 불러오는 중에 종류·네임스페이스를 바꾸면 끝난 뒤 한 번 더 (자동 새로고침은 건너뛰어도 된다)
        if (_busy) { if (!quiet) _reloadPending = true; return; }
        _busy = true;
        var kind = _kind;
        var ns = _namespace;
        if (!quiet) StatusText.Text = L.T("불러오는 중...");
        try
        {
            var r = await RunAsync(K8sResources.ListArgs(kind, ns));
            if (kind != _kind || ns != _namespace) return;   // 그 사이 종류·네임스페이스를 바꿨다
            if (r.TimedOut) { ShowError(L.T("응답이 없습니다 (시간 초과).")); return; }
            if (r.ExitCode != 0) { ShowError(FirstLine(r.Error.Length > 0 ? r.Error : r.Output)); return; }
            _items = K8sResources.Parse(kind, r.Output, DateTime.UtcNow);
            ApplyFilter();
            var bad = _items.Count(i => !i.Healthy);
            StatusText.Text = L.F("{0}개 · {1:HH:mm:ss} 갱신", _items.Count, DateTime.Now) + (bad > 0 ? " · " + L.F("확인 필요 {0}개", bad) : "");
            StatusText.Foreground = (Brush)FindResource("TextTertiary");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(ex.Message); }
        finally
        {
            _busy = false;
            if (_reloadPending && !_cts.IsCancellationRequested)
            {
                _reloadPending = false;
                _ = LoadItemsAsync(quiet: false);
            }
        }
    }

    private static string FirstLine(string s)
    {
        var line = s.Trim().Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
        return line.Length > 300 ? line[..300] + "…" : line;
    }

    private void ShowError(string message)
    {
        StatusText.Text = message;
        StatusText.Foreground = (Brush)FindResource("Danger");
        StatusText.ToolTip = message;
        if (_items.Count == 0)
        {
            EmptyText.Text = message;
            EmptyText.Visibility = Visibility.Visible;
        }
    }

    private void ApplyFilter()
    {
        var q = SearchBox.Text.Trim();
        var all = _namespace == null;
        var ok = (Brush)FindResource("Success");
        var bad = (Brush)FindResource("Warning");
        var rows = _items
            .Where(i => q.Length == 0 || i.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || i.Namespace.Contains(q, StringComparison.OrdinalIgnoreCase))
            .Select(i => new K8sItemRow
            {
                Item = i,
                DisplayName = all && i.Namespace.Length > 0 ? $"{i.Namespace}/{i.Name}" : i.Name,
                StatusBrush = i.Healthy ? ok : bad,
            }).ToList();
        var selected = (ItemList.SelectedItem as K8sItemRow)?.Item;
        ItemList.ItemsSource = rows;
        if (selected != null)
            ItemList.SelectedItem = rows.FirstOrDefault(r => r.Item.Name == selected.Name && r.Item.Namespace == selected.Namespace);
        EmptyText.Text = _items.Count == 0 ? L.T("항목이 없습니다.") : L.T("찾는 이름이 없습니다.");
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
        AutoButton.ToolTip = _auto ? L.T("자동 새로고침 켜짐 (5초) — 누르면 끔") : L.T("자동 새로고침 꺼짐 — 누르면 켬");
    }

    // ----- 화면 동작 -----

    private void OnRefresh(object sender, RoutedEventArgs e) => _ = LoadItemsAsync(quiet: false);

    private void OnToggleAuto(object sender, RoutedEventArgs e)
    {
        _auto = !_auto;
        UpdateAutoButton();
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
        _items = new List<K8sItem>();
        ApplyFilter();
        EmptyText.Visibility = Visibility.Collapsed;
        _ = LoadItemsAsync(quiet: false);
    }

    private void OnNamespaceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingNamespaces || NamespaceBox.SelectedItem is not string s) return;
        _namespace = NamespaceBox.SelectedIndex == 0 ? null : s;
        _items = new List<K8sItem>();
        ApplyFilter();
        EmptyText.Visibility = Visibility.Collapsed;
        _ = LoadItemsAsync(quiet: false);
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (ItemList != null) ApplyFilter();
    }

    private K8sItem? Selected => (ItemList.SelectedItem as K8sItemRow)?.Item;

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement(ItemList, d) is ListBoxItem && Selected is { } item) DefaultAction(item);
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Selected is { } item) { DefaultAction(item); e.Handled = true; }
    }

    /// <summary>더블클릭·Enter: 파드는 셸, 그 밖은 describe</summary>
    private void DefaultAction(K8sItem item)
    {
        if (item.Kind == K8sKind.Pods) ShellRequested?.Invoke(item, item.Containers.FirstOrDefault());
        else Describe(item);
    }

    private static string NsArg(K8sItem i) => i.Namespace.Length > 0 ? $" -n {i.Namespace}" : "";

    private void Describe(K8sItem i) => TerminalCommand?.Invoke($"kubectl describe {i.Ref}{NsArg(i)}");

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        ItemMenu.Items.Clear();
        if (Selected is not { } item) { e.Handled = true; return; }
        MenuItem Item(string header, Action act, bool danger = false)
        {
            var m = new MenuItem { Header = MainWindow.MenuText(header) };
            if (danger) m.Foreground = (Brush)FindResource("Danger");
            m.Click += (_, _) => act();
            return m;
        }
        if (item.Kind == K8sKind.Pods)
        {
            var containers = item.Containers;
            if (containers.Count <= 1)
            {
                ItemMenu.Items.Add(Item(L.T("셸 열기"), () => ShellRequested?.Invoke(item, containers.FirstOrDefault())));
                ItemMenu.Items.Add(Item(L.T("로그 보기"), () => LogsRequested?.Invoke(item, containers.FirstOrDefault(), false)));
            }
            else
            {
                var shell = new MenuItem { Header = L.T("셸 열기") };
                var logs = new MenuItem { Header = L.T("로그 보기") };
                foreach (var c in containers)
                {
                    shell.Items.Add(Item(c, () => ShellRequested?.Invoke(item, c)));
                    logs.Items.Add(Item(c, () => LogsRequested?.Invoke(item, c, false)));
                }
                ItemMenu.Items.Add(shell);
                ItemMenu.Items.Add(logs);
            }
            ItemMenu.Items.Add(Item(L.T("이전 컨테이너 로그 (재시작 전)"), () => LogsRequested?.Invoke(item, containers.FirstOrDefault(), true)));
            ItemMenu.Items.Add(new Separator());
        }
        ItemMenu.Items.Add(Item("describe", () => Describe(item)));
        ItemMenu.Items.Add(Item(L.T("YAML 보기"), () => TerminalCommand?.Invoke($"kubectl get {item.Ref}{NsArg(item)} -o yaml")));
        ItemMenu.Items.Add(Item(L.T("이름 복사"), () => { try { Clipboard.SetText(item.Name); } catch { } }));
        if (item.Kind == K8sKind.Pods)
        {
            ItemMenu.Items.Add(new Separator());
            ItemMenu.Items.Add(Item(L.T("삭제 (컨트롤러가 다시 만듦)..."), () => _ = ConfirmRunAsync(item,
                L.F("파드 '{0}' 을(를) 삭제할까요? 디플로이먼트 등이 관리하는 파드면 새로 만들어집니다.", item.Name),
                new[] { "delete", "pod", item.Name, "-n", item.Namespace, "--wait=false" }), danger: true));
        }
        else if (item.Kind == K8sKind.Deployments)
        {
            ItemMenu.Items.Add(new Separator());
            ItemMenu.Items.Add(Item(L.T("다시 시작 (rollout restart)..."), () => _ = ConfirmRunAsync(item,
                L.F("디플로이먼트 '{0}' 의 파드를 차례로 다시 시작할까요?", item.Name),
                new[] { "rollout", "restart", "deployment/" + item.Name, "-n", item.Namespace }), danger: true));
        }
    }

    /// <summary>바꾸는 동작은 묻고 실행한 뒤 결과를 상태 줄에 보인다</summary>
    private async Task ConfirmRunAsync(K8sItem item, string question, string[] args)
    {
        if (Dialog.Show(Window.GetWindow(this), question + "\n\n" + L.F("클러스터: {0}", _cluster.Name), "Kubernetes",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            var r = await RunAsync(args);
            if (r.ExitCode != 0) { ShowError(FirstLine(r.Error.Length > 0 ? r.Error : r.Output)); return; }
            StatusText.Text = FirstLine(r.Output);
            StatusText.Foreground = (Brush)FindResource("TextTertiary");
            await Task.Delay(800);
            await LoadItemsAsync(quiet: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(ex.Message); }
    }
}
