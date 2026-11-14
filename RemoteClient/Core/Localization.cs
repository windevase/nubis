using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows.Markup;

namespace Nubis;

/// <summary>
/// 화면 문구 번역. 한국어 원문이 곧 키이고(코드를 읽기 쉽게), 영어 사전은 Lang\en\*.json (임베디드 리소스).
/// 번역이 없으면 한국어를 그대로 보여준다. 언어는 앱을 시작할 때 정해진다(바꾸면 다시 시작해야 적용).
/// - 코드: L.T("호스트 추가"), 보간이 필요하면 L.F("{0} 에 업로드", path)
/// - XAML: Text="{l:Tr '주소'}"  (xmlns:l="clr-namespace:Nubis")
/// MCP 도구 설명·결과처럼 Claude 만 읽는 문구는 번역하지 않는다 (Claude 가 두 언어를 다 이해함).
/// </summary>
public static class L
{
    public const string Korean = "ko";
    public const string English = "en";

    private static readonly Lazy<Dictionary<string, string>> En = new(LoadEnglish);

    /// <summary>지금 언어가 영어인지</summary>
    public static bool IsEnglish { get; private set; }

    /// <summary>앱 시작 때 설정값으로 정한다 (null·알 수 없는 값 = 한국어)</summary>
    public static void Init(string? language) => IsEnglish = language == English;

    /// <summary>한국어 원문 → 현재 언어 문구</summary>
    public static string T(string ko) => Translate(ko, IsEnglish);

    /// <summary>언어를 지정해 번역 (테스트용 — 전역 언어를 바꾸지 않는다)</summary>
    internal static string Translate(string ko, bool english) => english && En.Value.TryGetValue(ko, out var en) ? en : ko;

    /// <summary>자리표시자({0}, {1:HH:mm} …)가 있는 문구</summary>
    public static string F(string ko, params object?[] args)
    {
        try { return string.Format(CultureInfo.CurrentCulture, T(ko), args); }
        catch (FormatException)
        {
            // 번역의 자리표시자가 잘못됐으면 한국어 원문으로, 원문도 잘못됐으면 원문 그대로 (화면 문구 때문에 앱이 죽지 않게)
            try { return string.Format(CultureInfo.CurrentCulture, ko, args); }
            catch (FormatException) { return ko; }
        }
    }

    /// <summary>영어 사전 전체 (테스트용)</summary>
    public static IReadOnlyDictionary<string, string> EnglishTable => En.Value;

    private static Dictionary<string, string> LoadEnglish()
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        var asm = typeof(L).Assembly;
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("Lang.en.", StringComparison.Ordinal)).OrderBy(n => n))
        {
            using var s = asm.GetManifestResourceStream(name);
            if (s == null) continue;
            var part = JsonSerializer.Deserialize<Dictionary<string, string>>(s);
            if (part == null) continue;
            foreach (var (k, v) in part) d[k] = v;
        }
        return d;
    }
}

/// <summary>XAML 번역: Text="{l:Tr '주소'}"</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }
    public TrExtension(string text) => Text = text;

    [ConstructorArgument("text")]
    public string Text { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => L.T(Text);
}
