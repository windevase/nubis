using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Nubis;

public partial class MainWindow
{
    // =================== Kubernetes: 클라우드에서 클러스터 찾기 · 동기화 ===================

    private bool _k8sFinding;

    /// <summary>
    /// 고른 클라우드 프로필로 클러스터를 찾아 목록에 반영한다 (조회 API 만, 리전은 지정하지 않고 프로바이더가 전 리전을 본다).
    /// 고른 프로필은 SyncClusters 를 켜 다음부터 VM 동기화 때 함께 갱신되고, 고르지 않은 프로필은 끈다.
    /// 고르지 않은 프로필의 클러스터 카드는 사용자가 요청하고 확인했을 때만 지운다
    /// </summary>
    private async void OnK8sCloudFind(object sender, RoutedEventArgs e)
    {
        if (_k8sFinding) return;
        if (_profiles.Count == 0)
        {
            Dialog.Show(this, L.T("등록한 클라우드 프로필이 없습니다. Cloud 페이지에서 프로필을 먼저 추가하세요."), "Kubernetes",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var choice = K8sProfilePickDialog.Ask(this, _profiles.ToList(), K8sStore.All());
        if (choice == null) return;
        var plan = K8sProfilePick.Make(_profiles.ToList(), choice.Selected, K8sStore.All(), choice.RemoveUnselected);
        if (plan.Remove.Count > 0 &&
            Dialog.Show(this, L.F("선택 해제한 프로필의 클러스터 {0}개를 목록에서 지울까요? 저장한 인증도 함께 지워집니다. 클러스터 자체에는 영향이 없습니다.", plan.Remove.Count),
                "Kubernetes", MessageBoxButton.YesNo, MessageBoxImage.Warning, string.Join("\n", plan.Remove.Select(c => c.Name))) != MessageBoxResult.Yes)
            return;
        ApplyUnselected(plan);
        if (plan.Fetch.Count == 0)
        {
            RefreshK8s();
            FlashHostStatus(plan.Remove.Count > 0
                ? L.F("클러스터 동기화를 끄고 클러스터 {0}개를 목록에서 지웠습니다.", plan.Remove.Count)
                : L.T("고른 프로필이 없어 클러스터 동기화만 껐습니다."), ToastKind.Info);
            return;
        }
        await FindClustersAsync(plan.Fetch);
    }

    /// <summary>고르지 않은 프로필: 클러스터 동기화를 끄고, 요청했으면 그 클러스터 카드를 지운다</summary>
    private void ApplyUnselected(K8sProfilePick.Plan plan)
    {
        foreach (var p in plan.Disable) p.SyncClusters = false;
        if (plan.Disable.Count > 0) SaveProfiles();
        if (plan.Remove.Count == 0) return;
        var gone = plan.Remove.Select(c => c.Id).ToHashSet();
        try { K8sStore.ReplaceAll(K8sStore.All().Where(c => !gone.Contains(c.Id)).Select(c => c.Clone()).ToList()); }
        catch (Exception ex) { Dialog.Show(this, L.F("저장 실패: {0}", ex.Message), "Kubernetes", MessageBoxButton.OK, MessageBoxImage.Error); return; }
        foreach (var id in gone) _k8sHealth.Remove(id);
    }

    /// <summary>
    /// 클라우드에서 찾기가 조회 중인 프로필 (UI 스레드에서만). VM 동기화의 클러스터 갱신(SyncProfileClustersAsync)은
    /// 이 프로필을 건너뛴다 — 같은 프로필을 두 조회가 겹쳐 반영하면 MissCount 가 한 번에 2 가 되어 바로 지워지고,
    /// 먼저 시작해 늦게 끝난 결과가 새 결과를 덮기 때문
    /// </summary>
    private readonly HashSet<Guid> _k8sFindBusy = new();

    /// <summary>프로필들의 클러스터를 조회해 반영하고 결과를 알린다 (동시 4개, 3분 제한, 저장은 한 번)</summary>
    private async Task FindClustersAsync(List<CloudProfile> profiles)
    {
        _k8sFinding = true;
        K8sCloudButton.IsEnabled = false;
        FlashHostStatus(L.F("클라우드 프로필 {0}개에서 클러스터를 찾는 중...", profiles.Count), ToastKind.Info);
        foreach (var p in profiles) _k8sFindBusy.Add(p.Id);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            using var gate = new SemaphoreSlim(4);
            // 사용자가 고른 프로필은 조회 결과와 상관없이(로그인 만료·시간 초과 등) 앞으로 동기화할 때 함께 갱신한다
            foreach (var p in profiles) p.SyncClusters = true;
            SaveProfiles();
            // 프로필마다 따로 실패·시간 초과를 받는다 — 한 프로필이 늦어도 끝난 프로필 결과는 반영
            var results = await Task.WhenAll(profiles.Select(async p =>
            {
                try
                {
                    // 이미 VM 동기화 중인 프로필은 그 동기화(클러스터 갱신 포함)가 끝난 뒤 조회한다 — 결과가 겹쳐 반영되지 않게.
                    // 표시한 뒤 시작한 VM 동기화는 클러스터 갱신을 건너뛴다 (_k8sFindBusy)
                    if (_syncTasks.TryGetValue(p, out var vmSync))
                    {
                        try { await vmSync.WaitAsync(cts.Token); }
                        catch (Exception ex) when (ex is not OperationCanceledException || !cts.IsCancellationRequested) { }
                    }
                    await gate.WaitAsync(cts.Token);
                    try
                    {
                        var provider = CloudProviders.Get(p.Provider);
                        var fetch = Task.Run(() => provider.FetchClustersAsync(p, cts.Token));
                        try { return (p, r: (ClusterFetchResult?)await fetch.WaitAsync(cts.Token), err: (string?)null); }
                        catch (OperationCanceledException) when (cts.IsCancellationRequested)
                        {
                            // 토큰을 무시하는 SDK 호출이 늦게 실패해도 관찰되지 않은 예외로 남지 않게
                            _ = fetch.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                            throw;
                        }
                    }
                    finally { gate.Release(); }
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    return (p, r: (ClusterFetchResult?)null, err: (string?)L.T("시간 안에 끝나지 않음 (3분)"));
                }
                catch (Exception ex) { return (p, r: (ClusterFetchResult?)null, err: (string?)CloudProviders.FirstLine(ex.Message)); }
            }));

            // 결과를 사본 하나에 모아 한 번에 저장한다 (프로필마다 전체 복사 · DPAPI 저장 · 다시 그리기를 하지 않게)
            var list = K8sStore.All().Select(c => c.Clone()).ToList();
            var lines = new List<string>();
            var applied = new List<CloudProfile>();
            int total = 0, added = 0;
            foreach (var (p, r, err) in results)
            {
                if (!_profiles.Contains(p)) continue;   // 찾는 동안 제거된 프로필
                var label = $"[{p.ProviderText}] {p.GroupName}";
                if (r == null) { lines.Add(L.F("{0} — 실패: {1}", label, err ?? "")); continue; }
                int a;
                try { (a, _, _) = K8sSync.Apply(p, r, list); }
                catch (Exception ex) { lines.Add(L.F("{0} — 실패: {1}", label, CloudProviders.FirstLine(ex.Message))); continue; }
                applied.Add(p);
                total += r.Items.Count;
                added += a;
                lines.Add(L.F("{0} — 클러스터 {1}개 (새로 {2}개)", label, r.Items.Count, a) +
                          (r.IsPartial ? " · " + L.F("일부 조회 실패 {0}건", r.Errors.Count) : ""));
            }
            if (applied.Count > 0)
            {
                try { CommitClusters(list, applied); }
                catch (Exception ex)
                {
                    Dialog.Show(this, L.F("클러스터 목록을 저장하지 못했습니다: {0}", ex.Message), "Kubernetes", MessageBoxButton.OK, MessageBoxImage.Error,
                        string.Join("\n", lines));
                    return;
                }
            }
            var head = total == 0
                ? L.F("프로필 {0}개에서 클러스터를 찾지 못했습니다.", profiles.Count)
                : L.F("클러스터 {0}개를 찾았습니다 (새로 {1}개). 고른 프로필은 앞으로 동기화할 때 함께 갱신합니다.", total, added);
            Dialog.Show(this, head, "Kubernetes", MessageBoxButton.OK, MessageBoxImage.Information,
                lines.Count > 0 ? string.Join("\n", lines) : null);
        }
        catch (Exception ex)
        {
            // 예상 못 한 오류 — async void 처리기로 올라가 앱이 죽지 않게
            Dialog.Show(this, L.F("클러스터 찾기 실패: {0}", CloudProviders.FirstLine(ex.Message)), "Kubernetes", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            foreach (var p in profiles) _k8sFindBusy.Remove(p.Id);
            // MissCount · SyncClusters 가 바뀌었으므로 저장 실패·중간 오류에도 프로필은 저장
            SaveProfiles();   // 실패는 SaveProfiles 가 알린다
            _k8sFinding = false;
            RefreshK8s();
        }
    }

    /// <summary>바뀐 클러스터 목록을 저장하고 연결 테스트 결과를 정리한다. 저장에 실패하면 예외 (저장소는 그대로)</summary>
    private void CommitClusters(List<K8sCluster> list, IReadOnlyCollection<CloudProfile> profiles)
    {
        K8sStore.ReplaceAll(list);
        var ids = profiles.Select(p => p.Id).ToHashSet();
        foreach (var c in list)
            if (c.CloudProfileId is { } pid && ids.Contains(pid) && c.IsStopped) _k8sHealth.Remove(c.Id);
        // 목록에서 빠진 클러스터의 연결 테스트 결과도 정리
        var alive = list.Select(c => c.Id).ToHashSet();
        foreach (var id in _k8sHealth.Keys.Where(id => !alive.Contains(id)).ToList()) _k8sHealth.Remove(id);
    }

    /// <summary>
    /// VM 동기화 뒤 이 프로필의 클러스터도 갱신 (SyncClusters 가 켜진 프로필만). 결과·실패는 프로필 상태 줄에 붙일 한 조각 —
    /// 클러스터 조회가 실패해도 VM 동기화 결과에는 영향이 없다
    /// </summary>
    private async Task<string?> SyncProfileClustersAsync(CloudProfile p, CancellationToken ct)
    {
        // 클라우드에서 찾기가 이 프로필을 조회 중 — 그쪽 결과가 반영되므로 겹쳐 반영하지 않는다
        if (_k8sFindBusy.Contains(p.Id)) return L.T("클러스터는 클라우드에서 찾기 중이라 이번엔 건너뜀");
        ClusterFetchResult r;
        Task<ClusterFetchResult>? fetch = null;
        try
        {
            var provider = CloudProviders.Get(p.Provider);
            fetch = Task.Run(() => provider.FetchClustersAsync(p, ct));
            r = await fetch.WaitAsync(ct);
            // 조회가 끝나 반영을 기다리는 사이에 취소가 와도 결과를 버린다 (VM 동기화와 같은 규칙)
            ct.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _ = fetch?.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            return L.T("클러스터 조회 취소");
        }
        catch (Exception ex) { return L.F("클러스터 조회 실패: {0}", CloudProviders.FirstLine(ex.Message)); }

        // 조회하는 동안 프로필이 제거됐거나 클러스터 동기화가 꺼졌으면 버린다
        if (!_profiles.Contains(p) || !p.SyncClusters) return null;
        // 클라우드에서 찾기가 시작됐으면 그쪽이 새 결과를 반영한다
        if (_k8sFindBusy.Contains(p.Id)) return L.T("클러스터는 클라우드에서 찾기 중이라 이번엔 건너뜀");
        int added, removed;
        try
        {
            var list = K8sStore.All().Select(c => c.Clone()).ToList();
            (added, _, removed) = K8sSync.Apply(p, r, list);
            CommitClusters(list, new[] { p });
        }
        catch (Exception ex) { return L.F("클러스터 목록 저장 실패: {0}", CloudProviders.FirstLine(ex.Message)); }
        if (K8sPage.Visibility == Visibility.Visible) RefreshK8s();
        var parts = new List<string>();
        if (added + removed > 0) parts.Add(L.F("클러스터 추가 {0} · 제거 {1}", added, removed));
        if (r.HasUnexpectedErrors) parts.Add(L.F("클러스터 일부 조회 실패 {0}건 — 이번엔 삭제하지 않음", r.Errors.Count));
        return parts.Count > 0 ? string.Join(" · ", parts) : null;
    }
}