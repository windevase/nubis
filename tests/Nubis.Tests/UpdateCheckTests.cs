using System;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>새 버전 알림 — 버전 비교 · 응답 해석 · 확인 주기 (네트워크 없이)</summary>
public class UpdateCheckTests
{
    [Theory]
    [InlineData("0.6.1", "v0.6.2", true)]
    [InlineData("0.6.1", "0.7.0", true)]
    [InlineData("0.6.1", "v1.0", true)]
    [InlineData("0.6.1", "v0.6.10", true)]        // 문자열이 아니라 숫자로 비교
    [InlineData("0.6.1.0", "v0.6.1", false)]      // 어셈블리 4자리 버전과 태그 3자리
    [InlineData("0.6.1", "v0.6.1", false)]
    [InlineData("0.6.2", "v0.6.1", false)]
    [InlineData("0.10.0", "v0.9.9", false)]
    [InlineData("0.6.1", "V0.6.2", true)]
    [InlineData("0.6.1+abc123", "v0.6.1+def456", false)]   // 빌드 메타데이터는 무시
    [InlineData("0.6.1", "v0.6.2+build.7", true)]
    [InlineData("0.6.1", "v0.6.2-beta", true)]
    [InlineData("0.6.2-beta", "v0.6.2", true)]    // 같은 숫자면 정식 > 프리릴리스
    [InlineData("0.6.2", "v0.6.2-rc1", false)]
    [InlineData("0.6.1", "", false)]
    [InlineData("0.6.1", null, false)]
    [InlineData("0.6.1", "latest", false)]
    [InlineData("0.6.1", "v0..2", false)]
    [InlineData("0.6.1", "v1.2.3.4.5", false)]
    [InlineData("0.6.1", "v-1.0", false)]
    [InlineData("0.6.1", "v99999999999.0", false)]  // 너무 큰 숫자는 해석하지 않음
    [InlineData("", "v0.6.2", false)]               // 현재 버전을 모르면 알리지 않음
    public void IsNewer_비교(string? current, string? latest, bool expected) =>
        Assert.Equal(expected, UpdateCheck.IsNewer(current, latest));

    [Theory]
    [InlineData("v0.6.2", "0.6.2")]
    [InlineData(" v0.6.2+abc ", "0.6.2")]
    [InlineData("0.6.2", "0.6.2")]
    [InlineData(null, "")]
    public void Clean_v와_메타데이터_제거(string? input, string expected) =>
        Assert.Equal(expected, UpdateCheck.Clean(input));

    [Fact]
    public void IsDue_하루에_한_번()
    {
        var now = new DateTime(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(UpdateCheck.IsDue(null, now));
        Assert.False(UpdateCheck.IsDue(now.AddHours(-23), now));
        Assert.True(UpdateCheck.IsDue(now.AddHours(-24), now));
        Assert.True(UpdateCheck.IsDue(now.AddDays(-3), now));
        Assert.True(UpdateCheck.IsDue(now.AddDays(2), now));   // 시계가 거꾸로 갔으면 다시 확인
    }

    private const string Sample = """
        {"url":"https://api.github.com/repos/windevase/nubis/releases/1","html_url":"https://github.com/windevase/nubis/releases/tag/v0.6.2",
         "tag_name":"v0.6.2","name":"Nubis 0.6.2","draft":false,"prerelease":false,"assets":[]}
        """;

    [Fact]
    public void Parse_정식_릴리스()
    {
        var r = UpdateCheck.Parse(Sample);
        Assert.NotNull(r);
        Assert.Equal("v0.6.2", r!.Tag);
        Assert.Equal("0.6.2", r.Version);
        Assert.Equal("https://github.com/windevase/nubis/releases/tag/v0.6.2", r.Url);
    }

    [Theory]
    [InlineData("""{"tag_name":"v0.7.0","html_url":"https://github.com/x","prerelease":true}""")]
    [InlineData("""{"tag_name":"v0.7.0","html_url":"https://github.com/x","draft":true}""")]
    [InlineData("""{"tag_name":"v0.7.0-beta","html_url":"https://github.com/x"}""")]       // 프리릴리스 표기 태그
    [InlineData("""{"tag_name":"v0.7.0","html_url":"javascript:alert(1)"}""")]
    [InlineData("""{"tag_name":"v0.7.0","html_url":"file:///C:/Windows/System32/calc.exe"}""")]
    [InlineData("""{"tag_name":"v0.7.0","html_url":"/relative"}""")]
    [InlineData("""{"tag_name":"v0.7.0"}""")]
    [InlineData("""{"html_url":"https://github.com/x"}""")]
    [InlineData("""{"tag_name":7,"html_url":"https://github.com/x"}""")]
    [InlineData("""{"message":"API rate limit exceeded","documentation_url":"https://docs.github.com"}""")]
    [InlineData("""[]""")]
    [InlineData("not json")]
    [InlineData("")]
    public void Parse_알리지_않는_응답은_null(string json) =>
        Assert.Null(UpdateCheck.Parse(json));

    [Fact]
    public void ShouldNotify_건너뛴_버전은_알리지_않고_더_새_버전은_알린다()
    {
        var r = new ReleaseInfo("v0.6.2", "https://github.com/windevase/nubis/releases/tag/v0.6.2");
        Assert.True(UpdateCheck.ShouldNotify("0.6.1", r, null));
        Assert.False(UpdateCheck.ShouldNotify("0.6.1", r, "0.6.2"));
        Assert.False(UpdateCheck.ShouldNotify("0.6.1", r, "v0.6.2"));
        Assert.True(UpdateCheck.ShouldNotify("0.6.1", new ReleaseInfo("v0.6.3", r.Url), "0.6.2"));
        Assert.False(UpdateCheck.ShouldNotify("0.6.2", r, null));   // 이미 최신
        Assert.False(UpdateCheck.ShouldNotify("0.6.1", null, null));
    }

    [Fact]
    public void 새_버전_알림은_기본_켜짐이고_Claude_설정_목록에_없다()
    {
        Assert.True(new AppSettings().CheckForUpdates);
        Assert.Null(SettingsCatalog.Find(nameof(AppSettings.CheckForUpdates)));
    }
}
