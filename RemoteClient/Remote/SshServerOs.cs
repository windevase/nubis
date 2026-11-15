using System;
using Renci.SshNet;

namespace Nubis;

/// <summary>
/// SSH 서버 배너(ServerVersion, 예: "SSH-2.0-OpenSSH_for_Windows_9.5")로 서버 OS 를 판별해 호스트에 기억한다.
/// 직접 추가한 호스트는 클라우드 동기화 Os 가 없어 셸 종류(sh / Windows cmd·PowerShell)를 모르므로 읽기 전용 자동 승인을 하지 않는데,
/// 한 번 접속해 배너를 보면 그다음부터는 sh 서버로 확인되어 자동 승인할 수 있다.
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

    /// <summary>연결된 클라이언트의 배너를 판별해 알린다 (실패해도 조용히 무시)</summary>
    public static void Report(HostEntry host, BaseClient client)
    {
        try
        {
            if (Classify(client.ConnectionInfo?.ServerVersion) is { } kind) Detected?.Invoke(host.Id, kind);
        }
        catch { }
    }
}
