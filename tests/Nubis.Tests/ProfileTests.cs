using System;
using System.IO;
using Nubis;
using Xunit;

namespace Nubis.Tests;

public class ProfileTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "Nubis.Tests.profiles", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void 프로필_이름은_제공자와_바뀌지_않는_계정_ID_로_만든다()
    {
        Assert.Equal("github_583231", Profiles.Key(LoginProviderKind.GitHub, "583231", "octocat"));
        Assert.Equal("github_583231", new LoginIdentity(LoginProviderKind.GitHub, "renamed-login", "x", "583231").ProfileKey);   // 아이디를 바꿔도 같은 프로필
        Assert.Equal("google_me@example.com", Profiles.Key(LoginProviderKind.Google, "", "Me@Example.com"));                       // ID 가 없으면 계정 이름
        Assert.Equal("microsoft_a_b_c", Profiles.Key(LoginProviderKind.Microsoft, "a/b\\c", ""));                                    // 경로 문자 제거
        Assert.Equal("github_user", Profiles.Key(LoginProviderKind.GitHub, "..", ""));
        Assert.DoesNotContain("..", Profiles.Key(LoginProviderKind.GitHub, "../../etc", ""));
        var st = new LoginState { Provider = LoginProviderKind.GitHub, Account = "octocat", AccountId = "583231" };
        Assert.Equal("github_583231", st.ProfileKey);
    }

    [Fact]
    public void 로그인을_켜면_로그인한_계정_끄면_소유자_프로필을_쓴다()
    {
        var on = new LoginConfig { Provider = LoginProviderKind.GitHub, ClientId = "x", OwnerKey = "github_1" };
        Assert.Equal("github_2", Profiles.Choose(on, "github_2", _ => true));
        var off = new LoginConfig { Provider = LoginProviderKind.None, OwnerKey = "github_1" };
        Assert.Equal("github_1", Profiles.Choose(off, null, k => k == "github_1"));
        Assert.Null(Profiles.Choose(off, null, _ => false));                                   // 소유자 프로필이 없으면 공통 폴더
        Assert.Null(Profiles.Choose(new LoginConfig(), null, _ => true));                      // 로그인을 한 번도 안 씀
    }

    [Fact]
    public void 기존_데이터는_소유자_프로필로_옮기고_이미_있는_파일은_건드리지_않는다()
    {
        var root = TempDir();
        var users = Path.Combine(root, "users");
        File.WriteAllText(Path.Combine(root, "hosts.json"), "legacy-hosts");
        File.WriteAllText(Path.Combine(root, "vault.json"), "legacy-vault");
        File.WriteAllText(Path.Combine(root, "settings.json"), "global-settings");   // 공통 설정은 옮기지 않음
        Directory.CreateDirectory(Path.Combine(root, "claude"));
        File.WriteAllText(Path.Combine(root, "claude", "CLAUDE.md"), "notes");
        var owner = Path.Combine(users, "github_1");
        Directory.CreateDirectory(owner);
        File.WriteAllText(Path.Combine(owner, "vault.json"), "owner-vault");

        Profiles.ClaimOwner(new LoginConfig { OwnerKey = "github_1" }, owner, root, users);

        Assert.Equal("legacy-hosts", File.ReadAllText(Path.Combine(owner, "hosts.json")));
        Assert.Equal("owner-vault", File.ReadAllText(Path.Combine(owner, "vault.json")));       // 이미 있으면 그대로
        Assert.True(File.Exists(Path.Combine(root, "vault.json")));                            // 옮기지 못한 원본은 남김
        Assert.False(File.Exists(Path.Combine(root, "hosts.json")));
        Assert.True(File.Exists(Path.Combine(root, "settings.json")));
        Assert.Equal("notes", File.ReadAllText(Path.Combine(owner, "claude", "CLAUDE.md")));
    }

    [Fact]
    public void 소유자가_로그인_방식을_바꾸면_예전_프로필을_새_이름으로_옮긴다()
    {
        var root = TempDir();
        var users = Path.Combine(root, "users");
        var old = Path.Combine(users, "github_1");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "hosts.json"), "mine");
        var fresh = Path.Combine(users, "google_99");

        Profiles.ClaimOwner(new LoginConfig { OwnerKey = "google_99", PreviousOwnerKey = "github_1" }, fresh, root, users);

        Assert.Equal("mine", File.ReadAllText(Path.Combine(fresh, "hosts.json")));
        Assert.False(Directory.Exists(old));
    }

    [Fact]
    public void Claude_는_공통_폴더와_다른_계정_프로필도_읽지_못한다()
    {
        var rules = ClaudeGuard.DenyRules(@"C:\Users\a\AppData\Roaming\Nubis\users\github_1", @"C:\Users\a\AppData\Roaming\Nubis");
        Assert.Contains("Read(//c/Users/a/AppData/Roaming/Nubis/users/github_1/hosts.json*)", rules);
        Assert.Contains("Read(//c/Users/a/AppData/Roaming/Nubis/users/**/vault.json*)", rules);
        Assert.Contains("Read(//c/Users/a/AppData/Roaming/Nubis/login.json*)", rules);
        Assert.Contains("Edit(//c/Users/a/AppData/Roaming/Nubis/settings.json*)", rules);         // 로그인 설정 우회 방지
        Assert.Contains("Edit(//c/Users/a/AppData/Roaming/Nubis/users/**/settings.json*)", rules);
        // 자기 작업 폴더(claude\)의 일반 파일은 막지 않는다
        Assert.DoesNotContain(rules, r => r == "Edit(//c/Users/a/AppData/Roaming/Nubis/users/**)");
    }
}
