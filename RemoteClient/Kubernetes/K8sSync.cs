using System;
using System.Collections.Generic;
using System.Linq;

namespace Nubis;

/// <summary>
/// 클라우드에서 조회한 클러스터를 목록에 반영 (화면 무관, K8sSyncTests). VM 동기화(CloudSync)와 같은 원칙:
/// 일부 리전·범위를 못 읽었으면(권한 거부 리전 제외) 아무것도 지우지 않고, 두 번 연속 안 보여야 지운다.
/// 사용자가 바꾼 이름·기본 네임스페이스·태그·즐겨찾기는 유지하고, 주소·버전·상태·kubeconfig(인증 방식)만 갱신한다.
/// VM 호스트와 저장소가 달라 서로를 지우지 않는다.
/// </summary>
public static class K8sSync
{
    public const int MissGrace = 2;

    /// <returns>(추가, 갱신, 제거) 개수. clusters 목록을 직접 바꾼다</returns>
    public static (int added, int updated, int removed) Apply(CloudProfile p, ClusterFetchResult result, List<K8sCluster> clusters)
    {
        var mine = clusters.Where(c => c.CloudProfileId == p.Id).ToList();
        var byId = new Dictionary<string, K8sCluster>(StringComparer.Ordinal);
        var duplicates = new List<K8sCluster>();
        foreach (var c in mine)
        {
            if (c.CloudClusterId == null) continue;
            if (byId.ContainsKey(c.CloudClusterId)) duplicates.Add(c);
            else byId[c.CloudClusterId] = c;
        }

        int added = 0, updated = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cc in result.Items)
        {
            if (!seen.Add(cc.Id)) continue;   // 조회 결과에 같은 클러스터가 두 번 와도 한 번만
            if (!byId.TryGetValue(cc.Id, out var c))
            {
                // 같은 API 서버를 kubeconfig 로 이미 직접 가져왔으면 새로 만들지 않는다 (같은 클러스터 카드가 둘 생기지 않게)
                if (cc.Endpoint.Length > 0 && clusters.Any(x => !x.IsCloud && SameEndpoint(x.Endpoint, cc.Endpoint)))
                    continue;
                c = new K8sCluster
                {
                    Source = K8sSource.Cloud,
                    CloudProfileId = p.Id,
                    CloudClusterId = cc.Id,
                    Name = cc.Name,
                };
                clusters.Add(c);
                byId[cc.Id] = c;
                added++;
            }
            else updated++;
            // 사용자가 이름을 바꾸지 않았으면 클라우드 이름을 따른다
            if (string.IsNullOrWhiteSpace(c.Name) || c.CloudName == null || c.Name == c.CloudName) c.Name = cc.Name;
            c.CloudName = cc.Name;
            c.Region = cc.Region;
            c.Endpoint = cc.Endpoint;
            if (cc.Version.Length > 0) c.Version = cc.Version;
            c.State = CloudCluster.NormalizeState(cc.State);
            c.AuthKind = cc.AuthKind;
            c.ContextName = cc.ContextName;
            if (cc.Kubeconfig.Length > 0 && cc.Kubeconfig != c.Kubeconfig) c.Kubeconfig = cc.Kubeconfig;
            c.MissCount = 0;
        }

        var removed = 0;
        // 중복은 언제나 정리 (같은 클러스터 카드가 둘)
        foreach (var d in duplicates) { clusters.Remove(d); removed++; }
        if (!result.HasUnexpectedErrors)
        {
            var denied = new HashSet<string>(result.DeniedRegions, StringComparer.OrdinalIgnoreCase);
            foreach (var c in mine.Except(duplicates).ToList())
            {
                if (c.CloudClusterId == null || seen.Contains(c.CloudClusterId)) continue;
                // 권한 거부 리전·리전을 모르는 클러스터는 이번에 보지 못한 것 — 남긴다
                if (string.IsNullOrEmpty(c.Region) || denied.Contains(c.Region) || denied.Any(r => c.Region.StartsWith(r + "-", StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (++c.MissCount >= MissGrace) { clusters.Remove(c); removed++; }
            }
        }
        return (added, updated, removed);
    }

    /// <summary>
    /// API 서버 주소 비교용 정규화: scheme·호스트 소문자, 기본 포트(https 443 · http 80)를 붙이고 끝 '/' 를 뗀다 —
    /// `https://fqdn` 과 `https://fqdn:443/` 를 같게 본다. scheme 이 없으면 https 로 본다. 해석하지 못하면 다듬은 원문 (소문자)
    /// </summary>
    public static string NormalizeEndpoint(string? endpoint)
    {
        var s = (endpoint ?? "").Trim();
        if (s.Length == 0) return "";
        var withScheme = s.Contains("://", StringComparison.Ordinal) ? s : "https://" + s;
        if (Uri.TryCreate(withScheme, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp) && u.Host.Length > 0)
            return $"{u.Scheme}://{u.Host}:{u.Port}{u.AbsolutePath.TrimEnd('/')}".ToLowerInvariant();
        return s.TrimEnd('/').ToLowerInvariant();
    }

    /// <summary>같은 API 서버인지 (둘 다 비어 있으면 같다고 본다)</summary>
    public static bool SameEndpoint(string? a, string? b) => NormalizeEndpoint(a) == NormalizeEndpoint(b);

    /// <summary>
    /// kubeconfig 가져오기 중복 판정: 같은 context 이름 + 같은 API 서버(정규화 비교)인 클러스터가 이미 있으면 true.
    /// 주소가 빈 기존 클러스터는 가져올 context 도 주소가 없을 때만 같은 것으로 본다
    /// </summary>
    public static bool AlreadyImported(IEnumerable<K8sCluster> existing, KubeContext c) =>
        existing.Any(x => x.ContextName == c.Name && SameEndpoint(x.Endpoint, c.Server));
}
