using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Nubis;

/// <summary>
/// WebView2 위의 xterm.js 터미널 화면. 데이터 소스(SSH, 로컬 ConPTY)와 무관한 공통 부분:
/// 테마/글꼴/구문 강조 적용, 복사·붙여넣기, 단축키, 버퍼 읽기(MCP 용).
/// </summary>
public sealed class XtermView : UserControl
{
    private static Task<CoreWebView2Environment>? _env;

    private readonly WebView2 _web = new();
    private readonly System.Text.StringBuilder _pending = new();   // 화면 준비 전 출력 (숨은 탭)
    private bool _closed;

    public uint Cols { get; private set; } = 120;
    public uint Rows { get; private set; } = 30;
    public bool IsReady { get; private set; }

    /// <summary>터미널 페이지가 처음 준비됨 (Cols/Rows 확정)</summary>
    public event Action? Ready;
    /// <summary>사용자 키 입력 (UTF-16 문자열)</summary>
    public event Action<string>? Input;
    public event Action<uint, uint>? Resized;
    /// <summary>셸이 OSC 7 로 알려준 현재 폴더 (원격 경로)</summary>
    public event Action<string>? CwdChanged;

    /// <summary>"file://host/home/a%20b" → "/home/a b". 형식이 아니면 null</summary>
    public static string? ParseOsc7(string data)
    {
        if (!data.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return null;
        var rest = data[7..];
        var slash = rest.IndexOf('/');
        if (slash < 0) return null;
        try { return Uri.UnescapeDataString(rest[slash..]); }
        catch { return null; }
    }

    /// <summary>
    /// 프롬프트에서 현재 폴더 읽기 (OSC 7 이 없을 때): "user@host:~/app$ " / "root@web:/etc# " 형식.
    /// ~ 는 home 으로 바꾼다. 경로를 알 수 없는 프롬프트(예: "[user@host app]$")면 null.
    /// </summary>
    public static string? PromptPath(string screen, string home)
    {
        var lines = screen.Replace("\r", "").Split('\n');
        for (var i = lines.Length - 1; i >= 0 && i >= lines.Length - 5; i--)
        {
            var line = lines[i].TrimEnd();
            if (line.Length == 0) continue;
            var m = System.Text.RegularExpressions.Regex.Match(line, @"^(?:\([^)]*\)\s*)?[^@\s]+@[^:\s]+:(?<p>~[^\s]*|/[^\s]*)\s*[$#]$");
            if (!m.Success) return null;
            var p = m.Groups["p"].Value;
            if (p == "~") return home;
            if (p.StartsWith("~/")) return home.TrimEnd('/') + p[1..];
            return p;
        }
        return null;
    }

    /// <summary>터미널에 포커스가 있어도 앱이 처리해야 하는 단축키 (nexttab, prevtab, claude, localterm)</summary>
    public static event Action<string>? Hotkey;

    public XtermView()
    {
        var bg = TerminalThemes.Get(SettingsStore.Current.TerminalTheme).BackgroundColor;
        Background = new SolidColorBrush(bg);
        _web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(bg.R, bg.G, bg.B);
        Content = _web;
        Loaded += async (_, _) => await InitWebAsync();
        // 안 보이는 탭은 브라우저 엔진 메모리 사용을 낮춘다
        IsVisibleChanged += (_, _) =>
        {
            try
            {
                if (_web.CoreWebView2 != null)
                    _web.CoreWebView2.MemoryUsageTargetLevel = IsVisible
                        ? CoreWebView2MemoryUsageTargetLevel.Normal
                        : CoreWebView2MemoryUsageTargetLevel.Low;
            }
            catch { }
        };
    }

    private async Task InitWebAsync()
    {
        if (_web.CoreWebView2 != null || _closed) return;
        _env ??= CoreWebView2Environment.CreateAsync(null, AppPaths.WebViewDir, new CoreWebView2EnvironmentOptions(
            // 터미널 화면은 같은 로컬 페이지라 렌더러 하나를 공유하고, 브라우저 부가 기능(동기화·번역·SmartScreen 등)은 끈다
            "--renderer-process-limit=1 --disable-background-networking --disable-component-update --disable-sync " +
            "--disable-features=msSmartScreenProtection,Translate,SpareRendererForSitePerProcess,AutofillServerCommunication,msEdgeTranslate" +
            // 글자 위주 화면이라 GPU 없이도 충분 — 기본은 꺼서 GPU 프로세스 메모리 절약 (Settings 에서 켤 수 있음)
            (SettingsStore.Current.TerminalGpu ? "" : " --disable-gpu --disable-gpu-compositing")));
        try
        {
            await _web.EnsureCoreWebView2Async(await _env);
        }
        catch (Exception ex)
        {
            // 실패한 환경을 붙들고 있으면 앱을 다시 켤 때까지 모든 터미널이 안 열리므로 다음 시도에서 새로 만든다
            _env = null;
            Content = new TextBlock
            {
                Text = L.F("터미널 화면(WebView2)을 초기화하지 못했습니다.\n{0}\n\nMicrosoft Edge WebView2 런타임 설치 여부를 확인한 뒤 탭을 다시 여세요.", ex.Message),
                Foreground = (Brush)Application.Current.Resources["SubText"],
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(24),
            };
            return;
        }
        if (_closed) return;

        var core = _web.CoreWebView2!;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.SetVirtualHostNameToFolderMapping("app.local",
            Path.Combine(AppContext.BaseDirectory, "wwwroot"), CoreWebView2HostResourceAccessKind.DenyCors);
        core.WebMessageReceived += OnWebMessage;
        core.Navigate("https://app.local/terminal.html");
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var m = doc.RootElement;
        switch (m.GetProperty("t").GetString())
        {
            case "ready":
                Cols = m.GetProperty("cols").GetUInt32();
                Rows = m.GetProperty("rows").GetUInt32();
                if (!IsReady)
                {
                    IsReady = true;
                    ApplySettings();
                    SettingsStore.Changed += OnSettingsChanged;
                    if (_pending.Length > 0)
                    {
                        Post(new { t = "out", d = _pending.ToString() });
                        _pending.Clear();
                    }
                    Ready?.Invoke();
                }
                break;

            case "resize":
                Cols = m.GetProperty("cols").GetUInt32();
                Rows = m.GetProperty("rows").GetUInt32();
                Resized?.Invoke(Cols, Rows);
                break;

            case "in":
                Input?.Invoke(m.GetProperty("d").GetString() ?? "");
                break;

            case "copy":
                try { Clipboard.SetText(m.GetProperty("d").GetString() ?? ""); } catch { }
                break;

            case "paste":
                try
                {
                    if (!Clipboard.ContainsText()) break;
                    var text = Clipboard.GetText().Replace("\r\n", "\r").Replace('\n', '\r');
                    // 여러 줄은 줄마다 바로 실행될 수 있어 확인 (서버가 bracketed paste 를 켰으면 실행되지 않으므로 묻지 않음)
                    var bracketed = m.TryGetProperty("bp", out var bp) && bp.ValueKind == JsonValueKind.True;
                    if (!bracketed && SettingsStore.Current.ConfirmMultilinePaste && !ConfirmPaste(text)) { FocusTerminal(); break; }
                    Post(new { t = "paste", d = text });
                }
                catch { }
                break;

            case "hotkey":
                Hotkey?.Invoke(m.GetProperty("k").GetString() ?? "");
                break;

            case "cwd":
                if (ParseOsc7(m.GetProperty("d").GetString() ?? "") is { } cwd) CwdChanged?.Invoke(cwd);
                break;
        }
    }

    /// <summary>여러 줄(마지막 줄바꿈 포함) 붙여넣기 확인. 붙여넣어도 되면 true</summary>
    private bool ConfirmPaste(string text)
    {
        var lines = text.TrimEnd('\r').Split('\r');
        if (lines.Length <= 1 && !text.EndsWith('\r')) return true;
        var preview = string.Join("\n", lines.Take(12)) + (lines.Length > 12 ? "\n..." : "");
        return Dialog.Show(Window.GetWindow(this), L.F("{0}줄을 붙여넣을까요?\n줄마다 Enter 가 들어가 바로 실행될 수 있습니다.", lines.Length),
            L.T("여러 줄 붙여넣기"), MessageBoxButton.YesNo, MessageBoxImage.Warning, preview) == MessageBoxResult.Yes;
    }

    private void OnSettingsChanged() => Dispatcher.BeginInvoke(ApplySettings);

    private void ApplySettings()
    {
        var s = SettingsStore.Current;
        var theme = TerminalThemes.Get(s.TerminalTheme);
        var c = theme.BackgroundColor;
        Background = new SolidColorBrush(c);
        _web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(c.R, c.G, c.B);
        Post(new
        {
            t = "settings", theme = theme.ToXterm(), fontSize = s.FontSize, fontFamily = s.FontFamily, autoContrast = s.AutoContrast,
            hlIp = s.HighlightIp, hlNum = s.HighlightNumbers, hlKeywords = s.HighlightKeywords, smartCopy = s.SmartCopyPaste,
        });
    }

    private void Post(object msg)
    {
        if (_closed || _web.CoreWebView2 == null) return;
        _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(msg));
    }

    private readonly System.Text.StringBuilder _outBuf = new();
    private readonly object _outLock = new();
    private bool _flushQueued;

    /// <summary>
    /// 터미널에 출력 (어느 스레드에서 불러도 됨).
    /// 출력이 쏟아질 때 조각마다 화면으로 보내지 않도록, UI 스레드가 처리할 때까지 들어온 출력을 묶어서 한 번에 보낸다.
    /// </summary>
    public void Write(string text)
    {
        if (text.Length == 0) return;
        lock (_outLock)
        {
            _outBuf.Append(text);
            if (_flushQueued) return;
            _flushQueued = true;
        }
        Dispatcher.BeginInvoke(FlushOutput, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void FlushOutput()
    {
        string text;
        lock (_outLock)
        {
            text = _outBuf.ToString();
            _outBuf.Clear();
            _flushQueued = false;
        }
        if (text.Length == 0) return;
        if (!IsReady)
        {
            // 탭이 아직 한 번도 보이지 않아 터미널이 준비 전 — 모아 뒀다가 준비되면 출력 (최대 2MB)
            _pending.Append(text);
            if (_pending.Length > 2_000_000) _pending.Remove(0, _pending.Length - 2_000_000);
            return;
        }
        Post(new { t = "out", d = text });
    }

    /// <summary>안내 문구 한 줄 (ANSI 색 코드: 90 회색, 31 빨강, 33 노랑)</summary>
    public void Info(string text, string color = "90") => Write($"\x1b[{color}m{text}\x1b[0m\r\n");

    public void FocusTerminal()
    {
        _web.Focus();
        Post(new { t = "focus" });
    }

    /// <summary>현재 화면 버퍼의 마지막 N 줄 (UI 스레드에서 호출)</summary>
    public async Task<string> ReadBufferAsync(int lines)
    {
        if (_closed) return "";
        if (!IsReady || _web.CoreWebView2 == null)
        {
            // 화면 준비 전: 모아 둔 출력에서 ANSI 제어문자를 빼고 마지막 N 줄
            var plain = System.Text.RegularExpressions.Regex.Replace(_pending.ToString(), @"\x1b\[[0-9;?]*[ -/]*[@-~]|\x1b\][^\x07]*\x07|\r", "");
            var all = plain.Split('\n');
            return string.Join("\n", all[Math.Max(0, all.Length - lines)..]).TrimEnd();
        }
        var json = await _web.CoreWebView2.ExecuteScriptAsync($"readBuffer({Math.Clamp(lines, 1, 5000)})");
        return JsonSerializer.Deserialize<string>(json) ?? "";
    }

    /// <summary>터미널에서 드래그로 선택한 텍스트 (UI 스레드에서 호출)</summary>
    public async Task<string> GetSelectionAsync()
    {
        if (_closed || _web.CoreWebView2 == null) return "";
        var json = await _web.CoreWebView2.ExecuteScriptAsync("term.getSelection()");
        return JsonSerializer.Deserialize<string>(json) ?? "";
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        SettingsStore.Changed -= OnSettingsChanged;
        _web.Dispose();
    }
}
