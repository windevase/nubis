using System;
using System.Collections.Generic;
using System.Linq;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>Kubernetes "클라우드에서 찾기" 프로필 선택: 기본 체크 · 해제한 프로필 처리 · 지울 클러스터</summary>
public class K8sProfilePickTests
{
    private static K8sCluster Cloud(CloudProfile p, string id) =>
        new() { Source = K8sSource.Cloud, CloudProfileId = p.Id, CloudClusterId = id, Name = id };

    [Fact]
    public void 동기화가_켜진_프로필이_있으면_그것만_기본_체크()
    {
        var a = new CloudProfile { SyncClusters = true };
        var b = new CloudProfile();
        var c = new CloudProfile { SyncClusters = true };
        Assert.Equal(new[] { a.Id, c.Id }.ToHashSet(), K8sProfilePick.DefaultChecked(new[] { a, b, c }));
    }

    [Fact]
    public void 켜진_프로필이_없으면_전부_기본_체크()
    {
        var a = new CloudProfile();
        var b = new CloudProfile();
        Assert.Equal(new[] { a.Id, b.Id }.ToHashSet(), K8sProfilePick.DefaultChecked(new[] { a, b }));
        Assert.Empty(K8sProfilePick.DefaultChecked(Array.Empty<CloudProfile>()));
    }

    [Fact]
    public void 프로필별_클러스터_수는_클라우드_클러스터만_센다()
    {
        var a = new CloudProfile();
        var list = new List<K8sCluster> { Cloud(a, "1"), Cloud(a, "2"), new() { Name = "직접" } };
        var counts = K8sProfilePick.ClusterCounts(list);
        Assert.Equal(2, counts[a.Id]);
        Assert.Single(counts);
    }

    [Fact]
    public void 고른_프로필만_조회하고_해제한_프로필은_켜져_있던_것만_끈다()
    {
        var a = new CloudProfile { SyncClusters = true };
        var b = new CloudProfile { SyncClusters = true };
        var c = new CloudProfile();
        var plan = K8sProfilePick.Make(new[] { a, b, c }, new HashSet<Guid> { a.Id }, new List<K8sCluster>(), removeUnselected: false);
        Assert.Equal(new[] { a }, plan.Fetch);
        Assert.Equal(new[] { b }, plan.Disable);   // c 는 원래 꺼져 있었다
        Assert.Empty(plan.Remove);
    }

    [Fact]
    public void 제거는_요청했을_때만_해제한_프로필의_클러스터만()
    {
        var a = new CloudProfile { SyncClusters = true };
        var b = new CloudProfile { SyncClusters = true };
        var other = Guid.NewGuid();   // 목록에 없는(이미 지운) 프로필의 클러스터는 건드리지 않는다
        var clusters = new List<K8sCluster>
        {
            Cloud(a, "a1"), Cloud(b, "b1"), Cloud(b, "b2"),
            new() { Name = "직접 등록" },
            new() { Source = K8sSource.Cloud, CloudProfileId = other, CloudClusterId = "x", Name = "x" },
        };
        var keep = K8sProfilePick.Make(new[] { a, b }, new HashSet<Guid> { a.Id }, clusters, removeUnselected: false);
        Assert.Empty(keep.Remove);

        var drop = K8sProfilePick.Make(new[] { a, b }, new HashSet<Guid> { a.Id }, clusters, removeUnselected: true);
        Assert.Equal(new[] { "b1", "b2" }, drop.Remove.Select(c => c.Name).OrderBy(n => n));
    }

    [Fact]
    public void 아무것도_고르지_않으면_조회_없이_전부_끈다()
    {
        var a = new CloudProfile { SyncClusters = true };
        var b = new CloudProfile();
        var plan = K8sProfilePick.Make(new[] { a, b }, new HashSet<Guid>(), new List<K8sCluster> { Cloud(a, "a1") }, removeUnselected: true);
        Assert.Empty(plan.Fetch);
        Assert.Equal(new[] { a }, plan.Disable);
        Assert.Single(plan.Remove);
    }

    [Fact]
    public void 같은_프로필이_두_번_와도_한_번만()
    {
        var a = new CloudProfile();
        var plan = K8sProfilePick.Make(new[] { a, a }, new HashSet<Guid> { a.Id }, new List<K8sCluster>(), removeUnselected: false);
        Assert.Single(plan.Fetch);
    }
}
