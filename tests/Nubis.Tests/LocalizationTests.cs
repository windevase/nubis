using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>
/// 화면 문구 번역 검사: 소스에서 L.T("…") · L.F("…") · {l:Tr '…'} 로 감싼 한국어 문구가
/// 모두 영어 사전(RemoteClient\Lang\en\*.json)에 있는지, 자리표시자({0} 등)가 맞는지 확인한다.
/// </summary>
public class LocalizationTests
{
    private static string SourceRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "RemoteClient", "Nubis.csproj"))) dir = Path.GetDirectoryName(dir);
        return Path.Combine(dir ?? throw new InvalidOperationException("저장소 루트를 찾지 못함"), "RemoteClient");
    }

    private static string UnescapeCs(string s) =>
        Regex.Replace(s, @"\\(u[0-9A-Fa-f]{4}|.)", m => m.Groups[1].Value switch
        {
            "n" => "\n", "r" => "\r", "t" => "\t", "\"" => "\"", "\\" => "\\", "0" => "\0",
            var u when u.StartsWith("u") && u.Length == 5 => ((char)Convert.ToInt32(u[1..], 16)).ToString(),
            var other => "\\" + other,
        });

    /// <summary>소스에서 번역 대상으로 감싼 키 (파일:줄 → 키)</summary>
    public static List<(string Where, string Key)> UsedKeys() =>
        UsedKeysWithKind().Select(k => (k.Where, k.Key)).ToList();

    /// <summary>L.F 로 쓰는 키 (string.Format 을 거치므로 자리표시자·중괄호가 올바라야 함)</summary>
    public static List<(string Where, string Key)> FormatKeys() =>
        UsedKeysWithKind().Where(k => k.IsFormat).Select(k => (k.Where, k.Key)).ToList();

    /// <summary>XAML 번역 확장: {l:Tr '…'} 와 이름 붙인 형태 {l:Tr Text='…'}</summary>
    internal const string XamlTrPattern = @"\{l:Tr\s+(?:Text\s*=\s*)?'((?:[^'\\]|\\.)*)'\s*\}";

    private static List<(string Where, string Key, bool IsFormat)> UsedKeysWithKind()
    {
        var root = SourceRoot();
        var list = new List<(string, string, bool)>();
        foreach (var path in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            var ext = Path.GetExtension(path);
            if (ext is not (".cs" or ".xaml")) continue;
            // 주석 줄(사용 예시 등)은 빼고, 줄 번호는 유지
            var text = string.Join("\n", File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n").Split('\n')
                .Select(l => l.TrimStart().StartsWith("//") || l.TrimStart().StartsWith("<!--") ? "" : l));
            var rel = Path.GetRelativePath(root, path);
            if (ext == ".cs")
            {
                foreach (Match m in Regex.Matches(text, @"\bL\.([TF])\(\s*""((?:[^""\\\n]|\\.)*)"""))
                    list.Add(($"{rel}:{LineOf(text, m.Index)}", UnescapeCs(m.Groups[2].Value), m.Groups[1].Value == "F"));
            }
            else
            {
                foreach (Match m in Regex.Matches(text, XamlTrPattern))
                    list.Add(($"{rel}:{LineOf(text, m.Index)}", WebUtility.HtmlDecode(m.Groups[1].Value.Replace("\\'", "'").Replace("\\\\", "\\")), false));
            }
        }
        return list;
    }

    private static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    [Fact]
    public void 감싼_문구는_모두_영어_번역이_있다()
    {
        var table = L.EnglishTable;
        var missing = UsedKeys().Where(k => !table.ContainsKey(k.Key)).Select(k => $"{k.Where}  \"{k.Key}\"").Distinct().ToList();
        Assert.True(missing.Count == 0, $"영어 번역이 없는 문구 {missing.Count}개:\n" + string.Join("\n", missing.Take(80)));
    }

    [Fact]
    public void 번역의_자리표시자가_원문과_같다()
    {
        var bad = L.EnglishTable.Where(kv => !Holes(kv.Key).SequenceEqual(Holes(kv.Value))).Select(kv => $"\"{kv.Key}\" → \"{kv.Value}\"").ToList();
        Assert.True(bad.Count == 0, "자리표시자가 다른 번역:\n" + string.Join("\n", bad));
    }

    /// <summary>자리표시자 전체 모양 ({0}, {0:MM-dd}, {1,5} …) — 번호뿐 아니라 정렬·서식까지 같아야 한다</summary>
    internal static string[] Holes(string s) =>
        Regex.Matches(s.Replace("{{", "").Replace("}}", ""), @"\{\d+(?:,\s*-?\d+)?(?::[^}]*)?\}").Select(m => m.Value).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray();

    [Fact]
    public void L_F_문구와_번역은_string_Format_으로_오류_없이_만들어진다()
    {
        var args = Enumerable.Range(0, 10).Select(i => (object?)$"v{i}").ToArray();   // 문자열은 서식 지정자를 무시하므로 어떤 서식에도 맞음
        var bad = new List<string>();
        foreach (var (where, key) in FormatKeys().DistinctBy(k => k.Key))
        {
            foreach (var text in new[] { key, L.Translate(key, english: true) })
            {
                try { _ = string.Format(System.Globalization.CultureInfo.InvariantCulture, text, args); }
                catch (FormatException ex) { bad.Add($"{where}  \"{text}\" — {ex.Message}"); }
            }
        }
        Assert.True(bad.Count == 0, "string.Format 에서 실패하는 L.F 문구 (중괄호는 {{ }} 로):\n" + string.Join("\n", bad));
    }

    [Fact]
    public void 영어_사전에_값이_다른_중복_키가_없다()
    {
        // 같은 키가 여러 파일(또는 한 파일 안)에 있으면 나중 것이 앞의 것을 덮는다. 값이 같으면 문제없으므로 허용
        var dir = Path.Combine(SourceRoot(), "Lang", "en");
        var seen = new Dictionary<string, (string File, string Value)>(StringComparer.Ordinal);
        var bad = new List<string>();
        foreach (var path in Directory.EnumerateFiles(dir, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            foreach (var prop in doc.RootElement.EnumerateObject())   // JsonDocument 는 한 파일 안의 중복 속성도 그대로 돌려준다
            {
                var value = prop.Value.GetString() ?? "";
                var file = Path.GetFileName(path);
                if (seen.TryGetValue(prop.Name, out var prev) && prev.Value != value)
                    bad.Add($"\"{prop.Name}\": {prev.File} \"{prev.Value}\" ↔ {file} \"{value}\"");
                else if (!seen.ContainsKey(prop.Name)) seen[prop.Name] = (file, value);
            }
        }
        Assert.True(bad.Count == 0, "값이 다른 중복 키:\n" + string.Join("\n", bad));
    }

    [Fact]
    public void XAML_이름_붙인_형태도_키로_뽑는다()
    {
        string? Key(string xaml) => Regex.Match(xaml, XamlTrPattern) is { Success: true } m ? m.Groups[1].Value : null;
        Assert.Equal("주소", Key("Text=\"{l:Tr '주소'}\""));
        Assert.Equal("주소", Key("Text=\"{l:Tr Text='주소'}\""));
        Assert.Equal("주소", Key("Text=\"{l:Tr Text = '주소' }\""));
        Assert.Equal(new[] { "{0:MM-dd}", "{0}" }, Holes("{0} {0:MM-dd} {{literal}}"));   // 서수 정렬 (':' < '}')
        Assert.NotEqual(Holes("{0:MM-dd HH:mm}"), Holes("{0}"));
    }

    [Fact]
    public void 번역은_비어_있지_않고_한국어가_남지_않는다()
    {
        var bad = L.EnglishTable.Where(kv => string.IsNullOrWhiteSpace(kv.Value) || Regex.IsMatch(kv.Value, "[가-힣]"))
            .Where(kv => !kv.Key.Contains("한국어")).Select(kv => $"\"{kv.Key}\" → \"{kv.Value}\"").ToList();
        Assert.True(bad.Count == 0, "비었거나 한국어가 남은 번역:\n" + string.Join("\n", bad.Take(50)));
    }

    [Fact]
    public void 영어면_번역하고_없는_키와_한국어는_원문()
    {
        Assert.Equal("없는 문구 그대로", L.Translate("없는 문구 그대로", english: true));
        Assert.Equal("Language changes apply after restarting Nubis.", L.Translate("화면 언어를 바꾸면 Nubis 를 다시 시작해야 적용됩니다.", english: true));
        Assert.Equal("화면 언어를 바꾸면 Nubis 를 다시 시작해야 적용됩니다.", L.Translate("화면 언어를 바꾸면 Nubis 를 다시 시작해야 적용됩니다.", english: false));
    }
}
