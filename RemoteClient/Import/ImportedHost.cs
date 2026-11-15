using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Nubis;

/// <summary>
/// 가져오기 파일을 텍스트로 읽는다. BOM(UTF-8 · UTF-16) → 엄격한 UTF-8 → 시스템 ANSI 코드 페이지(한국어 Windows 는 CP949) 순.
/// 한국어 Windows 엑셀의 "CSV (쉼표로 분리)" 는 CP949 로 저장되므로 UTF-8 로만 읽으면 한글 머리글이 깨진다.
/// </summary>
public static class ImportText
{
    public static string ReadFile(string path) => Decode(File.ReadAllBytes(path));

    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // UTF-8 이 아니면 시스템 ANSI 코드 페이지 (없으면 Latin-1 — 바이트를 잃지 않음)
            return AnsiEncoding(SystemAnsiCodePage()).GetString(bytes);
        }
    }

    /// <summary>
    /// UTF-8 이 아닌 파일을 읽을 레거시 코드 페이지. 시스템 ACP 가 UTF-8(65001, "세계 언어 지원을 위해 Unicode UTF-8 사용" 베타 옵션)이면
    /// 이미 UTF-8 이 아닌 걸 알고 있으므로 설치된 Windows 언어의 ANSI 코드 페이지(한국어면 949)로 읽는다. 못 쓰면 Latin-1 (바이트를 잃지 않음)
    /// </summary>
    internal static Encoding AnsiEncoding(int acp)
    {
        foreach (var cp in new[] { acp, CultureInfo.InstalledUICulture.TextInfo.ANSICodePage })
        {
            if (cp is 65001 or 0 or 1200 or 1201) continue;
            try
            {
                if (CodePagesEncodingProvider.Instance.GetEncoding(cp) is { } e) return e;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { }
        }
        return Encoding.Latin1;
    }

    /// <summary>시스템 로캘(유니코드를 지원하지 않는 프로그램용 언어)의 ANSI 코드 페이지. 앱 표시 언어와 무관</summary>
    private static int SystemAnsiCodePage()
    {
        try { return (int)GetACP(); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return CultureInfo.InstalledUICulture.TextInfo.ANSICodePage;
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetACP();
}

/// <summary>
/// 가져오기(엑셀/CSV · ~/.ssh/config · mRemoteNG · Ansible 인벤토리 · Active Directory)가 읽어 낸 서버 한 대.
/// 파서는 UI·저장소와 무관하게 이 형태만 만든다. 미리보기 화면이 이것을 HostEntry 로 바꿔 등록한다 (ImportMerge).
/// 비밀번호 등 비밀값은 어떤 출처에서도 가져오지 않는다 — 계정은 등록할 때 Vault 로 지정한다.
/// </summary>
public sealed record ImportedHost
{
    /// <summary>표시 이름 (없으면 주소)</summary>
    public string Name { get; init; } = "";
    /// <summary>IP 또는 호스트명 (DNS 이름 포함)</summary>
    public string Address { get; init; } = "";
    /// <summary>포트 (없으면 프로토콜 기본 포트)</summary>
    public int? Port { get; init; }
    /// <summary>SSH / RDP. null = 출처에 정보가 없음 (미리보기에서 포트·OS 로 추정, 기본 SSH)</summary>
    public Protocol? Protocol { get; init; }
    /// <summary>그룹 (폴더 · 인벤토리 그룹 · OU 등). 여러 단계면 " / " 로 잇는다</summary>
    public string? Group { get; init; }
    /// <summary>로그인 사용자명 (있을 때만)</summary>
    public string? Username { get; init; }
    /// <summary>점프 서버 (ProxyJump) — 같은 가져오기 결과 안의 다른 항목 Name 또는 "user@host:port" 형태 그대로</summary>
    public string? JumpHost { get; init; }
    /// <summary>OS 설명 (AD operatingSystem 등). Windows 가 들어 있으면 RDP 로 추정한다</summary>
    public string? Os { get; init; }
    /// <summary>설명 · 출처 메모 (미리보기에 표시)</summary>
    public string? Note { get; init; }
}

/// <summary>파서 결과: 가져온 서버 목록 + 사용자에게 보여 줄 경고 (건너뛴 줄, 무시한 비밀번호 등 — L.T/L.F 로 번역된 문구)</summary>
public sealed record ImportResult(List<ImportedHost> Hosts, List<string> Warnings)
{
    public static ImportResult Empty() => new(new List<ImportedHost>(), new List<string>());
}
