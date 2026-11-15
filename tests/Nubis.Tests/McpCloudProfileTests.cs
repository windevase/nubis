using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>클라우드 프로필 설정 변경 · 제거 공용 로직 (UI 프로필 설정 창 · MCP cloud_update_profile / cloud_remove_profile)</summary>
public class McpCloudProfileTests
{
    private static CloudProfile Profile(string name = "prod") => new() { Provider = CloudProvider.Aws, Name = name, DisplayName = "" };

    private static HostEntry Host(CloudProfile? p, Protocol proto, int port, string? publicIp = null, bool usePublic = false) => new()
    {
        Protocol = proto, Port = port, Address = "10.0.0." + Random.Shared.Next(1, 250), PublicIp = publicIp, UsePublicIp = usePublic,
        CloudProfileId = p?.Id,
    };

    [Fact]
    public void 공인IP_기본값_변경은_기본값을_따르던_서버만_바꾸고_override_면_전부()
    {
        foreach (var overrideCustom in new[] { false, true })
        {
            var p = Profile();
            var follow = Host(p, Protocol.Ssh, p.EffectiveSshPort, "1.1.1.1", usePublic: false);
            var custom = Host(p, Protocol.Ssh, p.EffectiveSshPort, "1.1.1.2", usePublic: true);
            var noPublic = Host(p, Protocol.Ssh, p.EffectiveSshPort);
            var other = Host(Profile("other"), Protocol.Ssh, 22, "1.1.1.3", usePublic: false);
            var hosts = new List<HostEntry> { follow, custom, noPublic, other };

            var before = CloudProfileEdit.Snapshot(p);
            Assert.Equal(1, CloudProfileEdit.CustomAddressCount(hosts, p, before.UsePublicIp));
            CloudProfileEdit.ApplySettings(p, CloudProfileSettings.From(p) with { UsePublicIp = true }, unhideRemoved: false);
            CloudProfileEdit.ApplyCascade(hosts, p, before, overrideCustom);

            Assert.True(follow.UsePublicIp);
            Assert.True(custom.UsePublicIp);           // 이미 공인 → 그대로
            Assert.False(noPublic.UsePublicIp);        // 공인 IP 없는 서버는 건드리지 않음
            Assert.False(other.UsePublicIp);           // 다른 프로필 서버는 건드리지 않음

            // 되돌릴 때: custom 이었던(따로 고른) 서버는 override 일 때만 바뀐다
            follow.UsePublicIp = false;               // follow 를 "따로 고른 서버" 로 만든다 (현재 기본 = 공인)
            var before2 = CloudProfileEdit.Snapshot(p);
            CloudProfileEdit.ApplySettings(p, CloudProfileSettings.From(p) with { UsePublicIp = false }, unhideRemoved: false);
            CloudProfileEdit.ApplyCascade(hosts, p, before2, overrideCustom);
            Assert.False(custom.UsePublicIp);         // 기본값(공인)을 따르던 서버 → 사설
            Assert.False(follow.UsePublicIp);
        }
    }

    [Fact]
    public void 공인IP_기본값을_그대로_두면_따로_고른_서버를_건드리지_않는다()
    {
        var p = Profile();
        var custom = Host(p, Protocol.Ssh, p.EffectiveSshPort, "1.1.1.2", usePublic: true);
        var before = CloudProfileEdit.Snapshot(p);
        CloudProfileEdit.ApplySettings(p, CloudProfileSettings.From(p) with { DisplayName = "새 이름" }, unhideRemoved: false);
        Assert.Equal((0, 0), CloudProfileEdit.ApplyCascade(new List<HostEntry> { custom }, p, before, overrideCustom: true));
        Assert.True(custom.UsePublicIp);
        Assert.Equal("새 이름", p.GroupName);
    }

    [Fact]
    public void 기본_포트를_바꾸면_이전_기본_포트를_쓰던_호스트만_바뀐다()
    {
        var p = Profile();
        var oldSsh = p.EffectiveSshPort;
        var oldRdp = p.EffectiveRdpPort;
        var sshDefault = Host(p, Protocol.Ssh, oldSsh);
        var sshCustom = Host(p, Protocol.Ssh, 2222);
        var rdpDefault = Host(p, Protocol.Rdp, oldRdp);
        var otherProfile = Host(Profile("x"), Protocol.Ssh, oldSsh);
        var hosts = new List<HostEntry> { sshDefault, sshCustom, rdpDefault, otherProfile };

        var before = CloudProfileEdit.Snapshot(p);
        CloudProfileEdit.ApplySettings(p, CloudProfileSettings.From(p) with { SshPort = 10022 }, unhideRemoved: false);
        var (address, port) = CloudProfileEdit.ApplyCascade(hosts, p, before, overrideCustom: false);

        Assert.Equal(0, address);
        Assert.Equal(1, port);
        Assert.Equal(10022, sshDefault.Port);
        Assert.Equal(2222, sshCustom.Port);
        Assert.Equal(oldRdp, rdpDefault.Port);
        Assert.Equal(oldSsh, otherProfile.Port);

        // 빈 값(null) = 설정의 기본 포트로 되돌림 → 10022 를 쓰던 호스트가 기본 포트로
        var before2 = CloudProfileEdit.Snapshot(p);
        CloudProfileEdit.ApplySettings(p, CloudProfileSettings.From(p) with { SshPort = null }, unhideRemoved: false);
        CloudProfileEdit.ApplyCascade(hosts, p, before2, overrideCustom: false);
        Assert.Equal(oldSsh, sshDefault.Port);
        Assert.Null(p.SshPort);
    }

    [Fact]
    public void 중지된_서버_포함을_켤_때만_바로_동기화한다()
    {
        var p = Profile();
        p.IncludeStopped = false;
        var before = CloudProfileEdit.Snapshot(p);
        CloudProfileEdit.ApplySettings(p, CloudProfileSettings.From(p) with { IncludeStopped = true }, unhideRemoved: false);
        Assert.True(CloudProfileEdit.NeedsSyncAfter(before, p));

        var before2 = CloudProfileEdit.Snapshot(p);
        CloudProfileEdit.ApplySettings(p, CloudProfileSettings.From(p) with { IncludeStopped = false }, unhideRemoved: false);
        Assert.False(CloudProfileEdit.NeedsSyncAfter(before2, p));
    }

    [Fact]
    public void 숨긴_서버_다시_표시는_요청했을_때만_비운다()
    {
        var p = Profile();
        p.HiddenInstances.Add("i-1");
        CloudProfileEdit.ApplySettings(p, CloudProfileSettings.From(p), unhideRemoved: false);
        Assert.Single(p.HiddenInstances);
        CloudProfileEdit.ApplySettings(p, CloudProfileSettings.From(p), unhideRemoved: true);
        Assert.Empty(p.HiddenInstances);
    }

    [Fact]
    public void 설정_반영은_기본_비밀번호와_Vault_를_건드리지_않는다()
    {
        var p = Profile();
        p.ProtectedDefaultPassword = "cipher";
        var vault = Guid.NewGuid();
        p.SshVaultId = vault;
        CloudProfileEdit.ApplySettings(p, CloudProfileSettings.From(p) with { DefaultUsername = "ec2-user", RdpPort = 13389 }, unhideRemoved: false);
        Assert.Equal("cipher", p.ProtectedDefaultPassword);
        Assert.Equal(vault, p.SshVaultId);
        Assert.Equal("ec2-user", p.DefaultUsername);
        Assert.Equal(13389, p.RdpPort);
        // 설정 레코드에는 비밀번호 필드가 없다 (MCP 로 받거나 돌려줄 수 없게)
        Assert.DoesNotContain(typeof(CloudProfileSettings).GetProperties(), pi => pi.Name.Contains("Password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void 점프_호스트_의존_호스트는_다른_프로필_그룹만_센다()
    {
        var p = Profile();
        var bastion = Host(p, Protocol.Ssh, 22);
        var inner = Host(p, Protocol.Ssh, 22);
        inner.JumpHostId = bastion.Id;                 // 같은 프로필 안 — 함께 지워지므로 대상 아님
        var local = Host(null, Protocol.Ssh, 22);
        local.JumpHostId = bastion.Id;
        var otherCloud = Host(Profile("y"), Protocol.Ssh, 22);
        otherCloud.JumpHostId = bastion.Id;
        var unrelated = Host(null, Protocol.Ssh, 22);
        unrelated.JumpHostId = Guid.NewGuid();
        var deps = CloudProfileEdit.JumpDependents(new List<HostEntry> { bastion, inner, local, otherCloud, unrelated }, p);
        Assert.Equal(new[] { local, otherCloud }, deps);
    }

    [Fact]
    public void 같은_Vault_계정_지정도_호스트에_반영할_것이_있으면_변경으로_본다()
    {
        var p = Profile();
        var id = Guid.NewGuid();
        p.SshVaultId = id;
        var plain = Host(p, Protocol.Ssh, 22);
        var rdp = Host(p, Protocol.Rdp, 3389);
        var hosts = new List<HostEntry> { plain, rdp };
        Assert.True(CloudProfileEdit.VaultNoop(hosts, p, Protocol.Ssh, id, overrideHosts: false, clearOwn: false));
        Assert.False(CloudProfileEdit.VaultNoop(hosts, p, Protocol.Ssh, Guid.NewGuid(), overrideHosts: false, clearOwn: false));   // 다른 계정

        // 직접 적은 사용자명: clearOwn 이면 지울 것이 있으므로 변경
        plain.Username = "ec2-user";
        Assert.True(CloudProfileEdit.VaultNoop(hosts, p, Protocol.Ssh, id, overrideHosts: false, clearOwn: false));
        Assert.False(CloudProfileEdit.VaultNoop(hosts, p, Protocol.Ssh, id, overrideHosts: false, clearOwn: true));
        plain.Username = "";

        // 지워진 Vault 계정을 직접 고른 호스트: 지정을 비우므로 변경
        plain.VaultId = Guid.NewGuid();
        Assert.False(CloudProfileEdit.VaultNoop(hosts, p, Protocol.Ssh, id, overrideHosts: false, clearOwn: false));

        // 해제: 프로필 기본이 이미 없어도 호스트에 직접 고른 계정이 있으면 변경 (UI 해제와 같음)
        Assert.True(CloudProfileEdit.VaultNoop(hosts, p, Protocol.Rdp, null, overrideHosts: false, clearOwn: false));
        rdp.VaultId = Guid.NewGuid();
        Assert.False(CloudProfileEdit.VaultNoop(hosts, p, Protocol.Rdp, null, overrideHosts: false, clearOwn: false));
    }

    [Fact]
    public void 프로필_지정은_정확히_일치를_먼저_본다()
    {
        var a = new CloudProfile { Provider = CloudProvider.Aws, Name = "prod", DisplayName = "" };
        var b = new CloudProfile { Provider = CloudProvider.Aws, Name = "prod-eu", DisplayName = "" };
        var c = new CloudProfile { Provider = CloudProvider.Gcp, Name = "proj-123", DisplayName = "Game" };
        var all = new[] { a, b, c };
        Assert.Equal(new[] { a }, CloudProfileEdit.Match(all, "prod"));
        Assert.Equal(new[] { a, b }, CloudProfileEdit.Match(all, "pro").Where(p => p.Provider == CloudProvider.Aws));
        Assert.Equal(new[] { c }, CloudProfileEdit.Match(all, "game"));
        Assert.Equal(new[] { c }, CloudProfileEdit.Match(all, "proj-123"));
        Assert.Equal(new[] { c }, CloudProfileEdit.Match(all, $"[{c.ProviderText}] Game"));
        Assert.Equal(new[] { b }, CloudProfileEdit.Match(all, b.Id.ToString()[..8]));
        Assert.Empty(CloudProfileEdit.Match(all, "nothing"));
    }

    [Fact]
    public void MCP_프로필_도구는_기본_비밀번호를_인자로_받지_않는다()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "RemoteClient", "Nubis.csproj"))) dir = Path.GetDirectoryName(dir);
        var src = File.ReadAllText(Path.Combine(dir!, "RemoteClient", "Claude", "MainWindow.McpCloudProfiles.cs"), Encoding.UTF8);
        Assert.DoesNotContain("default_password = new", src);
        Assert.DoesNotContain("DefaultPassword =", src);
        Assert.DoesNotContain("DefaultPassword}", src);
        // 프로필 설정 창이 열려 있는 프로필은 바꾸지 않는다 (요청 때 · 승인 뒤 두 번 확인 — 창의 OK 가 MCP 변경을 덮어쓰지 않게)
        Assert.Equal(2, src.Split("_editingProfile == p").Length - 1);
        // 승인·감사 로그 공통 경로를 거친다
        foreach (var tool in new[] { "cloud_update_profile", "cloud_remove_profile" })
        {
            Assert.Contains($"AskApprovalAsync(\"{tool}\"", src);
            Assert.Contains($"AuditLog.Write(\"{tool}\"", src);
        }
    }
}
