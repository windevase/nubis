using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Renci.SshNet;

namespace Nubis;

/// <summary>
/// SSH 서버 배너(ServerVersion, 예: "SSH-2.0-OpenSSH_for_Windows_9.5")로 서버 OS 를 판별해 호스트에 기억한다.
/// 직접 추가한 호스트는 클라우드 동기화 Os 가 없어 셸 종류(sh / Windows cmd·PowerShell)를 모르므로 읽기 전용 자동 승인을 하지 않는데,
/// 한 번 접속해 배너를 보면 그다음부터는 sh 서버로 확인되어 자동 승인할 수 있다.
/// 터미널 탭 접속 때는 Linux 배포판(/etc/os-release)도 읽어 OS 로고(<see cref="ServerOsKind"/>)에 쓴다.
/// </summary>
public static class SshServerOs
{
    public const string Windows = "windows";
    public const string Unix = "unix";

    /// <summary>배너 → "windows" / "unix". 배너가 없으면 null (판별 안 함)</summary>
    public static string? Classify(string? serverVersion)
    {
        if (string.IsNullOrWhiteSpace(serverVersion)) return null;
        return serverVersion.Contains("windows", StringComparison.OrdinalIgnoreCase) ? Windows : Unix;
    }

    /// <summary>판별 결과 알림 (호스트 Id, "windows"/"unix") — MainWindow 가 받아 저장된 호스트에 반영한다. 아무 스레드에서나 올 수 있다</summary>
    public static event Action<Guid, string>? Detected;

    /// <summary>
    /// 배포판 판별 알림 (호스트 Id, 접속한 주소, 포트, 배포판 낱말, os-release 에서 읽었는가, 판별을 시작할 때의 <see cref="ProbeGeneration"/>).
    /// 배너 짐작(false)은 저장된 값이 없을 때만 쓴다. 받는 쪽은 <see cref="ShouldApplyDistro"/> 로 저장할지 정한다. 아무 스레드에서나 올 수 있다
    /// </summary>
    public static event Action<Guid, string, int, string, bool, int>? DistroDetected;

    // 호스트별 무효화 세대 — ForgetProbes(저장된 배포판을 지움: OS 재설치 · 주소·포트 변경)마다 올린다.
    // 그 전에 시작한 판별(옛 OS 의 os-release)이 뒤늦게 끝나도 새로 지운 값을 덮어쓰지 않게
    private static readonly ConcurrentDictionary<Guid, int> Generations = new();

    /// <summary>이 호스트의 지금 무효화 세대 (판별을 시작할 때 기록해 두고 결과를 저장할 때 비교)</summary>
    public static int ProbeGeneration(Guid hostId) => Generations.TryGetValue(hostId, out var g) ? g : 0;

    /// <summary>
    /// 판별 결과를 저장된 호스트에 쓸 것인가 — 판별을 시작한 뒤 무효화되지 않았고(<paramref name="generation"/>),
    /// 저장 규칙(<see cref="ServerOsKind.ShouldStore"/>)에 맞고, 그사이 주소·포트가 바뀌지 않았을 때만(<see cref="SameEndpoint"/>)
    /// </summary>
    public static bool ShouldApplyDistro(HostEntry? h, string address, int port, string distro, bool fromOsRelease, int generation) =>
        h != null && ProbeGeneration(h.Id) == generation &&
        ServerOsKind.ShouldStore(h.OsDistro, distro, fromOsRelease) && SameEndpoint(h, address, port);

    /// <summary>판별한 연결(주소 · 포트)이 지금 호스트 설정의 서버인가 — 읽는 사이에 주소나 포트를 바꿨으면 그 결과는 다른 서버의 것일 수 있다</summary>
    public static bool SameEndpoint(HostEntry h, string address, int port) =>
        h.Port == port &&
        (string.Equals(h.Address, address, StringComparison.OrdinalIgnoreCase) || h.AddressCandidates().Contains(address, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// 이 호스트의 "이번 실행에서 읽어 봄" 기록을 지운다 — 저장된 배포판을 지운 경우(클라우드에서 OS 재설치를 알아챔)
    /// 같은 주소·포트로 다시 접속할 때 os-release 를 다시 읽게
    /// </summary>
    public static void ForgetProbes(Guid hostId)
    {
        Generations.AddOrUpdate(hostId, 1, (_, g) => g + 1);   // 이미 시작한 판별의 결과는 버려진다
        foreach (var key in Probed.Keys)
            if (key.Item1 == hostId) Probed.TryRemove(key, out _);
    }

    /// <summary>연결된 클라이언트의 배너를 판별해 알린다 (실패해도 조용히 무시). 서버에 아무것도 보내지 않는다</summary>
    public static void Report(HostEntry host, BaseClient client)
    {
        try
        {
            var banner = client.ConnectionInfo?.ServerVersion;
            if (Classify(banner) is { } kind) Detected?.Invoke(host.Id, kind);
            if (ServerOsKind.FromBanner(banner) is { } distro) DistroDetected?.Invoke(host.Id, host.Address, host.Port, distro, false, ProbeGeneration(host.Id));
        }
        catch { }
    }

    // 이번 실행에서 배포판을 읽어 본 호스트·주소·포트 — 접속할 때마다 읽지 않는다 (성공·실패 모두 한 번. 주소·포트가 바뀌면 다시
    // (같은 주소의 다른 포트는 NAT 뒤 다른 서버일 수 있음), 다음 실행 때도 다시: OS 업그레이드 반영)
    private static readonly ConcurrentDictionary<(Guid, string, int), byte> Probed = new();

    /// <summary>이번 실행에서 처음 읽는 호스트·주소·포트면 기록하고 true</summary>
    internal static bool MarkProbed(Guid hostId, string address, int port) => Probed.TryAdd((hostId, address.ToLowerInvariant(), port), 0);

    /// <summary>
    /// 배포판을 읽어 볼 서버인가 — 배너가 OpenSSH · Dropbear 인 Unix 서버만. Windows 와 네트워크 장비(Cisco · HUAWEI 등,
    /// 두 번째 채널을 견디지 못하거나 SFTP 가 없는 장비)에는 채널을 열지 않는다
    /// </summary>
    public static bool ShouldProbe(string? serverVersion) =>
        Classify(serverVersion) == Unix &&
        (serverVersion!.Contains("OpenSSH", StringComparison.OrdinalIgnoreCase) || serverVersion.Contains("dropbear", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 터미널 탭의 SSH 연결에서 /etc/os-release 를 읽어 배포판을 알린다 (백그라운드, 실패하면 조용히 무시).
    /// 명령을 실행하지 않고 같은 연결에 SFTP 채널을 잠깐 열어 파일만 읽는다 — 재인증 없음, 읽기 전용, 크기 상한
    /// <see cref="ServerOsKind.MaxOsReleaseBytes"/>. <see cref="ShouldProbe"/> 가 아닌 서버 · SFTP 가 막힌 서버(제한 셸 등)는 건너뛰거나 실패로 끝난다.
    /// </summary>
    /// <param name="address">접속한 주소 — 읽는 사이에 호스트 주소가 바뀌었으면 받는 쪽이 버린다</param>
    /// <returns>읽는 작업 (읽지 않으면 null) — 그동안 같은 연결의 SFTP 패널이 채널을 못 열면 이것이 끝나길 기다렸다 다시 연다</returns>
    public static Task? ProbeDistro(Guid hostId, string address, int port, SshClient client)
    {
        if (hostId == Guid.Empty || string.IsNullOrEmpty(address) || !client.IsConnected) return null;
        if (!ShouldProbe(client.ConnectionInfo?.ServerVersion)) return null;
        var generation = ProbeGeneration(hostId);
        if (!MarkProbed(hostId, address, port)) return null;
        return Task.Run(() =>
        {
            try
            {
                if (ReadOsRelease(client) is not { } text) return;
                if (ServerOsKind.ParseOsRelease(text) is { } distro) DistroDetected?.Invoke(hostId, address, port, distro, true, generation);
            }
            catch { }
        });
    }

    private static string? ReadOsRelease(SshClient client)
    {
        using var sftp = SharedSftpClient.TryOpen(client);
        if (sftp == null) return null;
        sftp.OperationTimeout = TimeSpan.FromSeconds(15);
        // /etc/os-release 가 기본, 없으면 규격상 대체 위치 /usr/lib/os-release
        foreach (var path in new[] { "/etc/os-release", "/usr/lib/os-release" })
        {
            try
            {
                using var stream = sftp.OpenRead(path);
                var buf = new byte[ServerOsKind.MaxOsReleaseBytes];
                int total = 0, n;
                while (total < buf.Length && (n = stream.Read(buf, total, buf.Length - total)) > 0) total += n;
                return Encoding.UTF8.GetString(buf, 0, total);
            }
            catch (Exception) when (client.IsConnected && sftp.IsConnected) { }   // 파일 없음 · 권한 없음 → 다음 위치
        }
        return null;
    }

}
