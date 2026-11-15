using System;
using System.Collections.Generic;
using System.IO;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>
/// MCP 호스트 일괄·그룹 도구(McpHostsMore)와 그룹 우클릭 메뉴가 함께 쓰는 로직 (MainWindow.Groups.cs 에서 뽑은 정적 함수)
/// </summary>
[Collection("settings")]
public class McpHostsMoreTests
{
    private static VaultEntry NewVault(string name)
    {
        var v = new VaultEntry { Name = name + "-" + Guid.NewGuid().ToString("N")[..6], Username = "u", Password = "p" };
        VaultStore.Upsert(v);
        return v;
    }

    [Fact]
    public void 일반_그룹_Vault_지정은_호스트에_직접()
    {
        var id = Guid.NewGuid();
        var a = new HostEntry { Label = "a", Group = "web", Username = "root", ProtectedPassword = "x" };
        var targets = new List<HostEntry> { a };
        var keep = MainWindow.GroupVaultKeep(targets, id, isCloud: false, overrideHostVault: false);
        Assert.Empty(keep);
        Assert.Equal(1, MainWindow.ApplyGroupVaultTo(targets, null, Protocol.Ssh, id, clearOwn: false, keep));
        Assert.Equal(id, a.VaultId);
        Assert.Equal("x", a.ProtectedPassword);   // 지우지 않음
    }

    [Fact]
    public void 클라우드_그룹_Vault_지정은_프로필_기본값_그리고_직접_고른_계정_유지()
    {
        var profile = new CloudProfile();
        var mine = NewVault("mine");
        var other = NewVault("other");
        var plain = new HostEntry { Label = "plain", CloudProfileId = profile.Id, Protocol = Protocol.Ssh };
        var picked = new HostEntry { Label = "picked", CloudProfileId = profile.Id, Protocol = Protocol.Ssh, VaultId = other.Id };
        var own = new HostEntry { Label = "own", CloudProfileId = profile.Id, Protocol = Protocol.Ssh, Username = "admin", ProtectedPassword = "x" };
        var sameWithHidden = new HostEntry { Label = "same", CloudProfileId = profile.Id, Protocol = Protocol.Ssh, VaultId = mine.Id, Username = "h" };
        var targets = new List<HostEntry> { plain, picked, own, sameWithHidden };

        // 기본(유지): 다른 계정을 고른 호스트 · 같은 계정 + 숨긴 값 호스트는 그대로
        var keep = MainWindow.GroupVaultKeep(targets, mine.Id, isCloud: true, overrideHostVault: false);
        Assert.Equal(new HashSet<HostEntry> { picked, sameWithHidden }, keep);
        Assert.Equal(new[] { picked }, MainWindow.HostsWithOtherVault(targets, mine.Id));

        var n = MainWindow.ApplyGroupVaultTo(targets, profile, Protocol.Ssh, mine.Id, clearOwn: false, keep);
        Assert.Equal(2, n);
        Assert.Equal(mine.Id, profile.SshVaultId);
        Assert.Null(profile.RdpVaultId);
        Assert.Null(plain.VaultId);
        Assert.Equal(other.Id, picked.VaultId);
        Assert.Equal(mine.Id, sameWithHidden.VaultId);
        Assert.Equal("x", own.ProtectedPassword);     // clearOwn=false 면 직접 적은 값 유지

        // 해제 + 지우기
        var keep2 = MainWindow.GroupVaultKeep(targets, mine.Id, isCloud: true, overrideHostVault: true);
        Assert.DoesNotContain(picked, keep2);
        MainWindow.ApplyGroupVaultTo(targets, profile, Protocol.Ssh, mine.Id, clearOwn: true, keep2);
        Assert.Null(picked.VaultId);
        Assert.Null(own.ProtectedPassword);
        Assert.Equal("", own.Username);
    }

    [Fact]
    public void 그룹_Vault_해제는_그_프로토콜만()
    {
        var profile = new CloudProfile { SshVaultId = Guid.NewGuid(), RdpVaultId = Guid.NewGuid() };
        var ssh = new HostEntry { Protocol = Protocol.Ssh, VaultId = Guid.NewGuid(), CloudProfileId = profile.Id };
        var rdp = new HostEntry { Protocol = Protocol.Rdp, VaultId = Guid.NewGuid(), CloudProfileId = profile.Id };
        Assert.Equal(1, MainWindow.ClearGroupVaultOn(new[] { ssh, rdp }, profile, Protocol.Ssh));
        Assert.Null(ssh.VaultId);
        Assert.NotNull(rdp.VaultId);
        Assert.Null(profile.SshVaultId);
        Assert.NotNull(profile.RdpVaultId);
    }

    [Fact]
    public void 그룹_공인_IP_는_공인_IP_가_있는_호스트와_프로필_기본값()
    {
        var profile = new CloudProfile();
        var with = new HostEntry { Address = "10.0.0.1", PrivateIp = "10.0.0.1", PublicIp = "1.2.3.4", CloudProfileId = profile.Id };
        var without = new HostEntry { Address = "10.0.0.2", PrivateIp = "10.0.0.2", CloudProfileId = profile.Id };
        Assert.Equal(1, MainWindow.SetGroupPublicIpOn(new[] { with, without }, profile, true));
        Assert.True(with.UsePublicIp);
        Assert.False(without.UsePublicIp);
        Assert.True(profile.UsePublicIp);
    }

    [Fact]
    public void 그룹_이름_바꾸기는_직접_등록한_호스트만_그리고_접힘_키를_옮긴다()
    {
        var a = new HostEntry { Label = "a", Group = "web" };
        var b = new HostEntry { Label = "b", Group = " WEB " };
        var c = new HostEntry { Label = "c", Group = "db" };
        var cloud = new HostEntry { Label = "x", Group = "web", CloudProfileId = Guid.NewGuid() };
        var all = new List<HostEntry> { a, b, c, cloud };
        var collapsed = new HashSet<string> { "L:WEB" };

        Assert.Equal(2, MainWindow.RenameLocalGroup(all, collapsed, "WEB", "L:WEB", "front"));
        Assert.Equal("front", a.Group);
        Assert.Equal("front", b.Group);
        Assert.Equal("db", c.Group);
        Assert.Equal("web", cloud.Group);
        Assert.Equal(new HashSet<string> { "L:front" }, collapsed);

        // 기존 그룹과 합쳐지면 그 그룹의 표기로
        Assert.Equal(2, MainWindow.RenameLocalGroup(all, collapsed, "front", "L:front", "DB"));
        Assert.Contains("L:DB", collapsed);
    }

    [Fact]
    public void 새_MCP_도구가_등록되고_승인_감사_경로를_쓴다()
    {
        // 등록은 McpHostsMore, 그룹 도구 구현은 McpGroups 로 나뉘어 있다
        var dir = Path.Combine(RepoRoot(), "RemoteClient", "Claude");
        var src = File.ReadAllText(Path.Combine(dir, "MainWindow.McpHostsMore.cs")) + "\n" + File.ReadAllText(Path.Combine(dir, "MainWindow.McpGroups.cs"));
        foreach (var tool in new[] { "bulk_update_hosts", "set_group_defaults", "rename_group", "set_group_collapsed", "duplicate_host", "connect_hosts" })
        {
            Assert.Contains($"new McpTool(\"{tool}\"", src);
            Assert.Contains($"AuditLog.Write(\"{tool}\"", src);
        }
        // 값을 바꾸는 도구는 승인
        foreach (var tool in new[] { "bulk_update_hosts", "set_group_defaults", "rename_group", "duplicate_host", "connect_hosts" })
            Assert.Contains($"AskApprovalAsync(\"{tool}\"", src);
        // 비밀번호 값을 결과·로그에 넣지 않는다 (있음/없음만)
        Assert.DoesNotContain("ProtectedPassword}", src);
        Assert.DoesNotContain("Secret.", src);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "build.ps1"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
