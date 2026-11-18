using System;
using System.Collections.Generic;
using System.Linq;

namespace Nubis;

/// <summary>
/// Kubernetes 페이지 "클라우드에서 찾기" 의 프로필 선택 판단 (화면 무관, K8sProfilePickTests).
/// 고른 프로필만 클러스터를 조회하고 동기화를 켠다. 고르지 않은 프로필은 동기화를 끄고,
/// 그 프로필의 클러스터 카드는 사용자가 따로 요청했을 때만 지운다
/// </summary>
public static class K8sProfilePick
{
    /// <summary>계획: 조회할 프로필 · 동기화를 끌 프로필 · 목록에서 지울 클러스터</summary>
    public sealed record Plan(List<CloudProfile> Fetch, List<CloudProfile> Disable, List<K8sCluster> Remove);

    /// <summary>
    /// 처음 체크할 프로필. 클러스터 동기화가 켜진 프로필이 있으면 그것만 (지난번 선택 유지),
    /// 하나도 없으면(처음 쓰는 경우) 전부 — 어느 계정에 클러스터가 있는지 아직 모르므로 전체를 훑고, 필요 없는 건 사용자가 끈다
    /// </summary>
    public static HashSet<Guid> DefaultChecked(IEnumerable<CloudProfile> profiles)
    {
        var list = profiles.ToList();
        var on = list.Where(p => p.SyncClusters).Select(p => p.Id).ToHashSet();
        return on.Count > 0 ? on : list.Select(p => p.Id).ToHashSet();
    }

    /// <summary>프로필별 등록된 클라우드 클러스터 수</summary>
    public static Dictionary<Guid, int> ClusterCounts(IEnumerable<K8sCluster> clusters) =>
        clusters.Where(c => c.CloudProfileId != null)
            .GroupBy(c => c.CloudProfileId!.Value)
            .ToDictionary(g => g.Key, g => g.Count());

    /// <summary>
    /// 선택 결과 → 계획. 고르지 않은 프로필 중 동기화가 켜져 있던 것만 끈다.
    /// removeUnselected 이면 고르지 않은 프로필(목록에 있는 프로필만)의 클라우드 클러스터를 지울 대상으로 모은다
    /// </summary>
    public static Plan Make(IEnumerable<CloudProfile> profiles, IReadOnlySet<Guid> selected, IEnumerable<K8sCluster> clusters, bool removeUnselected)
    {
        var list = profiles.GroupBy(p => p.Id).Select(g => g.First()).ToList();
        var fetch = list.Where(p => selected.Contains(p.Id)).ToList();
        var unselected = list.Where(p => !selected.Contains(p.Id)).ToList();
        var disable = unselected.Where(p => p.SyncClusters).ToList();
        var remove = new List<K8sCluster>();
        if (removeUnselected)
        {
            var ids = unselected.Select(p => p.Id).ToHashSet();
            remove = clusters.Where(c => c.CloudProfileId is { } pid && ids.Contains(pid)).ToList();
        }
        return new Plan(fetch, disable, remove);
    }
}
