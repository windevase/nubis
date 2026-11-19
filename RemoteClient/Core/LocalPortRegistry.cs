using System.Collections.Generic;
using System.Linq;

namespace Nubis;

/// <summary>
/// 이 앱 안의 로컬 포트 사용 현황 — SSH 포트 포워딩(SshPortForwardSet)과 쿠버네티스 포트 포워딩(K8sForward)이 서로의 포트를 알게 한다.
/// - 예약(Reserve): 실행 중인 쿠버네티스 포워딩. kubectl 은 API 서버 연결·인증이 끝난 뒤에야 포트를 열어 그 사이에는 소켓 검사로 보이지 않으므로
///   다른 포워딩(SSH 포함)이 그 포트를 열지 않게 잡아 둔다
/// - 희망(Claim): SSH 탭의 켜진 포워딩 포트 (연결을 기다리는 중 포함). 소켓이 아직 없어도 빈 포트 제안에서 피한다
/// 어느 스레드에서 불러도 된다
/// </summary>
public static class LocalPortRegistry
{
    private static readonly object Lock = new();
    private static readonly Dictionary<int, object> Reserved = new();
    private static readonly Dictionary<object, HashSet<int>> Claims = new(ReferenceEqualityComparer.Instance);

    /// <summary>포트를 owner 몫으로 잡는다. 다른 owner 가 잡고 있으면 false (같은 owner 는 다시 잡아도 true)</summary>
    public static bool TryReserve(int port, object owner)
    {
        lock (Lock)
        {
            if (Reserved.TryGetValue(port, out var o)) return ReferenceEquals(o, owner);
            Reserved[port] = owner;
            return true;
        }
    }

    /// <summary>owner 가 잡은 포트를 놓는다 (다른 owner 의 것이면 그대로)</summary>
    public static void Release(int port, object owner)
    {
        lock (Lock)
            if (Reserved.TryGetValue(port, out var o) && ReferenceEquals(o, owner)) Reserved.Remove(port);
    }

    public static bool IsReserved(int port)
    {
        lock (Lock) return Reserved.ContainsKey(port);
    }

    /// <summary>owner 가 쓰려는 포트 목록을 통째로 바꾼다 (빈 목록이면 지움)</summary>
    public static void SetClaims(object owner, IEnumerable<int> ports)
    {
        var set = ports.ToHashSet();
        lock (Lock)
        {
            if (set.Count == 0) Claims.Remove(owner);
            else Claims[owner] = set;
        }
    }

    public static void ClearClaims(object owner)
    {
        lock (Lock) Claims.Remove(owner);
    }

    /// <summary>어느 SSH 탭이 쓰려는 포트인가 (owner 를 주면 그 owner 의 것은 빼고 본다)</summary>
    public static bool IsClaimed(int port, object? except = null)
    {
        lock (Lock)
            return Claims.Any(kv => !ReferenceEquals(kv.Key, except) && kv.Value.Contains(port));
    }
}
