using System;
using System.Collections.Generic;
using System.Linq;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>
/// 호스트 목록 리뷰 3차 회귀 테스트: 접힘 키 표기 이전(저장 필요 여부), 점프 서버로 쓰는 호스트 수
/// </summary>
public class Review3HostsTests
{
    private static Dictionary<string, string> Canon(params string[] groups) =>
        MainWindow.CanonicalGroupNames(groups.Select(g => new HostEntry { Group = g }));

    [Fact]
    public void MigrateCollapsedKeys_MovesOldSpellingAndReportsChange()
    {
        var canon = Canon("web", "Web");   // 서수 비교로 "Web" 이 표기
        var collapsed = new HashSet<string> { "L:web", "F:" };
        Assert.True(MainWindow.MigrateCollapsedKeys(collapsed, canon));
        Assert.Contains("L:Web", collapsed);
        Assert.DoesNotContain("L:web", collapsed);
        Assert.Contains("F:", collapsed);
    }

    [Fact]
    public void MigrateCollapsedKeys_NoChangeWhenAlreadyCanonicalOrUnknown()
    {
        var canon = Canon("Web");
        // 이미 지금 표기 · 호스트가 없는 그룹 키는 그대로 (저장할 필요 없음)
        var collapsed = new HashSet<string> { "L:Web", "L:gone", "R:" };
        Assert.False(MainWindow.MigrateCollapsedKeys(collapsed, canon));
        Assert.Equal(3, collapsed.Count);
        Assert.Contains("L:gone", collapsed);
    }

    [Fact]
    public void MigrateCollapsedKeys_MergesBothSpellings()
    {
        var canon = Canon("db", "DB");
        var collapsed = new HashSet<string> { "L:db", "L:DB" };
        Assert.True(MainWindow.MigrateCollapsedKeys(collapsed, canon));
        Assert.Equal(new[] { "L:DB" }, collapsed.ToArray());
    }

    [Fact]
    public void JumpUsers_CountsOtherHostsUsingEditingAsJump()
    {
        var bastion = new HostEntry { Label = "bastion" };
        var a = new HostEntry { Label = "a", JumpHostId = bastion.Id };
        var b = new HostEntry { Label = "b", JumpHostId = bastion.Id };
        var c = new HostEntry { Label = "c", JumpHostId = a.Id };
        var all = new[] { bastion, a, b, c };
        Assert.Equal(2, MainWindow.JumpUsers(all, bastion));
        Assert.Equal(1, MainWindow.JumpUsers(all, a));
        Assert.Equal(0, MainWindow.JumpUsers(all, c));
        // 새 호스트·복제(편집 중인 원본 없음)는 0
        Assert.Equal(0, MainWindow.JumpUsers(all, null));
    }

    [Fact]
    public void JumpUsers_IgnoresSelfReference()
    {
        var h = new HostEntry();
        h.JumpHostId = h.Id;
        Assert.Equal(0, MainWindow.JumpUsers(new[] { h }, h));
    }
}
