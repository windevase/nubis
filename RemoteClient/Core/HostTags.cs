using System;
using System.Collections.Generic;
using System.Linq;

namespace Nubis;

/// <summary>호스트 사용자 태그(키=값) 변경 규칙 — 화면 무관 (MCP add_host / update_host, HostTagsTests)</summary>
public static class HostTags
{
    public const int MaxTags = 30;
    public const int MaxKeyLength = 40;
    public const int MaxValueLength = 200;

    /// <summary>바뀐 태그 하나 (From/To 가 null 이면 없음)</summary>
    public sealed record Change(string Key, string? From, string? To);

    /// <summary>
    /// current 에 updates 를 적용한 새 태그를 돌려준다 (비면 null). 값이 null·빈 문자열이면 그 키를 지운다.
    /// replace 면 updates 에 없는 키도 지운다. 키는 대소문자를 구분하지 않고, 원래 있던 키는 원래 표기를 유지한다.
    /// 잘못된 키·값이면 ArgumentException (편집 창·일괄 편집 화면에도 보이므로 번역된 문구)
    /// </summary>
    public static Dictionary<string, string>? Apply(IReadOnlyDictionary<string, string>? current,
        IEnumerable<KeyValuePair<string, string?>> updates, bool replace, List<Change> changes)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (current != null)
            foreach (var (k, v) in current) result[k] = v;
        var before = new Dictionary<string, string>(result, StringComparer.OrdinalIgnoreCase);

        var given = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rawKey, rawValue) in updates)
        {
            var key = CheckKey(rawKey);
            if (!given.Add(key)) throw new ArgumentException(L.F("태그 키 '{0}' 가 두 번 있습니다 (대소문자 구분 없음).", key));
            var value = CheckValue(key, rawValue);
            if (value == null) result.Remove(key);
            else
            {
                // 이미 있는 키는 원래 표기 유지 (Env → env 로 바꿔 적어도 같은 태그)
                var existing = result.Keys.FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (existing != null) result[existing] = value;
                else result[key] = value;
            }
        }
        if (replace)
            foreach (var k in result.Keys.Where(k => !given.Contains(k)).ToList()) result.Remove(k);
        if (result.Count > MaxTags) throw new ArgumentException(L.F("태그는 호스트마다 {0}개까지입니다.", MaxTags));

        foreach (var k in before.Keys.Union(result.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var from = before.GetValueOrDefault(k);
            var to = result.GetValueOrDefault(k);
            if (from != to) changes.Add(new Change(k, from, to));
        }
        return result.Count > 0 ? result : null;
    }

    /// <summary>편집 칸에 보여줄 글자: 한 줄에 "키=값"</summary>
    public static string ToText(IReadOnlyDictionary<string, string>? tags) =>
        tags is { Count: > 0 } ? string.Join(Environment.NewLine, tags.Select(kv => $"{kv.Key}={kv.Value}")) : "";

    /// <summary>
    /// 편집 칸 글자 → (키, 값) 목록. 한 줄에 "키=값" 하나, 빈 줄은 건너뛴다. "키=" 처럼 값이 비면 값 null (일괄 편집에서는 그 태그 지우기).
    /// 줄 번호가 담긴 오류는 FormatException (사용자 화면에 보이므로 번역된 문구)
    /// </summary>
    public static List<KeyValuePair<string, string?>> ParseText(string text)
    {
        var list = new List<KeyValuePair<string, string?>>();
        var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) throw new FormatException(L.F("태그 {0}번째 줄: '키=값' 형식으로 적으세요 ({1})", i + 1, line));
            var value = line[(eq + 1)..].Trim();
            list.Add(new(line[..eq].Trim(), value.Length > 0 ? value : null));
        }
        return list;
    }

    private static string CheckKey(string raw)
    {
        var key = (raw ?? "").Trim();
        if (key.Length == 0) throw new ArgumentException(L.T("태그 키가 비어 있습니다."));
        if (key.Length > MaxKeyLength) throw new ArgumentException(L.F("태그 키는 {0}자 이하입니다: {1}…", MaxKeyLength, McpHelpers.VisibleControlChars(key[..MaxKeyLength])));
        // 키·값에 보이지 않는 제어·양방향 문자를 넣으면 목록·승인 창에서 다르게 보이게 꾸밀 수 있다 → 거부
        if (McpHelpers.HasInvisibleChars(key) || key.Any(char.IsControl))
            throw new ArgumentException(L.F("태그 키에 보이지 않는 제어 문자·양방향 문자가 있습니다: {0}", McpHelpers.VisibleControlChars(key)));
        // "키=값, 키=값" 으로 보여주므로 구분 문자는 키에 쓸 수 없다
        if (key.IndexOfAny(new[] { '=', ',', '|' }) >= 0) throw new ArgumentException(L.F("태그 키에는 = , | 를 쓸 수 없습니다: {0}", key));
        return key;
    }

    private static string? CheckValue(string key, string? raw)
    {
        var value = (raw ?? "").Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (value.Length == 0) return null;
        if (value.Length > MaxValueLength) throw new ArgumentException(L.F("태그 '{0}' 의 값은 {1}자 이하입니다.", key, MaxValueLength));
        if (McpHelpers.HasInvisibleChars(value) || value.Any(char.IsControl))
            throw new ArgumentException(L.F("태그 '{0}' 의 값에 보이지 않는 제어 문자·양방향 문자가 있습니다: {1}", key, McpHelpers.VisibleControlChars(value)));
        return value;
    }
}
