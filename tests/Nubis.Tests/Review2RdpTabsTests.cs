using System;
using Nubis;
using Xunit;

namespace Nubis.Tests;

public class Review2RdpTabsTests
{
    [Theory]
    // 같은 폭: 절반을 넘어야 옮긴다
    [InlineData(0, 1, 59, 120, 120, false)]
    [InlineData(0, 1, 60, 120, 120, true)]
    // 좁은 탭(120)을 넓은 탭(230) 오른쪽으로: 230-120=110 보다 절반(115)이 커서 115 부터
    [InlineData(0, 1, 114, 230, 120, false)]
    [InlineData(0, 1, 116, 230, 120, true)]
    // 넓은 탭을 왼쪽으로: 절반(115)과 끄는 탭 폭(230) 중 작은 쪽
    [InlineData(1, 0, 116, 230, 230, false)]
    [InlineData(1, 0, 115, 230, 230, true)]
    // 넓은 대상(230)을 좁은 탭(100)으로 왼쪽 끌기: 커서가 100 안쪽이어야 (옮긴 뒤 끄는 탭 위)
    [InlineData(1, 0, 105, 230, 100, false)]
    [InlineData(1, 0, 99, 230, 100, true)]
    public void ShouldReorderTab_NoOscillation(int from, int to, double x, double overW, double dragW, bool expected) =>
        Assert.Equal(expected, MainWindow.ShouldReorderTab(from, to, x, overW, dragW));

    [Fact]
    public void ShouldReorderTab_AfterMove_CursorStaysOnDragged()
    {
        // 바꾼 직후 같은 커서 위치에서 반대로 다시 바꾸지 않아야 한다 (왕복 방지)
        foreach (var (wA, wB) in new[] { (120.0, 230.0), (230.0, 120.0), (100.0, 230.0), (230.0, 100.0), (150.0, 150.0) })
        {
            for (var x = 0.0; x < wB; x += 1)
            {
                if (!MainWindow.ShouldReorderTab(0, 1, x, wB, wA)) continue;
                // 오른쪽으로 옮긴 뒤 B 는 [-wA, wB-wA), A 는 [wB-wA, wB) — 커서(x, B 의 원래 기준)는 A 위
                Assert.True(x >= wB - wA, $"wA={wA} wB={wB} x={x}");
            }
            for (var x = 0.0; x < wB; x += 1)
            {
                if (!MainWindow.ShouldReorderTab(1, 0, x, wB, wA)) continue;
                // 왼쪽으로 옮긴 뒤 A 는 [0, wA) — 커서는 A 위
                Assert.True(x <= wA, $"wA={wA} wB={wB} x={x}");
            }
        }
    }

    [Fact]
    public void ShouldReorderTab_SameIndexOrZeroWidth_False()
    {
        Assert.False(MainWindow.ShouldReorderTab(2, 2, 50, 120, 120));
        Assert.False(MainWindow.ShouldReorderTab(0, 1, 50, 0, 120));
    }

    [Fact]
    public void LiveRemoteSessionCount_ExcludesLocalAndDisconnected()
    {
        var n = MainWindow.LiveRemoteSessionCount(new[]
        {
            (Protocol.Local, SessionStatus.Connected),
            (Protocol.Ssh, SessionStatus.Connected),
            (Protocol.Rdp, SessionStatus.Connecting),
            (Protocol.Rdp, SessionStatus.Disconnected),
        });
        Assert.Equal(2, n);
        Assert.Equal(0, MainWindow.LiveRemoteSessionCount(new[] { (Protocol.Local, SessionStatus.Connected) }));
    }

    [Fact]
    public void CloseAppPrompt_CountsK8sForwardsToo()
    {
        Assert.Null(MainWindow.CloseAppPrompt(0, 0));
        Assert.Equal(L.F("연결 중인 세션 {0}개가 종료됩니다. 닫을까요?", 2), MainWindow.CloseAppPrompt(2, 0));
        // 클러스터 탭은 로컬 터미널이라 세션 수에 들지 않지만 실행 중인 포워딩이 있으면 묻는다
        Assert.Equal(L.F("실행 중인 쿠버네티스 포트 포워딩 {0}개가 멈춥니다. 닫을까요?", 1), MainWindow.CloseAppPrompt(0, 1));
        Assert.Equal(L.F("연결 중인 세션 {0}개가 종료되고 쿠버네티스 포트 포워딩 {1}개가 멈춥니다. 닫을까요?", 2, 3), MainWindow.CloseAppPrompt(2, 3));
    }

    [Theory]
    [InlineData(2055, true)]
    [InlineData(3335, true)]
    [InlineData(4615, true)]
    [InlineData(1, false)]      // 로컬에서 끊음
    [InlineData(2, false)]      // 원격 로그오프
    [InlineData(264, false)]    // 연결 시간 초과
    public void RdpAuthFailureReason(int reason, bool expected) =>
        Assert.Equal(expected, RdpSessionView.IsAuthFailureReason(reason));
}
