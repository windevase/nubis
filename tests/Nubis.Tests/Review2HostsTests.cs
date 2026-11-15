using System;
using System.Collections.Generic;
using System.Linq;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>
/// 호스트 목록 리뷰 2차 회귀 테스트: 카드 격자 키보드 이동, Shift+클릭 범위(즐겨찾기·최근 접속 중복),
/// 그룹 이름 표기 고정(접힘 상태 키), 메뉴 글자 '_' 이스케이프
/// </summary>
public class Review2HostsTests
{
    // ----- GridMove: ↑↓ 는 같은 열, Tab 은 줄을 넘어 한 장씩 -----

    [Fact]
    public void GridMove_DownKeepsColumnAcrossShortRow()
    {
        // 그룹 A: 3장 + 1장, 그룹 B: 3장 → 줄 길이 3,1,3
        var rows = new List<int> { 3, 1, 3 };
        // 첫 줄 3번째 칸에서 ↓ → 짧은 줄은 마지막 칸(0)
        Assert.Equal((1, 0), MainWindow.GridMove(rows, (0, 2), 1, 0));
        // 원하는 열(2)을 기억해 다시 ↓ 하면 다음 줄 3번째 칸 (예전에는 한 줄 카드 수만큼 더해 열이 어긋났다)
        Assert.Equal((2, 2), MainWindow.GridMove(rows, (1, 2), 1, 0));
    }

    [Fact]
    public void GridMove_ClampsAtEdges()
    {
        var rows = new List<int> { 2, 2 };
        Assert.Equal((0, 1), MainWindow.GridMove(rows, (0, 1), -1, 0));
        Assert.Equal((1, 1), MainWindow.GridMove(rows, (1, 1), 1, 0));
        Assert.Equal((1, 1), MainWindow.GridMove(rows, (1, 1), 0, 1));
        Assert.Equal((0, 0), MainWindow.GridMove(rows, (0, 0), 0, -1));
    }

    [Fact]
    public void GridMove_TabWrapsToNextAndPreviousRow()
    {
        var rows = new List<int> { 3, 1, 3 };
        Assert.Equal((1, 0), MainWindow.GridMove(rows, (0, 2), 0, 1));
        Assert.Equal((2, 0), MainWindow.GridMove(rows, (1, 0), 0, 1));
        Assert.Equal((1, 0), MainWindow.GridMove(rows, (2, 0), 0, -1));
        Assert.Equal((0, 2), MainWindow.GridMove(rows, (1, 0), 0, -1));
    }

    [Fact]
    public void GridMove_NoCurrent_StartsAtFirstOrLast()
    {
        var rows = new List<int> { 3, 2 };
        Assert.Equal((0, 0), MainWindow.GridMove(rows, null, 1, 0));
        Assert.Equal((0, 0), MainWindow.GridMove(rows, null, 0, 1));
        Assert.Equal((1, 1), MainWindow.GridMove(rows, null, -1, 0));
        Assert.Equal((1, 1), MainWindow.GridMove(rows, null, 0, -1));
    }

    // ----- SelectionRange: 같은 호스트가 즐겨찾기와 원래 그룹에 두 번 있을 때 -----

    private static HostEntry H(string name) => new() { Label = name, Address = name };

    [Fact]
    public void SelectionRange_UsesClickedOccurrence()
    {
        var a = H("a"); var b = H("b"); var c = H("c"); var d = H("d");
        // 즐겨찾기: c / 원래 그룹: a b c d
        var order = new List<HostEntry> { c, a, b, c, d };
        // a(1) 클릭 후 원래 그룹의 c(3) 를 Shift+클릭 → a..c (예전에는 c 의 첫 위치(0)를 써서 즐겨찾기의 c..a 만 골랐다)
        Assert.Equal((1, 3), MainWindow.SelectionRange(order, a, 1, c, 3));
        // 클릭 위치를 모르면 시작 카드는 끝 카드에 가장 가까운 위치
        Assert.Equal((3, 4), MainWindow.SelectionRange(order, c, -1, d, 4));
    }

    [Fact]
    public void SelectionRange_StaleHintFallsBack()
    {
        var a = H("a"); var b = H("b"); var c = H("c");
        var order = new List<HostEntry> { a, b, c };
        // 힌트가 다른 호스트를 가리키면(목록이 다시 만들어짐) 실제 위치로
        Assert.Equal((0, 2), MainWindow.SelectionRange(order, a, 2, c, 0));
        Assert.Null(MainWindow.SelectionRange(order, H("x"), -1, c, 2));
        Assert.Null(MainWindow.SelectionRange(order, a, 0, H("y"), -1));
    }

    // ----- 그룹 이름 표기 고정 -----

    [Fact]
    public void CanonicalGroupNames_IndependentOfHostOrder()
    {
        var h1 = new HostEntry { Group = "web" };
        var h2 = new HostEntry { Group = " Web " };
        var h3 = new HostEntry { Group = "" };
        var one = MainWindow.CanonicalGroupNames(new[] { h1, h2, h3 });
        var two = MainWindow.CanonicalGroupNames(new[] { h3, h2, h1 });
        Assert.Equal(one["WEB"], two["web"]);
        Assert.Equal("Web", one["web"]);   // 서수 비교로 앞선 표기 ('W' < 'w')
        Assert.Equal("", one[""]);
    }

    [Fact]
    public void CanonicalGroupNames_IgnoresCloudHosts()
    {
        var cloud = new HostEntry { Group = "aws", CloudProfileId = Guid.NewGuid() };
        Assert.False(MainWindow.CanonicalGroupNames(new[] { cloud }).ContainsKey("aws"));
    }

    // ----- 메뉴 글자 -----

    [Fact]
    public void MenuText_EscapesAccessKeyUnderscore()
    {
        Assert.Equal("web__01", MainWindow.MenuText("web_01"));
        Assert.Equal("plain", MainWindow.MenuText("plain"));
    }
}
