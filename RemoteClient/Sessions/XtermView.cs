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
    // 읽기 스레드(Write)에서도 읽는다
    private volatile bool _closed;
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
    /// <summary>셸이 OSC 7 로 알려준 현재 폴더 (원격 경로, 호스트 — "file:///path" 처럼 호스트가 없으면 "")</summary>
    public event Action<string, string>? CwdChanged;
    /// <summary>WatchPrompt 이후 출력이 그려질 때마다: Enter 친 줄부터 커서 줄까지의 화면</summary>
    public event Action<string>? PromptScreen;

    /// <summary>지금 커서 줄(Enter 친 명령 줄)부터 출력을 지켜보며 PromptScreen 을 올린다 (ms 동안)</summary>
    public void WatchPrompt(int ms = 10000) => Post(new { t = "watch", ms });
    public void StopWatchPrompt() => Post(new { t = "unwatch" });

    /// <summary>"file://host/home/a%20b" → "/home/a b". 형식이 아니면 null</summary>
    public static string? ParseOsc7(string data) => ParseOsc7(data, out _);

    /// <summary>ParseOsc7 + 호스트 부분 ("file://web01/home" → "web01", "file:///home" → "")</summary>
    public static string? ParseOsc7(string data, out string host)
    {
        host = "";
        if (!data.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return null;
        var rest = data[7..];
        var slash = rest.IndexOf('/');
        if (slash < 0) return null;
        try
        {
            var path = Uri.UnescapeDataString(rest[slash..]);
            host = rest[..slash];
            return path;
        }
        catch { return null; }
    }

    /// <summary>
    /// 안내 문구(서버 배너·오류 메시지 포함)를 터미널에 그대로 쓰기 전에 제어 문자를 뺀다 — 서버가 보낸 배너의
    /// ESC 시퀀스가 화면·제목·링크를 바꾸지 못하게 (OpenSSH 도 배너를 걸러 보여 준다). 줄바꿈은 CRLF 로 맞춘다
    /// </summary>
    public static string SanitizeInfo(string text) =>
        SanitizePaste(text, out _).Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");

    /// <summary>
    /// 화면 준비 전 모아 둔 출력이 한도를 넘으면 앞부분을 버릴 위치. 줄 경계(다음 \n 뒤)에서 자른다 —
    /// ESC 시퀀스·서로게이트 쌍 중간에서 잘라 첫 줄이 깨지지 않게. 한도 근처에 줄바꿈이 없으면 서로게이트만 피한다
    /// </summary>
    public static int PendingCut(string text, int max)
    {
        if (text.Length <= max) return 0;
        var cut = text.Length - max;
        var nl = text.IndexOf('\n', cut, Math.Min(4096, text.Length - cut));
        if (nl >= 0) return nl + 1;
        if (cut < text.Length && char.IsLowSurrogate(text[cut])) cut++;
        return cut;
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
            if (_closed) return;   // 닫힌 뒤(탭을 목록에서 뺄 때) — Dispose 된 WebView2 를 건드리지 않는다
            // 숨겨지면 멈춰 둔 출력을 그대로 보내고 읽기 스레드를 풀어 준다 (흐름 제어는 보이는 동안만)
            _visible = IsVisible;
            ResetThrottle();
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
        // _closed 먼저 — Dispose 된 WebView2 의 CoreWebView2 속성은 예외를 낸다
        // 초기화 중에 Loaded 가 또 오면(탭이 트리에서 빠졌다 다시 들어옴) 처리기를 두 번 달지 않게 — 두 번 달면 키 입력이 두 번 간다
        if (_closed || _initializing || _web.CoreWebView2 != null) return;
        _initializing = true;
        try { await InitWebCoreAsync(); }
        finally { _initializing = false; }
    }

    private bool _initializing;

    private async Task InitWebCoreAsync()
    {
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
        catch (Exception) when (_closed)
        {
            // 초기화 중에 탭이 닫혀 WebView2 가 Dispose 됨 — 환경은 멀쩡하므로 버리지 않는다
            // (버리면 다음 탭이 새 환경·브라우저 프로세스를 또 만든다)
            return;
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
                _room.Set();   // 더 그려지지 않는다 — 출력을 기다리던 읽기 스레드를 풀어 준다
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
            _room.Set();
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
            _room.Set();
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
        // 앱 페이지가 아닌 곳(혹시 열린 다른 문서)에서 온 메시지는 무시 — 키 입력·붙여넣기로 이어질 수 있다.
        // 닫힌 뒤 늦게 온 메시지도 무시 — 특히 "ready" 가 SettingsStore.Changed 를 다시 구독하면 닫힌 탭이 정적 이벤트에 영영 붙잡힌다
        if (_closed || !IsAppPage(e.Source)) return;
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
                    // 새 페이지 — 옛 페이지에 보낸 출력의 그리기 응답은 오지 않는다. 이 탭의 글자 크기 조절도 되돌린다
                    ResetFlow();
                    ApplySettings(includeZoom: true);
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
                        var first = notice + _pending.ToString();
                        _pending.Clear();
                        lock (_outLock) { _unacked += first.Length; UpdateRoomLocked(); }
                        if (!PostOut(first)) lock (_outLock) { _unacked = Math.Max(0, _unacked - first.Length); UpdateRoomLocked(); }
                    }
                    // 다시 불러온 페이지에도 "다시 연결" 막대를 되살린다
                    if (_reconnectLabel != null) Post(new { t = "offline", label = _reconnectLabel });
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
            {
                // 확인 창(모달)을 WebView2 이벤트 처리 중에 띄우지 않도록 한 박자 늦춘다 (링크 열기와 같은 이유)
                var bracketed = m.TryGetProperty("bp", out var bp) && bp.ValueKind == JsonValueKind.True;
                Dispatcher.BeginInvoke(new Action(() => PasteFromClipboard(bracketed)));
                break;
            }

            case "ack":
                // 페이지가 출력 조각을 다 그렸다 (흐름 제어)
                if (m.TryGetProperty("n", out var an) && an.TryGetInt32(out var ackN) && ackN > 0) OnAck(ackN);
                break;

            case "reconnect":
                // 끊김 막대의 "다시 연결" 버튼
                // 다시 연결은 계정 입력 창(모달)을 띄울 수 있다 — WebView2 이벤트 처리 중이 아니라 한 박자 뒤에
                if (_reconnectLabel != null)
                    Dispatcher.BeginInvoke(new Action(() => { if (!_closed && _reconnectLabel != null) ReconnectRequested?.Invoke(); }));
                break;

            case "zoom":
                // 이 탭의 글자 크기 조절(Ctrl+휠 등) — 렌더러 복구로 페이지를 다시 불러와도 유지
                if (m.TryGetProperty("z", out var z) && z.TryGetInt32(out var zv)) _zoom = Math.Clamp(zv, MinZoom, MaxZoom);
                break;

            case "hotkey":
                Hotkey?.Invoke(m.GetProperty("k").GetString() ?? "");
                break;

            case "cwd":
                if (ParseOsc7(m.GetProperty("d").GetString() ?? "", out var osc7Host) is { } cwd) CwdChanged?.Invoke(cwd, osc7Host);
                break;

            case "screen":
                PromptScreen?.Invoke(m.GetProperty("d").GetString() ?? "");
                break;
        }
    }

    /// <summary>클립보드 붙여넣기 (UI 스레드). bracketed = 서버가 bracketed paste 를 켬 (줄마다 바로 실행되지 않음)</summary>
    private void PasteFromClipboard(bool bracketed)
    {
        if (_closed) return;
        try
        {
            if (!Clipboard.ContainsText()) return;
            // 제어 문자(ESC 등)는 빼고 붙여넣는다 — 남겨 두면 "\x1b[201~" 로 bracketed paste 를 빠져나가 실행될 수 있다
            var text = SanitizePaste(Clipboard.GetText(), out var stripped).Replace("\r\n", "\r").Replace('\n', '\r');
            if (text.Length == 0) { FocusTerminal(); return; }
            // 여러 줄은 줄마다 바로 실행될 수 있어 확인 (서버가 bracketed paste 를 켰으면 실행되지 않으므로 묻지 않음).
            // 제어 문자가 들어 있던 텍스트는 의도적으로 만든 것일 수 있어 설정·bracketed paste 와 관계없이 항상 확인
            var ask = stripped || (!bracketed && SettingsStore.Current.ConfirmMultilinePaste);
            if (ask && !ConfirmPaste(text, stripped)) { FocusTerminal(); return; }
            Post(new { t = "paste", d = text });   // 확인 창을 보는 사이 탭이 닫혔으면 Post 가 보내지 않는다
        }
        catch { }
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

    private void OnSettingsChanged() => Dispatcher.BeginInvoke(() => { if (!_closed) ApplySettings(); });

    /// <param name="includeZoom">
    /// 페이지를 새로 불러왔을 때만 기억해 둔 글자 크기 조절값을 보낸다 — 설정 변경 때마다 보내면 방금 Ctrl+휠로 바꾼 값(아직 'zoom' 메시지가
    /// 오기 전)을 옛 값으로 되돌릴 수 있다. 페이지는 살아 있는 동안 자기 값을 유지한다
    /// </param>
    private void ApplySettings(bool includeZoom = false)
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
            zoom = includeZoom ? _zoom : (int?)null,
            // 페이지 크롬(찾기 상자·끊김 카드·스크롤바)용 — 밝은 테마면 스크롤바를 어둡게, 문구는 앱 언어로 (없으면 페이지 기본값)
            light = theme.IsLight,
            ui = new
            {
                findPlaceholder = L.T("화면에서 찾기"),
                findPrev = L.T("이전 결과 (Shift+Enter)"),
                findNext = L.T("다음 결과 (Enter)"),
                findClose = L.T("닫기 (Esc)"),
                offline = L.T("세션이 종료되었습니다"),
                reconnectHint = L.T("Enter 키로도 할 수 있습니다"),
            },
        });
    }

    /// <summary>끊김 막대에 보일 버튼 문구 (보이는 중일 때만, 렌더러 복구 뒤 되살리기용)</summary>
    private string? _reconnectLabel;

    /// <summary>끊김 막대의 버튼을 눌렀다 (UI 스레드)</summary>
    public event Action? ReconnectRequested;

    /// <summary>터미널 아래쪽에 "다시 연결"(로컬은 "다시 시작") 버튼 막대를 보인다 (UI 스레드)</summary>
    public void ShowReconnect(string label)
    {
        _reconnectLabel = label;
        Post(new { t = "offline", label });
    }

    /// <summary>끊김 막대를 숨긴다 (UI 스레드)</summary>
    public void HideReconnect()
    {
        if (_reconnectLabel == null) return;
        _reconnectLabel = null;
        Post(new { t = "online" });
    }

    /// <summary>이 탭의 글자 크기 조절값 (페이지가 알려 줌, 설정 글꼴 크기에 더한다)</summary>
    private int _zoom;

    /// <summary>글자 크기 조절값 범위 (terminal.html 의 setZoom 과 같음)</summary>
    public const int MinZoom = -6, MaxZoom = 24;

    /// <summary>이 탭의 글자 크기 조절값 (0 = 설정 글꼴 크기 그대로)</summary>
    public int Zoom => _zoom;

    /// <summary>
    /// 글자 크기 조절 (Ctrl+= / Ctrl+- / Ctrl+0 과 같은 페이지 함수 setZoom 을 부른다 — MCP set_terminal_zoom). 범위로 자른 값을 돌려준다.
    /// 화면이 아직 준비되지 않았으면 기억해 두었다가 페이지를 불러올 때 적용된다 (ApplySettings includeZoom)
    /// </summary>
    public int SetZoom(int zoom)
    {
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        if (IsReady) Post(new { t = "setzoom", z = _zoom });
        return _zoom;
    }

    private void Post(object msg)
    {
        if (_closed || _web.CoreWebView2 == null) return;
        _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(msg));
    }

    private readonly System.Text.StringBuilder _outBuf = new();
    private readonly object _outLock = new();
    private bool _flushQueued;

    // ---------- 출력 흐름 제어 ----------
    // 화면(xterm.js)이 그리는 속도보다 출력이 빨리 오면(cat 큰 파일, yes 등) WebView 메시지가 쌓여 화면·앱이 멈추고 Ctrl+C 도 늦게 닿는다.
    // 페이지가 다 그린 조각마다 'ack'(글자 수)를 돌려주고, 아직 안 그려진 글자가 HighWater 를 넘으면 더 보내지 않고 _outBuf 에 모아 둔다.
    // _outBuf 는 HeldMax 를 넘으면 앞부분(이미 스크롤백 밖으로 밀려날 분량)을 버린다. 로컬 터미널은 WaitForRoom 으로 읽기 자체를 멈춘다.

    /// <summary>페이지에 보냈지만 아직 그려지지 않은 글자 수 (_outLock)</summary>
    private int _unacked;
    /// <summary>그리기를 기다리느라 보내기를 멈춘 상태 — ack 가 오면 이어서 보낸다 (_outLock)</summary>
    private bool _throttled;
    /// <summary>출력을 더 받아도 되는가 (로컬 터미널 읽기 스레드가 기다린다). 닫히면 항상 열어 둔다</summary>
    private readonly System.Threading.ManualResetEventSlim _room = new(true);

    /// <summary>화면이 보이는 중 (IsVisible 을 다른 스레드에서도 읽을 수 있게 둔 값)</summary>
    private volatile bool _visible;

    internal const int HighWater = 256 * 1024;
    internal const int LowWater = 64 * 1024;
    internal const int ChunkMax = 64 * 1024;
    internal const int HeldMax = 4_000_000;
    /// <summary>
    /// 숨은 탭에서 아직 안 그려진 글자 한도. 숨은 페이지는 브라우저가 타이머를 늦춰(오래 숨으면 1분에 한 번) 그리기가 거의 멈추는데,
    /// 한도 없이 보내면 렌더러의 xterm 쓰기 대기열이 50MB(xterm 이 버리는 한도)까지 탭마다 쌓인다. 넘으면 _outBuf 에 모은다(HeldMax 로 상한)
    /// </summary>
    internal const int HiddenHighWater = 2_000_000;

    /// <summary>지금 더 보내도 되는 글자 수 (_outLock). 보이는 탭은 HighWater, 숨은 탭은 HiddenHighWater 까지</summary>
    internal static int SendBudget(int unacked, bool visible) => Math.Max(0, (visible ? HighWater : HiddenHighWater) - unacked);

    /// <summary>
    /// 화면이 출력을 따라올 때까지 기다린다 (최대 ms). 받아도 되면 true. 백그라운드 읽기 스레드 전용 —
    /// 화면이 닫혔거나 준비 전(모아 두기)이면 바로 true.
    /// </summary>
    public bool WaitForRoom(int ms) => _room.Wait(ms);

    /// <summary>
    /// 문자열 앞에서 최대 max 글자를 떼어 낼 길이. 서로게이트 쌍 가운데서 자르지 않는다
    /// (ESC 시퀀스는 xterm.js 가 이어지는 조각과 이어 해석하므로 중간에서 잘려도 된다)
    /// </summary>
    public static int ChunkLength(System.Text.StringBuilder sb, int max)
    {
        if (sb.Length <= max) return sb.Length;
        var n = max;
        if (n > 0 && char.IsHighSurrogate(sb[n - 1])) n--;
        return n;
    }

    /// <summary>받아도 되는지 다시 계산 (_outLock 안에서). 이력(hysteresis)을 둬 자주 열고 닫히지 않게</summary>
    private void UpdateRoomLocked()
    {
        var backlog = (long)_outBuf.Length + _unacked;
        if (_closed || _failed || !_visible || backlog < HighWater) _room.Set();
        else if (backlog > HighWater * 2L) _room.Reset();
    }

    /// <summary>
    /// 터미널에 출력 (어느 스레드에서 불러도 됨).
    /// 출력이 쏟아질 때 조각마다 화면으로 보내지 않도록, UI 스레드가 처리할 때까지 들어온 출력을 묶어서 한 번에 보낸다.
    /// </summary>
    public void Write(string text)
    {
        if (text.Length == 0 || _closed) return;
        lock (_outLock)
        {
            _outBuf.Append(text);
            if (_outBuf.Length > HeldMax)
            {
                // 화면이 한참 뒤처짐 — 앞부분은 어차피 스크롤백(1만 줄) 밖으로 밀려날 분량이라 버린다 (메모리 상한)
                var cut = PendingCut(_outBuf.ToString(), HeldMax / 4 * 3);
                _outBuf.Remove(0, cut).Insert(0, "\x1b[0m");
            }
            UpdateRoomLocked();
            if (_flushQueued || _throttled) return;   // 멈춘 동안은 ack 가 이어서 보낸다
            _flushQueued = true;
        }
        Dispatcher.BeginInvoke(FlushOutput, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void FlushOutput()
    {
        if (_closed)
        {
            // 닫힌 뒤 늦게 온 flush — 모아 둔 출력은 버린다 (닫힌 탭이 잠시라도 MB 단위 버퍼를 들고 있지 않게)
            lock (_outLock) { _outBuf.Clear(); _flushQueued = false; }
            return;
        }
        if (!IsReady)
        {
            string text;
            lock (_outLock)
            {
                text = _outBuf.ToString();
                _outBuf.Clear();
                _flushQueued = false;
                UpdateRoomLocked();
            }
            if (text.Length == 0) return;
            // 탭이 아직 한 번도 보이지 않아 터미널이 준비 전 — 모아 뒀다가 준비되면 출력 (최대 2MB)
            _pending.Append(text);
            if (_pending.Length > PendingMax)
            {
                // 한도의 3/4 까지 줄여 출력이 계속 쏟아질 때 조각마다 2MB 를 복사하지 않게
                var cut = PendingCut(_pending.ToString(), PendingMax / 4 * 3);
                // 잘린 앞부분이 켜 둔 색·속성이 남지 않게 초기화부터
                _pending.Remove(0, cut).Insert(0, "\x1b[0m");
            }
            return;
        }
        // 화면이 따라오는 만큼만 보낸다. 보낼 분량은 한 번에 떼어 내고(큰 _outBuf 앞부분을 조각마다 Remove 하면
        // 남은 MB 를 매번 복사한다 — 출력이 쏟아질 때 CPU) 조각(ChunkMax)으로 나눠 보낸다
        while (true)
        {
            string text;
            lock (_outLock)
            {
                _flushQueued = false;
                if (_outBuf.Length == 0) { _throttled = false; UpdateRoomLocked(); return; }
                // 숨은 탭은 한도가 크다 — 숨은 페이지는 그리기 응답이 느려 보이는 탭 기준이면 로컬 탭의 빌드 출력 등이 숨어 있는 동안 막힌다
                var budget = SendBudget(_unacked, _visible);
                if (budget == 0) { _throttled = true; UpdateRoomLocked(); return; }
                var n = ChunkLength(_outBuf, Math.Max(budget, ChunkMax));
                text = _outBuf.ToString(0, n);
                _outBuf.Remove(0, n);
                _unacked += n;
                UpdateRoomLocked();
            }
            for (var pos = 0; pos < text.Length;)
            {
                var n = SliceLength(text, pos, ChunkMax);
                if (!PostOut(pos == 0 && n == text.Length ? text : text.Substring(pos, n)))
                {
                    // 보내지 못함(닫힘 등) — 남은 분량은 그리기 응답이 오지 않으므로 세지 않는다
                    lock (_outLock) { _unacked = Math.Max(0, _unacked - (text.Length - pos)); UpdateRoomLocked(); }
                    return;
                }
                pos += n;
            }
        }
    }

    /// <summary>text 의 pos 부터 최대 max 글자를 떼어 낼 길이. 서로게이트 쌍 가운데서 자르지 않는다</summary>
    internal static int SliceLength(string text, int pos, int max)
    {
        var n = Math.Min(max, text.Length - pos);
        if (n > 1 && pos + n < text.Length && char.IsHighSurrogate(text[pos + n - 1])) n--;
        return n;
    }

    /// <summary>페이지가 출력 조각을 다 그렸다 (UI 스레드)</summary>
    private void OnAck(int n)
    {
        bool resume;
        lock (_outLock)
        {
            _unacked = Math.Max(0, _unacked - n);
            resume = _throttled && _unacked < LowWater;
            if (resume) { _throttled = false; _flushQueued = true; }
            UpdateRoomLocked();
        }
        if (resume) FlushOutput();
    }

    /// <summary>페이지를 새로 불러옴(처음·렌더러 복구) — 그리기 응답을 기다리던 수는 버린다</summary>
    private void ResetFlow() => ResetThrottle(clearUnacked: true);

    /// <summary>멈춰 둔 출력을 다시 보내기 시작한다 (보임 상태 변경·페이지 새로 불러옴)</summary>
    private void ResetThrottle(bool clearUnacked = false)
    {
        bool flush;
        lock (_outLock)
        {
            if (clearUnacked) _unacked = 0;
            flush = _throttled || (_outBuf.Length > 0 && !_flushQueued);
            _throttled = false;
            if (flush) _flushQueued = true;
            UpdateRoomLocked();
        }
        if (flush) Dispatcher.BeginInvoke(FlushOutput, System.Windows.Threading.DispatcherPriority.Background);
    }

    private bool PostOut(string text)
    {
        if (_closed || _web.CoreWebView2 == null) return false;
        try
        {
            _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { t = "out", d = text }));
            return true;
        }
        catch { return false; }
    }

    private const int PendingMax = 2_000_000;

    /// <summary>안내 문구 한 줄 (ANSI 색 코드: 90 회색, 31 빨강, 33 노랑). 서버가 보낸 배너·오류 문구도 오므로 제어 문자는 뺀다</summary>
    public void Info(string text, string color = "90") => Write($"\x1b[{color}m{SanitizeInfo(text)}\x1b[0m\r\n");

    public void FocusTerminal()
    {
        if (_closed) return;
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
            var plain = System.Text.RegularExpressions.Regex.Replace(_pending.ToString(), @"\x1b\[[0-9;?<=>]*[ -/]*[@-~]|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)|\x1b[()][0-9A-Za-z]|\x1b[=>78]|\r", "");
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
        // 화면이 따라오길 기다리던 읽기 스레드를 풀어 준다 (닫힌 뒤 출력은 버려진다)
        _room.Set();
        SettingsStore.Changed -= OnSettingsChanged;
        _settingsHooked = true;   // 닫힌 뒤 "ready" 가 와도 다시 구독하지 않게 (OnWebMessage 도 막지만 한 번 더)
        // CoreWebView2(COM) 쪽 처리기를 먼저 뗀다 — Dispose 뒤에도 COM 객체(RCW)가 종료자 실행 전까지 위임을 들고 있으면
        // 그 위임이 이 화면(→ 세션 탭 전체)을 붙잡는다
        try
        {
            if (_web.CoreWebView2 is { } core)
            {
                core.WebMessageReceived -= OnWebMessage;
                core.ProcessFailed -= OnProcessFailed;
                core.NewWindowRequested -= OnNewWindowRequested;
                core.NavigationStarting -= OnNavigationStarting;
            }
        }
        catch { }
        HwndHostRelease.Detach(_web);   // 키보드 입력 사이트 등록을 풀고 트리에서 뺀 뒤 Dispose (안 하면 메인 창이 이 탭을 영구히 붙잡음)
        _web.Dispose();
        // 모아 둔 출력(최대 수 MB)과 이벤트 구독자(세션 탭의 람다)를 놓는다 — 누가 이 화면을 잠시 더 들고 있어도 큰 덩어리는 남지 않게
        lock (_outLock) { _outBuf.Clear(); _outBuf.Capacity = 0; _unacked = 0; }
        _pending.Clear();
        _pending.Capacity = 0;
        Ready = null;
        Input = null;
        Resized = null;
        CwdChanged = null;
        PromptScreen = null;
        ReconnectRequested = null;
    }
}
