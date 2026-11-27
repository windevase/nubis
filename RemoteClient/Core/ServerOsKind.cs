using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Nubis;

/// <summary>
/// 서버 OS 종류 판별 (호스트 카드 · 세션 탭 · 명령 팔레트의 OS 로고). 화면과 떨어진 순수 함수라 테스트에서 그대로 쓴다.
/// 판별 근거는 셋 — 우선순위 순:
/// 1) <see cref="HostEntry.OsDistro"/>: SSH 접속 때 /etc/os-release 의 ID · ID_LIKE (없으면 배너의 배포판 이름으로 짐작한 한 낱말)
/// 2) <see cref="HostEntry.Os"/>: 클라우드 동기화(AMI 플랫폼 · 이미지 이름 등) · 가져오기(AD operatingSystem 등)가 준 글
/// 3) <see cref="HostEntry.SshBannerOs"/>: SSH 배너가 Windows 인지
/// 구체적인 배포판이 일반 "Linux" 보다 이긴다 (AWS 처럼 클라우드는 "Linux/UNIX" 만 주고 접속해 보면 Ubuntu 인 경우).
/// 모르면 null — 화면은 기존 프로토콜 아이콘을 그대로 쓴다.
/// </summary>
public static class ServerOsKind
{
    public const string Ubuntu = "ubuntu";
    public const string Debian = "debian";
    public const string RedHat = "rhel";
    public const string CentOs = "centos";
    public const string Rocky = "rocky";
    public const string Alma = "almalinux";
    public const string Fedora = "fedora";
    public const string Amazon = "amazon";
    public const string Suse = "suse";
    public const string OpenSuse = "opensuse";
    public const string Arch = "arch";
    public const string Alpine = "alpine";
    /// <summary>그 밖의 Linux (배포판을 모르거나 로고가 없는 배포판)</summary>
    public const string Linux = "linux";
    public const string Windows = "windows";

    public static IReadOnlyList<string> All { get; } =
        new[] { Ubuntu, Debian, RedHat, CentOs, Rocky, Alma, Fedora, Amazon, Suse, OpenSuse, Arch, Alpine, Linux, Windows };

    /// <summary>os-release 를 읽을 때의 크기 상한 (보통 1KB 미만 — 서버가 큰 파일을 줘도 이만큼만)</summary>
    public const int MaxOsReleaseBytes = 8 * 1024;
    /// <summary>저장하는 낱말 수 상한 (ID + ID_LIKE 앞쪽)</summary>
    private const int MaxTokens = 4;

    // os-release 규격의 ID 문자 (소문자 · 숫자 · "._-"). 서버가 준 글이므로 이 모양만 저장한다 — 화면·MCP 결과에 이상한 글이 들어가지 않게
    private static readonly Regex TokenRx = new("^[a-z0-9][a-z0-9._-]{0,31}$", RegexOptions.CultureInvariant);

    // ID → 로고. 표에 없는 ID 는 ID_LIKE 를 차례로 보고, 그래도 없으면 일반 Linux
    private static readonly Dictionary<string, string> Known = new(StringComparer.Ordinal)
    {
        ["ubuntu"] = Ubuntu,
        ["debian"] = Debian, ["raspbian"] = Debian,
        ["rhel"] = RedHat,
        ["centos"] = CentOs,
        ["rocky"] = Rocky,
        ["almalinux"] = Alma,
        ["fedora"] = Fedora,
        ["amzn"] = Amazon,
        ["sles"] = Suse, ["sled"] = Suse, ["sles_sap"] = Suse, ["sle-micro"] = Suse, ["suse"] = Suse,
        ["opensuse"] = OpenSuse, ["opensuse-leap"] = OpenSuse, ["opensuse-tumbleweed"] = OpenSuse,
        ["opensuse-microos"] = OpenSuse, ["opensuse-slowroll"] = OpenSuse,
        ["arch"] = Arch, ["archarm"] = Arch,
        ["alpine"] = Alpine,
        // RHEL·Fedora 호환이지만 다른 배포판 — ID_LIKE(rhel·fedora) 로 남의 로고를 붙이지 않게 일반 Linux 로
        ["ol"] = Linux, ["tencentos"] = Linux, ["opencloudos"] = Linux, ["anolis"] = Linux, ["openeuler"] = Linux, ["kylin"] = Linux,
    };

    // os-release 가 있어도 Linux 가 아닌 OS — Linux 로고를 붙이지 않는다 (기존 아이콘)
    private static readonly HashSet<string> NonLinux = new(StringComparer.Ordinal)
    {
        "freebsd", "openbsd", "netbsd", "dragonfly", "illumos", "omnios", "openindiana", "smartos", "solaris", "macos", "darwin",
    };

    /// <summary>
    /// /etc/os-release 내용 → 저장할 배포판 낱말 ("ID ID_LIKE...", 예: "rocky rhel centos fedora"). 판별할 수 없으면 null.
    /// 따옴표 · 주석 · CRLF · 깨진 줄을 견디고, 규격에 맞지 않는 낱말(공백 · 특수 문자 · 너무 긴 값)은 버린다.
    /// </summary>
    public static string? ParseOsRelease(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        if (text.Length > MaxOsReleaseBytes) text = text[..MaxOsReleaseBytes];
        string? id = null, like = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0 || line[0] == '#') continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = Unquote(line[(eq + 1)..].Trim());
            // 같은 키가 여러 번이면 셸처럼 마지막 값
            if (key == "ID") id = value;
            else if (key == "ID_LIKE") like = value;
        }

        var tokens = new List<string>();
        void Add(string? t)
        {
            if (t == null || tokens.Count >= MaxTokens) return;
            t = t.ToLowerInvariant();
            if (TokenRx.IsMatch(t) && !tokens.Contains(t)) tokens.Add(t);
        }
        // ID 에 공백 등이 있으면(규격 위반) 그 값은 버린다 — 앞 낱말만 떼어 쓰지 않는다
        Add(id);
        if (like != null)
            foreach (var t in like.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) Add(t);
        return tokens.Count == 0 ? null : string.Join(' ', tokens);
    }

    /// <summary>값을 감싼 따옴표를 벗기고 셸 이스케이프(\" \\ \$ \`)를 푼다. 짝이 맞지 않는 따옴표는 그대로 둔다(→ 낱말 검사에서 버려짐)</summary>
    private static string Unquote(string v)
    {
        if (v.Length >= 2 && (v[0] == '"' || v[0] == '\'') && v[^1] == v[0]) v = v[1..^1];
        if (!v.Contains('\\')) return v;
        var sb = new StringBuilder(v.Length);
        for (var i = 0; i < v.Length; i++)
        {
            if (v[i] == '\\' && i + 1 < v.Length) i++;
            sb.Append(v[i]);
        }
        return sb.ToString();
    }

    /// <summary>저장된 배포판 낱말 → 로고 종류. 낱말이 없거나 Linux 가 아닌 OS(FreeBSD 등)면 null</summary>
    public static string? FromDistro(string? distro)
    {
        var tokens = Tokens(distro);
        if (tokens.Length == 0 || NonLinux.Contains(tokens[0])) return null;
        foreach (var t in tokens)
            if (Known.TryGetValue(t, out var kind)) return kind;
        return Linux;
    }

    private static string[] Tokens(string? distro) =>
        string.IsNullOrWhiteSpace(distro) ? Array.Empty<string>() : distro.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// 클라우드 · 가져오기가 준 OS 글 → 로고 종류. 예: AWS "Linux/UNIX" · "Red Hat Enterprise Linux" · "Windows with SQL Server",
    /// Tencent "CentOS 7.9 64位" · "TencentOS Server 3.1", GCP 라이선스 "ubuntu-2204-lts", Azure "0001-com-ubuntu-server-jammy 22_04-lts",
    /// OCI "Canonical Ubuntu" · "Oracle Linux", AD "Windows Server 2019 Datacenter". 모르면 null
    /// </summary>
    public static string? FromCloudOs(string? os)
    {
        if (string.IsNullOrWhiteSpace(os)) return null;
        bool Has(string s) => os.Contains(s, StringComparison.OrdinalIgnoreCase);
        if (Has("windows")) return Windows;
        // RHEL 호환 배포판은 남의 로고 대신 일반 Linux ("TencentOS" 는 "centos" 를 품고 있어 먼저 본다)
        if (Has("tencentos") || Has("opencloudos") || Has("oracle")) return Linux;
        if (Has("ubuntu")) return Ubuntu;
        if (Has("debian")) return Debian;
        if (Has("amazon linux") || Has("amazon-linux") || Has("amzn") || Has("al2023")) return Amazon;
        if (Has("red hat") || Has("redhat") || Has("rhel")) return RedHat;
        if (Has("centos")) return CentOs;
        if (Has("rocky")) return Rocky;
        if (Has("almalinux") || Has("alma linux")) return Alma;
        if (Has("fedora")) return Fedora;
        if (Has("opensuse")) return OpenSuse;
        if (Has("suse") || Has("sles")) return Suse;
        if (Has("arch linux") || Has("archlinux")) return Arch;
        if (Has("alpine")) return Alpine;
        if (Has("linux")) return Linux;
        return null;
    }

    private static readonly Regex BannerDistroRx = new(@"\b(ubuntu|debian|raspbian|freebsd)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// SSH 배너(예: "SSH-2.0-OpenSSH_8.9p1 Ubuntu-3ubuntu0.6")에 적힌 배포판 → 배포판 낱말. 없으면 null.
    /// os-release 를 읽지 못하는 서버(제한 셸 · SFTP 없음)를 위한 짐작 — os-release 결과가 있으면 그것이 이긴다
    /// </summary>
    public static string? FromBanner(string? serverVersion)
    {
        if (string.IsNullOrWhiteSpace(serverVersion)) return null;
        var m = BannerDistroRx.Match(serverVersion);
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
    }

    /// <summary>
    /// 접속해 알아낸 배포판을 저장할지: os-release 에서 읽은 값은 다르면 저장, 배너 짐작은 저장된 값이 없을 때만
    /// (os-release 로 알아낸 "linuxmint ubuntu debian" 을 배너의 "ubuntu" 로 덮지 않게)
    /// </summary>
    public static bool ShouldStore(string? current, string? detected, bool fromOsRelease) =>
        !string.IsNullOrEmpty(detected) && current != detected && (fromOsRelease || string.IsNullOrEmpty(current));

    /// <summary>호스트의 로고 종류 (우선순위는 클래스 설명). 모르면 null</summary>
    public static string? Resolve(string? distro, string? cloudOs, string? bannerOs)
    {
        var tokens = Tokens(distro);
        if (tokens.Length > 0 && NonLinux.Contains(tokens[0])) return null;   // 접속해 보니 FreeBSD 등 — 클라우드 "Linux/UNIX" 를 믿지 않는다
        var d = FromDistro(distro);
        if (d != null && d != Linux) return d;
        var c = FromCloudOs(cloudOs);
        if (c != null && c != Linux) return c;
        if (d != null || c != null) return Linux;
        return bannerOs == SshServerOs.Windows ? Windows : null;
    }

    /// <summary>로고 종류의 표시 이름 (제품 이름이라 번역하지 않는다)</summary>
    public static string? DisplayName(string? kind) => kind switch
    {
        Ubuntu => "Ubuntu",
        Debian => "Debian",
        RedHat => "Red Hat Enterprise Linux",
        CentOs => "CentOS",
        Rocky => "Rocky Linux",
        Alma => "AlmaLinux",
        Fedora => "Fedora",
        Amazon => "Amazon Linux",
        Suse => "SUSE Linux Enterprise",
        OpenSuse => "openSUSE",
        Arch => "Arch Linux",
        Alpine => "Alpine Linux",
        Linux => "Linux",
        Windows => "Windows",
        _ => null,
    };
}
