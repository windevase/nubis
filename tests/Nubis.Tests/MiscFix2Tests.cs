using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>WinRM 재시도·명령 구성, 로그인 유지 재확인, 프로필 옮기기, RDP 드라이브, 클라우드 조회 보조 로직</summary>
public class MiscFix2Tests
{
    // ----- WinRM -----

    [Fact]
    public void WinRM_인증서_불일치_인증_실패_WS_Man_오류_취소는_다른_포트로_재시도하지_않는다()
    {
        var none = CancellationToken.None;
        Assert.True(WinRm.StopRetrying(new WinRmCertException("mismatch"), false, none));
        Assert.True(WinRm.StopRetrying(new WinRmAuthException("401"), false, none));
        Assert.True(WinRm.StopRetrying(new WinRmFatalException("access denied"), false, none));
        Assert.True(WinRm.StopRetrying(new HttpRequestException("x"), shellCreated: true, none));   // 셸 생성 이후 = 중복 실행 방지
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.True(WinRm.StopRetrying(new OperationCanceledException(), false, cts.Token));
        // 연결·TLS 실패, WinRM 이 아닌 응답은 다음 포트·주소로
        Assert.False(WinRm.StopRetrying(new HttpRequestException("connection refused"), false, none));
        Assert.False(WinRm.StopRetrying(new TaskCanceledException(), false, none));                // HttpClient 시간 초과
        Assert.False(WinRm.StopRetrying(new InvalidOperationException("HTTP 404"), false, none));
    }

    [Theory]
    [InlineData("10.0.0.1", "10.0.0.1")]
    [InlineData("win01.corp.example", "win01.corp.example")]
    [InlineData("fe80::1", "[fe80::1]")]
    [InlineData("[2001:db8::5]", "[2001:db8::5]")]
    public void WinRM_URL_의_IPv6_주소는_대괄호로_감싼다(string host, string expected)
    {
        Assert.Equal(expected, WinRm.UrlHost(host));
        var u = new Uri($"https://{WinRm.UrlHost(host)}:{WinRm.HttpsPort}/wsman");
        Assert.Equal(WinRm.HttpsPort, u.Port);
    }

    [Fact]
    public void WinRM_PowerShell_은_cmd_를_거치지_않아_긴_스크립트도_보낸다()
    {
        var (exe, args, skip) = WinRm.BuildCommand("Get-Date", powershell: true);
        Assert.Equal("powershell.exe", exe);
        Assert.True(skip);
        Assert.StartsWith("-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ", args);

        // 5000 자 스크립트: 인코딩하면 cmd.exe 한도(8191)를 넘지만 CreateProcess 한도 안
        var (_, longArgs, _) = WinRm.BuildCommand(new string('a', 5000), powershell: true);
        Assert.True(longArgs!.Length > WinRm.MaxCmdCommandLine);
        Assert.True(longArgs.Length < WinRm.MaxProcessCommandLine);

        // 한도를 넘으면 서버에 보내기 전에 분명한 오류 (재시도하지 않는 Fatal)
        Assert.Throws<WinRmFatalException>(() => WinRm.BuildCommand(new string('a', 20000), powershell: true));

        // cmd 모드는 그대로 cmd.exe 로
        var (cmd, cmdArgs, cmdSkip) = WinRm.BuildCommand("ipconfig /all", powershell: false);
        Assert.Equal("ipconfig /all", cmd);
        Assert.Null(cmdArgs);
        Assert.False(cmdSkip);
    }

    // ----- 시작 화면 로그인 -----

    [Fact]
    public void 로그인_유지와_오프라인_허용은_지금_허용_목록에_있는_계정만()
    {
        var now = DateTime.UtcNow;
        var cfg = new LoginConfig { Provider = LoginProviderKind.GitHub, ClientId = "cid", AllowedAccounts = new() { "octocat" } };
        var st = new LoginState { Provider = LoginProviderKind.GitHub, ClientId = "cid", Account = "octocat", AccountId = "1", LastSuccessUtc = now.AddHours(-1) };
        Assert.True(st.CanEnterWithout(cfg, TimeSpan.FromHours(24), now));

        // 허용 목록에서 지운 계정은 유지 시간 안이어도 다시 로그인
        var removed = cfg.Clone();
        removed.AllowedAccounts = new() { "someone-else" };
        Assert.True(st.Within(removed, TimeSpan.FromHours(24), now));
        Assert.False(st.CanEnterWithout(removed, TimeSpan.FromHours(24), now));

        // Organization 멤버로만 허용되는 설정은 로그인 없이 확인할 수 없어 인정하지 않음
        var orgOnly = new LoginConfig { Provider = LoginProviderKind.GitHub, ClientId = "cid", GitHubOrg = "acme" };
        Assert.False(st.StillAllowed(orgOnly));

        // 도메인 허용
        var google = new LoginConfig { Provider = LoginProviderKind.Google, ClientId = "g", AllowedAccounts = new() { "@example.com" } };
        Assert.True(new LoginState { Provider = LoginProviderKind.Google, ClientId = "g", Account = "me@example.com" }.StillAllowed(google));
        Assert.False(new LoginState { Provider = LoginProviderKind.Google, ClientId = "g", Account = "" }.StillAllowed(google));
    }

    // ----- 프로필 옮기기 -----

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "Nubis.Tests.profiles2", Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void 예전_프로필을_옮기지_못하면_빈_새_폴더를_만들지_않고_다음에_다시_옮긴다()
    {
        var root = TempDir();
        var users = Path.Combine(root, "users");
        var old = Path.Combine(users, "github_1");
        Directory.CreateDirectory(old);
        var hosts = Path.Combine(old, "hosts.json");
        File.WriteAllText(hosts, "mine");
        var fresh = Path.Combine(users, "google_9");
        var cfg = new LoginConfig { OwnerKey = "google_9", PreviousOwnerKey = "github_1" };
        var warnings = new List<string>();

        using (new FileStream(hosts, FileMode.Open, FileAccess.Read, FileShare.None))   // 다른 프로그램이 쥐고 있어 폴더 이동 실패
        {
            Assert.False(Profiles.ClaimOwner(cfg, fresh, root, users, warnings.Add));
        }
        Assert.Single(warnings);
        Assert.False(Directory.Exists(fresh));          // 빈 새 폴더를 만들지 않음
        Assert.True(File.Exists(hosts));

        // 다음 실행: 다시 옮긴다
        Assert.True(Profiles.ClaimOwner(cfg, fresh, root, users, warnings.Add));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(fresh, "hosts.json")));
        Assert.False(Directory.Exists(old));
    }

    [Fact]
    public void 데이터_없는_새_폴더가_있으면_비켜_두고_예전_프로필을_옮긴다()
    {
        var root = TempDir();
        var users = Path.Combine(root, "users");
        var old = Path.Combine(users, "github_1");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "hosts.json"), "mine");
        var fresh = Path.Combine(users, "google_9");
        Directory.CreateDirectory(fresh);
        File.WriteAllText(Path.Combine(fresh, "settings.json"), "{}");   // 실패한 실행에서 생긴 설정만 있는 폴더
        var warnings = new List<string>();

        Assert.True(Profiles.ClaimOwner(new LoginConfig { OwnerKey = "google_9", PreviousOwnerKey = "github_1" }, fresh, root, users, warnings.Add));
        Assert.Empty(warnings);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(fresh, "hosts.json")));
        Assert.Single(Directory.GetDirectories(users, "google_9.empty-*"));   // 지우지 않고 남김
    }

    [Fact]
    public void 두_프로필_모두_데이터가_있으면_합치지_않고_알린다()
    {
        var root = TempDir();
        var users = Path.Combine(root, "users");
        var old = Path.Combine(users, "github_1");
        var fresh = Path.Combine(users, "google_9");
        Directory.CreateDirectory(old);
        Directory.CreateDirectory(fresh);
        File.WriteAllText(Path.Combine(old, "hosts.json"), "old");
        File.WriteAllText(Path.Combine(fresh, "hosts.json"), "new");
        var warnings = new List<string>();

        Assert.False(Profiles.ClaimOwner(new LoginConfig { OwnerKey = "google_9", PreviousOwnerKey = "github_1" }, fresh, root, users, warnings.Add));
        Assert.Single(warnings);
        Assert.Equal("old", File.ReadAllText(Path.Combine(old, "hosts.json")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(fresh, "hosts.json")));
    }

    // ----- RDP Default.rdp 드라이브 -----

    [Theory]
    [InlineData("drivestoredirect:s:*\n", true)]
    [InlineData("drivestoredirect:s:C:\\;\n", false)]           // 드라이브 하나만 고른 것 → 전체 공유로 넓히지 않음
    [InlineData("drivestoredirect:s:C:\\;D:\\;DynamicDrives\n", false)]
    [InlineData("drivestoredirect:s:DynamicDrives\n", false)]
    [InlineData("drivestoredirect:s:\n", false)]
    [InlineData("redirectdrives:i:1\n", true)]
    [InlineData("redirectdrives:i:1\ndrivestoredirect:s:C:\\;\n", false)]
    [InlineData("", false)]
    public void Default_rdp_는_모든_드라이브를_고른_경우만_드라이브를_리디렉션한다(string rdp, bool expected) =>
        Assert.Equal(expected, RdpDefaults.Parse(rdp).RedirectDrives);

    // ----- 클라우드 -----

    [Fact]
    public void AWS_SSO_안내는_SSO_설정이_있는_프로필의_토큰_오류만()
    {
        Assert.True(AwsProvider.HasSsoSettings("https://x.awsapps.com/start", null));
        Assert.True(AwsProvider.HasSsoSettings(null, "my-sso"));
        Assert.False(AwsProvider.HasSsoSettings("", " "));
        Assert.True(AwsProvider.LooksLikeSsoTokenError(new InvalidOperationException("outer", new Exception("The SSO session has expired"))));
        Assert.False(AwsProvider.LooksLikeSsoTokenError(new HttpRequestException("No such host is known")));
    }

    [Theory]
    [InlineData("RUNNING", false)]
    [InlineData("STOPPED", false)]
    [InlineData("PENDING", false)]
    [InlineData("SHUTDOWN", false)]        // 격리(복구 가능) — 빼면 동기화가 호스트·사용자 설정을 지운다 (AppFix3)
    [InlineData("TERMINATING", true)]
    [InlineData("LAUNCH_FAILED", true)]
    [InlineData("shutdown", false)]
    [InlineData("terminating", true)]
    public void Tencent_는_삭제중_생성실패_인스턴스만_빼낸다(string state, bool gone) =>
        Assert.Equal(gone, TencentProvider.IsGone(state));

    [Fact]
    public void Azure_건너뛴_테넌트는_사유_첫_줄을_짧게_남긴다()
    {
        Assert.Equal("t1: AADSTS50076 MFA required", AzureProvider.SkippedTenant("t1", "AADSTS50076 MFA required\nTrace ID: x"));
        var longReason = AzureProvider.SkippedTenant("t2", new string('x', 500));
        Assert.True(longReason.Length < 200);
        Assert.EndsWith("…", longReason);
    }

    // ----- 화면 문구 -----

    [Fact]
    public void 자리표시자가_잘못된_문구도_L_F_가_예외를_내지_않는다()
    {
        Assert.Equal("값 x", L.F("값 {0}", "x"));
        Assert.Equal("잘못된 {0} {", L.F("잘못된 {0} {", "x"));   // 원문 그대로
    }
}
