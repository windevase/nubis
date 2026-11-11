using System;
using Nubis;
using Xunit;

namespace Nubis.Tests;

public class BulkEditTests
{
    private static HostEntry Local(string name, string group = "") =>
        new() { Label = name, Address = name, Group = group, Protocol = Protocol.Ssh, Port = 22, Username = "root" };

    private static HostEntry Cloud(string name, string? publicIp = null) =>
        new() { Label = name, Address = "10.0.0.1", PrivateIp = "10.0.0.1", PublicIp = publicIp, Protocol = Protocol.Rdp, Port = 3389,
                CloudProfileId = Guid.NewGuid(), CloudInstanceId = "i-" + name, Group = "" };

    [Fact]
    public void 체크하지_않은_항목은_바꾸지_않는다()
    {
        var h = Local("a", "web");
        var changed = new BulkEdit { Favorite = true }.Apply(new[] { h });
        Assert.Equal(1, changed);
        Assert.True(h.Favorite);
        Assert.Equal("web", h.Group);
        Assert.Equal("root", h.Username);
        Assert.Equal(22, h.Port);
    }

    [Fact]
    public void 그룹은_직접_등록한_호스트에만()
    {
        var local = Local("a", "old");
        var cloud = Cloud("b");
        var changed = new BulkEdit { Group = "  new  " }.Apply(new[] { local, cloud });
        Assert.Equal(1, changed);
        Assert.Equal("new", local.Group);
        Assert.Equal("", cloud.Group);
    }

    [Fact]
    public void Vault_지정과_직접_적은_값_지우기()
    {
        var id = Guid.NewGuid();
        var h = Local("a");
        h.ProtectedPassword = "x";
        h.KeyPath = @"C:\k";
        new BulkEdit { SetVault = true, VaultId = id, ClearOwnCredentials = true }.Apply(new[] { h });
        Assert.Equal(id, h.VaultId);
        Assert.Equal("", h.Username);
        Assert.Null(h.ProtectedPassword);
        Assert.Null(h.KeyPath);

        // VaultId = null 로 지정 = 해제
        new BulkEdit { SetVault = true, VaultId = null }.Apply(new[] { h });
        Assert.Null(h.VaultId);
    }

    [Fact]
    public void 공인_IP_는_공인_IP_가_있는_호스트에만()
    {
        var with = Cloud("a", "1.2.3.4");
        var without = Cloud("b");
        var changed = new BulkEdit { UsePublicIp = true }.Apply(new[] { with, without });
        Assert.Equal(1, changed);
        Assert.True(with.UsePublicIp);
        Assert.False(without.UsePublicIp);
    }

    [Fact]
    public void 로컬_탭_호스트에는_계정_포트를_적용하지_않는다()
    {
        var local = new HostEntry { Label = "ps", Protocol = Protocol.Local, Port = 0 };
        var changed = new BulkEdit { Port = 2222, Username = "u", SetVault = true, VaultId = Guid.NewGuid() }.Apply(new[] { local });
        Assert.Equal(0, changed);
        Assert.Equal(0, local.Port);
        Assert.Null(local.VaultId);
    }

    [Fact]
    public void 잘못된_포트는_무시하고_빈_편집을_알아본다()
    {
        var h = Local("a");
        Assert.Equal(0, new BulkEdit { Port = 70000 }.Apply(new[] { h }));
        Assert.Equal(22, h.Port);
        Assert.True(new BulkEdit().IsEmpty);
        Assert.False(new BulkEdit { Username = "" }.IsEmpty);
    }
}
