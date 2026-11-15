using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Nubis;
using Xunit;

namespace Nubis.Tests;

public class McpFix5Tests
{
    // ---------- discover_sources 제한 시간 처리 (DiscoverWithTimeoutAsync) ----------
    // 20초 제한 시간 자체는 단위 테스트로 기다리지 않고, 바로 끝나는 경로(성공·예외·요청 취소)만 확인한다

    private sealed class FakeProvider(Func<CancellationToken, Task<List<CloudCandidate>>> discover) : ICloudProvider
    {
        public CloudProvider Kind => CloudProvider.Aws;
        public string AuthHint => "";
        public string LoginCommand(CloudProfile? p) => "";
        public Task<List<CloudCandidate>> DiscoverAsync(CancellationToken ct) => discover(ct);
        public Task<CloudFetchResult> FetchAsync(CloudProfile p, CancellationToken ct) => throw new NotSupportedException();
    }

    private static Task<List<CloudCandidate>> Discover(ICloudProvider p, CancellationToken outer)
    {
        var m = typeof(MainWindow).GetMethod("DiscoverWithTimeoutAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (Task<List<CloudCandidate>>)m.Invoke(null, new object[] { p, outer })!;
    }

    [Fact]
    public async Task 탐색이_끝나면_결과를_그대로_돌려준다()
    {
        var list = new List<CloudCandidate>();
        var r = await Discover(new FakeProvider(_ => Task.FromResult(list)), CancellationToken.None);
        Assert.Same(list, r);
    }

    [Fact]
    public async Task 탐색_오류는_원래_예외로_올린다()
    {
        var p = new FakeProvider(_ => throw new InvalidOperationException("boom"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Discover(p, CancellationToken.None));
        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public async Task 탐색_자체의_TimeoutException_은_원래_메시지를_유지한다()
    {
        var p = new FakeProvider(_ => throw new TimeoutException("provider timeout"));
        var ex = await Assert.ThrowsAsync<TimeoutException>(() => Discover(p, CancellationToken.None));
        Assert.Equal("provider timeout", ex.Message);
    }

    [Fact]
    public async Task 요청을_취소하면_시간_초과가_아니라_취소로_끝난다()
    {
        using var outer = new CancellationTokenSource();
        var p = new FakeProvider(async ct => { await Task.Delay(Timeout.Infinite, ct); return new List<CloudCandidate>(); });
        var t = Discover(p, outer.Token);
        outer.Cancel();
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t);
        Assert.IsNotType<TimeoutException>(ex);
    }

    [Fact]
    public async Task 토큰을_무시하는_탐색도_요청_취소에는_바로_돌아온다()
    {
        using var outer = new CancellationTokenSource();
        var release = new TaskCompletionSource<List<CloudCandidate>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var p = new FakeProvider(_ => release.Task);
        var t = Discover(p, outer.Token);
        outer.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t.WaitAsync(TimeSpan.FromSeconds(5)));
        // 기다리기를 그만둔 뒤 늦게 실패하는 탐색 (예외는 ContinueWith 로 관찰된다)
        release.SetException(new InvalidOperationException("late"));
    }
}
