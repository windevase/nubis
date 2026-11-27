using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>
/// 같은 SSH 연결 위의 잠깐 쓰는 채널(서버 모니터 · OS 배포판 읽기)과 SFTP 패널의 순서 맞추기 (통합 검수:
/// 세션 수 제한이 작은 서버에서 패널이 그 순간 채널을 못 열어 별도 연결 · OTP 재입력으로 밀려나던 경로)
/// </summary>
public class ChannelGateTests
{
    [Fact]
    public async Task NothingPending_ReturnsFalse_SoPanelFallsBackRightAway()
    {
        Assert.False(await ChannelGate.WaitPendingAsync([], TimeSpan.FromSeconds(5)));
        Assert.False(await ChannelGate.WaitPendingAsync([null, null], TimeSpan.FromSeconds(5)));
        Assert.False(await ChannelGate.WaitPendingAsync([Task.CompletedTask, null], TimeSpan.FromSeconds(5)));
        // 이미 실패로 끝난 채널도 기다릴 것이 아니다
        Assert.False(await ChannelGate.WaitPendingAsync([Task.FromException(new InvalidOperationException())], TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Pending_WaitsUntilDone_ThenTrue()
    {
        var tcs = new TaskCompletionSource();
        var wait = ChannelGate.WaitPendingAsync([null, tcs.Task], TimeSpan.FromSeconds(30));
        await Task.Delay(50);
        Assert.False(wait.IsCompleted);
        tcs.SetResult();
        Assert.True(await wait);
    }

    [Fact]
    public async Task PendingThatFails_DoesNotThrow()
    {
        var tcs = new TaskCompletionSource();
        var wait = ChannelGate.WaitPendingAsync([tcs.Task], TimeSpan.FromSeconds(30));
        tcs.SetException(new TimeoutException("exec 채널 시간 초과"));
        Assert.True(await wait);
    }

    [Fact]
    public async Task NeverEnding_StopsAtLimit()
    {
        var never = new TaskCompletionSource().Task;
        var sw = Stopwatch.StartNew();
        Assert.True(await ChannelGate.WaitPendingAsync([never], TimeSpan.FromMilliseconds(100)));
        Assert.InRange(sw.ElapsedMilliseconds, 50, 5000);
    }

    [Fact]
    public async Task Cancelled_Throws()
    {
        var never = new TaskCompletionSource().Task;
        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ChannelGate.WaitPendingAsync([never], TimeSpan.FromSeconds(30), cts.Token));
    }

    [Fact]
    public async Task BusyAtStart_ButDoneByFailure_StillRetries()
    {
        // CodeRabbit (PR #50): 모니터 명령이 채널 열기 실패 직후 끝나면 기다릴 것이 없어 재시도 없이 별도 연결(OTP · 점프 서버 실패)로 넘어가던 경로
        var monitor = new TaskCompletionSource();
        var atStart = ChannelGate.Pending([monitor.Task, null, Task.CompletedTask]);
        Assert.Equal([monitor.Task], atStart);
        monitor.SetResult();   // 채널 열기가 실패한 바로 뒤에 끝남
        Assert.True(await ChannelGate.WaitBeforeRetryAsync(atStart, [monitor.Task, null], TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task BusyOnlyAfterStart_WaitsThenRetries()
    {
        var probe = new TaskCompletionSource();
        var retry = ChannelGate.WaitBeforeRetryAsync(ChannelGate.Pending([null]), [probe.Task], TimeSpan.FromSeconds(30));
        await Task.Delay(50);
        Assert.False(retry.IsCompleted);
        probe.SetResult();
        Assert.True(await retry);
    }

    [Fact]
    public async Task NothingBusy_NoRetry_FallsBackToSeparateConnection()
    {
        Assert.Empty(ChannelGate.Pending(null));
        Assert.False(await ChannelGate.WaitBeforeRetryAsync(ChannelGate.Pending(null), null, TimeSpan.FromSeconds(5)));
        Assert.False(await ChannelGate.WaitBeforeRetryAsync(ChannelGate.Pending([Task.CompletedTask]), [Task.CompletedTask], TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void TransientWait_OutlastsMonitorCommandTimeout()
    {
        // 서버 모니터 명령의 시간 제한(10초)이 끝날 때까지는 기다린다 — 그보다 짧으면 막힌 채널이 풀리기 전에 별도 연결로 넘어간다
        Assert.True(ChannelGate.TransientWait > TimeSpan.FromSeconds(10));
        Assert.True(ChannelGate.TransientWait < TimeSpan.FromSeconds(30));
    }
}
