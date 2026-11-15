using System;
using System.Collections.Generic;
using System.IO;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>리뷰 2차: Vault 저장소 사본 수정·이름 중복, 손상 파일 보존, 소유자 복구, 프로필 옮기기 경고, 로그인 도메인 확인</summary>
public class Review2VaultStorageTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "Nubis.Tests.review2", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    // ----- Vault -----

    [Fact]
    public void Vault_사본을_고쳐도_Upsert_전에는_저장된_계정이_바뀌지_않는다()
    {
        var v = new VaultEntry { Name = "r2-clone-" + Guid.NewGuid().ToString("N")[..6], Username = "root", Password = "before" };
        VaultStore.Upsert(v);
        try
        {
            var copy = VaultStore.Get(v.Id)!.Clone();
            copy.Username = "changed";
            copy.Password = "after";
            Assert.Equal("root", VaultStore.Get(v.Id)!.Username);
            Assert.Equal("before", VaultStore.Get(v.Id)!.Password);

            VaultStore.Upsert(copy);   // 같은 Id → 바꿔 넣기 (중복 추가 아님)
            Assert.Equal("changed", VaultStore.Get(v.Id)!.Username);
            Assert.Equal("after", VaultStore.Get(v.Id)!.Password);
            Assert.Single(VaultStore.All(), e => e.Id == v.Id);
        }
        finally { VaultStore.Remove(v.Id); }
        Assert.Null(VaultStore.Get(v.Id));
    }

    [Fact]
    public void Vault_이름_중복을_대소문자_무시하고_찾되_자기_자신은_빼고_본다()
    {
        var name = "R2-Dup-" + Guid.NewGuid().ToString("N")[..6];
        var v = new VaultEntry { Name = name, Username = "u", Password = "p" };
        VaultStore.Upsert(v);
        try
        {
            Assert.True(VaultStore.NameTaken(name.ToLowerInvariant(), Guid.NewGuid()));
            Assert.True(VaultStore.NameTaken(" " + name + " ", Guid.NewGuid()));
            Assert.False(VaultStore.NameTaken(name, v.Id));
            Assert.False(VaultStore.NameTaken(name + "x", Guid.NewGuid()));
        }
        finally { VaultStore.Remove(v.Id); }
    }

    [Fact]
    public void 없는_계정을_지워도_예외가_없다()
    {
        VaultStore.Remove(Guid.NewGuid());
    }

    // ----- 손상 파일 -----

    [Fact]
    public void 읽기_실패가_없던_파일은_원본_보존으로_본다()
    {
        Assert.True(SafeFile.OriginalPreserved(Path.Combine(TempDir(), "never-read.json")));
    }

    [Fact]
    public void 손상_파일을_읽으면_corrupt_사본을_만들고_원본_보존으로_본다()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "data.json");
        File.WriteAllText(path, "{ broken");
        var value = SafeFile.LoadProtected(path, s => System.Text.Json.JsonSerializer.Deserialize<List<string>>(s), () => new List<string>(), out var failed);
        Assert.True(failed);
        Assert.Empty(value);
        Assert.NotEmpty(Directory.GetFiles(dir, "data.json.corrupt-*"));
        Assert.True(SafeFile.OriginalPreserved(path));
    }

    // ----- 공통 설정 손상 → 소유자 이름만 복구 -----

    [Fact]
    public void 공통_설정을_읽지_못하면_소유자_이름만_직전_저장본에서_되살린다()
    {
        var dir = TempDir();
        var bak = Path.Combine(dir, "settings.json.bak");
        File.WriteAllText(bak, """{ "Login": { "Provider": "GitHub", "ClientId": "cid", "OwnerKey": "github_42", "PreviousOwnerKey": "google_1" } }""");
        var s = new AppSettings();
        SettingsStore.RecoverOwner(s, bak);
        Assert.Equal("github_42", s.Login.OwnerKey);
        Assert.Equal("google_1", s.Login.PreviousOwnerKey);
        Assert.Equal(LoginProviderKind.None, s.Login.Provider);   // 로그인은 꺼진 채 (갇히지 않게)
        Assert.False(s.Login.Enabled);

        var none = new AppSettings();
        SettingsStore.RecoverOwner(none, Path.Combine(dir, "missing.bak"));
        Assert.Equal("", none.Login.OwnerKey);
    }

    // ----- 프로필 옮기기 -----

    [Fact]
    public void 로그인_전_데이터를_일부_옮기지_못하면_알린다()
    {
        var root = TempDir();
        var profile = Path.Combine(root, "users", "github_1");
        File.WriteAllText(Path.Combine(root, "hosts.json"), "h");
        File.WriteAllText(Path.Combine(root, "vault.json"), "v");
        var warnings = new List<string>();
        int moved;
        using (new FileStream(Path.Combine(root, "vault.json"), FileMode.Open, FileAccess.Read, FileShare.None))
            moved = Profiles.ClaimLegacy(root, profile, warnings.Add);
        Assert.Equal(1, moved);
        Assert.True(File.Exists(Path.Combine(profile, "hosts.json")));
        Assert.Single(warnings);
        Assert.Contains("vault.json", warnings[0]);
    }

    // ----- 로그인 도메인 확인 -----

    [Fact]
    public void Google_도메인_규칙은_hd_가_그_도메인인_계정만_허용한다()
    {
        Assert.True(DeviceFlow.GoogleDomainVerified("kim@example.com", "example.com"));
        Assert.False(DeviceFlow.GoogleDomainVerified("kim@example.com", null));           // 회사 이메일로 만든 개인 계정
        Assert.False(DeviceFlow.GoogleDomainVerified("kim@example.com", "other.com"));

        var cfg = new LoginConfig { Provider = LoginProviderKind.Google, AllowedAccounts = { "@example.com", "boss@gmail.com" } };
        Assert.True(cfg.IsAllowed(new LoginIdentity(LoginProviderKind.Google, "kim@example.com", "", "1", DomainVerified: true), false));
        Assert.False(cfg.IsAllowed(new LoginIdentity(LoginProviderKind.Google, "kim@example.com", "", "1", DomainVerified: false), false));
        // 계정을 콕 집어 적은 규칙은 도메인 확인과 상관없다
        Assert.True(cfg.IsAllowed(new LoginIdentity(LoginProviderKind.Google, "boss@gmail.com", "", "2", DomainVerified: false), false));
    }

    [Fact]
    public void Microsoft_개인_계정_테넌트는_도메인_규칙에_쓰지_않는다()
    {
        Assert.False(DeviceFlow.MicrosoftDomainVerified(DeviceFlow.MicrosoftConsumerTenant));
        Assert.False(DeviceFlow.MicrosoftDomainVerified(null));
        Assert.True(DeviceFlow.MicrosoftDomainVerified("72f988bf-86f1-41af-91ab-2d7cd011db47"));
    }

    [Fact]
    public void 로그인_기록에_도메인_확인_결과를_남긴다()
    {
        var cfg = new LoginConfig { Provider = LoginProviderKind.Google, ClientId = "g", AllowedAccounts = { "@example.com" } };
        var personal = LoginState.Create(new LoginIdentity(LoginProviderKind.Google, "kim@example.com", "", "1", DomainVerified: false), cfg, false, DateTime.UtcNow);
        Assert.False(personal.StillAllowed(cfg));
        var managed = LoginState.Create(new LoginIdentity(LoginProviderKind.Google, "kim@example.com", "", "1", DomainVerified: true), cfg, false, DateTime.UtcNow);
        Assert.True(managed.StillAllowed(cfg));
    }

    [Theory]
    [InlineData("authorization_declined", "로그인이 거부되었습니다.")]
    [InlineData("code_expired", "로그인 코드가 만료되었습니다. 다시 시도하세요.")]
    [InlineData("bad_verification_code", "로그인 코드가 올바르지 않거나 이미 쓰였습니다. 다시 시도하세요.")]
    public void Microsoft_오류_코드도_안내로_바꾼다(string code, string expected)
    {
        Assert.Equal(expected, DeviceFlow.Describe(code, null));
    }
}
