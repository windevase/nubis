using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>settings.json .bak 복구 제외 · 손상 파일 경고 한 번만 · 비밀번호 비교 · Tencent 상태 · RDP 드라이브 · 로그인 기록 · 소유자 프로필 폴더</summary>
public class AppFix3Tests
{
    private static string TempFile(string name) =>
        Path.Combine(AppPaths.DataDir, $"{name}-{Guid.NewGuid():N}.json");

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "Nubis.Tests.profiles3", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    // ----- 저장 -----

    [Fact]
    public void settings_처럼_bak_복구를_끈_파일은_손상돼도_예전_설정이_아니라_기본값으로_시작한다()
    {
        var path = TempFile("settings");
        SafeFile.WriteAtomic(path, "[\"login-on\"]");
        SafeFile.WriteAtomic(path, "[\"login-on-2\"]");          // .bak = login-on
        File.WriteAllText(path, "[\"login-off\"");                 // 사용자가 손으로 고치다 JSON 을 틀림

        var list = SafeFile.Load(path, s => JsonSerializer.Deserialize<string[]>(s), () => Array.Empty<string>(), restoreBackup: false);
        Assert.Empty(list);                                         // 기본값
        Assert.Equal("[\"login-off\"", File.ReadAllText(path));     // 원본을 .bak 으로 덮어쓰지 않음
        Assert.NotEmpty(Directory.GetFiles(AppPaths.DataDir, Path.GetFileName(path) + ".corrupt-*"));

        // 기본(복구 켬)은 예전처럼 .bak 으로
        Assert.Equal("login-on", SafeFile.Load(path, s => JsonSerializer.Deserialize<string[]>(s), () => Array.Empty<string>()).Single());
    }

    [Fact]
    public void bak_을_원본_자리로_되돌려_쓰지_못해도_같은_손상_파일은_경고를_한_번만_남긴다()
    {
        var path = TempFile("locked");
        SafeFile.WriteProtected(path, "[\"old\"]");
        SafeFile.WriteProtected(path, "[\"new\"]");
        File.WriteAllBytes(path, Array.Empty<byte>());

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))   // 읽기는 되지만 덮어쓰기(File.Copy)는 실패
        {
            // LoadWarnings 는 병렬로 도는 다른 테스트도 쓰므로 이 파일 이름이 든 경고만 센다
            var name = Path.GetFileName(path);
            int Mine() { lock (SafeFile.LoadWarnings) return SafeFile.LoadWarnings.Count(w => w.Contains(name)); }
            var first = SafeFile.LoadProtected(path, s => JsonSerializer.Deserialize<string[]>(s), () => Array.Empty<string>(), out var failed1);
            Assert.False(failed1);
            Assert.Equal("old", first.Single());
            var afterFirst = Mine();
            Assert.Equal(1, afterFirst);
            var corrupt = Directory.GetFiles(AppPaths.DataDir, Path.GetFileName(path) + ".corrupt-*").Length;

            var second = SafeFile.LoadProtected(path, s => JsonSerializer.Deserialize<string[]>(s), () => Array.Empty<string>(), out var failed2);
            Assert.False(failed2);
            Assert.Equal("old", second.Single());
            Assert.Equal(afterFirst, Mine());
            Assert.Equal(corrupt, Directory.GetFiles(AppPaths.DataDir, Path.GetFileName(path) + ".corrupt-*").Length);
        }
    }

    // ----- 비밀번호 비교 -----

    [Fact]
    public void 풀리지_않는_암호문은_둘_다_빈_값으로_보지_않고_암호문으로_비교한다()
    {
        var foreignA = Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });   // 다른 PC 에서 만든 암호문 흉내
        var foreignB = Convert.ToBase64String(new byte[] { 9, 9, 9, 9, 9, 9, 9, 9 });
        Assert.Equal("", Secret.Unprotect(foreignA));
        Assert.False(Secret.TryUnprotect(foreignA, out _));

        Assert.False(HostEntry.SamePassword(foreignA, foreignB));   // 예전: 둘 다 "" → 같다고 봄
        Assert.True(HostEntry.SamePassword(foreignA, foreignA));
        Assert.False(HostEntry.SamePassword(foreignA, Secret.Protect("")));   // 한쪽만 풀림 → 다름
        Assert.False(HostEntry.SamePassword(foreignA, null));
        Assert.True(HostEntry.SamePassword(null, ""));
        Assert.True(HostEntry.SamePassword(Secret.Protect("pw"), Secret.Protect("pw")));
    }

    // ----- Tencent -----

    [Theory]
    [InlineData("RUNNING", false)]
    [InlineData("STOPPED", false)]
    [InlineData("SHUTDOWN", false)]        // 격리(복구 가능) — 남겨 두어 사용자 설정이 지워지지 않게
    [InlineData("shutdown", false)]
    [InlineData("TERMINATING", true)]
    [InlineData("LAUNCH_FAILED", true)]
    public void Tencent_는_삭제중_생성실패만_목록에서_뺀다(string state, bool gone) =>
        Assert.Equal(gone, TencentProvider.IsGone(state));

    // ----- RDP -----

    [Theory]
    [InlineData("redirectdrives:i:1\ndrivestoredirect:s:\n", true)]    // 값이 비면 redirectdrives 를 따른다
    [InlineData("redirectdrives:i:0\ndrivestoredirect:s:\n", false)]
    [InlineData("drivestoredirect:s:\n", false)]
    [InlineData("redirectdrives:i:1\ndrivestoredirect:s:C:\\;\n", false)]
    [InlineData("redirectdrives:i:0\ndrivestoredirect:s:*\n", true)]
    public void Default_rdp_의_빈_drivestoredirect_는_redirectdrives_를_따른다(string rdp, bool expected) =>
        Assert.Equal(expected, RdpDefaults.Parse(rdp).RedirectDrives);

    // ----- 로그인 기록 -----

    [Fact]
    public void Organization_멤버로만_허용돼도_마지막_온라인_로그인_기록으로_유지_기간_안에는_들어간다()
    {
        var now = DateTime.UtcNow;
        var cfg = new LoginConfig { Provider = LoginProviderKind.GitHub, ClientId = "cid", GitHubOrg = "acme" };
        var who = new LoginIdentity(LoginProviderKind.GitHub, "octocat", "", "1");

        var st = LoginState.Create(who, cfg, orgMember: true, now.AddHours(-2));
        Assert.True(st.OrgMember);
        Assert.True(st.StillAllowed(cfg));
        Assert.True(st.CanEnterWithout(cfg, TimeSpan.FromHours(24), now));
        Assert.False(st.CanEnterWithout(cfg, TimeSpan.FromHours(1), now));        // 기간 밖
        Assert.True(st.CanEnterWithout(cfg, TimeSpan.FromDays(7), now, offline: true));

        // Organization 을 바꾸면 인정하지 않음
        var other = cfg.Clone();
        other.GitHubOrg = "other";
        Assert.False(st.StillAllowed(other));
        // 대소문자·공백은 무시
        var spaced = cfg.Clone();
        spaced.GitHubOrg = " ACME ";
        Assert.True(st.StillAllowed(spaced));

        // 멤버 확인 기록이 없으면 예전처럼 다시 로그인
        Assert.False(LoginState.Create(who, cfg, orgMember: false, now).StillAllowed(cfg));
        // JSON 저장·읽기 후에도 유지
        var round = JsonSerializer.Deserialize<LoginState>(JsonSerializer.Serialize(st))!;
        Assert.True(round.StillAllowed(cfg));
    }

    [Fact]
    public void 소유자는_허용_목록에_없어도_오프라인_기간_안에는_들어간다()
    {
        var now = DateTime.UtcNow;
        var who = new LoginIdentity(LoginProviderKind.GitHub, "owner", "", "42");
        var cfg = new LoginConfig
        {
            Provider = LoginProviderKind.GitHub, ClientId = "cid", GitHubOrg = "acme", AllowedAccounts = new() { "someone" },
            OwnerKey = who.ProfileKey,
        };
        var st = LoginState.Create(who, cfg, orgMember: false, now.AddDays(-1));

        Assert.True(st.CanEnterWithout(cfg, TimeSpan.FromDays(3), now, offline: true));
        Assert.False(st.CanEnterWithout(cfg, TimeSpan.FromHours(12), now, offline: true));   // 오프라인 기간 밖
        Assert.False(st.CanEnterWithout(cfg, TimeSpan.FromDays(3), now));                    // 로그인 유지는 허용 목록 기준 그대로

        var notOwner = cfg.Clone();
        notOwner.OwnerKey = "github_7";
        Assert.False(st.CanEnterWithout(notOwner, TimeSpan.FromDays(3), now, offline: true));
    }

    // ----- 소유자 프로필 폴더 -----

    [Fact]
    public void 예전_프로필을_옮기지_못하면_이번_실행은_예전_폴더를_쓴다()
    {
        var root = TempDir();
        var users = Path.Combine(root, "users");
        var old = Path.Combine(users, "github_1");
        Directory.CreateDirectory(old);
        var hosts = Path.Combine(old, "hosts.json");
        File.WriteAllText(hosts, "mine");
        var fresh = Path.Combine(users, "google_9");
        var cfg = new LoginConfig { OwnerKey = "google_9", PreviousOwnerKey = "github_1" };

        using (new FileStream(hosts, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var claimed = Profiles.ClaimOwner(cfg, fresh, root, users, null);
            Assert.False(claimed);
            Assert.Equal(old, Profiles.DirForRun(cfg, fresh, users, claimed));
        }

        // 옮겼으면 새 폴더
        Assert.Equal(fresh, Profiles.DirForRun(cfg, fresh, users, claimed: true));
        // 두 쪽 다 데이터가 있어 합치지 않은 경우도 새 폴더 (데이터가 있음)
        Directory.CreateDirectory(fresh);
        File.WriteAllText(Path.Combine(fresh, "hosts.json"), "new");
        Assert.Equal(fresh, Profiles.DirForRun(cfg, fresh, users, claimed: false));
        // 예전 소유자 기록이 없으면 새 폴더
        var empty = Path.Combine(users, "github_5");
        Assert.Equal(empty, Profiles.DirForRun(new LoginConfig { OwnerKey = "github_5" }, empty, users, claimed: false));
    }
}
