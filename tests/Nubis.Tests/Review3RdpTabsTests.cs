using System;
using Nubis;
using Xunit;

namespace Nubis.Tests;

public class Review3RdpTabsTests
{
    [Theory]
    [InlineData(1, true)]       // 이 쪽에서 끊음
    [InlineData(2, true)]       // 원격 로그오프·연결 끊기
    [InlineData(3, true)]       // 서버(관리자·유휴 정책)가 끊음
    [InlineData(0, false)]
    [InlineData(-5, false)]     // RDP 컨트롤 치명적 오류
    [InlineData(264, false)]    // 연결 시간 초과
    [InlineData(2055, false)]   // 비밀번호 오류
    public void NormalDisconnectReason(int reason, bool expected) =>
        Assert.Equal(expected, RdpSessionView.IsNormalDisconnectReason(reason));

    [Fact]
    public void NormalDisconnect_IsNeverAuthFailure()
    {
        foreach (var r in new[] { 1, 2, 3 }) Assert.False(RdpSessionView.IsAuthFailureReason(r));
    }

    [Fact]
    public void PooledControl_WaitsForStaleDisconnect()
    {
        // 반납 때 끊은 연결의 OnDisconnected 를 아직 못 받았으면 다른 탭에 내주지 않는다
        Assert.False(RdpAxHost.IsReusable(true, TimeSpan.FromSeconds(1)));
        Assert.True(RdpAxHost.IsReusable(false, TimeSpan.Zero));
        // 이벤트가 끝내 오지 않아도 풀 자리를 영영 차지하지는 않는다
        Assert.True(RdpAxHost.IsReusable(true, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void ClosedTabHistory_PopsLastFirst_AndSkipsDeletedHosts()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var h = new ClosedTabHistory();
        h.Push(new ClosedTabHistory.Entry(a, true));
        h.Push(new ClosedTabHistory.Entry(null, null));   // 로컬 PowerShell
        h.Push(new ClosedTabHistory.Entry(b, null));

        Func<Guid, bool> exists = id => id == a;   // b 는 그 사이 지워짐
        Assert.True(h.HasAny(exists));

        var first = h.Pop(exists);
        Assert.NotNull(first);
        Assert.Null(first.Value.HostId);            // b 는 건너뛰고 로컬

        var second = h.Pop(exists);
        Assert.NotNull(second);
        Assert.Equal(a, second.Value.HostId);
        Assert.Equal(true, second.Value.UsePublic); // 공인/사설 선택 유지

        Assert.Null(h.Pop(exists));
        Assert.False(h.HasAny(exists));
    }

    [Fact]
    public void ClosedTabHistory_KeepsOnlyMax()
    {
        var h = new ClosedTabHistory();
        var ids = new Guid[ClosedTabHistory.Max + 3];
        for (var i = 0; i < ids.Length; i++) { ids[i] = Guid.NewGuid(); h.Push(new ClosedTabHistory.Entry(ids[i], null)); }
        Assert.Equal(ClosedTabHistory.Max, h.Count);

        // 가장 오래된 3개는 버려지고, 마지막 것부터 나온다
        for (var i = ids.Length - 1; i >= 3; i--)
        {
            var e = h.Pop(_ => true);
            Assert.NotNull(e);
            Assert.Equal(ids[i], e.Value.HostId);
        }
        Assert.Null(h.Pop(_ => true));
    }

    [Fact]
    public void ClosedTabHistory_AllDeleted_HasNone()
    {
        var h = new ClosedTabHistory();
        h.Push(new ClosedTabHistory.Entry(Guid.NewGuid(), false));
        Assert.False(h.HasAny(_ => false));
        Assert.Null(h.Pop(_ => false));
        Assert.Equal(0, h.Count);
    }
}
