using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Nubis;

/// <summary>
/// 엑셀 · 구글 시트에서 복사한 표(탭 구분)나 CSV(쉼표 · 세미콜론)로 된 서버 목록을 읽는다.
/// - 구분자 자동 감지 (탭 > ; > ,), RFC4180 따옴표 (구분자 · 큰따옴표 두 번 · 줄바꿈 포함 칸)
/// - 첫 줄이 머리글이면 열 이름(한국어 · 영어 동의어)으로 매핑, 아니면 줄마다 값 모양으로 추정
/// - 비밀번호 열은 절대 읽지 않는다 (경고만 한 번)
/// UI 와 무관한 순수 함수 — TableImportTests 로 검증한다.
/// </summary>
public static class TableImport
{
    private enum Field { Name, Address, Port, Protocol, Group, User, Os, Note, Jump, Password }

    /// <summary>머리글 동의어 (정규화한 형태로 비교: 소문자, 공백 · _ · - · . 등 제거)</summary>
    private static readonly Dictionary<string, Field> HeaderNames = BuildHeaderNames();

    private static Dictionary<string, Field> BuildHeaderNames()
    {
        var d = new Dictionary<string, Field>(StringComparer.Ordinal);
        void Add(Field f, params string[] names) { foreach (var n in names) d[NormHeader(n)] = f; }
        Add(Field.Name, "name", "이름", "호스트명", "호스트 이름", "서버명", "서버 이름", "서버", "server", "server name",
            "hostname", "host name", "label", "display name", "표시 이름", "장비명", "명칭", "computer name", "컴퓨터 이름");
        Add(Field.Address, "address", "주소", "ip", "ip address", "ip 주소", "아이피", "host", "호스트", "addr", "접속 주소",
            "fqdn", "dns", "dns name", "private ip", "public ip", "사설 ip", "공인 ip", "내부 ip", "외부 ip", "ipv4", "ipv6");
        Add(Field.Port, "port", "포트", "포트 번호", "port number", "ssh port", "rdp port");
        Add(Field.Protocol, "protocol", "프로토콜", "종류", "type", "유형", "접속 방식", "접속 유형", "connection", "connection type");
        Add(Field.Group, "group", "그룹", "분류", "환경", "folder", "폴더", "env", "environment", "category", "카테고리", "구분",
            "project", "프로젝트");
        Add(Field.User, "user", "username", "user name", "사용자", "사용자명", "계정", "계정명", "login", "login id", "account", "user id", "userid");
        Add(Field.Os, "os", "운영체제", "운영 체제", "platform", "플랫폼", "os type", "os name", "operating system",
            "os 종류", "os 버전", "os version", "운영체제 종류", "운영체제 버전", "os 정보");
        Add(Field.Note, "note", "notes", "설명", "비고", "description", "purpose", "용도", "memo", "메모", "comment", "comments",
            "주석", "역할", "role");
        Add(Field.Jump, "jump", "jump host", "jump server", "점프", "점프 서버", "점프 호스트", "bastion", "bastion host", "배스천",
            "proxyjump", "proxy jump", "경유", "경유 서버");
        Add(Field.Password, "password", "비밀번호", "pw", "passwd", "pass", "pwd", "패스워드", "암호", "secret", "비번");
        return d;
    }

    private static readonly Regex Ipv4 = new(@"^\d{1,3}(\.\d{1,3}){3}$", RegexOptions.CultureInvariant);
    private static readonly Regex HostnameRx = new(
        @"^(?=.{1,253}$)([a-z0-9_]([a-z0-9_-]{0,61}[a-z0-9_])?\.)+[a-z]([a-z0-9-]{0,61}[a-z0-9])?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex OsWordRx = new(
        @"^(windows|linux|ubuntu|centos|rhel|red ?hat|debian|rocky|alma(linux)?|amazon ?linux|suse|freebsd|unix)(\s.*|\d[\d.]*)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly string[] LinuxWords =
        { "linux", "ubuntu", "centos", "rhel", "red hat", "redhat", "debian", "rocky", "alma", "suse", "fedora", "oracle linux", "freebsd", "unix" };

    /// <summary>붙여 넣은 표 텍스트 → 가져올 서버 목록 + 경고</summary>
    public static ImportResult Parse(string text)
    {
        var result = ImportResult.Empty();
        if (string.IsNullOrWhiteSpace(text)) return result;

        text = text.TrimStart('﻿').Replace("\r\n", "\n").Replace('\r', '\n');
        var delim = DetectDelimiter(text);
        var rows = ReadRows(text, delim, out var unclosedQuote);

        int first = rows.FindIndex(r => !IsBlank(r));
        if (first < 0) return result;

        var map = TryHeader(rows[first]);
        int start = first;
        bool noAddressColumn = false;
        if (map != null)
        {
            start = first + 1;
            if (map.ContainsKey(Field.Password))
                result.Warnings.Add(L.T("비밀번호 열은 가져오지 않습니다. 계정은 등록할 때 Vault 로 지정하세요."));
            if (!map.ContainsKey(Field.Address))
            {
                noAddressColumn = true;
                result.Warnings.Add(L.T("주소(IP) 열을 찾지 못했습니다. 이름 열이 주소 형태인 줄만 가져옵니다."));
            }
        }

        var missing = new List<int>();
        var badPort = new List<int>();
        // 엑셀에서 병합한 그룹 칸은 복사하면 첫 줄에만 값이 있고 나머지는 빈 칸 → 위 줄의 그룹으로 채운다
        string? lastGroup = null;
        int filledGroups = 0;
        for (int i = start; i < rows.Count; i++)
        {
            if (IsBlank(rows[i])) continue;
            var cells = rows[i].Select(c => Narrow(c).Trim()).ToList();
            int rowNo = i + 1;   // 표의 행 번호 (머리글 · 빈 줄 포함, 1부터)
            bool portBad = false;
            var host = map != null ? FromHeader(cells, map, noAddressColumn, ref portBad) : FromCells(cells);
            if (portBad) badPort.Add(rowNo);
            if (host == null) { missing.Add(rowNo); continue; }
            if (map != null && map.ContainsKey(Field.Group))
            {
                if (host.Group != null) lastGroup = host.Group;
                else if (lastGroup != null) { host = host with { Group = lastGroup }; filledGroups++; }
            }
            result.Hosts.Add(host);
        }
        if (filledGroups > 0)
            result.Warnings.Add(L.F("그룹 칸이 빈 {0}개 줄은 위 줄의 그룹으로 채웠습니다 (병합된 셀). 다르면 미리보기에서 확인하세요.", filledGroups));

        if (missing.Count > 0)
            result.Warnings.Add(L.F("주소가 없어 건너뛴 줄 ({0}개): {1}", missing.Count, FormatRows(missing)));
        if (badPort.Count > 0)
            result.Warnings.Add(L.F("포트 값이 올바르지 않아 무시한 줄 ({0}개): {1}", badPort.Count, FormatRows(badPort)));
        if (unclosedQuote)
            result.Warnings.Add(L.T("닫히지 않은 따옴표가 있습니다. 마지막 줄의 내용을 확인하세요."));
        return result;
    }

    // ───────── 구분자 · 행 읽기 ─────────

    /// <summary>앞쪽 비어 있지 않은 줄(최대 5줄)에서 따옴표 밖 구분자를 센다. 우선순위 탭 > ; > ,</summary>
    private static char DetectDelimiter(string text)
    {
        var sample = text.Split('\n').Where(l => l.Trim().Length > 0).Take(5).ToList();
        if (sample.Count == 0) return ',';
        var candidates = new[] { '\t', ';', ',' };
        int need = (sample.Count + 1) / 2;
        foreach (var c in candidates)
            if (sample.Count(l => CountOutsideQuotes(l, c) > 0) >= need) return c;
        foreach (var c in candidates)
            if (sample.Any(l => CountOutsideQuotes(l, c) > 0)) return c;
        return ',';
    }

    private static int CountOutsideQuotes(string line, char c)
    {
        int n = 0;
        bool inQ = false;
        foreach (var ch in line)
        {
            if (ch == '"') inQ = !inQ;
            else if (ch == c && !inQ) n++;
        }
        return n;
    }

    /// <summary>RFC4180 방식으로 행 · 칸을 나눈다 (따옴표 칸 안의 구분자 · 줄바꿈 · "" 처리). 빈 줄도 행으로 남겨 행 번호를 맞춘다</summary>
    private static List<List<string>> ReadRows(string text, char delim, out bool unclosedQuote)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        bool inQuotes = false, quoted = false, hasText = false;

        void EndCell()
        {
            row.Add(cell.ToString());
            cell.Clear();
            quoted = false;
            hasText = false;
        }

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else inQuotes = false;
                }
                else cell.Append(c);
                continue;
            }
            if (c == '"' && !hasText && !quoted)
            {
                // 칸 시작(앞 공백만 있음)의 따옴표 → 따옴표 칸
                cell.Clear();
                inQuotes = true;
                quoted = true;
                continue;
            }
            if (c == delim) { EndCell(); continue; }
            if (c == '\n')
            {
                EndCell();
                rows.Add(row);
                row = new List<string>();
                continue;
            }
            cell.Append(c);
            if (!char.IsWhiteSpace(c)) hasText = true;
        }

        unclosedQuote = inQuotes;
        // 마지막 줄 (텍스트가 줄바꿈으로 끝나면 빈 행을 더하지 않는다)
        if (cell.Length > 0 || row.Count > 0 || quoted)
        {
            EndCell();
            rows.Add(row);
        }
        return rows;
    }

    private static bool IsBlank(List<string> row) => row.All(c => c.Trim().Length == 0);

    // ───────── 머리글 ─────────

    private static string NormHeader(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Trim().ToLowerInvariant())
            if (!char.IsWhiteSpace(ch) && ch is not ('_' or '-' or '.' or '/' or '(' or ')' or '[' or ']' or ':' or '*' or '#'))
                sb.Append(ch);
        return sb.ToString();
    }

    private static Field? MapHeader(string header)
    {
        var n = NormHeader(header);
        if (n.Length == 0) return null;
        // 비밀번호는 정확히 일치하지 않아도 (예: "root 비밀번호", "admin password") 무조건 비밀번호 열로 본다
        if (n.Contains("password") || n.Contains("passwd") || n.Contains("비밀번호") || n.Contains("패스워드") || n.Contains("암호"))
            return Field.Password;
        if (HeaderNames.TryGetValue(n, out var f)) return f;
        // 흔한 변형: "서버 IP", "관리 IP", "SSH 포트", "접속 계정"
        if (n.EndsWith("ip", StringComparison.Ordinal) || n.EndsWith("ip주소", StringComparison.Ordinal) || n.EndsWith("아이피", StringComparison.Ordinal))
            return Field.Address;
        // "IP(사설)" · "IP 공인" 처럼 IP 가 앞에 오는 형태
        if (n.StartsWith("ip", StringComparison.Ordinal) && (n.Contains("사설") || n.Contains("공인") || n.Contains("내부") || n.Contains("외부")
            || n.EndsWith("private", StringComparison.Ordinal) || n.EndsWith("public", StringComparison.Ordinal)))
            return Field.Address;
        if (n.EndsWith("port", StringComparison.Ordinal) || n.EndsWith("포트", StringComparison.Ordinal)
            || n.StartsWith("port", StringComparison.Ordinal) || n.StartsWith("포트", StringComparison.Ordinal)) return Field.Port;
        if (n.EndsWith("username", StringComparison.Ordinal) || n.EndsWith("계정", StringComparison.Ordinal)) return Field.User;
        return null;
    }

    /// <summary>첫 행이 머리글이면 열 매핑을 돌려준다. 알려진 열 이름이 하나 이상이고 IP · 포트 같은 값이 없어야 머리글로 본다</summary>
    private static Dictionary<Field, List<int>>? TryHeader(List<string> row)
    {
        var map = new Dictionary<Field, List<int>>();
        for (int i = 0; i < row.Count; i++)
        {
            var c = Narrow(row[i]).Trim();
            if (c.Length == 0) continue;
            if (IsIpLiteral(SplitAddress(c).Host) || ParsePort(c) != null) return null;
            if (MapHeader(c) is { } f)
            {
                if (!map.TryGetValue(f, out var list)) map[f] = list = new List<int>();
                list.Add(i);
            }
        }
        return map.Count > 0 ? map : null;
    }

    // ───────── 행 → 서버 ─────────

    private static ImportedHost? FromHeader(List<string> cells, Dictionary<Field, List<int>> map, bool noAddressColumn, ref bool portBad)
    {
        // 같은 종류 열이 여러 개면 (예: 사설 IP · 공인 IP) 앞에서부터 처음 값이 있는 칸
        string? Get(Field f)
        {
            if (f == Field.Password || !map.TryGetValue(f, out var idx)) return null;
            foreach (var i in idx)
                if (i < cells.Count && cells[i].Length > 0) return cells[i];
            return null;
        }

        var name = Get(Field.Name);
        var address = Get(Field.Address);
        if (noAddressColumn && name != null && LooksLikeAddress(name)) address = name;

        return Build(name, address, Get(Field.Port), Get(Field.Protocol), Get(Field.Group), Get(Field.User),
                     Get(Field.Os), Get(Field.Note), Get(Field.Jump), ref portBad);
    }

    /// <summary>머리글 없는 행: IP → 호스트명 순으로 주소를 고르고, 1~65535 정수 → 포트, ssh/rdp → 프로토콜, OS 이름 → OS, 남은 첫 글자 칸 → 이름</summary>
    private static ImportedHost? FromCells(List<string> cells)
    {
        int addrIdx = cells.FindIndex(c => c.Length > 0 && !c.Any(char.IsWhiteSpace) && IsIpLiteral(SplitAddress(c).Host));
        if (addrIdx < 0) addrIdx = cells.FindIndex(c => c.Length > 0 && LooksLikeAddress(c));
        if (addrIdx < 0) return null;

        string? port = null, proto = null, os = null, name = null;
        for (int i = 0; i < cells.Count; i++)
        {
            var c = cells[i];
            if (i == addrIdx || c.Length == 0) continue;
            if (port == null && ParsePort(c) != null) { port = c; continue; }
            if (proto == null && IsProtocolWord(c)) { proto = c; continue; }
            if (os == null && OsWordRx.IsMatch(c)) { os = c; continue; }
            if (name == null && ParsePort(c) == null) name = c;
            // 나머지 칸은 버린다 (머리글이 없으면 비밀번호 칸인지 알 수 없으므로 메모에도 넣지 않는다)
        }
        bool ignored = false;
        return Build(name, cells[addrIdx], port, proto, null, null, os, null, null, ref ignored);
    }

    private static ImportedHost? Build(string? name, string? addressRaw, string? portRaw, string? protoRaw, string? group,
                                       string? user, string? os, string? note, string? jump, ref bool portBad)
    {
        if (string.IsNullOrWhiteSpace(addressRaw)) return null;
        // "10. 0. 0. 1" · "10.0.0.1 (VIP)" 처럼 IP 에 공백·설명이 섞인 칸: 공백을 빼서 IP 가 되면 그것, 아니면 첫 토큰이 IP 면 그것
        if (addressRaw.Any(char.IsWhiteSpace))
        {
            var squeezed = new string(addressRaw.Where(ch => !char.IsWhiteSpace(ch)).ToArray());
            var firstToken = addressRaw.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
            if (IsIpLiteral(SplitAddress(squeezed).Host)) addressRaw = squeezed;
            else if (IsIpLiteral(SplitAddress(firstToken).Host)) addressRaw = firstToken;
        }
        var addr = SplitAddress(addressRaw);
        if (addr.Host.Length == 0) return null;

        int? port = null;
        if (!string.IsNullOrWhiteSpace(portRaw))
        {
            port = ParsePort(portRaw);
            if (port == null) portBad = true;
        }
        port ??= addr.Port;

        os = OneLine(os);
        Protocol? proto = ParseProtocol(protoRaw) ?? addr.Scheme ?? InferProtocol(port, os);

        var display = OneLine(name);
        return new ImportedHost
        {
            Name = string.IsNullOrEmpty(display) ? addr.Host : display,
            Address = addr.Host,
            Port = port,
            Protocol = proto,
            Group = NormalizeGroup(group),
            Username = OneLine(user) ?? addr.User,
            Os = os,
            Note = OneLine(note),
            JumpHost = OneLine(jump),
        };
    }

    // ───────── 값 해석 ─────────

    /// <summary>"[scheme://][user@]host[:port]" 분해. IPv6 는 [::1]:22 처럼 대괄호일 때만 포트를 뗀다</summary>
    private static (string Host, int? Port, string? User, Protocol? Scheme) SplitAddress(string raw)
    {
        var s = raw.Trim();
        Protocol? scheme = null;
        int sep = s.IndexOf("://", StringComparison.Ordinal);
        if (sep > 0)
        {
            scheme = s[..sep].Trim().ToLowerInvariant() switch
            {
                "ssh" or "sftp" => Nubis.Protocol.Ssh,
                "rdp" => Nubis.Protocol.Rdp,
                _ => null,
            };
            s = s[(sep + 3)..].TrimEnd('/');
        }

        string? user = null;
        int at = s.LastIndexOf('@');
        if (at >= 0)
        {
            var u = s[..at].Trim();
            user = u.Length > 0 ? u : null;
            s = s[(at + 1)..].Trim();
        }

        int? port = null;
        if (s.StartsWith('['))
        {
            int close = s.IndexOf(']');
            if (close > 0)
            {
                var rest = s[(close + 1)..].Trim();
                if (rest.StartsWith(':')) port = ParsePort(rest[1..]);
                s = s[1..close];
            }
        }
        else if (s.Count(ch => ch == ':') == 1)
        {
            int colon = s.IndexOf(':');
            var p = ParsePort(s[(colon + 1)..]);
            if (p != null)
            {
                port = p;
                s = s[..colon];
            }
        }
        return (s.Trim(), port, user, scheme);
    }

    /// <summary>1~65535. 엑셀 숫자 서식의 천 단위 쉼표("3,389")도 받는다 (쉼표 자리가 맞을 때만)</summary>
    private static int? ParsePort(string s)
    {
        s = s.Trim();
        if (s.Length is 5 or 6 && s[^4] == ',' && s.Count(ch => ch == ',') == 1) s = s.Remove(s.Length - 4, 1);
        return int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is >= 1 and <= 65535 ? p : null;
    }

    /// <summary>전각 영숫자·기호(１０．０．０．１, ：)와 전각 공백을 반각으로. 한글 등 다른 글자는 그대로</summary>
    private static string Narrow(string s)
    {
        StringBuilder? sb = null;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            char n = c is >= '！' and <= '～' ? (char)(c - 0xFEE0) : c == '　' ? ' ' : c;
            if (n != c)
            {
                sb ??= new StringBuilder(s, 0, i, s.Length);
                sb.Append(n);
            }
            else sb?.Append(c);
        }
        return sb?.ToString() ?? s;
    }

    private static bool IsIpLiteral(string host)
    {
        if (host.Length == 0) return false;
        if (Ipv4.IsMatch(host))
            return host.Split('.').All(o => int.TryParse(o, NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v <= 255);
        return host.Contains(':') && IPAddress.TryParse(host, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6;
    }

    /// <summary>주소처럼 보이는 칸: IP, 점이 있는 호스트명, localhost, 또는 user@ · :포트 · scheme:// 가 붙은 형태</summary>
    private static bool LooksLikeAddress(string cell)
    {
        var c = cell.Trim();
        if (c.Length == 0 || c.Any(char.IsWhiteSpace)) return false;
        var a = SplitAddress(c);
        if (a.Host.Length == 0 || a.Host.Any(char.IsWhiteSpace)) return false;
        if (IsIpLiteral(a.Host) || HostnameRx.IsMatch(a.Host) || a.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        // 점 없는 이름이라도 user@ · :포트 · ssh:// 처럼 접속 주소 형태가 분명하면 주소로 본다
        return (a.User != null || a.Port != null || a.Scheme != null) && Regex.IsMatch(a.Host, @"^[A-Za-z0-9_][A-Za-z0-9_.-]*$");
    }

    private static bool IsProtocolWord(string s) =>
        s.Trim().ToLowerInvariant() is "ssh" or "rdp" or "sftp";

    /// <summary>프로토콜 열 값: ssh/rdp 또는 windows/linux 같은 종류 이름</summary>
    private static Protocol? ParseProtocol(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var v = s.Trim().ToLowerInvariant();
        if (v.Contains("rdp") || v.Contains("windows") || v == "win" || v.Contains("원격 데스크톱") || v.Contains("원격데스크톱") || v == "윈도우")
            return Nubis.Protocol.Rdp;
        if (v.Contains("ssh") || v == "sftp" || v == "리눅스" || IsLinuxOs(v)) return Nubis.Protocol.Ssh;
        return null;
    }

    /// <summary>프로토콜 정보가 없을 때: 포트 3389/33890 → RDP, 22 → SSH, 그다음 OS (Windows → RDP, 리눅스 계열 → SSH)</summary>
    private static Protocol? InferProtocol(int? port, string? os)
    {
        if (port is 3389 or 33890) return Nubis.Protocol.Rdp;
        if (port == 22) return Nubis.Protocol.Ssh;
        if (string.IsNullOrWhiteSpace(os)) return null;
        var v = os.ToLowerInvariant();
        if (v.Contains("windows") || v.Contains("윈도우")) return Nubis.Protocol.Rdp;
        if (IsLinuxOs(v) || v.Contains("리눅스")) return Nubis.Protocol.Ssh;
        return null;
    }

    private static bool IsLinuxOs(string lower) => LinuxWords.Any(w => lower.Contains(w, StringComparison.Ordinal));

    /// <summary>그룹 여러 단계 (a/b, a\b) → "a / b"</summary>
    private static string? NormalizeGroup(string? g)
    {
        if (string.IsNullOrWhiteSpace(g)) return null;
        var parts = g.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
                     .Select(p => OneLine(p))
                     .Where(p => !string.IsNullOrEmpty(p))
                     .ToList();
        return parts.Count == 0 ? null : string.Join(" / ", parts);
    }

    /// <summary>칸 안 줄바꿈 · 연속 공백을 한 칸으로. 빈 값은 null</summary>
    private static string? OneLine(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return Regex.Replace(s.Trim(), @"\s+", " ");
    }

    /// <summary>행 번호 목록을 "3-5, 8, 10" 처럼 줄인다 (너무 길면 앞 20구간 + …)</summary>
    internal static string FormatRows(IReadOnlyList<int> rows)
    {
        var sorted = rows.Distinct().OrderBy(x => x).ToList();
        var parts = new List<string>();
        for (int i = 0; i < sorted.Count;)
        {
            int j = i;
            while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1) j++;
            parts.Add(j == i ? sorted[i].ToString(CultureInfo.InvariantCulture)
                             : sorted[i].ToString(CultureInfo.InvariantCulture) + "-" + sorted[j].ToString(CultureInfo.InvariantCulture));
            i = j + 1;
        }
        const int max = 20;
        return parts.Count <= max ? string.Join(", ", parts) : string.Join(", ", parts.Take(max)) + ", …";
    }
}
