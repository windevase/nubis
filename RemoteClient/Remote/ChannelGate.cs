using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Nubis;

/// <summary>
/// SSH 탭 한 연결 위의 채널 순서 맞추기. 서버 모니터(exec, 2초마다) · OS 배포판 읽기(SFTP, 접속 후 한 번)는 잠깐 쓰는 채널이라,
/// 세션 수 제한(sshd MaxSessions)이 작은 서버에서 그 순간 SFTP 패널이 채널을 못 열면 별도 연결(OTP 재입력 · 점프 서버 호스트는 실패)로 밀려난다.
/// 잠깐 쓰는 쪽은 패널이 채널을 연 뒤에 시작하고, 패널은 그 채널들이 끝나길 기다렸다 한 번 더 시도한다. UI 와 무관 (테스트)
/// </summary>
public static class ChannelGate
{
    /// <summary>잠깐 쓰는 채널을 기다리는 최대 시간 — 서버 모니터 명령 시간 제한(10초)보다 조금 길게</summary>
    public static readonly TimeSpan TransientWait = TimeSpan.FromSeconds(12);

    /// <summary>
    /// 아직 끝나지 않은 작업이 있으면 모두 끝나거나 <paramref name="limit"/> 이 지날 때까지 기다리고 true, 기다릴 것이 없었으면 false.
    /// 작업의 실패는 던지지 않는다 (그 채널의 결과는 그쪽이 처리). <paramref name="ct"/> 가 취소되면 OperationCanceledException
    /// </summary>
    public static async Task<bool> WaitPendingAsync(IEnumerable<Task?> tasks, TimeSpan limit, CancellationToken ct = default)
    {
        var pending = tasks.Where(t => t is { IsCompleted: false }).Select(t => t!).ToArray();
        if (pending.Length == 0) return false;
        // WhenAny 는 던지지 않는다 — 실패한 작업 · 취소된 지연 모두 그냥 끝난 것으로
        await Task.WhenAny(Task.WhenAll(pending), Task.Delay(limit, ct));
        ct.ThrowIfCancellationRequested();
        return true;
    }

    /// <summary>지금 끝나지 않은 작업들 (채널 열기를 시도하기 전에 찍어 둔다)</summary>
    public static Task[] Pending(IEnumerable<Task?>? tasks) =>
        tasks == null ? [] : tasks.Where(t => t is { IsCompleted: false }).Select(t => t!).ToArray();

    /// <summary>
    /// 채널 열기가 실패한 뒤 한 번 더 시도할 것인가 — 시도를 시작할 때 돌던 작업(<paramref name="pendingAtStart"/>, 그사이 끝났어도)이나
    /// 지금 도는 작업(<paramref name="pendingNow"/>)이 있었으면 그것들이 끝나길(최대 <paramref name="limit"/>) 기다리고 true.
    /// 실패 직후 작업이 막 끝나 기다릴 것이 없어도 그 채널이 자리를 차지했을 수 있으므로 다시 시도한다. 둘 다 없었으면 false (별도 연결로)
    /// </summary>
    public static async Task<bool> WaitBeforeRetryAsync(Task[] pendingAtStart, IEnumerable<Task?>? pendingNow, TimeSpan limit, CancellationToken ct = default)
    {
        var waited = await WaitPendingAsync(pendingAtStart.Concat(pendingNow ?? []), limit, ct);
        return waited || pendingAtStart.Length > 0;
    }
}
