using System.Collections.Generic;
using Nubis;
using Xunit;

namespace Nubis.Tests;

public class Review7MainTabsTests
{
    private static readonly string[] Tabs = { "a", "b", "c", "d", "e" };

    [Fact]
    public void NeighborAfterClose_PrefersRightSurvivor()
    {
        // c 를 닫고 d 도 닫으면 오른쪽에서 처음 남는 e
        Assert.Equal("e", MainWindow.NeighborAfterClose(Tabs, 2, new HashSet<string> { "c", "d" }));
    }

    [Fact]
    public void NeighborAfterClose_FallsBackToLeft()
    {
        // 오른쪽이 모두 닫히면 왼쪽에서 가장 가까운 것
        Assert.Equal("b", MainWindow.NeighborAfterClose(Tabs, 2, new HashSet<string> { "c", "d", "e" }));
    }

    [Fact]
    public void NeighborAfterClose_AllClosed_Null()
    {
        Assert.Null(MainWindow.NeighborAfterClose(Tabs, 0, new HashSet<string>(Tabs)));
    }

    [Fact]
    public void NeighborAfterClose_OthersClosed_KeepsSurvivor()
    {
        // "다른 탭 모두 닫기" — 지금 탭(a)이 닫힐 목록에 있고 오른쪽 클릭한 c 만 남는 경우
        Assert.Equal("c", MainWindow.NeighborAfterClose(Tabs, 0, new HashSet<string> { "a", "b", "d", "e" }));
    }
}
