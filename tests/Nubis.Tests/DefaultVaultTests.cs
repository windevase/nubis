using System;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>설정(SettingsStore.Current)을 바꾸는 테스트 — 다른 테스트와 동시에 돌지 않게</summary>
[CollectionDefinition("settings", DisableParallelization = true)]
public class SettingsCollection { }

[Collection("settings")]
public class DefaultVaultTests
{
    private static VaultEntry NewVault(string name, string user, string pw)
    {
        var v = new VaultEntry { Name = name, Username = user, Password = pw };
        VaultStore.Upsert(v);
        return v;
    }

    /// <summary>설정의 기본 Vault 를 잠깐 바꾸고 끝나면 되돌린다</summary>
    private static void WithDefaults(Guid? ssh, Guid? rdp, Action body)
    {
        var s = SettingsStore.Current;
        var (oldSsh, oldRdp) = (s.DefaultSshVaultId, s.DefaultRdpVaultId);
        s.DefaultSshVaultId = ssh;
        s.DefaultRdpVaultId = rdp;
        try { body(); }
        finally { s.DefaultSshVaultId = oldSsh; s.DefaultRdpVaultId = oldRdp; }
    }

    [Fact]
    public void 계정을_정하지_않은_호스트는_설정의_기본_Vault_로_로그인한다()
    {
        var linux = NewVault("t-def-linux", "ec2-user", "linux-pw");
        var win = NewVault("t-def-win", "Administrator", "win-pw");
        try
        {
            WithDefaults(linux.Id, win.Id, () =>
            {
                var ssh = new HostEntry { Address = "10.0.0.1", Protocol = Protocol.Ssh }.WithDefaults(null);
                Assert.Equal("ec2-user", ssh.Username);
                Assert.Equal("linux-pw", ssh.Password);

                var rdp = new HostEntry { Address = "10.0.0.2", Protocol = Protocol.Rdp }.WithDefaults(null);
                Assert.Equal("Administrator", rdp.Username);
                Assert.Equal("win-pw", rdp.Password);
            });
        }
        finally { VaultStore.Remove(linux.Id); VaultStore.Remove(win.Id); }
    }

    [Fact]
    public void 우선순위는_호스트_Vault_직접입력_프로필_Vault_프로필_계정_기본_Vault_순이다()
    {
        var global = NewVault("t-prio-global", "global", "g-pw");
        var profileVault = NewVault("t-prio-profile", "profile", "p-pw");
        var hostVault = NewVault("t-prio-host", "host", "h-pw");
        try
        {
            WithDefaults(global.Id, null, () =>
            {
                var p = new CloudProfile { SshVaultId = profileVault.Id };

                // 1) 호스트에 고른 Vault
                Assert.Equal("host", new HostEntry { Protocol = Protocol.Ssh, VaultId = hostVault.Id }.WithDefaults(p).Username);
                // 2) 호스트에 비밀번호를 직접 적었으면 Vault 를 쓰지 않음
                var own = new HostEntry { Protocol = Protocol.Ssh, Username = "me", Password = "mine" };
                Assert.Null(own.EffectiveVaultId(p));
                Assert.Equal("mine", own.WithDefaults(p).Password);
                // 3) 프로필 기본 Vault
                var (id, source) = new HostEntry { Protocol = Protocol.Ssh }.ResolveVault(p);
                Assert.Equal(profileVault.Id, id);
                Assert.Equal("profile", source);
                // 4) 프로필에 기본 사용자명·비밀번호를 적었으면 설정의 기본 Vault 보다 우선
                var legacy = new CloudProfile { DefaultUsername = "ubuntu", DefaultPassword = "legacy" };
                var c = new HostEntry { Protocol = Protocol.Ssh }.WithDefaults(legacy);
                Assert.Equal("ubuntu", c.Username);
                Assert.Equal("legacy", c.Password);
                // 5) 그 외에는 설정의 기본 Vault
                Assert.Equal(("global", "default"), (new HostEntry { Protocol = Protocol.Ssh }.WithDefaults(new CloudProfile()).Username,
                    new HostEntry { Protocol = Protocol.Ssh }.ResolveVault(null).source));
                // RDP 기본 계정은 비어 있으므로 RDP 호스트는 Vault 없음
                Assert.Null(new HostEntry { Protocol = Protocol.Rdp }.EffectiveVaultId(null));
            });
        }
        finally { VaultStore.Remove(global.Id); VaultStore.Remove(profileVault.Id); VaultStore.Remove(hostVault.Id); }
    }

    [Fact]
    public void 지워진_기본_Vault_는_무시한다()
    {
        WithDefaults(Guid.NewGuid(), null, () =>
        {
            var h = new HostEntry { Protocol = Protocol.Ssh, Address = "10.0.0.9" };
            Assert.Null(h.EffectiveVaultId(null));
            Assert.Same(h, h.WithDefaults(null));
        });
    }
}
