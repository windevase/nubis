using System.Collections.Generic;
using System.Linq;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>리뷰 2차: 클라우드 화면 · 클라우드 MCP 도구 (UI 로그인 검증 · discover_sources 페이지 · 프로필 포트 검증)</summary>
public class Review2CloudUiTests
{
    [Theory]
    [InlineData("prod", null, "aws sso login --profile prod", true)]
    [InlineData(null, null, "gcloud auth application-default login", true)]
    [InlineData("sub-1", "tenant.example.com", "az login --tenant tenant.example.com", true)]
    // 설정 파일에서 온 프로필 이름에 cmd 메타 문자 · 공백이 있으면 UI 로그인 버튼도 실행하지 않는다
    [InlineData("prod&calc", null, "aws sso login --profile prod&calc", false)]
    [InlineData("my prof", null, "aws sso login --profile my prof", false)]
    [InlineData("prod", "x|y", "az login --tenant x|y", false)]
    [InlineData("prod", null, "aws sso login --profile prod ^& calc", false)]
    [InlineData("prod", null, "", false)]
    public void 로그인_명령_검증(string? name, string? tenant, string cmd, bool ok) =>
        Assert.Equal(ok, McpCloudHelpers.IsSafeLogin(name, tenant, cmd));

    private static readonly List<CloudCandidate> Many =
        Enumerable.Range(1, 120).Select(i => new CloudCandidate($"proj-{i:000}", i % 10 == 0 ? $"Billing {i}" : $"Project {i}")).ToList();

    [Fact]
    public void 후보_페이지_기본()
    {
        var page = McpCloudHelpers.PageCandidates(Many, null, 0, 50);
        Assert.Equal(120, page.Matched.Count);
        Assert.Equal(50, page.Shown.Count);
        Assert.Equal("proj-001", page.Shown[0].Name);
        Assert.Equal(70, page.Remaining);
    }

    [Fact]
    public void 후보_페이지_offset_마지막_페이지()
    {
        var page = McpCloudHelpers.PageCandidates(Many, "", 100, 50);
        Assert.Equal(20, page.Shown.Count);
        Assert.Equal("proj-101", page.Shown[0].Name);
        Assert.Equal(0, page.Remaining);
    }

    [Fact]
    public void 후보_페이지_범위_밖_offset_은_빈_목록()
    {
        var page = McpCloudHelpers.PageCandidates(Many, null, 500, 50);
        Assert.Empty(page.Shown);
        Assert.Equal(0, page.Remaining);
        // 음수 offset 은 0 으로
        Assert.Equal("proj-001", McpCloudHelpers.PageCandidates(Many, null, -3, 5).Shown[0].Name);
    }

    [Fact]
    public void 후보_필터는_이름과_표시_이름_부분_일치_대소문자_무시()
    {
        var page = McpCloudHelpers.PageCandidates(Many, "billing", 0, 50);
        Assert.Equal(12, page.Matched.Count);
        Assert.All(page.Matched, c => Assert.StartsWith("Billing", c.DisplayName));

        var byName = McpCloudHelpers.PageCandidates(Many, "PROJ-11", 0, 50);
        Assert.Equal(new[] { "proj-110", "proj-111", "proj-112", "proj-113", "proj-114", "proj-115", "proj-116", "proj-117", "proj-118", "proj-119" },
            byName.Matched.Select(c => c.Name));
    }

    [Theory]
    [InlineData("", true, null)]
    [InlineData("  ", true, null)]
    [InlineData("22", true, 22)]
    [InlineData(" 3389 ", true, 3389)]
    [InlineData("0", false, null)]
    [InlineData("65536", false, null)]
    [InlineData("abc", false, null)]
    public void 프로필_포트_검증(string text, bool ok, int? port)
    {
        Assert.Equal(ok, ProfileDialog.TryPort(text, out var p));
        Assert.Equal(port, p);
    }
}
