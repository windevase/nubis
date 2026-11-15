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

    /// <summary>터미널 페이지가 올라가는 가상 호스트 (wwwroot 폴더 매핑)</summary>
    private const string PageHost = "app.local";
    private const string PageUrl = "https://" + PageHost + "/terminal.html";
    /// <summary>렌더러가 이 시간 안에 이만큼 죽으면 다시 불러오기를 멈춘다 (무한 재시작 방지)</summary>
    private const int MaxRecoveries = 3;
    private static readonly TimeSpan RecoveryWindow = TimeSpan.FromMinutes(1);

    private readonly WebView2 _web = new();
    private readonly System.Text.StringBuilder _pending = new();   // 화면 준비 전 출력 (숨은 탭, 렌더러 복구 중)
    private readonly System.Collections.Generic.List<DateTime> _recoveries = new();
    private bool _closed;
    private bool _settingsHooked;
    private bool _recovering;   // 렌더러가 죽어 페이지를 다시 불러오는 중 (다음 "ready" 는 복구)
    private bool _failed;       // 복구를 포기함 — 안내 문구만 보임

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
    /// <summary>WatchPrompt 이후 출력이 그려질 때마다: Enter 친 줄부터 커서 줄까지의 화면</summary>
    public event Action<string>? PromptScreen;

    /// <summary>지금 커서 줄(Enter 친 명령 줄)부터 출력을 지켜보며 PromptScreen 을 올린다 (ms 동안)</summary>
    public void WatchPrompt(int ms = 10000) => Post(new { t = "watch", ms });
    public void StopWatchPrompt() => Post(new { t = "unwatch" });

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
            ShowNotice(L.F("터미널 화면(WebView2)을 초기화하지 못했습니다.\n{0}\n\nMicrosoft Edge WebView2 런타임 설치 여부를 확인한 뒤 탭을 다시 여세요.", ex.Message));
            return;
        }
        if (_closed) return;

        var core = _web.CoreWebView2!;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.SetVirtualHostNameToFolderMapping(PageHost,
            Path.Combine(AppContext.BaseDirectory, "wwwroot"), CoreWebView2HostResourceAccessKind.DenyCors);
        core.WebMessageReceived += OnWebMessage;
        core.ProcessFailed += OnProcessFailed;
        core.NewWindowRequested += OnNewWindowRequested;
        core.NavigationStarting += OnNavigationStarting;
        core.Navigate(PageUrl);
    }

    /// <summary>앱의 터미널 페이지(https://app.local/...)인지. 다른 출처의 메시지·이동은 받지 않는다</summary>
    public static bool IsAppPage(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.IsDefaultPort
        && string.Equals(u.Host, PageHost, StringComparison.OrdinalIgnoreCase);

    /// <summary>http/https 링크면 정규화한 주소, 아니면 null (javascript:, file: 등은 열지 않는다)</summary>
    public static string? WebLink(string? uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
            ? u.AbsoluteUri : null;

    /// <summary>
    /// 붙여넣을 텍스트에서 터미널 제어 문자를 뺀다: C0(탭·CR·LF 제외, ESC 포함)·DEL·C1(0x80-0x9F).
    /// ESC 가 빠지므로 "\x1b[201~" 로 bracketed paste 를 빠져나가 명령을 실행시킬 수 없다. 뺀 글자가 있으면 removed = true
    /// </summary>
    public static string SanitizePaste(string text, out bool removed)
    {
        removed = false;
        System.Text.StringBuilder? sb = null;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var control = (c < '\x20' && c != '\t' && c != '\r' && c != '\n') || (c >= '\x7F' && c <= '\x9F');
            if (control)
            {
                sb ??= new System.Text.StringBuilder(text.Length).Append(text, 0, i);
                removed = true;
            }
            else sb?.Append(c);
        }
        return sb?.ToString() ?? text;
    }

    /// <summary>터미널 대신 안내 문구를 보여 준다 (초기화 실패·복구 포기)</summary>
    private void ShowNotice(string text)
    {
        Content = new TextBlock
        {
            Text = text,
            Foreground = (Brush)Application.Current.Resources["SubText"],
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(24),
        };
    }

    /// <summary>
    /// 렌더러(모든 터미널 탭이 하나를 공유)가 죽거나 멈추면 페이지를 다시 불러온다.
    /// 그동안 출력은 화면 준비 전처럼 모아 두고, 다시 "ready" 가 오면 설정을 재적용해 이어서 그린다.
    /// </summary>
    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        if (_closed || _failed) return;
        var now = DateTime.UtcNow;
        switch (e.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
                break;
            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                // 방금 다시 불러오기 시작했으면 반복되는 멈춤 알림은 넘긴다 (계속 멈춰 있으면 10초 뒤 다시 시도 → 한도에서 포기)
                if (_recovering && _recoveries.Count > 0 && now - _recoveries[^1] < TimeSpan.FromSeconds(10)) return;
                break;
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                // 브라우저 프로세스가 죽으면 이 WebView2 는 되살릴 수 없다. 다음에 여는 탭은 새 환경으로 만든다
                _failed = true;
                IsReady = false;
                _env = null;
                ShowNotice(L.T("터미널 화면(WebView2 브라우저 프로세스)이 종료되었습니다.\n탭을 닫고 다시 여세요."));
                return;
            default:
                return;   // GPU·유틸리티 프로세스 등은 WebView2 가 알아서 다시 띄운다
        }

        _recoveries.RemoveAll(t => now - t > RecoveryWindow);
        if (_recoveries.Count >= MaxRecoveries)
        {
            _failed = true;
            IsReady = false;
            ShowNotice(L.T("터미널 화면이 계속 비정상 종료되어 다시 불러오기를 멈췄습니다.\n탭을 닫고 다시 여세요."));
            return;
        }
        _recoveries.Add(now);
        IsReady = false;
        _recovering = true;
        try { _web.CoreWebView2?.Navigate(PageUrl); }
        catch
        {
            _failed = true;
            ShowNotice(L.T("터미널 화면이 비정상 종료되어 다시 불러오지 못했습니다.\n탭을 닫고 다시 여세요."));
        }
    }

    /// <summary>앱 페이지 밖으로의 이동은 막는다 (파일 끌어 놓기 등으로 다른 페이지가 열리지 않게)</summary>
    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!IsAppPage(e.Uri)) e.Cancel = true;
    }

    /// <summary>새 창(OSC 8 링크 등)은 앱 안 팝업으로 열지 않고, http/https 만 확인 후 기본 브라우저로 연다</summary>
    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        var uri = e.Uri;
        // WebView2 이벤트 처리 중에 모달 창을 띄우지 않도록 한 박자 늦춘다
        Dispatcher.BeginInvoke(new Action(() => OpenLink(uri)));
    }

    private void OpenLink(string? uri)
    {
        if (_closed || WebLink(uri) is not { } link) return;
        var owner = Window.GetWindow(this);
        if (Dialog.Show(owner, L.T("터미널의 링크를 기본 브라우저로 열까요?\n아래 주소가 맞는지 확인하세요."), L.T("링크 열기"),
                MessageBoxButton.YesNo, MessageBoxImage.Question, link) != MessageBoxResult.Yes)
        {
            FocusTerminal();
            return;
        }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(link) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            Dialog.Show(owner, L.F("링크를 열지 못했습니다.\n{0}", ex.Message), L.T("링크 열기"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // 앱 페이지가 아닌 곳(혹시 열린 다른 문서)에서 온 메시지는 무시 — 키 입력·붙여넣기로 이어질 수 있다
        if (!IsAppPage(e.Source)) return;
        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var m = doc.RootElement;
        switch (m.GetProperty("t").GetString())
        {
            case "ready":
                Cols = m.GetProperty("cols").GetUInt32();
                Rows = m.GetProperty("rows").GetUInt32();
                if (!IsReady && !_failed)
                {
                    IsReady = true;
                    ApplySettings();
                    if (!_settingsHooked)
                    {
                        _settingsHooked = true;
                        SettingsStore.Changed += OnSettingsChanged;
                    }
                    var recovered = _recovering;
                    _recovering = false;
                    var notice = recovered
                        ? "\x1b[33m" + L.T("터미널 화면이 비정상 종료되어 다시 불러왔습니다. 이전 화면 내용은 지워졌습니다.") + "\x1b[0m\r\n"
                        : "";
                    if (_pending.Length > 0 || notice.Length > 0)
                    {
                        Post(new { t = "out", d = notice + _pending.ToString() });
                        _pending.Clear();
                    }
                    // 복구면 세션을 다시 시작하지 않고(Ready 는 처음 한 번만) 새 화면 크기만 알린다
                    if (recovered) Resized?.Invoke(Cols, Rows);
                    else Ready?.Invoke();
                }
                break;

            case "link":
            {
                var uri = m.TryGetProperty("d", out var d) ? d.GetString() : null;
                Dispatcher.BeginInvoke(new Action(() => OpenLink(uri)));
                break;
            }

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
                    // 제어 문자(ESC 등)는 빼고 붙여넣는다 — 남겨 두면 "\x1b[201~" 로 bracketed paste 를 빠져나가 실행될 수 있다
                    var text = SanitizePaste(Clipboard.GetText(), out var stripped).Replace("\r\n", "\r").Replace('\n', '\r');
                    if (text.Length == 0) { FocusTerminal(); break; }
                    // 여러 줄은 줄마다 바로 실행될 수 있어 확인 (서버가 bracketed paste 를 켰으면 실행되지 않으므로 묻지 않음).
                    // 제어 문자가 들어 있던 텍스트는 의도적으로 만든 것일 수 있어 설정·bracketed paste 와 관계없이 항상 확인
                    var bracketed = m.TryGetProperty("bp", out var bp) && bp.ValueKind == JsonValueKind.True;
                    var ask = stripped || (!bracketed && SettingsStore.Current.ConfirmMultilinePaste);
                    if (ask && !ConfirmPaste(text, stripped)) { FocusTerminal(); break; }
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

            case "screen":
                PromptScreen?.Invoke(m.GetProperty("d").GetString() ?? "");
                break;
        }
    }

    /// <summary>
    /// 여러 줄(마지막 줄바꿈 포함) 붙여넣기 확인. strippedControl 이면(제어 문자를 뺀 텍스트) 한 줄이어도 묻는다.
    /// 붙여넣어도 되면 true
    /// </summary>
    private bool ConfirmPaste(string text, bool strippedControl = false)
    {
        var lines = text.TrimEnd('\r').Split('\r');
        var multi = lines.Length > 1 || text.EndsWith('\r');
        if (!multi && !strippedControl) return true;
        var preview = string.Join("\n", lines.Take(12)) + (lines.Length > 12 ? "\n..." : "");
        var message = multi ? L.F("{0}줄을 붙여넣을까요?\n줄마다 Enter 가 들어가 바로 실행될 수 있습니다.", lines.Length) : "";
        var title = L.T("여러 줄 붙여넣기");
        if (strippedControl)
        {
            message = L.T("붙여넣을 텍스트에 터미널 제어 문자(ESC 등)가 들어 있어 제거했습니다.\n제거한 내용으로 붙여넣을까요?")
                + (multi ? "\n\n" + message : "");
            title = L.T("제어 문자가 포함된 붙여넣기");
        }
        return Dialog.Show(Window.GetWindow(this), message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, preview) == MessageBoxResult.Yes;
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
            hlIp = s.HighlightIp, hlNum = s.HighlightNumbers, hlKeywords = s.HighlightKeywords, smartCopy = s.SmartCopyPaste, copyOnSelect = s.CopyOnSelect,
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
