using System;
using System.IO;
using System.Runtime.CompilerServices;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>테스트가 사용자 데이터(%APPDATA%\Nubis)를 건드리지 않게 데이터 폴더를 임시 폴더로 바꾼다 (AppPaths 초기화 전에 실행)</summary>
internal static class TestDataDir
{
    [ModuleInitializer]
    internal static void Init()
    {
        var dir = Path.Combine(Path.GetTempPath(), "Nubis.Tests", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("NUBIS_DATA_DIR", dir);
    }
}

public class VaultTests
{
    // 테스트 키는 실행할 때마다 새로 만든다 (TestKeys) — 저장소에 개인키를 넣지 않는다
    private static string Key => TestKeys.Rsa;

    [Fact]
    public void 테스트_데이터_폴더는_사용자_폴더가_아니다()
    {
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Nubis");
        Assert.NotEqual(Path.GetFullPath(appData), Path.GetFullPath(AppPaths.DataDir));
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(AppPaths.DataDir));
    }

    [Fact]
    public void 공개키를_ssh_keygen_과_같은_형식으로_만든다()
    {
        // RSA 는 서명 알고리즘(rsa-sha2-*)이 아니라 키 형식(ssh-rsa)으로 표시
        Assert.Equal(TestKeys.RsaPublic, VaultStore.ReadPublicKey(TestKeys.Rsa, null));
        Assert.Equal(TestKeys.RsaPublic, VaultStore.ReadPublicKey(TestKeys.RsaEncrypted, TestKeys.Passphrase));
    }

    [Fact]
    public void Ed25519_키도_ssh_keygen_과_같은_공개키를_만든다()
    {
        // ssh-keygen 이 없는 PC 에서는 건너뜀
        if (TestKeys.Ed25519() is not { } plain) return;
        Assert.Equal(plain.Public, VaultStore.ReadPublicKey(plain.Private, null));
        if (TestKeys.Ed25519("pw-123") is not { } enc) return;
        Assert.Equal(enc.Public, VaultStore.ReadPublicKey(enc.Private, "pw-123"));
        Assert.ThrowsAny<Exception>(() => VaultStore.ReadPublicKey(enc.Private, null));
        Assert.Equal("ssh-ed25519", VaultStore.BlobType(Convert.FromBase64String(plain.Public.Split(' ')[1])));
    }

    [Fact]
    public void 암호가_걸린_키는_키_암호가_없거나_틀리면_거부한다()
    {
        Assert.ThrowsAny<Exception>(() => VaultStore.ReadPublicKey(TestKeys.RsaEncrypted, null));
        Assert.ThrowsAny<Exception>(() => VaultStore.ReadPublicKey(TestKeys.RsaEncrypted, "wrong"));
        Assert.ThrowsAny<Exception>(() => VaultStore.ReadPublicKey("not a key", null));
    }

    [Fact]
    public void 비밀_값은_암호화해_저장하고_평문을_파일에_남기지_않는다()
    {
        var v = new VaultEntry { Name = "t-plain", Username = "root", Password = "S3cret-Plain!", Key = Key, Passphrase = "passphrase-xyz" };
        VaultStore.Upsert(v);

        var json = File.ReadAllText(VaultStore.FilePath);
        Assert.DoesNotContain("S3cret-Plain!", json);
        Assert.DoesNotContain("passphrase-xyz", json);
        Assert.DoesNotContain("PRIVATE KEY", json);
        Assert.DoesNotContain("\"Kind\"", json);             // 예전 형식 필드는 쓰지 않는다
        Assert.DoesNotContain("ProtectedSecret", json);
        var back = VaultStore.Get(v.Id)!;
        Assert.Equal("S3cret-Plain!", back.Password);
        Assert.Equal(Key, back.Key);
        Assert.Equal("passphrase-xyz", back.Passphrase);
        Assert.Equal("비밀번호 + SSH 키", back.MethodsText);

        VaultStore.Remove(v.Id);
        Assert.Null(VaultStore.Get(v.Id));
    }

    [Fact]
    public void Vault_계정_하나로_사용자명_비밀번호_키를_모두_채운다()
    {
        var v = new VaultEntry { Name = "t-root", Username = "root", Password = "vault-pw", Key = Key, Passphrase = "kp" };
        VaultStore.Upsert(v);
        try
        {
            var h = new HostEntry { Address = "10.0.0.1", Protocol = Protocol.Ssh, VaultId = v.Id };
            var c = h.WithDefaults(null);

            Assert.NotSame(h, c);
            Assert.Equal("root", c.Username);
            Assert.Equal("vault-pw", c.Password);
            Assert.Equal(Key, c.KeyMaterial);
            Assert.Equal("kp", c.KeyPassphrase);
            Assert.True(c.HasKey);

            // 원본(저장되는 호스트)은 그대로, 직렬화에도 키·키 암호가 들어가지 않는다
            Assert.Equal("", h.Username);
            Assert.Null(h.KeyMaterial);
            var json = System.Text.Json.JsonSerializer.Serialize(c);
            Assert.DoesNotContain("PRIVATE KEY", json);
            Assert.DoesNotContain("KeyPassphrase", json);
            Assert.DoesNotContain("KeyMaterial", json);
            Assert.DoesNotContain("VaultPasswordId", json);
        }
        finally { VaultStore.Remove(v.Id); }
    }

    [Fact]
    public void 직접_고른_Vault_계정이_호스트에_적은_값보다_우선한다()
    {
        var v = new VaultEntry { Name = "t-admin", Username = "Administrator", Password = "vault-pw", Key = Key };
        VaultStore.Upsert(v);
        try
        {
            // 호스트에 적힌 사용자명·비밀번호·키 파일은 숨겨진 채 쓰지 않는다 (지우지는 않음 — 해제하면 다시 쓰인다)
            var h = new HostEntry { Address = "10.0.0.2", Protocol = Protocol.Ssh, VaultId = v.Id, Username = "me", KeyPath = @"C:\mine.pem" };
            h.Password = "host-pw";
            var c = h.WithDefaults(null);
            Assert.Equal("Administrator", c.Username);
            Assert.Equal("vault-pw", c.Password);
            Assert.Null(c.KeyPath);
            Assert.Equal(Key, c.KeyMaterial);
            Assert.Equal("host-pw", h.Password);
            Assert.Equal(@"C:\mine.pem", h.KeyPath);

            // 해제하면 호스트에 적힌 값으로
            h.VaultId = null;
            var own = h.WithDefaults(null);
            Assert.Equal("me", own.Username);
            Assert.Equal("host-pw", own.Password);
            Assert.Equal(@"C:\mine.pem", own.KeyPath);
            Assert.Null(own.KeyMaterial);
        }
        finally { VaultStore.Remove(v.Id); }
    }

    [Fact]
    public void RDP_는_Vault_계정의_키를_쓰지_않는다()
    {
        var v = new VaultEntry { Name = "t-win", Username = @"CORP\admin", Password = "p", Key = Key };
        VaultStore.Upsert(v);
        try
        {
            var c = new HostEntry { Address = "10.0.0.4", Protocol = Protocol.Rdp, VaultId = v.Id }.WithDefaults(null);
            Assert.Equal(@"CORP\admin", c.Username);
            Assert.Equal("p", c.Password);
            Assert.Null(c.KeyMaterial);
        }
        finally { VaultStore.Remove(v.Id); }
    }

    [Fact]
    public void 예전_분리형_Vault_데이터를_새_형식으로_옮긴다()
    {
        // 예전 형식: 암호 항목은 ProtectedSecret = 비밀번호, 키 항목은 ProtectedSecret = 키 암호
        var oldPw = new VaultEntry { Kind = VaultKind.Password, ProtectedSecret = Secret.Protect("old-pw") };
        Assert.True(oldPw.MigrateLegacy());
        Assert.Equal("old-pw", oldPw.Password);
        Assert.Null(oldPw.Kind);
        Assert.Null(oldPw.ProtectedSecret);

        var oldKey = new VaultEntry { Kind = VaultKind.SshKey, Key = Key, ProtectedSecret = Secret.Protect("old-pass") };
        Assert.True(oldKey.MigrateLegacy());
        Assert.Equal("old-pass", oldKey.Passphrase);
        Assert.False(oldKey.HasPassword);
        Assert.False(oldKey.MigrateLegacy());          // 두 번째는 바뀔 것 없음

        // 예전 호스트 참조: 암호 항목 우선, 없으면 키 항목
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var h = new HostEntry { VaultPasswordId = a, VaultKeyId = b };
        Assert.True(h.MigrateLegacyVault());
        Assert.Equal(a, h.VaultId);
        Assert.Null(h.VaultPasswordId);
        var k = new HostEntry { VaultKeyId = b };
        Assert.True(k.MigrateLegacyVault());
        Assert.Equal(b, k.VaultId);
        Assert.False(new HostEntry().MigrateLegacyVault());
    }

    [Fact]
    public void 지워진_Vault_항목은_무시하고_클라우드_기본_계정으로_채운다()
    {
        var h = new HostEntry { Address = "10.0.0.3", VaultId = Guid.NewGuid(), CloudProfileId = Guid.NewGuid() };
        var p = new CloudProfile { DefaultUsername = "ubuntu", DefaultPassword = "profile-pw" };
        var c = h.WithDefaults(p);
        Assert.Equal("ubuntu", c.Username);
        Assert.Equal("profile-pw", c.Password);
    }

    [Fact]
    public void Vault_파일도_Claude_패널에서_읽지_못하게_막는다()
    {
        var rules = ClaudeGuard.DenyRules(@"C:\Users\a\AppData\Roaming\Nubis");
        Assert.Contains("Read(//c/Users/a/AppData/Roaming/Nubis/vault.json*)", rules);
        Assert.Contains("PowerShell(*vault.json*)", rules);
        Assert.Contains("Bash(*ProtectedKey*)", rules);
        Assert.Contains("PowerShell(*ProtectedPassphrase*)", rules);
    }

    [Fact]
    public void 공개키_blob_에서_키_형식을_읽는다()
    {
        var blob = Convert.FromBase64String(TestKeys.RsaPublic.Split(' ')[1]);
        Assert.Equal("ssh-rsa", VaultStore.BlobType(blob));
        Assert.Equal("ssh-key", VaultStore.BlobType(new byte[] { 0, 0 }));
        Assert.Equal("ssh-key", VaultStore.BlobType(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 1 }));
    }
}
