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
}
