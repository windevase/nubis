using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace Nubis;

/// <summary>터미널 색 테마 (배경/전경/커서/선택 + ANSI 16색)</summary>
public sealed class TerminalTheme
{
    private static readonly string[] AnsiNames =
    {
        "black", "red", "green", "yellow", "blue", "magenta", "cyan", "white",
        "brightBlack", "brightRed", "brightGreen", "brightYellow", "brightBlue", "brightMagenta", "brightCyan", "brightWhite",
    };

    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool IsLight { get; init; }
    public required string Background { get; init; }
    public required string Foreground { get; init; }
    public required string Cursor { get; init; }
    public required string Selection { get; init; }
    /// <summary>ANSI 0~15 (black, red, green, yellow, blue, magenta, cyan, white, bright...)</summary>
    public required string[] Ansi { get; init; }

    /// <summary>xterm.js ITheme 형식</summary>
    public Dictionary<string, string> ToXterm()
    {
        var d = new Dictionary<string, string>
        {
            ["background"] = Background,
            ["foreground"] = Foreground,
            ["cursor"] = Cursor,
            ["cursorAccent"] = Background,
            ["selectionBackground"] = Selection,
        };
        for (var i = 0; i < 16; i++) d[AnsiNames[i]] = Ansi[i];
        return d;
    }

    public Color BackgroundColor => Parse(Background);

    // 미리보기 카드용 브러시
    public Brush BgBrush => Br(Background);
    public Brush FgBrush => Br(Foreground);
    public Brush RedBrush => Br(Ansi[1]);
    public Brush GreenBrush => Br(Ansi[2]);
    public Brush YellowBrush => Br(Ansi[3]);
    public Brush BlueBrush => Br(Ansi[4]);
    public Brush MagentaBrush => Br(Ansi[5]);
    public Brush CyanBrush => Br(Ansi[6]);
    public Brush CommentBrush => Br(Ansi[8]);
    public Brush CursorBrush => Br(Cursor);

    private static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);
    private static Brush Br(string hex) { var b = new SolidColorBrush(Parse(hex)); b.Freeze(); return b; }
}

public static class TerminalThemes
{
    public const string DefaultId = "remote-dark";

    public static IReadOnlyList<TerminalTheme> All { get; } = new List<TerminalTheme>
    {
        new()
        {
            Id = "remote-dark", Name = "Remote Dark",
            Background = "#161925", Foreground = "#E6E8F0", Cursor = "#20C5A0", Selection = "#3A4A6A",
            Ansi = new[] { "#1E2232", "#F0616D", "#20C5A0", "#F5B740", "#4F8CFF", "#C27BFF", "#3EC7E0", "#C9CDDB",
                           "#5B6180", "#FF7A85", "#4BE0BC", "#FFD06B", "#7AA8FF", "#D59BFF", "#6ADCF0", "#FFFFFF" },
        },
        new()
        {
            Id = "campbell", Name = "Campbell (Windows)",
            Background = "#0C0C0C", Foreground = "#CCCCCC", Cursor = "#FFFFFF", Selection = "#264F78",
            Ansi = new[] { "#0C0C0C", "#C50F1F", "#13A10E", "#C19C00", "#0037DA", "#881798", "#3A96DD", "#CCCCCC",
                           "#767676", "#E74856", "#16C60C", "#F9F1A5", "#3B78FF", "#B4009E", "#61D6D6", "#F2F2F2" },
        },
        new()
        {
            Id = "dracula", Name = "Dracula",
            Background = "#282A36", Foreground = "#F8F8F2", Cursor = "#F8F8F2", Selection = "#44475A",
            Ansi = new[] { "#21222C", "#FF5555", "#50FA7B", "#F1FA8C", "#BD93F9", "#FF79C6", "#8BE9FD", "#F8F8F2",
                           "#6272A4", "#FF6E6E", "#69FF94", "#FFFFA5", "#D6ACFF", "#FF92DF", "#A4FFFF", "#FFFFFF" },
        },
        new()
        {
            Id = "one-dark", Name = "One Dark",
            Background = "#282C34", Foreground = "#ABB2BF", Cursor = "#528BFF", Selection = "#3E4451",
            Ansi = new[] { "#3F4451", "#E06C75", "#98C379", "#E5C07B", "#61AFEF", "#C678DD", "#56B6C2", "#D7DAE0",
                           "#5C6370", "#FF7B86", "#B1E18B", "#F0D197", "#7BC3FF", "#DA8EF0", "#6CCBD6", "#FFFFFF" },
        },
        new()
        {
            Id = "nord", Name = "Nord",
            Background = "#2E3440", Foreground = "#D8DEE9", Cursor = "#D8DEE9", Selection = "#434C5E",
            Ansi = new[] { "#3B4252", "#BF616A", "#A3BE8C", "#EBCB8B", "#81A1C1", "#B48EAD", "#88C0D0", "#E5E9F0",
                           "#4C566A", "#BF616A", "#A3BE8C", "#EBCB8B", "#81A1C1", "#B48EAD", "#8FBCBB", "#ECEFF4" },
        },
        new()
        {
            Id = "gruvbox-dark", Name = "Gruvbox Dark",
            Background = "#282828", Foreground = "#EBDBB2", Cursor = "#EBDBB2", Selection = "#504945",
            Ansi = new[] { "#282828", "#CC241D", "#98971A", "#D79921", "#458588", "#B16286", "#689D6A", "#A89984",
                           "#928374", "#FB4934", "#B8BB26", "#FABD2F", "#83A598", "#D3869B", "#8EC07C", "#EBDBB2" },
        },
        new()
        {
            Id = "monokai", Name = "Monokai",
            Background = "#272822", Foreground = "#F8F8F2", Cursor = "#F8F8F0", Selection = "#49483E",
            Ansi = new[] { "#272822", "#F92672", "#A6E22E", "#F4BF75", "#66D9EF", "#AE81FF", "#A1EFE4", "#F8F8F2",
                           "#75715E", "#F92672", "#A6E22E", "#F4BF75", "#66D9EF", "#AE81FF", "#A1EFE4", "#F9F8F5" },
        },
        new()
        {
            Id = "tomorrow-night", Name = "Tomorrow Night",
            Background = "#1D1F21", Foreground = "#C5C8C6", Cursor = "#C5C8C6", Selection = "#373B41",
            Ansi = new[] { "#1D1F21", "#CC6666", "#B5BD68", "#F0C674", "#81A2BE", "#B294BB", "#8ABEB7", "#C5C8C6",
                           "#969896", "#D54E53", "#B9CA4A", "#E7C547", "#7AA6DA", "#C397D8", "#70C0B1", "#FFFFFF" },
        },
        new()
        {
            Id = "solarized-dark", Name = "Solarized Dark",
            Background = "#002B36", Foreground = "#839496", Cursor = "#93A1A1", Selection = "#073642",
            Ansi = new[] { "#073642", "#DC322F", "#859900", "#B58900", "#268BD2", "#D33682", "#2AA198", "#EEE8D5",
                           "#586E75", "#CB4B16", "#586E75", "#657B83", "#839496", "#6C71C4", "#93A1A1", "#FDF6E3" },
        },
        new()
        {
            Id = "solarized-light", Name = "Solarized Light", IsLight = true,
            Background = "#FDF6E3", Foreground = "#586E75", Cursor = "#586E75", Selection = "#E4DCC4",
            Ansi = new[] { "#073642", "#DC322F", "#859900", "#B58900", "#268BD2", "#D33682", "#2AA198", "#EEE8D5",
                           "#93A1A1", "#CB4B16", "#586E75", "#657B83", "#839496", "#6C71C4", "#93A1A1", "#FDF6E3" },
        },
        new()
        {
            Id = "github-light", Name = "GitHub Light", IsLight = true,
            Background = "#FFFFFF", Foreground = "#24292F", Cursor = "#0969DA", Selection = "#CCE5FF",
            Ansi = new[] { "#24292F", "#CF222E", "#116329", "#4D2D00", "#0969DA", "#8250DF", "#1B7C83", "#6E7781",
                           "#57606A", "#A40E26", "#1A7F37", "#633C01", "#218BFF", "#A475F9", "#3192AA", "#8C959F" },
        },
    };

    public static TerminalTheme Get(string? id) => All.FirstOrDefault(t => t.Id == id) ?? All[0];
}

/// <summary>설정 화면의 테마 카드 바인딩용</summary>
public sealed class ThemeItem : INotifyPropertyChanged
{
    private bool _isSelected;
    public required TerminalTheme Theme { get; init; }
    public bool IsSelected { get => _isSelected; set { _isSelected = value; OnChanged(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
