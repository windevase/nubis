using System;
using System.Runtime.CompilerServices;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>터미널 누수·CPU 점검: 흐름 제어 예산(숨은 탭 상한) · 조각 자르기 · ConPTY Dispose 가 구독자를 놓는지</summary>
public class LeakTerminalTests
{
    [Fact]
    public void 보이는_탭은_HighWater_까지만_보낸다()
    {
        Assert.Equal(XtermView.HighWater, XtermView.SendBudget(0, visible: true));
        Assert.Equal(0, XtermView.SendBudget(XtermView.HighWater, visible: true));
        Assert.Equal(0, XtermView.SendBudget(XtermView.HighWater + 10, visible: true));
    }

    [Fact]
    public void 숨은_탭도_상한이_있다()
    {
        // 숨은 페이지는 그리기가 거의 멈춘다 — 한도 없이 보내면 렌더러에 탭마다 수십 MB 가 쌓인다
        Assert.Equal(XtermView.HiddenHighWater, XtermView.SendBudget(0, visible: false));
        Assert.Equal(0, XtermView.SendBudget(XtermView.HiddenHighWater, visible: false));
        Assert.True(XtermView.HiddenHighWater > XtermView.HighWater);
        Assert.True(XtermView.HiddenHighWater <= XtermView.HeldMax);
    }

    [Fact]
    public void 조각은_서로게이트_쌍_가운데서_자르지_않는다()
    {
        var text = "ab\U0001F600cd";   // a b [hi lo] c d
        Assert.Equal(2, XtermView.SliceLength(text, 0, 3));   // 3 이면 hi 뒤에서 잘림 → 2
        Assert.Equal(4, XtermView.SliceLength(text, 0, 4));
        Assert.Equal(2, XtermView.SliceLength(text, 4, 10));  // 끝까지
        // 남은 게 한 글자뿐이면 그대로 (무한 반복 방지)
        Assert.Equal(1, XtermView.SliceLength("x", 0, 1));
    }

    [Fact]
    public void 조각을_이어_붙이면_원문과_같다()
    {
        var text = string.Concat(System.Linq.Enumerable.Repeat("가\U0001F600x", 5000));
        var sb = new System.Text.StringBuilder();
        for (var pos = 0; pos < text.Length;)
        {
            var n = XtermView.SliceLength(text, pos, 7);
            Assert.True(n > 0);
            sb.Append(text, pos, n);
            pos += n;
        }
        Assert.Equal(text, sb.ToString());
    }

    [Fact]
    public void 시작하지_않은_ConPTY_는_두_번_Dispose_해도_된다()
    {
        var pty = new PseudoConsoleProcess();
        pty.Dispose();
        pty.Dispose();
    }

    [Fact]
    public void ConPTY_Dispose_는_구독자와_게이트를_놓는다()
    {
        // 종료 대기 등으로 PseudoConsoleProcess 가 잠시 더 살아 있어도 화면(구독자·게이트 대상)을 붙잡지 않아야 한다
        var (pty, weak) = Subscribe();
        pty.Dispose();
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.False(weak.IsAlive);
        GC.KeepAlive(pty);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (PseudoConsoleProcess, WeakReference) Subscribe()
    {
        var target = new Target();
        var pty = new PseudoConsoleProcess { OutputGate = target.Gate };
        pty.Output += target.OnOutput;
        pty.Exited += target.OnExit;
        return (pty, new WeakReference(target));
    }

    private sealed class Target
    {
        public int Count;
        public bool Gate(int ms) => true;
        public void OnOutput(string s) => Count += s.Length;
        public void OnExit(int code) => Count++;
    }
}
