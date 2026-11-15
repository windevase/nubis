using System;
using System.Collections.Generic;

namespace Nubis;

/// <summary>명령 팔레트 항목 종류</summary>
internal enum PaletteKind { Host, Tab, Action }

/// <summary>
/// 명령 팔레트(Ctrl+K)의 한 줄 — 호스트 · 열린 탭 · 앱 동작. WPF 에 기대지 않는 순수 데이터라 테스트에서 그대로 만든다.
/// 검색용 문자열(Name · Address · Group · Extra)은 <see cref="PaletteIndex"/> 를 만들 때 한 번만 소문자로 바꿔 둔다.
/// </summary>
internal sealed class PaletteItem
{
    public required PaletteKind Kind { get; init; }
    /// <summary>화면 제목 (호스트 이름 · 탭 제목 · 동작 이름). 검색의 이름 필드</summary>
    public required string Title { get; init; }
    /// <summary>제목 아래 보조 줄 (주소 등)</summary>
    public string Subtitle { get; init; } = "";
    /// <summary>오른쪽 작은 글자 (그룹 · "열린 탭" · 단축키)</summary>
    public string Badge { get; init; } = "";
    /// <summary>Segoe Fluent Icons 글리프</summary>
    public string Glyph { get; init; } = "";
    /// <summary>아이콘 색 구분 (SSH · RDP · 그 밖)</summary>
    public Protocol? Protocol { get; init; }

    // ----- 검색 필드 (원문. 소문자 변환은 인덱스가 한 번) -----
    /// <summary>주소들 (사설 · 공인 · 입력 주소). 앞쪽 일치를 주소마다 따로 본다</summary>
    public IReadOnlyList<string?>? Addresses { get; init; }
    public string? Group { get; init; }
    /// <summary>그 밖에 찾을 말 (사용자 · 프로토콜 · 인스턴스 ID · 리전 · 동작의 영어 · 한국어 이름 등). 낮은 점수</summary>
    public string? Extra { get; init; }

    /// <summary>마지막 사용 (호스트 = 마지막 접속). 검색어가 없을 때 "최근" 순서 · 동점 정렬에 쓴다</summary>
    public DateTime? LastUsed { get; init; }
    public bool Favorite { get; init; }
    /// <summary>공인 IP 로 연결할 수 있는 호스트 (Shift/Alt+Enter)</summary>
    public bool HasPublicIp { get; init; }

    /// <summary>실행 대상: HostEntry · SessionTab · Action (동작)</summary>
    public object? Target { get; init; }
}

/// <summary>
/// 명령 팔레트 검색 · 순위. 팔레트를 열 때 한 번 만들고(항목 수 × 문자열 4개 소문자 변환), 글자를 칠 때마다 <see cref="Search"/>.
/// · 검색어는 공백으로 나눈 단어가 <b>모두</b> 맞아야 한다 (호스트 목록 검색과 같은 AND 규칙). 대소문자 무시(서수 비교)
/// · 단어마다 점수: 이름 일치 &gt; 이름 앞쪽 &gt; 이름 단어 앞 &gt; 이름 포함 &gt; 주소 앞쪽 &gt; 주소 포함 &gt; 그룹 &gt; 기타 &gt; 이름 퍼지(글자 순서만 맞음)
/// · 동점이면 최근 사용 → 원래 순서. 열린 탭 · 즐겨찾기 · 최근 7일/30일 사용에 작은 가산점
/// · 검색어가 없으면: 최근 접속 호스트(최대 <see cref="RecentCount"/>) → 열린 탭 → 동작 → 그 밖의 즐겨찾기
/// 글자마다 새로 만드는 것은 소문자 검색어 문자열 하나와 결과 목록 채우기뿐 — 점수 버퍼 · 비교 대리자는 인덱스가 재사용한다.
/// 1000개 기준 단어 하나에 문자열 IndexOf 4~5번 = 1ms 미만이라 입력 지연(디바운스) 없이 바로 거른다.
/// </summary>
internal sealed class PaletteIndex
{
    public const int DefaultLimit = 50;
    public const int RecentCount = 10;
    /// <summary>주소 여러 개를 한 문자열로 이을 때의 구분자 (검색어에 나올 수 없는 글자)</summary>
    private const char AddrSep = '\u0001';

    private readonly PaletteItem[] _items;
    private readonly string[] _name, _addr, _group, _extra;
    private readonly (int Score, int Index)[] _buf;
    // 비교자는 인덱스마다 한 번만 만든다 (글자마다 대리자 · 비교자를 새로 만들지 않게)
    private readonly IComparer<(int Score, int Index)> _byScore;
    private readonly IComparer<(int Score, int Index)> _byRecent;

    public int Count => _items.Length;

    public PaletteIndex(IReadOnlyList<PaletteItem> items)
    {
        var n = items.Count;
        _items = new PaletteItem[n];
        _name = new string[n];
        _addr = new string[n];
        _group = new string[n];
        _extra = new string[n];
        _buf = new (int, int)[n];
        for (var i = 0; i < n; i++)
        {
            var it = items[i];
            _items[i] = it;
            _name[i] = Lower(it.Title);
            _addr[i] = JoinAddresses(it.Addresses);
            _group[i] = Lower(it.Group);
            _extra[i] = Lower(it.Extra);
        }
        _byScore = Comparer<(int Score, int Index)>.Create(CompareByScore);
        _byRecent = Comparer<(int Score, int Index)>.Create(CompareByRecent);
    }

    private static string Lower(string? s) => string.IsNullOrEmpty(s) ? "" : s.ToLowerInvariant();

    private static string JoinAddresses(IReadOnlyList<string?>? list)
    {
        if (list == null || list.Count == 0) return "";
        var parts = new List<string>(list.Count);
        foreach (var a in list)
        {
            if (string.IsNullOrWhiteSpace(a)) continue;
            var l = a.Trim().ToLowerInvariant();
            if (!parts.Contains(l)) parts.Add(l);
        }
        return string.Join(AddrSep, parts);
    }

    /// <summary>
    /// 검색해 output 을 비우고 결과(최대 limit 개)로 채운다. now = 최근 가산점 기준 (테스트에서 고정)
    /// </summary>
    public void Search(string? query, List<PaletteItem> output, int limit = DefaultLimit, DateTime? now = null)
    {
        output.Clear();
        if (limit <= 0 || _items.Length == 0) return;
        var q = string.IsNullOrWhiteSpace(query) ? "" : query.Trim().ToLowerInvariant();
        if (q.Length == 0) { EmptyQuery(output, limit); return; }

        var t = now ?? DateTime.Now;
        var count = 0;
        for (var i = 0; i < _items.Length; i++)
        {
            var total = 0;
            var all = true;
            // 공백으로 나눈 단어마다 (Split 없이 span 으로)
            var rest = q.AsSpan();
            while (rest.Length > 0)
            {
                var sp = rest.IndexOf(' ');
                var token = sp < 0 ? rest : rest[..sp];
                rest = sp < 0 ? ReadOnlySpan<char>.Empty : rest[(sp + 1)..];
                if (token.Length == 0) continue;
                var s = ScoreToken(token, _name[i], _addr[i], _group[i], _extra[i]);
                if (s <= 0) { all = false; break; }
                total += s;
            }
            if (!all) continue;
            _buf[count++] = (total + Bonus(_items[i], t), i);
        }
        if (count == 0) return;
        Array.Sort(_buf, 0, count, _byScore);
        for (var k = 0; k < count && output.Count < limit; k++) output.Add(_items[_buf[k].Index]);
    }

    /// <summary>검색어 없음: 최근 접속 호스트 → 열린 탭 → 동작 → (최근에 없던) 즐겨찾기 호스트</summary>
    private void EmptyQuery(List<PaletteItem> output, int limit)
    {
        var count = 0;
        for (var i = 0; i < _items.Length; i++)
            if (_items[i] is { Kind: PaletteKind.Host, LastUsed: not null }) _buf[count++] = (0, i);
        Array.Sort(_buf, 0, count, _byRecent);
        var recent = Math.Min(count, RecentCount);
        for (var k = 0; k < recent && output.Count < limit; k++) output.Add(_items[_buf[k].Index]);
        for (var i = 0; i < _items.Length && output.Count < limit; i++)
            if (_items[i].Kind == PaletteKind.Tab) output.Add(_items[i]);
        for (var i = 0; i < _items.Length && output.Count < limit; i++)
            if (_items[i].Kind == PaletteKind.Action) output.Add(_items[i]);
        // 최근 목록에 이미 넣은 호스트(_buf 앞쪽 recent 개)는 즐겨찾기로 두 번 넣지 않는다
        for (var i = 0; i < _items.Length && output.Count < limit; i++)
        {
            var it = _items[i];
            if (it.Kind != PaletteKind.Host || !it.Favorite || InRecent(i, recent)) continue;
            output.Add(it);
        }
    }

    private bool InRecent(int index, int recent)
    {
        for (var k = 0; k < recent; k++) if (_buf[k].Index == index) return true;
        return false;
    }

    private int CompareByScore((int Score, int Index) a, (int Score, int Index) b)
    {
        if (a.Score != b.Score) return b.Score.CompareTo(a.Score);
        var r = CompareByRecent(a, b);
        return r != 0 ? r : a.Index.CompareTo(b.Index);
    }

    private int CompareByRecent((int Score, int Index) a, (int Score, int Index) b)
    {
        var la = _items[a.Index].LastUsed ?? DateTime.MinValue;
        var lb = _items[b.Index].LastUsed ?? DateTime.MinValue;
        var r = lb.CompareTo(la);
        return r != 0 ? r : a.Index.CompareTo(b.Index);
    }

    /// <summary>종류 · 즐겨찾기 · 최근 사용 가산점 (단어 점수 차이를 뒤집지 않을 만큼 작게)</summary>
    internal static int Bonus(PaletteItem it, DateTime now)
    {
        var b = 0;
        if (it.Kind == PaletteKind.Tab) b += 30;   // 이미 열린 탭은 새 연결보다 먼저
        if (it.Favorite) b += 20;
        if (it.LastUsed is { } last)
        {
            var age = now - last;
            if (age <= TimeSpan.FromDays(7)) b += 25;
            else if (age <= TimeSpan.FromDays(30)) b += 10;
        }
        return b;
    }

    // ----- 단어 점수 -----
    public const int NameExact = 1000, NamePrefix = 800, NameWord = 600, NameContains = 400;
    public const int AddrPrefix = 350, AddrContains = 250, GroupPrefix = 200, GroupContains = 150, ExtraContains = 100;
    public const int FuzzyMax = 90;

    /// <summary>
    /// 소문자 단어 하나의 점수 (0 = 맞지 않음). 필드들은 이미 소문자. 가장 높은 필드 점수 하나만 쓴다
    /// </summary>
    internal static int ScoreToken(ReadOnlySpan<char> token, string name, string addr, string group, string extra)
    {
        if (token.Length == 0) return 0;
        var n = MatchKind(name, token, ' ');
        if (n == Match.Exact) return NameExact;
        if (n == Match.Prefix) return NamePrefix;
        if (n == Match.Word) return NameWord;
        if (n == Match.Inside) return NameContains;
        var a = MatchKind(addr, token, AddrSep);
        if (a is Match.Exact or Match.Prefix or Match.Segment) return AddrPrefix;
        if (a != Match.None) return AddrContains;
        var g = MatchKind(group, token, ' ');
        if (g is Match.Exact or Match.Prefix) return GroupPrefix;
        if (g != Match.None) return GroupContains;
        if (extra.Length > 0 && extra.AsSpan().IndexOf(token) >= 0) return ExtraContains;
        return FuzzyScore(token, name);
    }

    private enum Match { None, Inside, Word, Segment, Prefix, Exact }

    /// <summary>
    /// text 안에서 token 이 어떻게 맞는지. Segment = 구분자(sep) 바로 뒤에서 시작 (주소 여러 개 중 하나의 앞쪽),
    /// Word = 글자·숫자가 아닌 글자 뒤에서 시작 (이름의 단어 앞: "web-prod" 의 "prod")
    /// </summary>
    private static Match MatchKind(string text, ReadOnlySpan<char> token, char sep)
    {
        if (text.Length < token.Length) return Match.None;
        var span = text.AsSpan();
        var idx = span.IndexOf(token);
        if (idx < 0) return Match.None;
        if (idx == 0)
        {
            if (token.Length == text.Length) return Match.Exact;
            if (sep == AddrSep && span[token.Length] == AddrSep) return Match.Exact;   // 첫 주소와 정확히 같음
            return Match.Prefix;
        }
        var best = Match.Inside;
        // 같은 단어가 여러 번 나오면 가장 좋은 위치 (단어 앞 · 주소 앞)
        while (idx >= 0)
        {
            var prev = span[idx - 1];
            if (prev == sep && sep == AddrSep) return Match.Segment;
            if (!char.IsLetterOrDigit(prev) && best < Match.Word) best = Match.Word;
            var next = span[(idx + 1)..].IndexOf(token);
            idx = next < 0 ? -1 : idx + 1 + next;
        }
        return best;
    }

    /// <summary>
    /// 퍼지: token 글자들이 text 에 순서대로 다 나오면 1~<see cref="FuzzyMax"/> (흩어질수록 낮게), 아니면 0.
    /// 두 글자 미만이면 쓰지 않는다 (한 글자 퍼지는 거의 모든 이름에 걸림)
    /// </summary>
    internal static int FuzzyScore(ReadOnlySpan<char> token, string text)
    {
        if (token.Length < 2 || text.Length < token.Length) return 0;
        var first = -1;
        var last = -1;
        var ti = 0;
        for (var i = 0; i < text.Length && ti < token.Length; i++)
        {
            if (text[i] != token[ti]) continue;
            if (first < 0) first = i;
            last = i;
            ti++;
        }
        if (ti < token.Length) return 0;
        var spread = last - first + 1 - token.Length;   // 사이에 낀 글자 수
        return Math.Max(1, FuzzyMax - spread * 5 - first);
    }
}
