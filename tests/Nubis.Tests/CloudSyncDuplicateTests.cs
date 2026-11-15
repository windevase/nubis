using System.Collections.Generic;
using System.Linq;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>동기화 병합의 중복 처리 (조회 결과 중복 · 기존 호스트 중복)</summary>
public class CloudSyncDuplicateTests
{
    private static CloudProfile Profile() =>
        new() { Provider = CloudProvider.Tencent, Name = "dup", DisplayName = "DUP" };

    private static CloudInstance Linux(string id, string name = "web", string ip = "10.0.0.1") =>
        new(id, name, "api", "running", "ap-seoul-1", ip, "", false, "Linux");

    [Fact]
    public void 조회_결과에_같은_인스턴스가_두번_와도_호스트는_하나만_추가된다()
    {
        var p = Profile();
        var hosts = new List<HostEntry>();
        var (added, updated, removed) = CloudSync.Apply(p, new() { Linux("ins-1"), Linux("ins-1", name: "web-dup", ip: "10.0.0.99") }, hosts);

        Assert.Equal(1, added);
        Assert.Equal(0, updated);
        Assert.Equal(0, removed);
        var h = Assert.Single(hosts);
        // 첫 번째 결과가 반영된다
        Assert.Equal("web", h.Label);
        Assert.Equal("10.0.0.1", h.Address);
    }

    [Fact]
    public void 기존_호스트에_중복된_인스턴스가_있어도_재동기화로_새로_늘지_않는다()
    {
        var p = Profile();
        var hosts = new List<HostEntry>();
        CloudSync.Apply(p, new() { Linux("ins-1") }, hosts);
        var (added, _, _) = CloudSync.Apply(p, new() { Linux("ins-1"), Linux("ins-1") }, hosts);

        Assert.Equal(0, added);
        Assert.Single(hosts);
    }

    [Fact]
    public void 같은_프로필_인스턴스로_중복된_기존_호스트는_첫번째만_남기고_제거한다()
    {
        var p = Profile();
        var first = new HostEntry { CloudProfileId = p.Id, CloudInstanceId = "ins-1", Label = "a", Address = "10.0.0.1", Port = 2222 };
        var extra = new HostEntry { CloudProfileId = p.Id, CloudInstanceId = "ins-1", Label = "b", Address = "10.0.0.1" };
        var otherProfile = new HostEntry { CloudProfileId = System.Guid.NewGuid(), CloudInstanceId = "ins-1", Label = "c", Address = "10.0.0.1" };
        var hosts = new List<HostEntry> { first, extra, otherProfile };

        var (added, updated, removed) = CloudSync.Apply(p, new() { Linux("ins-1", ip: "10.0.0.5") }, hosts);

        Assert.Equal(0, added);
        Assert.Equal(1, updated);
        Assert.Equal(1, removed);
        Assert.Contains(first, hosts);
        Assert.DoesNotContain(extra, hosts);
        Assert.Contains(otherProfile, hosts);   // 다른 프로필의 같은 ID 는 건드리지 않는다
        Assert.Equal("10.0.0.5", first.Address);
        Assert.Equal(2222, first.Port);          // 사용자가 바꾼 포트 유지
    }

    [Fact]
    public void 부분_조회면_중복된_기존_호스트도_지우지_않는다()
    {
        var p = Profile();
        var first = new HostEntry { CloudProfileId = p.Id, CloudInstanceId = "ins-1", Label = "a", Address = "10.0.0.1" };
        var extra = new HostEntry { CloudProfileId = p.Id, CloudInstanceId = "ins-1", Label = "b", Address = "10.0.0.1" };
        var hosts = new List<HostEntry> { first, extra };

        var (_, _, removed) = CloudSync.Apply(p, new() { Linux("ins-1") }, hosts, removeMissing: false);

        Assert.Equal(0, removed);
        Assert.Equal(2, hosts.Count);
    }
}
