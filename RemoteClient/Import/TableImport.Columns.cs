using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Nubis;

/// <summary>표 가져오기: 머리글 → 열 매핑 · 행 → 서버 (TableImport 의 일부)</summary>
public static partial class TableImport
{
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
        string? Get(Field f) => Cell(cells, map, f);

        var name = Get(Field.Name);
        var address = Get(Field.Address);
        if (noAddressColumn && name != null && LooksLikeAddress(name)) address = name;

        return Build(name, address, Get(Field.Port), Get(Field.Protocol), Get(Field.Group), Get(Field.User),
                     Get(Field.Os), Get(Field.Note), Get(Field.Jump), ref portBad);
    }

    /// <summary>열 값. 같은 종류 열이 여러 개면 (예: 사설 IP · 공인 IP) 앞에서부터 처음 값이 있는 칸. 비밀번호 열은 읽지 않는다</summary>
    private static string? Cell(List<string> cells, Dictionary<Field, List<int>> map, Field f)
    {
        if (f == Field.Password || !map.TryGetValue(f, out var idx)) return null;
        foreach (var i in idx)
            if (i < cells.Count && cells[i].Length > 0) return cells[i];
        return null;
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
            // 이름: 주소 앞 칸을 먼저 쓴다. 주소 뒤 칸은 비밀번호처럼 보이면 쓰지 않는다 —
            // "IP · 비밀번호" 처럼 이름 열이 없는 표에서 비밀번호가 호스트 이름으로 저장 · 표시되지 않게
            if (name == null && ParsePort(c) == null && (i < addrIdx || !LooksLikeSecret(c))) name = c;
            // 나머지 칸은 버린다 (머리글이 없으면 비밀번호 칸인지 알 수 없으므로 메모에도 넣지 않는다)
        }
        bool ignored = false;
        return Build(name, cells[addrIdx], port, proto, null, null, os, null, null, ref ignored);
    }

    /// <summary>
    /// 머리글 없는 칸이 비밀번호로 보이는가: 글자 · 숫자 · 공백 · '-' '_' '.' 밖의 기호가 있거나,
    /// 공백 없이 대문자 · 소문자 · 숫자가 모두 섞였으면 (web01 · WEB-01 · 웹서버 1 은 이름, P@ss · Passw0rd 는 비밀번호)
    /// </summary>
    internal static bool LooksLikeSecret(string c)
    {
        if (c.Any(ch => !char.IsLetterOrDigit(ch) && !char.IsWhiteSpace(ch) && ch is not ('-' or '_' or '.'))) return true;
        return !c.Any(char.IsWhiteSpace) && c.Any(char.IsUpper) && c.Any(char.IsLower) && c.Any(char.IsDigit);
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
}
