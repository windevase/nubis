using System;
using System.IO;
using System.Text;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>리뷰 7차: 평문 .bak 암호화, 암호 없는 키의 키 암호 판별, 프로필 설정의 로그인 사본 제거</summary>
public class Review7VaultStorageTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "Nubis.Tests.review7", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void 예전_평문_파일을_읽으면_평문_직전_저장본도_암호화한다()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "vault.json");
        File.WriteAllText(path, "[\"now\"]");
        File.WriteAllText(SafeFile.BackupPath(path), "[\"before\"]");

        var list = SafeFile.LoadProtected(path, s => System.Text.Json.JsonSerializer.Deserialize<string[]>(s), () => Array.Empty<string>());
        Assert.Equal(new[] { "now" }, list);

        var main = File.ReadAllBytes(path);
        var bak = File.ReadAllBytes(SafeFile.BackupPath(path));
        Assert.True(SafeFile.IsProtected(main));
        Assert.True(SafeFile.IsProtected(bak));
        Assert.Equal("[\"before\"]", SafeFile.UnprotectBytes(bak));   // 내용은 그대로 (복구에 쓸 수 있게)
        Assert.DoesNotContain("before", Encoding.UTF8.GetString(bak));
    }

    [Fact]
    public void 이미_암호화된_직전_저장본은_건드리지_않는다()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "hosts.json");
        var bakPath = SafeFile.BackupPath(path);
        File.WriteAllBytes(bakPath, SafeFile.ProtectBytes("[]"));
        var before = File.ReadAllBytes(bakPath);
        SafeFile.ProtectPlainBackup(path);
        Assert.Equal(before, File.ReadAllBytes(bakPath));
        SafeFile.ProtectPlainBackup(Path.Combine(dir, "none.json"));   // .bak 이 없으면 아무것도 하지 않는다
        Assert.False(File.Exists(SafeFile.BackupPath(Path.Combine(dir, "none.json"))));
    }

    [Fact]
    public void 암호_없는_키만_공개키를_돌려준다()
    {
        Assert.NotNull(VaultStore.TryReadUnencryptedPublicKey(TestKeys.Rsa));
        Assert.Null(VaultStore.TryReadUnencryptedPublicKey(TestKeys.RsaEncrypted));
        Assert.Null(VaultStore.TryReadUnencryptedPublicKey("not a key"));
    }

    [Fact]
    public void 프로필_설정에서는_로그인_설정을_비운다()
    {
        var s = new AppSettings { FontSize = 17 };
        s.Login.Provider = LoginProviderKind.GitHub;
        s.Login.ClientId = "abc";
        s.Login.OwnerKey = "github_1";
        SettingsStore.StripGlobalOnly(s);
        Assert.Equal(LoginProviderKind.None, s.Login.Provider);
        Assert.Equal("", s.Login.ClientId);
        Assert.Equal("", s.Login.OwnerKey);
        Assert.Equal(17, s.FontSize);   // 다른 설정은 그대로
    }
}
