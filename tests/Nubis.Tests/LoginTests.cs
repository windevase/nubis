using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Nubis;
using Xunit;

namespace Nubis.Tests;

public class ProtectedFileTests
{
    [Fact]
    public void 서버_목록_파일은_통째로_암호화되어_IP_와_이름이_보이지_않는다()
    {
        var hosts = new[] { new HostEntry { Label = "secret-web-01", Address = "10.20.30.40", Username = "deploy" } }.ToList();
        HostStore.Save(hosts);
        var bytes = File.ReadAllBytes(AppPaths.HostsFile);
        var raw = Encoding.UTF8.GetString(bytes);
        Assert.True(SafeFile.IsProtected(bytes));
        Assert.DoesNotContain("10.20.30.40", raw);
        Assert.DoesNotContain("secret-web-01", raw);
        Assert.DoesNotContain("deploy", raw);
        var back = HostStore.Load();
        Assert.Equal("10.20.30.40", back.Single().Address);
    }

    [Fact]
    public void 예전_평문_파일은_읽은_뒤_바로_암호화한다()
    {
        var path = Path.Combine(AppPaths.DataDir, "legacy-test.json");
        File.WriteAllText(path, "[\"10.0.0.9\"]");
        var list = SafeFile.LoadProtected(path, s => JsonSerializer.Deserialize<string[]>(s), () => Array.Empty<string>());
        Assert.Equal("10.0.0.9", list.Single());
        var bytes = File.ReadAllBytes(path);
        Assert.True(SafeFile.IsProtected(bytes));
        Assert.DoesNotContain("10.0.0.9", Encoding.UTF8.GetString(bytes));
        // BOM 이 붙은 평문도 읽는다
        File.WriteAllText(path, "[\"x\"]", new UTF8Encoding(true));
        Assert.Equal("x", SafeFile.LoadProtected(path, s => JsonSerializer.Deserialize<string[]>(s), () => Array.Empty<string>()).Single());
    }

    [Fact]
    public void 풀_수_없는_파일은_원본을_보관하고_빈_값으로_시작한다()
    {
        var path = Path.Combine(AppPaths.DataDir, "foreign-test.json");
        // 다른 PC·계정에서 만든 것처럼: 표식은 있는데 DPAPI 로 풀리지 않는 내용
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("NUBIS-DPAPI1\n").Concat(new byte[] { 1, 2, 3, 4, 5 }).ToArray());
        var before = SafeFile.LoadWarnings.Count;
        var list = SafeFile.LoadProtected(path, s => JsonSerializer.Deserialize<string[]>(s), () => Array.Empty<string>());
        Assert.Empty(list);
        Assert.True(SafeFile.LoadWarnings.Count > before);
        Assert.NotEmpty(Directory.GetFiles(AppPaths.DataDir, "foreign-test.json.corrupt-*"));
    }

    [Fact]
    public void 로그인_기록과_known_hosts_도_Claude_패널에서_막는다()
    {
        var rules = ClaudeGuard.DenyRules(@"C:\Users\a\AppData\Roaming\Nubis");
        Assert.Contains("Read(//c/Users/a/AppData/Roaming/Nubis/login.json*)", rules);
        Assert.Contains("Read(//c/Users/a/AppData/Roaming/Nubis/known_hosts.json*)", rules);
    }
}

public class LoginTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static LoginIdentity Who(string account, LoginProviderKind p = LoginProviderKind.GitHub) => new(p, account, account, "1");

    [Fact]
    public void 허용_계정은_대소문자를_무시하고_도메인_규칙을_지원한다()
    {
        var c = new LoginConfig { Provider = LoginProviderKind.GitHub, AllowedAccounts = { "Octocat", "@example.com" } };
        Assert.True(c.IsAllowed(Who("octocat"), false));
        Assert.True(c.IsAllowed(Who("kim@Example.com", LoginProviderKind.Google), false));
        Assert.False(c.IsAllowed(Who("kim@example.com.evil.com", LoginProviderKind.Google), false));
        Assert.False(c.IsAllowed(Who("someone"), false));
        Assert.False(c.IsAllowed(Who("octocat2"), false));
        Assert.True(c.IsAllowed(Who("someone"), orgMember: true));   // Organization 멤버
        Assert.Equal(new[] { "a", "b", "c" }, LoginConfig.ParseList(" a, b;c  a\n"));
    }

    [Fact]
    public void 설정_검증()
    {
        Assert.Null(new LoginConfig { Provider = LoginProviderKind.None }.Validate());
        Assert.NotNull(new LoginConfig { Provider = LoginProviderKind.GitHub, AllowedAccounts = { "a" } }.Validate());        // Client ID 없음
        Assert.NotNull(new LoginConfig { Provider = LoginProviderKind.GitHub, ClientId = "x" }.Validate());                   // 허용 대상 없음
        Assert.Null(new LoginConfig { Provider = LoginProviderKind.GitHub, ClientId = "x", GitHubOrg = "my-org" }.Validate()); // Org 만으로도 됨
        Assert.NotNull(new LoginConfig { Provider = LoginProviderKind.Google, ClientId = "x", AllowedAccounts = { "a@b.c" } }.Validate()); // Secret 없음
        Assert.NotNull(new LoginConfig { Provider = LoginProviderKind.GitHub, ClientId = "x", AllowedAccounts = { "a" }, RememberHours = -1 }.Validate());
        Assert.False(new LoginConfig { Provider = LoginProviderKind.GitHub }.Enabled);
        Assert.True(new LoginConfig { Provider = LoginProviderKind.GitHub, ClientId = "Iv1.x" }.Enabled);
    }

    [Fact]
    public void 로그인_유지와_오프라인_허용_기간()
    {
        var cfg = new LoginConfig { Provider = LoginProviderKind.GitHub, ClientId = "Iv1.abc" };
        var now = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        var st = new LoginState { Provider = LoginProviderKind.GitHub, ClientId = "Iv1.abc", Account = "octocat", LastSuccessUtc = now.AddHours(-3) };
        Assert.True(st.Within(cfg, TimeSpan.FromHours(8), now));
        Assert.False(st.Within(cfg, TimeSpan.FromHours(2), now));
        Assert.False(st.Within(cfg, TimeSpan.Zero, now));                                     // 0 = 매번 로그인
        Assert.False(st.Within(new LoginConfig { Provider = LoginProviderKind.GitHub, ClientId = "other" }, TimeSpan.FromDays(1), now)); // 다른 앱
        Assert.False(st.Within(new LoginConfig { Provider = LoginProviderKind.Google, ClientId = "Iv1.abc" }, TimeSpan.FromDays(1), now));
        var future = new LoginState { Provider = LoginProviderKind.GitHub, ClientId = "Iv1.abc", Account = "a", LastSuccessUtc = now.AddHours(1) };
        Assert.False(future.Within(cfg, TimeSpan.FromDays(1), now));                          // 시계를 되돌린 경우
    }

    [Fact]
    public void 로그인_기록은_암호화해_저장한다()
    {
        var cfg = new LoginConfig { Provider = LoginProviderKind.GitHub, ClientId = "Iv1.test" };
        LoginState.Save(new LoginIdentity(LoginProviderKind.GitHub, "octocat", "w", "1"), cfg);
        var raw = File.ReadAllText(Path.Combine(AppPaths.DataDir, LoginState.FileName));
        Assert.DoesNotContain("octocat", raw);
        Assert.Equal("octocat", LoginState.Load()!.Account);
        LoginState.Clear();
        Assert.Null(LoginState.Load());
    }

    [Fact]
    public void Device_Flow_응답을_제공자별로_읽는다()
    {
        var gh = DeviceFlow.ParseDeviceCode(J("""{"device_code":"d1","user_code":"ABCD-1234","verification_uri":"https://github.com/login/device","expires_in":899,"interval":5}"""));
        Assert.Equal("ABCD-1234", gh.UserCode);
        Assert.Equal(5, gh.IntervalSec);
        var google = DeviceFlow.ParseDeviceCode(J("""{"device_code":"d2","user_code":"GQVQ-JKEC","verification_url":"https://www.google.com/device","expires_in":1800,"interval":5}"""));
        Assert.Equal("https://www.google.com/device", google.VerificationUri);
        Assert.Throws<LoginException>(() => DeviceFlow.ParseDeviceCode(J("""{"error":"device_flow_disabled"}""")));
        Assert.Throws<LoginException>(() => DeviceFlow.ParseDeviceCode(J("""{"message":"Not Found"}""")));

        Assert.Null(DeviceFlow.ParseTokenPoll(J("""{"error":"authorization_pending"}""")).Token);
        Assert.True(DeviceFlow.ParseTokenPoll(J("""{"error":"slow_down","interval":10}""")).SlowDown);
        Assert.NotNull(DeviceFlow.ParseTokenPoll(J("""{"access_token":"gho_x","token_type":"bearer"}""")).Token);
        var denied = Assert.Throws<LoginException>(() => DeviceFlow.ParseTokenPoll(J("""{"error":"access_denied"}""")));
        Assert.Contains("거부", denied.Message);
        Assert.Contains("Device Flow", DeviceFlow.Describe("device_flow_disabled", null));
        // 실제 GitHub 응답: 없는 Client ID → {"error":"Not Found"} (HTTP 404)
        var nf = Assert.Throws<LoginException>(() => DeviceFlow.ParseDeviceCode(J("""{"error":"Not Found"}""")));
        Assert.Contains("Client ID", nf.Message);
    }

    [Fact]
    public void Microsoft_id_token_에서_계정을_읽는다()
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var jwt = B64("""{"alg":"RS256"}""") + "." + B64("""{"preferred_username":"kim@example.com","name":"김","oid":"o1"}""") + ".sig";
        var claims = DeviceFlow.JwtPayload(jwt);
        Assert.Equal("kim@example.com", claims.GetProperty("preferred_username").GetString());
        Assert.Throws<LoginException>(() => DeviceFlow.JwtPayload("nodots"));
    }

    [Theory]
    [InlineData("https://github.com/login/device", true)]
    [InlineData("https://www.google.com/device", true)]
    [InlineData("https://microsoft.com/devicelogin", true)]
    [InlineData("https://login.microsoftonline.com/common/oauth2/deviceauth", true)]
    [InlineData("http://github.com/login/device", false)]
    [InlineData("https://evilgithub.com/login/device", false)]
    [InlineData("https://github.com.evil.io/device", false)]
    public void 브라우저는_로그인_제공자_주소만_연다(string url, bool ok) => Assert.Equal(ok, LoginWindow.IsTrustedLoginUrl(new Uri(url)));

    [Fact]
    public void 로그인_설정은_Claude_가_바꿀_수_없다() => Assert.Null(SettingsCatalog.Find("Login"));
}
