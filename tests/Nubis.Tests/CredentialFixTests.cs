using System;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>
/// 0.2.4 버그 수정 회귀 테스트: 접속 때 입력한 계정 저장 대상, 지워진 호스트 Vault, "vault:이름" 검색
/// </summary>
[Collection("settings")]
public class CredentialFixTests
{
    private static VaultEntry NewVault(string name, string user, string pw)
    {
        var v = new VaultEntry { Name = name, Username = user, Password = pw };
        VaultStore.Upsert(v);
        return v;
    }

    private static void WithDefaultSsh(Guid? ssh, Action body)
    {
        var s = SettingsStore.Current;
        var old = s.DefaultSshVaultId;
        s.DefaultSshVaultId = ssh;
        try { body(); }
        finally { s.DefaultSshVaultId = old; }
    }

    [Fact]
    public void 직접_고른_Vault_와_같은_사용자명이면_Vault_비밀번호를_바꾼다()
    {
        var v = NewVault("t-save-host", "deploy", "old");
        try
        {
            var h = new HostEntry { Protocol = Protocol.Ssh, Address = "10.0.0.1", VaultId = v.Id };
            Assert.Equal(v.Id, MainWindow.CredentialSaveTarget(h, null, "deploy")?.Id);
        }
        finally { VaultStore.Remove(v.Id); }
    }

    [Fact]
    public void 다른_사용자명을_입력하면_Vault_를_건드리지_않는다()
    {
        var v = NewVault("t-save-other", "deploy", "old");
        try
        {
            var h = new HostEntry { Protocol = Protocol.Ssh, Address = "10.0.0.1", VaultId = v.Id };
            Assert.Null(MainWindow.CredentialSaveTarget(h, null, "admin"));
            Assert.Null(MainWindow.CredentialSaveTarget(h, null, "Deploy"));   // 사용자명은 대소문자를 구분
        }
        finally { VaultStore.Remove(v.Id); }
    }

    [Fact]
    public void 기본_계정은_여러_호스트가_함께_쓰므로_입력한_계정으로_바꾸지_않는다()
    {
        var global = NewVault("t-save-global", "ubuntu", "shared");
        var profileVault = NewVault("t-save-profile", "ec2-user", "shared");
        try
        {
            WithDefaultSsh(global.Id, () =>
            {
                // 설정의 기본 계정
                var h = new HostEntry { Protocol = Protocol.Ssh, Address = "10.0.0.2" };
                Assert.Equal("default", h.ResolveVault(null).source);
                Assert.Null(MainWindow.CredentialSaveTarget(h, null, "ubuntu"));
                Assert.Null(MainWindow.CredentialSaveTarget(h, null, "admin"));
                // 클라우드 프로필의 기본 계정
                var p = new CloudProfile { SshVaultId = profileVault.Id };
                Assert.Null(MainWindow.CredentialSaveTarget(h, p, "ec2-user"));
            });
        }
        finally { VaultStore.Remove(global.Id); VaultStore.Remove(profileVault.Id); }
    }

    [Fact]
    public void 직접_고른_Vault_가_호스트에_적힌_값보다_우선한다()
    {
        var v = NewVault("t-prefer-vault", "deploy", "vault-pw");
        try
        {
            // 호스트에 예전 계정이 남아 있어도 Vault 계정으로 로그인하고(숨겨진 값은 지우지 않음), 입력한 계정은 Vault 에 저장
            var h = new HostEntry { Protocol = Protocol.Ssh, Address = "10.0.0.3", VaultId = v.Id, Username = "old", Password = "mine", KeyPath = @"C:\k" };
            var c = h.WithDefaults(null);
            Assert.Equal("deploy", c.Username);
            Assert.Equal("vault-pw", c.Password);
            Assert.Null(c.KeyPath);
            Assert.Equal("mine", h.Password);
            Assert.Equal(v.Id, MainWindow.CredentialSaveTarget(h, null, "deploy")?.Id);

            // Vault 를 해제하면 호스트에 적힌 값으로
            h.VaultId = null;
            var own = h.WithDefaults(null);
            Assert.Equal("old", own.Username);
            Assert.Equal("mine", own.Password);
        }
        finally { VaultStore.Remove(v.Id); }
    }

    [Fact]
    public void 사용자명이_없는_Vault_계정이면_호스트의_사용자명을_쓴다()
    {
        var v = NewVault("t-no-user", "", "vault-pw");
        try
        {
            var h = new HostEntry { Protocol = Protocol.Ssh, Address = "10.0.0.5", VaultId = v.Id, Username = "ubuntu" };
            var c = h.WithDefaults(null);
            Assert.Equal("ubuntu", c.Username);
            Assert.Equal("vault-pw", c.Password);
        }
        finally { VaultStore.Remove(v.Id); }
    }

    [Fact]
    public void 호스트가_고른_Vault_가_지워졌으면_기본_계정으로_넘어간다()
    {
        var global = NewVault("t-deleted-global", "ubuntu", "g-pw");
        try
        {
            WithDefaultSsh(global.Id, () =>
            {
                var h = new HostEntry { Protocol = Protocol.Ssh, Address = "10.0.0.4", VaultId = Guid.NewGuid() };
                Assert.Equal((global.Id, "default"), h.ResolveVault(null));
                var c = h.WithDefaults(null);
                Assert.Equal("ubuntu", c.Username);
                Assert.Equal("g-pw", c.Password);
            });
        }
        finally { VaultStore.Remove(global.Id); }
    }

    [Theory]
    [InlineData("prod deploy", "prod_deploy", true)]
    [InlineData("prod admin", "prod_deploy", false)]   // 이름이 정확히 같은 계정이 있으면 그 계정만
    [InlineData("bad-user", "ad", true)]               // 정확히 같은 이름이 없으면 부분 일치 (직접 일부만 입력)
    [InlineData("ad", "ad", true)]
    [InlineData("bad-user", "AD", true)]               // 대소문자 무시
    public void vault_검색은_정확한_이름을_먼저_본다(string vaultName, string query, bool expected)
    {
        var all = new[] { "prod admin", "prod deploy", "bad-user", "admin" };
        Assert.Equal(expected, MainWindow.VaultNameMatches(vaultName, query, all));
    }

    [Fact]
    public void 정확히_같은_이름의_계정이_있으면_부분_일치는_빠진다()
    {
        var all = new[] { "ad", "bad-user" };
        Assert.True(MainWindow.VaultNameMatches("ad", "ad", all));
        Assert.False(MainWindow.VaultNameMatches("bad-user", "ad", all));
        Assert.Equal("prod_deploy", MainWindow.VaultSearchName(" prod deploy "));
    }
}
