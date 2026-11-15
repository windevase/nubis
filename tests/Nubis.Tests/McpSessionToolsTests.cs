using System;
using System.Collections.Generic;
using System.Linq;
using Nubis;
using Xunit;

namespace Nubis.Tests;

public class McpSessionToolsTests
{
    private sealed class Tab(string name, bool disconnected)
    {
        public string Name { get; } = name;
        public bool Disconnected { get; } = disconnected;
    }

    private static readonly Tab A = new("a", false), B = new("b", true), C = new("c", false), D = new("d", true);
    private static readonly IReadOnlyList<Tab> All = new[] { A, B, C, D };

    private static string Names(IEnumerable<Tab> tabs) => string.Concat(tabs.Select(t => t.Name));

    [Fact]
    public void others_는_기준_탭만_남긴다() =>
        Assert.Equal("acd", Names(McpSessionLogic.PickTabsToClose(All, B, "others", t => t.Disconnected)));

    [Fact]
    public void right_는_기준_오른쪽만_닫는다()
    {
        Assert.Equal("cd", Names(McpSessionLogic.PickTabsToClose(All, B, "right", t => t.Disconnected)));
        Assert.Empty(McpSessionLogic.PickTabsToClose(All, D, "right", t => t.Disconnected));
        // 이미 닫힌(목록에 없는) 기준 탭이면 아무것도 닫지 않는다
        Assert.Empty(McpSessionLogic.PickTabsToClose(All, new Tab("x", false), "right", t => t.Disconnected));
    }

    [Fact]
    public void disconnected_는_기준_없이_끊긴_탭만() =>
        Assert.Equal("bd", Names(McpSessionLogic.PickTabsToClose(All, null, "disconnected", t => t.Disconnected)));

    [Fact]
    public void 기준_탭이_필요한_필터에_기준이_없으면_예외()
    {
        Assert.ThrowsAny<ArgumentException>(() => McpSessionLogic.PickTabsToClose(All, null, "others", t => t.Disconnected));
        Assert.ThrowsAny<ArgumentException>(() => McpSessionLogic.PickTabsToClose(All, A, "all", t => t.Disconnected));
    }

    [Theory]
    [InlineData("  web-01  ", "web-01")]
    [InlineData("운영 서버", "운영 서버")]
    public void 탭_이름은_앞뒤_공백을_뗀다(string input, string expected) =>
        Assert.Equal(expected, McpSessionLogic.NormalizeTitle(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a\nb")]
    [InlineData("a\tb")]
    [InlineData("a\u001bb")]
    [InlineData("a\u0085b")]
    [InlineData("pro\u200Bd")]
    [InlineData("prod\u202Eevil")]
    [InlineData("a\u2028b")]
    [InlineData("a\uFEFFb")]
    [InlineData("web01 (10.0.0.5)")]
    [InlineData("web01 \uFF0810.0.0.5\uFF09")]
    public void 빈_이름과_보이지_않는_문자는_거부(string input) =>
        Assert.Throws<ArgumentException>(() => McpSessionLogic.NormalizeTitle(input));

    [Fact]
    public void 너무_긴_이름은_거부()
    {
        Assert.Equal(McpSessionLogic.MaxTitleLength, McpSessionLogic.NormalizeTitle(new string('a', McpSessionLogic.MaxTitleLength)).Length);
        Assert.Throws<ArgumentException>(() => McpSessionLogic.NormalizeTitle(new string('a', McpSessionLogic.MaxTitleLength + 1)));
    }
}
