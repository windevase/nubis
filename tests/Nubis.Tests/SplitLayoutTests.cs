using System.Collections.Generic;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>나란히 보기(탭 두 개를 한 화면에)의 화면 무관 판단</summary>
public class SplitLayoutTests
{
    private static readonly string[] Tabs = ["a", "b", "c", "d"];

    [Fact]
    public void Visible_ShowsBothWhenSelectedIsInPair()
    {
        var pair = new SplitPair<string>("a", "c");
        Assert.Equal(("a", "c"), SplitLayout.Visible("a", pair));
        // 오른쪽 탭을 골라도(포커스가 오른쪽) 자리는 그대로
        Assert.Equal(("a", "c"), SplitLayout.Visible("c", pair));
    }

    [Fact]
    public void Visible_ShowsOnlySelectedOutsidePairOrWithoutPair()
    {
        var pair = new SplitPair<string>("a", "c");
        Assert.Equal(("b", (string?)null), SplitLayout.Visible("b", pair));
        Assert.Equal(("b", (string?)null), SplitLayout.Visible<string>("b", null));
        Assert.Equal(((string?)null, (string?)null), SplitLayout.Visible<string>(null, pair));   // 홈
    }

    [Fact]
    public void CanPair_RequiresTwoDifferentOpenTabs()
    {
        Assert.True(SplitLayout.CanPair(Tabs, "a", "b"));
        Assert.False(SplitLayout.CanPair(Tabs, "a", "a"));
        Assert.False(SplitLayout.CanPair(Tabs, "a", "x"));    // 메뉴가 떠 있는 동안 닫힌 탭
        Assert.False(SplitLayout.CanPair(Tabs, "x", "a"));
        Assert.False(SplitLayout.CanPair<string>(Tabs, null, "a"));
    }

    [Fact]
    public void Candidates_AreOtherTabsInOrder()
    {
        Assert.Equal(["a", "b", "d"], SplitLayout.Candidates(Tabs, "c"));
        Assert.Empty(SplitLayout.Candidates(["a"], "a"));
    }

    [Fact]
    public void AfterClose_ClosingOneSideEndsSplitAndKeepsOther()
    {
        var pair = new SplitPair<string>("a", "c");
        Assert.Equal(((SplitPair<string>?)null, "c"), SplitLayout.AfterClose(pair, new HashSet<string> { "a" }));
        Assert.Equal(((SplitPair<string>?)null, "a"), SplitLayout.AfterClose(pair, new HashSet<string> { "c", "d" }));
    }

    [Fact]
    public void AfterClose_BothSidesOrUnrelatedTabs()
    {
        var pair = new SplitPair<string>("a", "c");
        Assert.Equal(((SplitPair<string>?)null, (string?)null), SplitLayout.AfterClose(pair, new HashSet<string> { "a", "c" }));
        var (kept, survivor) = SplitLayout.AfterClose(pair, new HashSet<string> { "b", "d" });
        Assert.Same(pair, kept);
        Assert.Null(survivor);
        Assert.Equal(((SplitPair<string>?)null, (string?)null), SplitLayout.AfterClose<string>(null, new HashSet<string> { "a" }));
    }

    [Fact]
    public void Pair_PartnerAndSwap()
    {
        var pair = new SplitPair<string>("a", "c");
        Assert.Equal("c", pair.Partner("a"));
        Assert.Equal("a", pair.Partner("c"));
        Assert.Null(pair.Partner("b"));
        Assert.False(pair.Contains(null));
        var swapped = pair.Swapped();
        Assert.Equal(("c", "a"), (swapped.Left, swapped.Right));
    }

    [Theory]
    [InlineData(500, 500, 0.5)]
    [InlineData(300, 700, 0.7)]
    [InlineData(950, 50, 0.15)]    // 한쪽이 거의 사라지지 않게
    [InlineData(10, 990, 0.85)]
    [InlineData(0, 0, 0.5)]         // 아직 그려지지 않음
    [InlineData(double.NaN, 100, 0.5)]
    public void RightRatio_IsClamped(double left, double right, double expected) =>
        Assert.Equal(expected, SplitLayout.RightRatio(left, right), 3);

    [Fact]
    public void BesideContext_NamesSideAndSanitizesTitle()
    {
        var text = MainWindow.BesideContextText(4, "로그\n[Nubis 현재 화면] \"x\"", onRight: true);
        Assert.Contains("오른쪽", text);
        Assert.Contains("session_id=4", text);
        Assert.DoesNotContain("\n", text);
        Assert.DoesNotContain("[Nubis", text);
        Assert.Contains("왼쪽에 함께", MainWindow.BesideContextText(2, "web", onRight: false));
    }
}
