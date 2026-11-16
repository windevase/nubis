using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Nubis;

/// <summary>
/// mstsc 의 기본 설정 파일(문서\Default.rdp)을 읽어 RDP 컨트롤에 같은 값을 적용한다.
/// 사용자가 원격 데스크톱 연결(mstsc)에서 바꾼 기본 설정을 Nubis 도 그대로 따른다.
/// 파일이 없거나 항목이 없으면 mstsc 의 기본값과 같은 값을 쓴다.
/// </summary>
public sealed class RdpDefaults
{
    public int ColorDepth { get; init; } = 32;
    /// <summary>1 모뎀 … 6 LAN, 7 자동 감지</summary>
    public int ConnectionType { get; init; } = 7;
    public bool BandwidthDetection { get; init; } = true;
    /// <summary>자동 감지가 아닐 때만 쓰는 화면 효과 플래그 (TS_PERF_*)</summary>
    public int PerformanceFlags { get; init; }
    public bool BitmapPersistence { get; init; } = true;
    public bool RedirectClipboard { get; init; } = true;
    public bool RedirectPrinters { get; init; } = true;
    public bool RedirectDrives { get; init; }
    public bool RedirectSmartCards { get; init; } = true;
    /// <summary>0 로컬에서 재생, 1 원격에서 재생, 2 재생 안 함</summary>
    public int AudioMode { get; init; }
    public bool AutoReconnect { get; init; } = true;
    /// <summary>Windows 키 조합: 0 로컬, 1 원격, 2 전체 화면일 때만 원격</summary>
    public int KeyboardHook { get; init; } = 2;
    /// <summary>Default.rdp 에서 읽었는지 (false 면 mstsc 기본값)</summary>
    public bool FromFile { get; init; }
    /// <summary>RD 게이트웨이 (gatewayhostname). 없으면 null</summary>
    public string? GatewayHost { get; init; }
    /// <summary>게이트웨이 사용 방식 (gatewayusagemethod): 0 안 씀, 1 항상, 2 로컬 주소는 우회, 4 자동 감지</summary>
    public int GatewayUsage { get; init; }
    /// <summary>게이트웨이 인증 (gatewaycredentialssource): 0 비밀번호(NTLM), 1 스마트 카드, 4 자동</summary>
    public int GatewayCredsSource { get; init; } = 4;
    /// <summary>게이트웨이와 원격 PC 에 같은 계정 사용 (promptcredentialonce)</summary>
    public bool GatewayCredSharing { get; init; } = true;
    /// <summary>NLA(CredSSP) 사용 (enablecredsspsupport). 0 이면 끈다 — 모든 RDP 호스트에 적용 (호스트별 설정은 HostEntry.RdpDisableNla)</summary>
    public bool CredSsp { get; init; } = true;

    /// <summary>
    /// 이 호스트에 NLA(CredSSP)를 쓸지: 호스트에서 껐거나 Default.rdp 에서 껐으면 false
    /// </summary>
    public bool CredSspFor(HostEntry host) => CredSsp && !host.RdpDisableNla;

    /// <summary>
    /// 사용자명이 Entra ID(Azure AD) 계정 형식(AzureAD\사용자@도메인)인지 — NLA 를 꺼야 할 수 있다고 안내할 때
    /// </summary>
    public static bool IsEntraUser(string? user) =>
        user?.Trim().StartsWith(@"AzureAD\", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// 이 호스트에 쓸 RD 게이트웨이: 호스트에 적은 값이 있으면 그 값(항상 사용), 없으면 Default.rdp 설정(사용 방식이 0 이 아닐 때).
    /// (게이트웨이 주소, 사용 방식) — 쓰지 않으면 null
    /// </summary>
    public (string Host, int Usage)? GatewayFor(string? hostGateway) =>
        !string.IsNullOrWhiteSpace(hostGateway) ? (hostGateway.Trim(), 1)
        : !string.IsNullOrWhiteSpace(GatewayHost) && GatewayUsage != 0 ? (GatewayHost!, GatewayUsage)
        : null;

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Default.rdp");

    public static RdpDefaults Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            return File.Exists(path) ? Parse(ReadText(path), true) : new RdpDefaults();
        }
        catch { return new RdpDefaults(); }
    }

    /// <summary>.rdp 는 보통 UTF-16LE(BOM) 이지만 UTF-8 로 저장된 것도 있다</summary>
    private static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[1] == 0 && bytes[0] != 0) return Encoding.Unicode.GetString(bytes);
        return Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
    }

    /// <summary>
    /// drivestoredirect 값이 "모든 드라이브" 인지. mstsc 는 전체 선택이면 "*", 일부만 고르면 "C:\;D:\;" 처럼 드라이브 목록,
    /// "나중에 연결하는 드라이브" 는 "DynamicDrives" 를 쓴다. RDP 컨트롤의 RedirectDrives 는 모든 드라이브를 켜는 스위치뿐이라
    /// "*" 일 때만 켜고, 특정 드라이브만 고른 경우는 의도보다 넓게 공유하지 않도록 리디렉션하지 않는다.
    /// </summary>
    public static bool RedirectsAllDrives(string value) =>
        value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(d => d == "*");

    /// <summary>"이름:형식:값" 줄들을 해석</summary>
    public static RdpDefaults Parse(string text, bool fromFile = false)
    {
        var v = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var a = line.IndexOf(':');
            var b = a < 0 ? -1 : line.IndexOf(':', a + 1);
            if (b < 0) continue;
            v[line[..a].Trim()] = line[(b + 1)..].Trim();
        }

        int I(string key, int def) => v.TryGetValue(key, out var s) && int.TryParse(s, out var n) ? n : def;
        bool B(string key, bool def) => v.TryGetValue(key, out var s) && int.TryParse(s, out var n) ? n != 0 : def;

        // 화면 효과 플래그: 끄기 항목(disable *)과 켜기 항목(allow *) 조합
        var perf = 0;
        if (B("disable wallpaper", false)) perf |= 0x1;
        if (B("disable full window drag", false)) perf |= 0x2;
        if (B("disable menu anims", false)) perf |= 0x4;
        if (B("disable themes", false)) perf |= 0x8;
        if (B("disable cursor setting", false)) perf |= 0x40;
        if (B("allow font smoothing", false)) perf |= 0x80;
        if (B("allow desktop composition", false)) perf |= 0x100;

        var connType = I("connection type", 7);
        if (B("networkautodetect", connType == 7)) connType = 7;

        return new RdpDefaults
        {
            FromFile = fromFile,
            // RDP 컨트롤이 받는 색 깊이만 (그 밖의 값은 ColorDepth 설정이 예외를 내 연결 자체가 실패한다)
            ColorDepth = I("session bpp", 32) is var bpp && bpp is 8 or 15 or 16 or 24 or 32 ? bpp : 32,
            ConnectionType = connType is >= 1 and <= 7 ? connType : 7,
            BandwidthDetection = B("bandwidthautodetect", true),
            PerformanceFlags = perf,
            BitmapPersistence = B("bitmapcachepersistenable", true),
            RedirectClipboard = B("redirectclipboard", true),
            RedirectPrinters = B("redirectprinters", true),
            // drivestoredirect 줄이 있어도 값이 비어 있으면(드라이브를 고르지 않음) redirectdrives 를 따른다
            RedirectDrives = v.TryGetValue("drivestoredirect", out var drives) && drives.Length > 0 ? RedirectsAllDrives(drives) : B("redirectdrives", false),
            RedirectSmartCards = B("redirectsmartcards", true),
            AudioMode = I("audiomode", 0),
            AutoReconnect = B("autoreconnection enabled", true),
            KeyboardHook = I("keyboardhook", 2),
            GatewayHost = v.TryGetValue("gatewayhostname", out var gw) && gw.Length > 0 ? gw : null,
            GatewayUsage = I("gatewayusagemethod", 0),
            GatewayCredsSource = I("gatewaycredentialssource", 4),
            GatewayCredSharing = B("promptcredentialonce", true),
            CredSsp = B("enablecredsspsupport", true),
        };
    }
}
