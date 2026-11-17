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

    /// <summary>프로필들의 클러스터를 조회해 반영하고 결과를 알린다 (동시 4개, 3분 제한)</summary>
    private async Task FindClustersAsync(List<CloudProfile> profiles)
    {
        _k8sFinding = true;
        K8sCloudButton.IsEnabled = false;
        FlashHostStatus(L.F("클라우드 프로필 {0}개에서 클러스터를 찾는 중...", profiles.Count), ToastKind.Info);
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
                    await gate.WaitAsync(cts.Token);
                    try
                    {
                        var provider = CloudProviders.Get(p.Provider);
                        return (p, r: (ClusterFetchResult?)await Task.Run(() => provider.FetchClustersAsync(p, cts.Token)).WaitAsync(cts.Token), err: (string?)null);
                    }
                    finally { gate.Release(); }
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    return (p, r: (ClusterFetchResult?)null, err: (string?)L.T("시간 안에 끝나지 않음 (3분)"));
                }
                catch (Exception ex) { return (p, r: (ClusterFetchResult?)null, err: (string?)CloudProviders.FirstLine(ex.Message)); }
            }));

            var lines = new List<string>();
            int total = 0, added = 0;
            foreach (var (p, r, err) in results)
            {
                if (!_profiles.Contains(p)) continue;   // 찾는 동안 제거된 프로필
                var label = $"[{p.ProviderText}] {p.GroupName}";
                if (r == null) { lines.Add(L.F("{0} — 실패: {1}", label, err ?? "")); continue; }
                var (a, _, _) = ApplyClusters(p, r);
                total += r.Items.Count;
                added += a;
                lines.Add(L.F("{0} — 클러스터 {1}개 (새로 {2}개)", label, r.Items.Count, a) +
                          (r.IsPartial ? " · " + L.F("일부 조회 실패 {0}건", r.Errors.Count) : ""));
            }
            SaveProfiles();
            RefreshK8s();
            var head = total == 0
                ? L.F("프로필 {0}개에서 클러스터를 찾지 못했습니다.", profiles.Count)
                : L.F("클러스터 {0}개를 찾았습니다 (새로 {1}개). 고른 프로필은 앞으로 동기화할 때 함께 갱신합니다.", total, added);
            Dialog.Show(this, head, "Kubernetes", MessageBoxButton.OK, MessageBoxImage.Information,
                lines.Count > 0 ? string.Join("\n", lines) : null);
        }
        catch (Exception ex)
        {
            // 저장 실패 등 — async void 처리기로 올라가 앱이 죽지 않게
            Dialog.Show(this, L.F("저장 실패: {0}", ex.Message), "Kubernetes", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _k8sFinding = false;
            K8sCloudButton.IsEnabled = _profiles.Count > 0;
        }
    }

    /// <summary>조회 결과를 저장소에 반영 (사본에 적용한 뒤 한 번에 저장 — 저장에 실패하면 그대로)</summary>
    private (int added, int updated, int removed) ApplyClusters(CloudProfile p, ClusterFetchResult r)
    {
        var list = K8sStore.All().Select(c => c.Clone()).ToList();
        var res = K8sSync.Apply(p, r, list);
        K8sStore.ReplaceAll(list);
        foreach (var c in list) if (c.CloudProfileId == p.Id && _k8sHealth.ContainsKey(c.Id) && c.IsStopped) _k8sHealth.Remove(c.Id);
        // 목록에서 빠진 클러스터의 연결 테스트 결과도 정리
        var ids = list.Select(c => c.Id).ToHashSet();
        foreach (var id in _k8sHealth.Keys.Where(id => !ids.Contains(id)).ToList()) _k8sHealth.Remove(id);
        if (K8sPage.Visibility == Visibility.Visible) RefreshK8s();
        return res;
    }

    /// <summary>
    /// VM 동기화 뒤 이 프로필의 클러스터도 갱신 (SyncClusters 가 켜진 프로필만). 결과·실패는 프로필 상태 줄에 붙일 한 조각 —
    /// 클러스터 조회가 실패해도 VM 동기화 결과에는 영향이 없다
    /// </summary>
    private async Task<string?> SyncProfileClustersAsync(CloudProfile p, CancellationToken ct)
    {
        try
        {
            var provider = CloudProviders.Get(p.Provider);
            var r = await Task.Run(() => provider.FetchClustersAsync(p, ct)).WaitAsync(ct);
            if (!_profiles.Contains(p)) return null;
            var (added, _, removed) = ApplyClusters(p, r);
            var parts = new List<string>();
            if (added + removed > 0) parts.Add(L.F("클러스터 추가 {0} · 제거 {1}", added, removed));
            if (r.HasUnexpectedErrors) parts.Add(L.F("클러스터 일부 조회 실패 {0}건 — 이번엔 삭제하지 않음", r.Errors.Count));
            return parts.Count > 0 ? string.Join(" · ", parts) : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return L.T("클러스터 조회 취소"); }
        catch (Exception ex) { return L.F("클러스터 조회 실패: {0}", CloudProviders.FirstLine(ex.Message)); }
    }
}
