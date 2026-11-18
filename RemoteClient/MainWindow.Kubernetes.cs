using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace Nubis;

/// <summary>Kubernetes 페이지의 클러스터 행</summary>
public sealed class K8sClusterRow
{
    public required K8sCluster Cluster { get; init; }
    public string Name => Cluster.Name;
    public string AuthBadge => Cluster.AuthBadge;
    public string Version => Cluster.Version;
    public Visibility VersionVisibility => Cluster.Version.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public required string GroupLabel { get; init; }
    public required string Detail { get; init; }
    public string Warning { get; init; } = "";
    public string WarningTip { get; init; } = "";
    public Visibility WarningVisibility => Warning.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public required Brush StateBrush { get; init; }
}

public partial class MainWindow
{
    // =================== Kubernetes 페이지 ===================

    /// <summary>이번 실행에서 연결 테스트한 결과 (행의 상태 점 색)</summary>
    private readonly Dictionary<Guid, bool> _k8sHealth = new();

    private void RefreshK8s()
    {
        UpdateKubectlBar();
        var profiles = _profiles.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
        string Group(K8sCluster c) =>
            c.CloudProfileId is { } pid && profiles.TryGetValue(pid, out var p) ? $"[{p.ProviderText}] {p.GroupName}"
            : c.Group.Length > 0 ? c.Group : L.T("직접 등록");
        var all = K8sStore.All();
        // 지워진 클러스터의 인증 확인 캐시·연결 상태는 버린다 (프로필 제거·동기화로 사라진 것 포함)
        var ids = all.Select(c => c.Id).ToHashSet();
        foreach (var id in _k8sAuthCache.Keys.Where(id => !ids.Contains(id)).ToList()) _k8sAuthCache.Remove(id);
        foreach (var id in _k8sHealth.Keys.Where(id => !ids.Contains(id)).ToList()) _k8sHealth.Remove(id);
        var rows = all
            .OrderBy(c => c.IsCloud).ThenBy(Group, StringComparer.CurrentCultureIgnoreCase).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => MakeK8sRow(c, Group(c))).ToList();
        var view = new ListCollectionView(rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(K8sClusterRow.GroupLabel)));
        K8sList.ItemsSource = view;
        K8sEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // 찾는 중에 다시 그려도(MCP 테스트·동기화 등) 버튼이 다시 켜지지 않게
        K8sCloudButton.IsEnabled = _profiles.Count > 0 && !_k8sFinding;
    }

    /// <summary>Kubernetes 페이지가 보일 때만 다시 그린다 (MCP·동기화 경로 — 페이지를 열 때 RefreshK8s 가 다시 그린다)</summary>
    private void RefreshK8sIfVisible()
    {
        if (K8sPage.Visibility == Visibility.Visible) RefreshK8s();
    }

    /// <summary>
    /// 클러스터별 인증 확인 결과 캐시 (암호문이 같으면 다시 풀지 않는다): 풀렸는지 + exec 명령.
    /// 카드를 다시 그릴 때마다 카드당 DPAPI 해제·YAML 파싱을 UI 스레드에서 하지 않게
    /// </summary>
    private readonly Dictionary<Guid, (string? Blob, bool Ok, string? ExecCommand)> _k8sAuthCache = new();

    private (bool Ok, string? ExecCommand) K8sAuthInfo(K8sCluster c)
    {
        if (_k8sAuthCache.TryGetValue(c.Id, out var hit) && hit.Blob == c.ProtectedKubeconfig) return (hit.Ok, hit.ExecCommand);
        bool ok = true;
        string? cmd = null;
        if (!string.IsNullOrEmpty(c.ProtectedKubeconfig))
        {
            ok = Secret.TryUnprotect(c.ProtectedKubeconfig, out var yaml);
            if (ok)
            {
                try { cmd = Kubeconfig.ExecCommand(yaml); }
                catch (FormatException) { }
            }
        }
        _k8sAuthCache[c.Id] = (c.ProtectedKubeconfig, ok, cmd);
        return (ok, cmd);
    }

    private K8sClusterRow MakeK8sRow(K8sCluster c, string group)
    {
        string warning = "", tip = "";
        var (authOk, execCmd) = K8sAuthInfo(c);
        if (!authOk)
        {
            warning = L.T("인증을 풀 수 없음");
            tip = L.T("다른 PC · Windows 계정에서 저장한 인증이라 이 PC 에서 풀리지 않습니다. 다시 가져오세요.");
        }
        else if (c.IsStopped)
        {
            warning = c.State ?? "";
            tip = L.T("클라우드에서 실행 중이 아닌 클러스터입니다.");
        }
        else if (execCmd is { } cmd && !Kubectl.CommandExists(cmd))
        {
            warning = L.F("{0} 없음", Path.GetFileName(cmd));
            tip = L.F("이 클러스터 인증에는 '{0}' 명령이 필요합니다. 설치한 뒤 PATH 에 넣으세요.", cmd) + "\n" + K8sPluginHint(c.AuthKind);
        }
        var parts = new List<string>();
        if (c.Endpoint.Length > 0) parts.Add(c.Endpoint);
        if (!string.IsNullOrEmpty(c.Region)) parts.Add(c.Region);
        parts.Add(L.F("네임스페이스 {0}", c.NamespaceOrDefault));
        if (c.LastConnected is { } last) parts.Add(L.F("최근 {0:yyyy-MM-dd HH:mm}", last));
        Brush state = _k8sHealth.TryGetValue(c.Id, out var ok) ? (Brush)FindResource(ok ? "Success" : "Danger")
            : c.IsStopped ? (Brush)FindResource("Warning")
            : (Brush)FindResource("TextTertiary");
        return new K8sClusterRow { Cluster = c, GroupLabel = group, Detail = string.Join("  ·  ", parts), Warning = warning, WarningTip = tip, StateBrush = state };
    }

    /// <summary>인증 플러그인 설치 안내 한 줄</summary>
    private static string K8sPluginHint(K8sAuthKind kind) => kind switch
    {
        K8sAuthKind.Aws => L.T("AWS CLI: winget install Amazon.AWSCLI"),
        K8sAuthKind.Gcp => L.T("gcloud components install gke-gcloud-auth-plugin"),
        K8sAuthKind.Azure => L.T("kubelogin: winget install Microsoft.Azure.Kubelogin"),
        K8sAuthKind.Oci => L.T("OCI CLI: pip install oci-cli"),
        _ => "",
    };

    private void UpdateKubectlBar()
    {
        var path = Kubectl.Locate(SettingsStore.Current.KubectlPath);
        if (path != null)
        {
            K8sKubectlIcon.Text = "";
            K8sKubectlIcon.Foreground = (Brush)FindResource("Success");
            K8sKubectlText.Text = L.F("kubectl: {0}", path);
            K8sKubectlText.ToolTip = path;
            K8sCopyInstall.Visibility = Visibility.Collapsed;
        }
        else
        {
            K8sKubectlIcon.Text = "";
            K8sKubectlIcon.Foreground = (Brush)FindResource("Warning");
            K8sKubectlText.Text = string.IsNullOrWhiteSpace(SettingsStore.Current.KubectlPath)
                ? L.T("kubectl 을 찾지 못했습니다. 설치하거나(winget install Kubernetes.kubectl) 경로를 지정하세요.")
                : L.F("지정한 kubectl 이 없습니다: {0}", SettingsStore.Current.KubectlPath);
            K8sKubectlText.ToolTip = K8sKubectlText.Text;
            K8sCopyInstall.Visibility = Visibility.Visible;
        }
    }

    private void OnK8sCopyInstall(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText("winget install Kubernetes.kubectl"); FlashHostStatus(L.T("설치 명령을 복사했습니다"), ToastKind.Info); } catch { }
    }

    private void OnK8sPickKubectl(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = L.T("kubectl.exe 선택"), Filter = "kubectl.exe|kubectl.exe|*.exe|*.exe" };
        if (dlg.ShowDialog(this) != true) return;
        SettingsStore.Update(s => s.KubectlPath = dlg.FileName);
        RefreshK8s();
    }

    private void OnK8sImport(object sender, RoutedEventArgs e)
    {
        if (KubeconfigImportDialog.Ask(this) is not { Count: > 0 } list) return;
        var (added, error) = AddK8sClusters(list);
        if (error != null) Dialog.Show(this, L.F("저장 실패: {0}", error), "Kubernetes", MessageBoxButton.OK, MessageBoxImage.Error);
        RefreshK8s();
        if (added > 0) FlashHostStatus(L.F("클러스터 {0}개를 추가했습니다", added), ToastKind.Success);
    }

    /// <summary>
    /// 가져온 클러스터를 저장한다 (UI 가져오기 · MCP import_k8s_clusters 공용). 하나씩 저장하다 실패하면 멈추고 (추가한 수, 오류)를 돌려준다
    /// </summary>
    private (int Added, string? Error) AddK8sClusters(IEnumerable<K8sCluster> list)
    {
        var added = 0;
        string? error = null;
        foreach (var c in list)
        {
            try { K8sStore.Upsert(c); added++; }
            catch (Exception ex) { error = ex.Message; break; }
        }
        RefreshK8sIfVisible();
        return (added, error);
    }

    private static K8sCluster? RowCluster(object sender) =>
        (sender as FrameworkElement)?.DataContext is K8sClusterRow r ? K8sStore.Get(r.Cluster.Id) : null;

    private void OnK8sRowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && RowCluster(sender) is { } c) { OpenK8sTab(c); e.Handled = true; }
        else (sender as UIElement)?.Focus();
    }

    private void OnK8sRowKeyDown(object sender, KeyEventArgs e)
    {
        if (RowCluster(sender) is not { } c) return;
        switch (e.Key)
        {
            case Key.Enter: OpenK8sTab(c); e.Handled = true; break;
            case Key.F2: RenameK8s(c); e.Handled = true; break;
            case Key.Delete: DeleteK8s(c); e.Handled = true; break;
        }
    }

    private void OnK8sOpen(object sender, RoutedEventArgs e)
    {
        if (RowCluster(sender) is { } c) OpenK8sTab(c);
    }

    private void OnK8sTest(object sender, RoutedEventArgs e)
    {
        if (RowCluster(sender) is { } c) _ = TestK8sAsync(c);
    }

    private void OnK8sMore(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || RowCluster(sender) is not { } c) return;
        var menu = new ContextMenu { PlacementTarget = fe, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        void Add(string header, Action act, bool danger = false)
        {
            var m = new MenuItem { Header = MenuText(header) };
            if (danger) m.Foreground = (Brush)FindResource("Danger");
            m.Click += (_, _) => act();
            menu.Items.Add(m);
        }
        Add(L.T("이름 바꾸기") + "  (F2)", () => RenameK8s(c));
        Add(L.T("기본 네임스페이스..."), () => SetK8sNamespace(c));
        if (!c.IsCloud) Add(L.T("그룹..."), () => SetK8sGroup(c));
        Add(L.T("kubeconfig 내보내기..."), () => ExportK8s(c));
        menu.Items.Add(new Separator());
        Add(L.T("삭제") + "  (Delete)", () => DeleteK8s(c), danger: true);
        menu.IsOpen = true;
    }

    /// <summary>바꾼 사본을 저장하고 목록을 다시 그린다. 실패하면 알리고 false</summary>
    private bool SaveK8s(K8sCluster copy)
    {
        try { K8sStore.Upsert(copy); RefreshK8s(); return true; }
        catch (Exception ex)
        {
            Dialog.Show(this, L.F("저장 실패: {0}", ex.Message), "Kubernetes", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    /// <summary>
    /// 대화상자를 닫은 뒤 바꾼 필드만 최신 저장본에 적용해 저장한다 — 대화상자가 열려 있는 동안 동기화가 갱신한
    /// 주소·상태·kubeconfig 를 되돌리거나, 그사이 지워진 클러스터를 되살리지 않게. 지워졌으면 알리고 저장하지 않는다
    /// </summary>
    private void UpdateK8s(Guid id, Action<K8sCluster> change)
    {
        switch (ChangeK8s(id, change, out var error))
        {
            case K8sChange.Gone:
                FlashHostStatus(L.T("그사이 목록에서 지워진 클러스터입니다."), ToastKind.Info);
                RefreshK8s();
                break;
            case K8sChange.Failed:
                Dialog.Show(this, L.F("저장 실패: {0}", error ?? ""), "Kubernetes", MessageBoxButton.OK, MessageBoxImage.Error);
                break;
        }
    }

    internal enum K8sChange { Saved, Gone, Failed }

    /// <summary>
    /// 바꾼 필드만 최신 저장본의 사본에 적용해 저장한다 (UI 이름·네임스페이스·그룹 · MCP update_k8s_cluster 공용, 알림은 부른 쪽이).
    /// 지워졌으면 Gone, 저장 실패면 Failed + 오류 (저장소는 그대로)
    /// </summary>
    private K8sChange ChangeK8s(Guid id, Action<K8sCluster> change, out string? error)
    {
        error = null;
        if (K8sStore.Get(id) is not { } cur) return K8sChange.Gone;
        var copy = cur.Clone();
        change(copy);
        try { K8sStore.Upsert(copy); }
        catch (Exception ex) { error = ex.Message; return K8sChange.Failed; }
        RefreshK8sIfVisible();
        return K8sChange.Saved;
    }

    private void RenameK8s(K8sCluster c)
    {
        if (PromptDialog.Ask(this, L.T("이름 바꾸기"), L.T("클러스터 이름"), c.Name) is not { } name) return;
        name = name.Trim();
        if (name.Length == 0 || name == c.Name) return;
        UpdateK8s(c.Id, x => x.Name = name);
    }

    private void SetK8sNamespace(K8sCluster c)
    {
        if (PromptDialog.Ask(this, L.T("기본 네임스페이스"), L.T("비우면 kubeconfig 의 값 (없으면 default)"), c.DefaultNamespace) is not { } ns) return;
        ns = ns.Trim();
        if (ns.Length > 0 && !K8sCluster.IsValidNamespace(ns))
        {
            Dialog.Show(this, L.T("네임스페이스는 소문자·숫자·하이픈(-)만 쓸 수 있습니다."), "Kubernetes", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        UpdateK8s(c.Id, x => x.DefaultNamespace = ns);
    }

    private void SetK8sGroup(K8sCluster c)
    {
        if (PromptDialog.Ask(this, L.T("그룹"), L.T("비우면 그룹 없음"), c.Group) is not { } g) return;
        var group = g.Trim();
        UpdateK8s(c.Id, x => x.Group = group);
    }

    private void ExportK8s(K8sCluster c)
    {
        if (Dialog.Show(this, L.T("내보낸 파일에는 이 클러스터의 인증(토큰 · 클라이언트 키)이 암호화되지 않은 채 들어갑니다. 안전한 곳에 저장하세요."),
                L.T("kubeconfig 내보내기"), MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        var safe = string.Concat(c.Name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        var dlg = new Microsoft.Win32.SaveFileDialog { Title = L.T("kubeconfig 내보내기"), FileName = safe + ".kubeconfig", Filter = "kubeconfig|*.kubeconfig;*.yaml|*.*|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        // 창이 열려 있는 동안 동기화가 kubeconfig 를 바꿨을 수 있다 — 최신 저장본으로 (지워졌으면 중단)
        if (K8sStore.Get(c.Id) is not { } cur) { FlashHostStatus(L.T("그사이 목록에서 지워진 클러스터입니다."), ToastKind.Info); RefreshK8s(); return; }
        c = cur;
        try
        {
            File.WriteAllText(dlg.FileName, c.Kubeconfig, new System.Text.UTF8Encoding(false));
            FlashHostStatus(L.T("내보냈습니다"), ToastKind.Success);
        }
        catch (Exception ex) { Dialog.Show(this, ex.Message, L.T("kubeconfig 내보내기"), MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void DeleteK8s(K8sCluster c)
    {
        if (Dialog.Show(this, K8sDeleteMessage(c.Name, c.IsCloud), "Kubernetes", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (RemoveK8sCluster(c.Id) is { } error)
            Dialog.Show(this, L.F("저장 실패: {0}", error), "Kubernetes", MessageBoxButton.OK, MessageBoxImage.Error);
        RefreshK8s();
    }

    /// <summary>삭제 확인 문구 (UI 확인 창 · MCP delete_k8s_cluster 승인 창 공용)</summary>
    internal static string K8sDeleteMessage(string name, bool isCloud)
    {
        var msg = L.F("클러스터 '{0}' 을(를) 목록에서 지울까요? 저장한 인증도 함께 지워집니다. 클러스터 자체에는 영향이 없습니다.", name);
        if (isCloud) msg += "\n" + L.T("클라우드에서 가져온 클러스터는 다음 동기화 때 다시 나타날 수 있습니다.");
        return msg;
    }

    /// <summary>
    /// 클러스터를 저장소에서 지우고 연결 상태·인증 캐시를 정리한다 (UI 삭제 · MCP delete_k8s_cluster 공용). 저장 실패면 오류 문구 (클러스터는 남는다)
    /// </summary>
    private string? RemoveK8sCluster(Guid id)
    {
        string? error = null;
        try { K8sStore.Remove(id); }
        catch (Exception ex) { error = ex.Message; }
        if (K8sStore.Get(id) == null)
        {
            _k8sHealth.Remove(id);
            _k8sAuthCache.Remove(id);
        }
        RefreshK8sIfVisible();
        return error;
    }

    /// <summary>연결 테스트: 서버 버전 + 파드 목록 권한. 결과를 알리고 버전을 저장한다</summary>
    private async Task TestK8sAsync(K8sCluster c)
    {
        FlashHostStatus(L.F("'{0}' 연결 확인 중...", c.Name), ToastKind.Info);
        var r = await RunK8sTestAsync(c, CancellationToken.None);   // 상태 점·버전 반영과 다시 그리기는 안에서
        if (r.Threw) { Dialog.Show(this, r.Error, "Kubernetes", MessageBoxButton.OK, MessageBoxImage.Error); return; }
        if (!r.Ok)
        {
            Dialog.Show(this, L.F("'{0}' 에 연결하지 못했습니다.", c.Name), "Kubernetes", MessageBoxButton.OK, MessageBoxImage.Error, r.Error);
            return;
        }
        FlashHostStatus(L.F("'{0}' 연결됨 · 쿠버네티스 {1}", c.Name, r.Version ?? "") + (r.PodsOk ? "" : " · " + L.T("파드 목록 권한 없음")),
            r.PodsOk ? ToastKind.Success : ToastKind.Info);
    }

    /// <summary>연결 테스트 결과. Error 는 화면에 보일 오류 (kubectl 출력 최대 2000자), Threw 는 kubectl 실행 자체가 실패한 경우</summary>
    internal sealed record K8sTestResult(bool Ok, string? Version, bool PodsOk, string Error, bool TimedOut, bool Threw);

    /// <summary>
    /// 연결 테스트 핵심 (UI 연결 테스트 · MCP test_k8s_cluster 공용, UI 스레드에서): kubectl version + auth can-i list pods.
    /// 상태 점(_k8sHealth)과 저장된 Version 을 반영하고 페이지가 보이면 다시 그린다. 40초 제한.
    /// ct 로 취소하면 OperationCanceledException (상태 점은 바꾸지 않는다). 40초 제한에 걸리면 TimedOut 결과
    /// </summary>
    private async Task<K8sTestResult> RunK8sTestAsync(K8sCluster c, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(40));
        try
        {
            var v = await Kubectl.RunAsync(c, new[] { "version", "-o", "json", "--request-timeout=20s" }, TimeSpan.FromSeconds(30), cts.Token);
            var server = K8sVersion(v.Output);
            if (server == null)
            {
                _k8sHealth[c.Id] = false;
                RefreshK8sIfVisible();
                var err = (v.Error.Length > 0 ? v.Error : v.Output).Trim();
                return new(false, null, false, v.TimedOut ? L.T("응답이 없습니다 (시간 초과).") : err.Length > 2000 ? err[..2000] : err, v.TimedOut, false);
            }
            var can = await Kubectl.RunAsync(c, new[] { "auth", "can-i", "list", "pods", "--request-timeout=20s" }, TimeSpan.FromSeconds(30), cts.Token);
            _k8sHealth[c.Id] = true;
            if (K8sStore.Get(c.Id) is { } cur && cur.Version != server)
            {
                var copy = cur.Clone();
                copy.Version = server;
                try { K8sStore.Upsert(copy); } catch { }
            }
            RefreshK8sIfVisible();
            return new(true, server, can.Output.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase), "", false, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // 호출자가 아니라 40초 제한이 취소한 경우 — kubectl 이 그 사이 끝나지 않았다 (예외 원문 "A task was canceled." 대신 시간 초과로)
            _k8sHealth[c.Id] = false;
            RefreshK8sIfVisible();
            return new(false, null, false, L.T("응답이 없습니다 (시간 초과)."), true, false);
        }
        catch (Exception ex)
        {
            _k8sHealth[c.Id] = false;
            RefreshK8sIfVisible();
            return new(false, null, false, ex.Message, false, true);
        }
    }

    /// <summary>kubectl version -o json 의 serverVersion.gitVersion (없으면 null)</summary>
    internal static string? K8sVersion(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("serverVersion", out var s) && s.TryGetProperty("gitVersion", out var g) ? g.GetString() : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    // =================== 클러스터 탭 ===================

    /// <summary>클러스터 탭을 연다 (같은 클러스터 탭이 이미 있어도 새로 — 여러 터미널이 필요할 때가 있다)</summary>
    internal SessionTab? OpenK8sTab(K8sCluster c)
    {
        if (!EnsureK8sReady(c)) return null;
        K8sSessionView view;
        try { view = new K8sSessionView(c.Clone()); }
        catch (Exception ex) { Dialog.Show(this, ex.Message, "Kubernetes", MessageBoxButton.OK, MessageBoxImage.Error); return null; }
        view.ShellRequested += (cl, pod, container) => OpenK8sPodTab(cl, pod, container, logs: false, previous: false);
        view.LogsRequested += (cl, pod, container, prev) => OpenK8sPodTab(cl, pod, container, logs: true, previous: prev);
        var tab = AddTab(new SessionTab { Id = _nextSessionId++, Title = c.Name, Protocol = Protocol.Local, View = view, K8sClusterId = c.Id });
        if (K8sStore.Get(c.Id) is { } cur)
        {
            var copy = cur.Clone();
            copy.LastConnected = DateTime.Now;
            try { K8sStore.Upsert(copy); } catch { }
        }
        return tab;
    }

    /// <summary>파드 셸·로그 탭</summary>
    internal SessionTab? OpenK8sPodTab(K8sCluster c, K8sItem pod, string? container, bool logs, bool previous)
    {
        if (!EnsureK8sReady(c)) return null;
        try
        {
            var args = logs ? K8sTerminal.LogsArgs(c, pod, container, previous) : K8sTerminal.ShellArgs(c, pod, container);
            var hint = logs ? L.T("로그 보기가 끝났습니다.") : L.T("파드 셸이 종료되었습니다.");
            var view = K8sTerminal.PodView(c, args, hint);
            var title = logs ? L.F("{0} 로그", pod.Name) : pod.Name;
            return AddTab(new SessionTab { Id = _nextSessionId++, Title = title, Protocol = Protocol.Local, View = view, K8sClusterId = c.Id, K8sPod = true });
        }
        catch (Exception ex)
        {
            Dialog.Show(this, ex.Message, "Kubernetes", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
    }

    /// <summary>kubectl 이 있고 인증을 풀 수 있는지. 아니면 알리고 false</summary>
    private bool EnsureK8sReady(K8sCluster c)
    {
        if (Kubectl.Locate(SettingsStore.Current.KubectlPath) == null)
        {
            Dialog.Show(this, L.T("kubectl 을 찾지 못했습니다. 설치하거나(winget install Kubernetes.kubectl) Kubernetes 페이지에서 경로를 지정하세요."),
                "Kubernetes", MessageBoxButton.OK, MessageBoxImage.Warning);
            ShowPage(NavK8s);
            return false;
        }
        if (string.IsNullOrEmpty(c.Kubeconfig))
        {
            Dialog.Show(this, L.F("'{0}' 의 인증을 풀 수 없습니다. 다시 가져오세요.", c.Name), "Kubernetes", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        return true;
    }
}
