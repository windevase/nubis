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
public static partial class TableImport
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
        // 병합 셀은 붙어 있는 줄에만 걸치므로 빈 줄을 만나면 끊는다 (빈 줄로 나눈 다음 묶음은 그룹이 없는 게 의도일 수 있음).
        // 주소가 없어 건너뛴 줄이라도 그룹 칸에 값이 있으면 그 값부터 아래로 채운다
        string? lastGroup = null;
        int filledGroups = 0;
        bool fillGroups = map != null && map.ContainsKey(Field.Group);
        for (int i = start; i < rows.Count; i++)
        {
            if (IsBlank(rows[i])) { lastGroup = null; continue; }
            var cells = rows[i].Select(c => Narrow(c).Trim()).ToList();
            int rowNo = i + 1;   // 표의 행 번호 (머리글 · 빈 줄 포함, 1부터)
            bool portBad = false;
            var host = map != null ? FromHeader(cells, map, noAddressColumn, ref portBad) : FromCells(cells);
            if (portBad) badPort.Add(rowNo);
            if (fillGroups && NormalizeGroup(Cell(cells, map!, Field.Group)) is { } rowGroup) lastGroup = rowGroup;
            if (host == null) { missing.Add(rowNo); continue; }
            if (fillGroups && host.Group == null && lastGroup != null) { host = host with { Group = lastGroup }; filledGroups++; }
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
}
