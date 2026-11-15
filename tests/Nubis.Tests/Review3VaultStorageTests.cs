using System;
using System.Collections.Generic;
using System.IO;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>리뷰 3차: 원본 사본을 못 만든 파일의 저장 막힘 해소, Vault 선택 창 "지정 해제" 값, 도메인 확인 기록 이전 흐름</summary>
public class Review3VaultStorageTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "Nubis.Tests.review3", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    private static List<string> LoadList(string path, out bool failed) =>
        SafeFile.LoadProtected(path, s => System.Text.Json.JsonSerializer.Deserialize<List<string>>(s), () => new List<string>(), out failed);

    // ----- 원본 사본을 만들지 못한 파일 -----

    [Fact]
    public void 사본을_못_만든_파일은_잠금이_풀리면_다시_사본을_만들고_저장을_허용한다()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "locked.json");
        File.WriteAllText(path, "[\"a\"]");
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // 잠긴 동안: 읽기 실패 → 사본도 실패 → 덮어쓰기 금지
            LoadList(path, out var failed);
            Assert.True(failed);
            Assert.False(SafeFile.OriginalPreserved(path));
            Assert.False(SafeFile.EnsureOriginalPreserved(path));
        }
        Assert.Empty(Directory.GetFiles(dir, "locked.json.corrupt-*"));

        // 잠금이 풀리면 저장 직전 확인에서 사본을 만들고 허용 (이번 실행 내내 막히지 않게)
        Assert.True(SafeFile.EnsureOriginalPreserved(path));
        Assert.True(SafeFile.OriginalPreserved(path));
        var copy = Assert.Single(Directory.GetFiles(dir, "locked.json.corrupt-*"));
        Assert.Equal("[\"a\"]", File.ReadAllText(copy));
    }

    [Fact]
    public void 사본을_못_만든_파일이_없어졌으면_잃을_것이_없어_저장을_허용한다()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "gone.json");
        File.WriteAllText(path, "[\"a\"]");
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            LoadList(path, out _);
        Assert.False(SafeFile.OriginalPreserved(path));

        File.Delete(path);
        Assert.True(SafeFile.EnsureOriginalPreserved(path));
    }

    [Fact]
    public void 읽기_실패가_없던_파일과_처음_실행은_저장이_막히지_않는다()
    {
        var dir = TempDir();
        Assert.True(SafeFile.EnsureOriginalPreserved(Path.Combine(dir, "never.json")));   // 파일 없음 (처음 실행)

        var ok = Path.Combine(dir, "ok.json");
        File.WriteAllText(ok, "[\"x\"]");
        Assert.Equal(new[] { "x" }, LoadList(ok, out var failed));
        Assert.False(failed);
        Assert.True(SafeFile.EnsureOriginalPreserved(ok));
        Assert.Empty(Directory.GetFiles(dir, "*.corrupt-*"));   // 멀쩡한 파일은 사본을 만들지 않는다
    }

    [Fact]
    public void 오래전_corrupt_사본이_있어도_지금_읽은_파일이_정상이면_저장이_막히지_않는다()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "data.json");
        File.WriteAllText(path + ".corrupt-20200101-000000", "{ old broken");
        File.WriteAllText(path, "[\"y\"]");
        LoadList(path, out var failed);
        Assert.False(failed);
        Assert.True(SafeFile.OriginalPreserved(path));
        Assert.True(SafeFile.EnsureOriginalPreserved(path));
    }

    // ----- Vault 선택 창 -----

    [Fact]
    public void 지정_해제_값은_실제_계정_Id_와_겹치지_않는다()
    {
        Assert.Equal(Guid.Empty, VaultPickerDialog.Cleared);
        Assert.NotEqual(VaultPickerDialog.Cleared, new VaultEntry().Id);
        Assert.Null(VaultStore.Get(VaultPickerDialog.Cleared));
    }

    // ----- 로그인 도메인 확인 기록 -----

    [Fact]
    public void 도메인_확인_기록이_없는_예전_사용자는_한_번_다시_로그인하면_계속_들어간다()
    {
        var cfg = new LoginConfig { Provider = LoginProviderKind.Google, ClientId = "g", AllowedAccounts = new() { "@example.com" }, RememberHours = 24 };
        var now = DateTime.UtcNow;
        var old = new LoginState { Provider = LoginProviderKind.Google, ClientId = "g", Account = "me@example.com", AccountId = "1", LastSuccessUtc = now };
        Assert.False(old.CanEnterWithout(cfg, TimeSpan.FromHours(24), now));   // 예전 기록 → 다시 로그인

        // Workspace 계정(hd 일치)으로 다시 로그인한 기록은 인정
        var who = new LoginIdentity(LoginProviderKind.Google, "me@example.com", "me", "1", DeviceFlow.GoogleDomainVerified("me@example.com", "example.com"));
        Assert.True(cfg.IsAllowed(who, orgMember: false));
        var fresh = LoginState.Create(who, cfg, false, now);
        Assert.True(fresh.DomainVerified);
        Assert.True(fresh.CanEnterWithout(cfg, TimeSpan.FromHours(24), now));
    }

    [Fact]
    public void 도메인_확인_기록이_없어도_소유자는_오프라인으로_들어가고_정확한_계정_허용은_그대로다()
    {
        var state = new LoginState { Provider = LoginProviderKind.Microsoft, ClientId = "m", Account = "me@example.com", AccountId = "oid1", Tenant = "organizations", LastSuccessUtc = DateTime.UtcNow };
        var cfg = new LoginConfig { Provider = LoginProviderKind.Microsoft, ClientId = "m", AllowedAccounts = new() { "@example.com" } };
        Assert.False(state.StillAllowed(cfg));
        cfg.OwnerKey = state.ProfileKey;
        Assert.True(state.StillAllowed(cfg, offline: true));

        var exact = new LoginConfig { Provider = LoginProviderKind.Microsoft, ClientId = "m", AllowedAccounts = new() { "ME@example.com" } };
        Assert.True(state.StillAllowed(exact));   // 계정을 정확히 적은 규칙은 도메인 확인과 상관없다
    }

    [Fact]
    public void GitHub_예전_기록은_도메인_확인과_상관없이_인정된다()
    {
        var state = new LoginState { Provider = LoginProviderKind.GitHub, ClientId = "c", Account = "octocat", AccountId = "1", LastSuccessUtc = DateTime.UtcNow };
        var cfg = new LoginConfig { Provider = LoginProviderKind.GitHub, ClientId = "c", AllowedAccounts = new() { "octocat" } };
        Assert.True(state.StillAllowed(cfg));
    }

    [Fact]
    public void Microsoft_개인_계정_테넌트와_tid_없음은_도메인_미확인이다()
    {
        Assert.False(DeviceFlow.MicrosoftDomainVerified(DeviceFlow.MicrosoftConsumerTenant.ToUpperInvariant()));
        Assert.False(DeviceFlow.MicrosoftDomainVerified(null));
        Assert.False(DeviceFlow.MicrosoftDomainVerified(" "));
        Assert.True(DeviceFlow.MicrosoftDomainVerified(Guid.NewGuid().ToString()));
        Assert.False(DeviceFlow.GoogleDomainVerified("me@example.com", null));
        Assert.False(DeviceFlow.GoogleDomainVerified("me@evil.com", "example.com"));
        Assert.False(DeviceFlow.GoogleDomainVerified("me@sub.example.com", "example.com"));
    }
}
