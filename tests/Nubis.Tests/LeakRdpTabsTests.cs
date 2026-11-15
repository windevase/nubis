using System;
using System.Collections.Generic;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>RDP 탭·탭 줄의 누수·불필요한 갱신 방지 (UI 없이 확인할 수 있는 부분)</summary>
public class LeakRdpTabsTests
{
    [Fact]
    public void ClosedTabHistory_IsBounded()
    {
        var h = new ClosedTabHistory();
        for (var i = 0; i < ClosedTabHistory.Max * 5; i++) h.Push(new ClosedTabHistory.Entry(Guid.NewGuid(), null));
        Assert.Equal(ClosedTabHistory.Max, h.Count);
    }

    [Fact]
    public void ClosedTabHistory_KeepsNewestWhenFull()
    {
        var h = new ClosedTabHistory();
        var ids = new List<Guid>();
        for (var i = 0; i < ClosedTabHistory.Max + 3; i++)
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            h.Push(new ClosedTabHistory.Entry(id, null));
        }
        Assert.Equal(ids[^1], h.Pop(_ => true)!.Value.HostId);
    }

    [Fact]
    public void Pool_StuckControlIsDroppedOnlyWhenStillConnectedAfterWait()
    {
        Assert.False(RdpAxHost.IsStuck(0, TimeSpan.FromMinutes(10)));    // 끊긴 컨트롤은 재사용 대상
        Assert.False(RdpAxHost.IsStuck(1, TimeSpan.FromSeconds(5)));     // 아직 끊는 중
        Assert.True(RdpAxHost.IsStuck(1, TimeSpan.FromSeconds(61)));     // 끝내 안 끊김 → 풀 자리 반납
    }

    [Fact]
    public void Pool_StaleDisconnectWaitsThenReuses()
    {
        Assert.True(RdpAxHost.IsReusable(false, TimeSpan.Zero));
        Assert.False(RdpAxHost.IsReusable(true, TimeSpan.FromSeconds(1)));
        Assert.True(RdpAxHost.IsReusable(true, TimeSpan.FromSeconds(11)));
    }

    private static SessionTab NewTab() => new() { Id = 1, Title = "t", Protocol = Protocol.Rdp, View = null! };

    [Fact]
    public void SessionTab_SameValuesDoNotRaisePropertyChanged()
    {
        var tab = NewTab();
        var raised = new List<string?>();
        tab.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        tab.Status = tab.Status;
        tab.IsSelected = tab.IsSelected;
        tab.Title = "t";
        Assert.Empty(raised);

        tab.Status = SessionStatus.Connected;
        Assert.Equal(new[] { nameof(SessionTab.Status), nameof(SessionTab.StatusBrush) }, raised);
    }

    [Fact]
    public void SessionTab_StatusBrushIsSharedAndFrozen()
    {
        var a = NewTab();
        var b = NewTab();
        a.Status = SessionStatus.Connected;
        b.Status = SessionStatus.Connected;
        Assert.Same(a.StatusBrush, b.StatusBrush);
        Assert.True(a.StatusBrush.IsFrozen);
    }
}
