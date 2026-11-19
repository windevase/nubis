using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Nubis;

/// <summary>
/// 원격 데스크톱 연결(mstsc) 의 .rdp 파일 → RDP 호스트. 파일 하나가 호스트 하나 (이름 = 파일 이름, 확장자 제외).
/// 옮기는 값: full address(호스트[:포트]) · server port · username(AzureAD\ · DOMAIN\ 형식 그대로) ·
/// enablecredsspsupport:i:0 → NLA 끄기 · gatewayhostname + gatewayusagemethod(0 이 아닐 때) → RD 게이트웨이.
/// 비밀번호(password 51:b: 등)는 어떤 경우에도 가져오지 않는다. 화면 · 저장소와 무관 (RdpFileImportTests)
/// </summary>
public static class RdpFileImport
{
    /// <summary>.rdp 한 개의 최대 크기 — 서명된 파일도 수 KB 라 이보다 크면 .rdp 가 아니라고 본다</summary>
    public const long MaxFileBytes = 1024 * 1024;
    /// <summary>한 번에 가져올 최대 파일 수</summary>
    public const int MaxFiles = 1000;
    /// <summary>사용자명 최대 길이 (UPN 은 256자까지 — 넘으면 잘못 읽은 값)</summary>
    private const int MaxUserLength = 256;
    private const int MaxNameLength = 200;

    /// <summary>여러 파일을 읽는다. 읽지 못한 파일은 건너뛰고 경고에 남긴다 (한 파일 때문에 전체를 버리지 않게)</summary>
    public static ImportResult ParseFiles(IEnumerable<string> paths)
    {
        var result = ImportResult.Empty();
        var list = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count > MaxFiles)
        {
            result.Warnings.Add(L.F("파일이 {0}개를 넘어 나머지는 가져오지 않았습니다.", MaxFiles));
            list = list.Take(MaxFiles).ToList();
        }
        foreach (var path in list)
        {
            try
            {
                var r = ParseFile(path);
                result.Hosts.AddRange(r.Hosts);
                result.Warnings.AddRange(r.Warnings);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException)
            {
                result.Warnings.Add(L.F("{0}: 읽지 못했습니다 ({1})", Path.GetFileName(path), ex.Message));
            }
        }
        return result;
    }

    /// <summary>파일 하나 (크기 검사 → UTF-16LE · UTF-8 판별 → Parse). 너무 크거나 텍스트가 아니면 InvalidDataException</summary>
    public static ImportResult ParseFile(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxFileBytes)
            throw new InvalidDataException(L.F("파일이 너무 큽니다 ({0:N0} 바이트). .rdp 파일이 맞는지 확인하세요.", info.Length));
        return Parse(Decode(File.ReadAllBytes(path)), NameFromPath(path));
    }

    /// <summary>파일 이름(확장자 제외)을 호스트 이름으로</summary>
    public static string NameFromPath(string path) => Path.GetFileNameWithoutExtension(path.Trim()).Trim();

    /// <summary>.rdp 바이트 → 텍스트 (mstsc 는 UTF-16LE(BOM) 로 저장하지만 UTF-8 로 고친 파일도 있다)</summary>
    public static string Decode(byte[] bytes)
    {
        var text = RdpDefaults.Decode(bytes);
        // NUL 이 섞여 있으면 판별이 틀렸거나 텍스트가 아닌 파일
        if (text.IndexOf('\0') >= 0) throw new InvalidDataException(L.T(".rdp 텍스트 파일이 아닙니다."));
        return text;
    }

    /// <summary>
    /// "이름:형식:값" 줄들 → 호스트 하나 (full address 가 없으면 호스트 없이 경고만).
    /// name 은 호스트 이름 (보통 파일 이름). 비밀번호 줄은 읽지 않고 경고만 남긴다
    /// </summary>
    public static ImportResult Parse(string text, string name)
    {
        var result = ImportResult.Empty();
        var label = name.Trim();
        if (label.Length > MaxNameLength) label = label[..MaxNameLength].Trim();
        var shown = label.Length > 0 ? label : ".rdp";

        var v = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var passwordSeen = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var a = line.IndexOf(':');
            var b = a < 0 ? -1 : line.IndexOf(':', a + 1);
            if (b < 0) continue;
            var key = line[..a].Trim();
            // 서명 블록은 쓰지 않는다 (서명은 원래 파일에만 의미가 있음)
            if (key.Equals("signature", StringComparison.OrdinalIgnoreCase) || key.Equals("signscope", StringComparison.OrdinalIgnoreCase)) continue;
            // 비밀번호(password 51:b: — DPAPI 암호문) · 게이트웨이 접근 토큰 등은 값을 아예 담지 않는다
            if (IsSecretKey(key))
            {
                if (line[(b + 1)..].Trim().Length > 0) passwordSeen = true;
                continue;
            }
            v[key] = line[(b + 1)..].Trim();
        }
        if (passwordSeen) result.Warnings.Add(L.F("{0}: .rdp 에 저장된 비밀번호는 가져오지 않습니다. 등록한 뒤 Vault 계정을 지정하세요.", shown));

        var full = v.TryGetValue("full address", out var fa) ? fa : "";
        if (full.Length == 0)
        {
            result.Warnings.Add(L.F("{0}: 컴퓨터 주소(full address)가 없어 건너뛰었습니다.", shown));
            return result;
        }

        // 포트: full address 에 붙은 포트가 우선 (mstsc 와 같음), 없으면 server port
        var (host, port) = ImportMerge.SplitHostPort(full);
        if (port == null && v.TryGetValue("server port", out var sp) && sp.Length > 0)
        {
            if (int.TryParse(sp, NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is >= 1 and <= 65535) port = p;
            else result.Warnings.Add(L.F("{0}: 포트 값이 올바르지 않아 무시했습니다: {1}", shown, sp));
        }
        // 콜론이 하나뿐인데 포트로 읽지 못한 "host:99999" 는 그대로 넘겨 미리보기에서 오류로 보이게 한다
        var address = port == null && full.Count(c => c == ':') == 1 ? full : host;

        string? user = null;
        if (v.TryGetValue("username", out var u) && u.Length > 0)
        {
            if (u.Length > MaxUserLength || u.Any(char.IsControl))
                result.Warnings.Add(L.F("{0}: 사용자명이 올바르지 않아 비워 둡니다.", shown));
            else user = u;
        }

        string? gateway = null;
        if (v.TryGetValue("gatewayhostname", out var gw) && gw.Length > 0 &&
            !(v.TryGetValue("gatewayusagemethod", out var gu) && int.TryParse(gu, NumberStyles.Integer, CultureInfo.InvariantCulture, out var usage) && usage == 0))
        {
            if (IsValidGateway(gw)) gateway = gw;
            else result.Warnings.Add(L.F("{0}: RD 게이트웨이 주소가 올바르지 않아 비워 둡니다: {1}", shown, gw));
        }

        var disableNla = v.TryGetValue("enablecredsspsupport", out var cs) &&
                         int.TryParse(cs, NumberStyles.Integer, CultureInfo.InvariantCulture, out var credssp) && credssp == 0;

        // 미리보기의 표에는 NLA · 게이트웨이 칸이 없어 메모에 보여 준다
        var notes = new List<string>();
        if (gateway != null) notes.Add(L.F("RD 게이트웨이 {0}", gateway));
        if (disableNla) notes.Add(L.T("NLA 끔"));

        result.Hosts.Add(new ImportedHost
        {
            Name = label,
            Address = address,
            Port = port,
            Protocol = Protocol.Rdp,
            Username = user,
            RdpGateway = gateway,
            RdpDisableNla = disableNla,
            Note = notes.Count > 0 ? string.Join(" · ", notes) : null,
        });
        return result;
    }

    /// <summary>값을 읽지 않을 키: 비밀번호 · 게이트웨이 접근 토큰 등 비밀값</summary>
    private static bool IsSecretKey(string key) =>
        key.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        key.Contains("accesstoken", StringComparison.OrdinalIgnoreCase);

    /// <summary>게이트웨이: 호스트명 · IP (뒤에 :포트 가능)</summary>
    private static bool IsValidGateway(string gw)
    {
        var (h, _) = ImportMerge.SplitHostPort(gw);
        return ImportMerge.IsValidAddress(h) && h.Count(c => c == ':') != 1;
    }
}
