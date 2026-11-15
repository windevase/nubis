using System;
using System.Collections.Generic;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>SFTP 패널 누수·CPU 점검: 진행 보고 줄이기(ThrottledProgress) · 빈 진행 보고(NoProgress)</summary>
public class LeakSftpTests
{
    [Fact]
    public void 간격_안의_보고는_한_번만_올리고_마지막_값을_적용한다()
    {
        long now = 1000;
        var posted = new List<Action>();
        var applied = new List<int>();
        var p = new ThrottledProgress<int>(posted.Add, applied.Add, 250, () => now);

        p.Report(1);
        p.Report(2);   // 올려 둔 것이 아직 안 돌았음 — 새로 올리지 않는다
        p.Report(3);
        Assert.Single(posted);

        posted[0]();
        Assert.Equal(new[] { 3 }, applied);   // 그 사이 최신 값
    }

    [Fact]
    public void 간격이_지나야_다시_올린다()
    {
        long now = 1000;
        var posted = new List<Action>();
        var applied = new List<int>();
        var p = new ThrottledProgress<int>(posted.Add, applied.Add, 250, () => now);

        p.Report(1);
        posted[0]();
        now += 100;
        p.Report(2);   // 0.1초 뒤 — 버린다
        Assert.Single(posted);
        now += 200;
        p.Report(3);   // 0.3초 뒤 — 올린다
        Assert.Equal(2, posted.Count);
        posted[1]();
        Assert.Equal(new[] { 1, 3 }, applied);
    }

    [Fact]
    public void 보고가_수천_번_와도_올리는_횟수는_시간에_비례한다()
    {
        long now = 0;
        var posted = 0;
        var p = new ThrottledProgress<long>(a => { posted++; a(); }, _ => { }, 250, () => now);
        // 1초 동안 1ms 마다 보고 (빠른 전송에서 32KB 마다 보고하는 것과 비슷)
        for (var i = 0; i < 1000; i++) { now = i; p.Report(i); }
        Assert.InRange(posted, 1, 5);
    }

    [Fact]
    public void 빈_진행_보고는_아무것도_하지_않는다()
    {
        IProgress<int> p = NoProgress<int>.Instance;
        p.Report(1);   // 예외 없음, UI 스레드에 아무것도 올리지 않음
        Assert.Same(NoProgress<int>.Instance, p);
    }
}
