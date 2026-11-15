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
    /// 등록한 모든 클라우드 프로필로 클러스터를 찾아 목록에 반영한다 (조회 API 만).
    /// 클러스터가 있는 프로필은 SyncClusters 를 켜 다음부터 VM 동기화 때 함께 갱신된다
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
        _k8sFinding = true;
        K8sCloudButton.IsEnabled = false;
        FlashHostStatus(L.T("클라우드 프로필에서 클러스터를 찾는 중..."), ToastKind.Info);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var profiles = _profiles.ToList();
            using var gate = new SemaphoreSlim(4);
            var results = await Task.WhenAll(profiles.Select(async p =>
            {
                await gate.WaitAsync(cts.Token);
                try
                {
                    var provider = CloudProviders.Get(p.Provider);
                    return (p, r: await Task.Run(() => provider.FetchClustersAsync(p, cts.Token)), err: (string?)null);
                }
                catch (Exception ex) { return (p, r: (ClusterFetchResult?)null, err: (string?)CloudProviders.FirstLine(ex.Message)); }
                finally { gate.Release(); }
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
                if (r.Items.Count > 0 && !p.SyncClusters) p.SyncClusters = true;
                if (r.Items.Count > 0 || r.IsPartial)
                    lines.Add(L.F("{0} — 클러스터 {1}개 (새로 {2}개)", label, r.Items.Count, a) +
                              (r.IsPartial ? " · " + L.F("일부 조회 실패 {0}건", r.Errors.Count) : ""));
            }
            SaveProfiles();
            RefreshK8s();
            var head = total == 0
                ? L.F("프로필 {0}개에서 클러스터를 찾지 못했습니다.", profiles.Count)
                : L.F("클러스터 {0}개를 찾았습니다 (새로 {1}개). 클러스터가 있는 프로필은 앞으로 동기화할 때 함께 갱신합니다.", total, added);
            Dialog.Show(this, head, "Kubernetes", MessageBoxButton.OK, MessageBoxImage.Information,
                lines.Count > 0 ? string.Join("\n", lines) : null);
        }
        catch (OperationCanceledException)
        {
            Dialog.Show(this, L.T("클러스터 찾기가 시간 안에 끝나지 않았습니다."), "Kubernetes", MessageBoxButton.OK, MessageBoxImage.Warning);
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
