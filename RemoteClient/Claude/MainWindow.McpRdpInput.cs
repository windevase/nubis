using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace Nubis;

// MCP 도구: RDP 화면 조작 (rdp_click · rdp_drag · rdp_scroll · rdp_key · rdp_type) — capture_screen 으로 본 화면에 마우스·키 입력을 보낸다.
// 보안 원칙:
// - 원격 서버에 영향을 주는 입력이므로 승인 + 감사 로그. 클릭·끌기·스크롤·키 조합은 탭마다 한 번 승인하면 RdpControlMinutes 분 동안 묻지 않고(모두 감사 로그),
//   글자 입력(rdp_type)은 매번 그 글자를 보여 주고 승인받는다. 글자 키 단독·붙여넣기·화상 키보드·AltGr 조합은 rdp_key 로 못 보내게 막는다(RdpKeys.ParseCombo).
//   다만 클릭·키 조합만으로도 원격에서 프로그램 실행·명령 기록 재실행·(클립보드 공유 시) 붙여넣기가 가능하므로 허용 승인 창에 그대로 알린다.
// - 허용은 그 탭의 그 연결에만 (다시 연결하면 새로 승인). 탭 메뉴에서 끝내면 진행 중인 입력도 멈춘다.
// - 한 탭의 입력은 한 번에 하나씩 (탭별 잠금) — 동시에 온 요청의 키가 섞여 Ctrl+글자 같은 다른 입력이 되지 않게.
// - 입력은 RDP 컨트롤의 입력 창에 직접 — 사용자의 실제 커서·키보드 포커스는 건드리지 않는다.
// - 좌표는 Claude 가 본 마지막 캡처 이미지 기준. 그 뒤 탭 크기가 바뀌었으면 다시 캡처하게 한다 (엉뚱한 곳을 누르지 않게).
public partial class MainWindow
{
    /// <summary>클릭·키 조합 허용 시간 (분)</summary>
    private const int RdpControlMinutes = 10;

    /// <summary>탭 하나의 화면 조작 허용: 만료 시각 · 허용한 연결 번호 · 끝낼 때 진행 중인 입력을 멈추는 토큰</summary>
    private sealed class RdpGrant(DateTime until, int generation)
    {
        public DateTime Until { get; } = until;
        public int Generation { get; } = generation;
        public CancellationTokenSource Stop { get; } = new();
    }

    // 탭별 허용 · 입력 잠금 — 약한 참조라 닫힌 탭은 함께 사라진다
    private readonly ConditionalWeakTable<SessionTab, RdpGrant> _rdpGrants = new();
    private readonly ConditionalWeakTable<SessionTab, SemaphoreSlim> _rdpInputLocks = new();

    private RdpGrant? ActiveGrant(SessionTab tab) =>
        _rdpGrants.TryGetValue(tab, out var g) && g.Until > DateTime.UtcNow && !g.Stop.IsCancellationRequested &&
        tab.View is RdpSessionView v && v.SessionGeneration == g.Generation ? g : null;

    /// <summary>이 탭에 Claude 의 화면 조작(클릭·키 조합)이 허용돼 있는가 (탭 메뉴에서 끝내기 표시용)</summary>
    internal bool HasRdpControl(SessionTab tab) => ActiveGrant(tab) != null;

    /// <summary>화면 조작 허용을 바로 끝낸다 (탭 우클릭 메뉴) — 진행 중인 입력도 멈춘다</summary>
    internal void RevokeRdpControl(SessionTab tab)
    {
        if (!_rdpGrants.TryGetValue(tab, out var g)) return;
        _rdpGrants.Remove(tab);
        try { g.Stop.Cancel(); } catch (ObjectDisposedException) { }
        AuditLog.Write("rdp_control", new[] { SessionAuditTarget(tab) }, "(화면 조작 허용 끝냄)", "사용자 해제");
        Toast.Show(L.F("{0} 화면의 Claude 조작 허용을 끝냈습니다.", tab.Title));
    }

    private static readonly object CaptureArgs = new { type = "boolean", description = "동작 뒤 화면을 다시 캡처해 함께 돌려준다 (기본 true). 여러 동작을 이어서 할 때 중간 것은 false 로 토큰 절약" };
    private static readonly object WaitArgs = new { type = "integer", description = "동작 뒤 캡처까지 기다릴 ms (기본 800, 0~10000) — 창이 뜨는 데 오래 걸리면 늘린다" };
    private static readonly object WidthArgs = new { type = "integer", description = "캡처 이미지 최대 가로 (기본: 마지막 캡처와 같은 크기 — 좌표 기준이 그대로 유지됨)" };
    private static readonly object ImageWidthArg = new { type = "integer", description = "선택: x·y 를 읽은 이미지의 가로 픽셀. 주면 앱이 기억하는 마지막 캡처와 다를 때 거부한다 (다른 이미지 기준 좌표로 엉뚱한 곳을 누르지 않게)" };

    private const string CoordNote = "x·y 는 이 탭의 마지막 capture_screen(또는 rdp_* 결과) 이미지의 픽셀 좌표. 그 뒤 탭 크기가 바뀌었으면 다시 캡처해야 한다. ";
    private const string GrantNote = "탭마다 처음 한 번 사용자 승인을 받으면 그 연결에서 10분 동안 클릭·끌기·스크롤·키 조합은 다시 묻지 않는다 (모두 감사 로그). ";

    private IEnumerable<McpTool> RdpInputTools()
    {
        yield return new McpTool("rdp_click",
            "RDP(원격 데스크톱) 탭 화면을 클릭한다. " + CoordNote + GrantNote +
            "사용자의 실제 마우스 커서는 움직이지 않는다. 결과는 동작 뒤 캡처로 확인. " +
            "PowerShell 을 열 때는 시작 메뉴를 찾아 누르기보다 rdp_key 'win+r' → rdp_type 'powershell' 이 빠르고 정확하다. " +
            "콘솔 창(PowerShell·cmd)에서 오른쪽 클릭은 붙여넣기이므로 쓰지 말 것.",
            Schema(new
            {
                session_id = new { type = "integer", description = "생략 시 현재 탭" },
                x = new { type = "number" },
                y = new { type = "number" },
                button = new { type = "string", @enum = new[] { "left", "right", "middle" }, description = "기본 left" },
                @double = new { type = "boolean", description = "더블 클릭" },
                image_width = ImageWidthArg, capture_after = CaptureArgs, wait_ms = WaitArgs, max_width = WidthArgs,
            }, "x", "y"),
            a => OnUiAsync(async () =>
            {
                var button = Arg(a, "button") is { Length: > 0 } b ? b.ToLowerInvariant() : "left";
                if (button is not ("left" or "right" or "middle")) throw new McpToolException("button 은 left / right / middle 중 하나입니다.");
                var count = BoolArg(a, "double") == true ? 2 : 1;
                var (x, y) = (RequireNum(a, "x"), RequireNum(a, "y"));
                var action = $"rdp_click ({x:0.#}, {y:0.#}) {button}{(count == 2 ? " double" : "")}";
                return await RdpActAsync(a, "rdp_click", action, grant: true, async (tab, ih, ct) =>
                {
                    var p = MapToInput(tab, ih, x, y, IntArg(a, "image_width"));
                    await RdpInputSender.ClickAsync(ih, p.Client.X, p.Client.Y, button, count, ct);
                });
            }));

        yield return new McpTool("rdp_drag",
            "RDP 탭 화면에서 왼쪽 버튼을 누른 채 (x, y) 에서 (to_x, to_y) 로 끈다 — 창 이동·크기 조절·선택 영역. " + CoordNote + GrantNote,
            Schema(new
            {
                session_id = new { type = "integer" },
                x = new { type = "number" }, y = new { type = "number" },
                to_x = new { type = "number" }, to_y = new { type = "number" },
                image_width = ImageWidthArg, capture_after = CaptureArgs, wait_ms = WaitArgs, max_width = WidthArgs,
            }, "x", "y", "to_x", "to_y"),
            a => OnUiAsync(async () =>
            {
                var (x, y, tx, ty) = (RequireNum(a, "x"), RequireNum(a, "y"), RequireNum(a, "to_x"), RequireNum(a, "to_y"));
                var action = $"rdp_drag ({x:0.#}, {y:0.#}) -> ({tx:0.#}, {ty:0.#})";
                return await RdpActAsync(a, "rdp_drag", action, grant: true, async (tab, ih, ct) =>
                {
                    var from = MapToInput(tab, ih, x, y, IntArg(a, "image_width"));
                    var to = MapToInput(tab, ih, tx, ty, IntArg(a, "image_width"));
                    await RdpInputSender.DragAsync(ih, from.Client, to.Client, ct);
                });
            }));

        yield return new McpTool("rdp_scroll",
            "RDP 탭 화면의 (x, y) 위치에서 마우스 휠을 굴린다. amount 양수 = 위로, 음수 = 아래로 (한 칸 = 보통 3줄, 최대 ±20). " + CoordNote + GrantNote,
            Schema(new
            {
                session_id = new { type = "integer" },
                x = new { type = "number" }, y = new { type = "number" },
                amount = new { type = "integer", description = "휠 칸 수 (양수 위, 음수 아래)" },
                image_width = ImageWidthArg, capture_after = CaptureArgs, wait_ms = WaitArgs, max_width = WidthArgs,
            }, "x", "y", "amount"),
            a => OnUiAsync(async () =>
            {
                var (x, y) = (RequireNum(a, "x"), RequireNum(a, "y"));
                var amount = IntArg(a, "amount") ?? 0;
                if (amount == 0 || Math.Abs(amount) > 20) throw new McpToolException("amount 는 -20~20 사이의 0 이 아닌 정수입니다.");
                var action = $"rdp_scroll ({x:0.#}, {y:0.#}) {amount:+0;-0}";
                return await RdpActAsync(a, "rdp_scroll", action, grant: true, async (tab, ih, ct) =>
                {
                    var p = MapToInput(tab, ih, x, y, IntArg(a, "image_width"));
                    await RdpInputSender.ScrollAsync(ih, p.Client, p.Screen, amount, ct);
                });
            }));

        yield return new McpTool("rdp_key",
            "RDP 탭에 키 조합을 차례로 누른다. keys 예: [\"win+r\"], [\"alt+f4\"], [\"ctrl+shift+esc\"], [\"enter\"], [\"tab\",\"tab\",\"enter\"], [\"hangul\"]. " +
            "사용 가능: 수정 키 ctrl·shift·alt·win + 글자 1개 / enter·tab·esc·backspace·delete·insert·home·end·pageup·pagedown·up·down·left·right·f1~f12·apps·capslock·printscreen·hangul·ralt. " +
            "글자·공백을 수정 키 없이(또는 shift 만으로) 누를 수는 없다 — 글자는 rdp_type. 붙여넣기(ctrl+v·shift+insert·win+v)·화상 키보드(ctrl+win+o)·ctrl+alt+글자(AltGr)는 막혀 있다. " +
            "입력은 원격에서 지금 포커스를 가진 창으로 간다. " + GrantNote +
            "win 키 조합이 원격에 전달되지 않으면(창 모드에서는 Win 키를 로컬이 가질 수 있음) 시작 단추를 rdp_click 으로 누를 것.",
            Schema(new
            {
                session_id = new { type = "integer" },
                keys = new { type = "array", items = new { type = "string" }, description = $"차례로 누를 키 조합 (최대 {RdpKeys.MaxCombos}개)" },
                capture_after = CaptureArgs, wait_ms = WaitArgs, max_width = WidthArgs,
            }, "keys"),
            a => OnUiAsync(async () =>
            {
                var keys = ListArg(a, "keys");
                if (keys.Count == 0) throw new McpToolException("keys 에 누를 키 조합을 넣으세요 (예: [\"win+r\"]).");
                if (keys.Count > RdpKeys.MaxCombos) throw new McpToolException($"한 번에 {RdpKeys.MaxCombos}개까지 누를 수 있습니다.");
                var events = new List<RdpKeyEvent>();
                try { foreach (var k in keys) events.AddRange(RdpKeys.ParseCombo(k)); }
                catch (ArgumentException ex) { throw new McpToolException(ex.Message); }
                var action = "rdp_key " + string.Join(", ", keys.Select(k => McpHelpers.VisibleControlChars(k.Trim().ToLowerInvariant())));
                return await RdpActAsync(a, "rdp_key", action, grant: true, (_, ih, ct) => RdpInputSender.KeysAsync(ih, events, ct));
            }));

        yield return new McpTool("rdp_type",
            "RDP 탭에서 지금 포커스를 가진 창(실행 창·PowerShell·메모장 등)에 글자를 입력한다. 입력할 때마다 앱이 그 글자를 보여 주고 사용자 승인을 받는다. " +
            "영문·숫자·기호·줄바꿈·탭만 가능(US 자판 스캔코드 — 한글 등은 거부). 원격 입력기가 한글 모드면 영문이 한글로 찍히니 캡처로 확인하고 rdp_key [\"hangul\"] 로 바꾼 뒤 다시 입력. " +
            "입력 전에 capture_screen 으로 올바른 창에 포커스가 있는지 확인할 것 (다른 창에 명령이 들어가지 않게). " +
            "명령은 press_enter=true 로 한 번에 보낼 것 (승인 창에 Enter 까지 보여 준다). " +
            "PowerShell 명령 출력은 화면에 보이는 만큼만 읽을 수 있으니 긴 출력은 Select-Object -First N, Format-Table -AutoSize 등으로 줄일 것.",
            Schema(new
            {
                session_id = new { type = "integer" },
                text = new { type = "string" },
                press_enter = new { type = "boolean", description = "끝에 Enter (기본 true)" },
                capture_after = CaptureArgs, wait_ms = WaitArgs, max_width = WidthArgs,
            }, "text"),
            a => OnUiAsync(async () =>
            {
                var text = Arg(a, "text");
                var enter = BoolArg(a, "press_enter") != false;
                if (text.Length == 0 && !enter) throw new McpToolException("입력할 text 가 비어 있습니다.");
                List<RdpKeyEvent> events;
                try { events = RdpKeys.TextToEvents(text); }
                catch (ArgumentException ex) { throw new McpToolException(ex.Message); }
                if (enter) events.AddRange(RdpKeys.EnterEvents());
                var shown = text + (enter ? "\n⏎ Enter" : "");
                var logged = "rdp_type " + McpHelpers.VisibleControlChars(text) + (enter ? " ⏎" : "");
                return await RdpActAsync(a, "rdp_type", logged, grant: false, (_, ih, ct) => RdpInputSender.KeysAsync(ih, events, ct),
                    approval: (L.T("Claude 가 원격 데스크톱에 글자 입력을 요청합니다"),
                        enter ? L.T("아래 RDP 화면에서 지금 포커스를 가진 창에 글자를 입력하고 Enter 를 누릅니다.")
                              : L.T("아래 RDP 화면에서 지금 포커스를 가진 창에 글자를 입력합니다."),
                        shown));
            }));
    }

    private static double RequireNum(JsonElement a, string name) =>
        NumArg(a, name) is { } v && double.IsFinite(v) ? v : throw new McpToolException($"{name} 좌표(숫자)가 필요합니다.");

    /// <summary>
    /// RDP 조작 공통: 탭 확인 → 탭별 잠금 → 승인(grant = 탭 단위 시간 허용, 아니면 approval 로 매번) → 탭 표시 → 입력 → 감사 로그 → (선택) 다시 캡처.
    /// action 은 감사 로그·결과 문구(언어 무관 표기). 인자 오류는 모두 승인 전에 던진다.
    /// </summary>
    private async Task<object> RdpActAsync(JsonElement a, string tool, string action, bool grant,
        Func<SessionTab, IntPtr, CancellationToken, Task> act, (string Title, string Description, string Shown)? approval = null)
    {
        var ct = McpRequest.Token;
        var captureAfter = BoolArg(a, "capture_after") != false;
        var waitMs = Math.Clamp(IntArg(a, "wait_ms") ?? 800, 0, 10000);
        var maxWidthArg = IntArg(a, "max_width");
        var tab = RequireSession(a, allowCurrent: true);
        CheckRdpTab(tab);
        var target = SessionAuditTarget(tab);

        // 한 탭의 입력은 한 번에 하나씩 — 승인 대기까지 포함해 잠근다 (첫 허용 승인 창이 두 번 뜨지 않고, 승인한 글자 사이에 다른 키가 끼지 않게)
        var gate = _rdpInputLocks.GetValue(tab, _ => new SemaphoreSlim(1, 1));
        try { await gate.WaitAsync(ct); }
        catch (OperationCanceledException) { throw new McpToolException("Claude 쪽에서 요청이 취소되어 실행하지 않았습니다."); }
        var approved = false;
        try
        {
            CancellationToken stop = default;
            if (grant) stop = (await EnsureRdpControlAsync(tab, tool, action)).Stop.Token;
            else if (approval is { } ap && !await AskApprovalAsync(tool, ap.Title, ap.Description, new[] { target }, ap.Shown))
            {
                AuditLog.Write(tool, new[] { target }, action, "거부");
                throw new McpToolException("사용자가 입력을 거부했습니다.");
            }
            approved = true;

            IntPtr ih;
            try
            {
                // 승인을 기다리는 동안 탭이 닫혔거나 끊겼을 수 있다
                if (!_tabs.Contains(tab)) throw new McpToolException("승인을 기다리는 동안 탭이 닫혀 입력하지 않았습니다.");
                CheckRdpTab(tab);
                // 조작하는 모습이 보이게 그 탭을 띄운다 (숨은 탭은 크기가 0 이라 좌표를 맞출 수 없음)
                if (!tab.IsSelected)
                {
                    ShowRdpTabForInput(tab);
                    await Task.Delay(400, ct);
                    if (!_tabs.Contains(tab)) throw new McpToolException("탭이 닫혀 입력하지 않았습니다.");
                }
                ih = ((RdpSessionView)tab.View).FindInputWindow();
                if (ih == IntPtr.Zero) throw new McpToolException("RDP 입력 창을 찾지 못했습니다 (연결 중이거나 끊김). list_sessions 로 상태를 확인하세요.");
            }
            catch (Exception ex) when (ex is McpToolException or OperationCanceledException)
            {
                AuditLog.Write(tool, new[] { target }, action, "승인했으나 실행 안 함" + (ex is OperationCanceledException ? "(요청 취소)" : ""));
                if (ex is OperationCanceledException) throw new McpToolException("요청이 취소되어 입력하지 않았습니다.");
                throw;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stop);
            try { await act(tab, ih, linked.Token); }
            catch (OperationCanceledException)
            {
                var why = stop.IsCancellationRequested ? "사용자가 화면 조작 허용을 끝내" : "요청이 취소되어";
                AuditLog.Write(tool, new[] { target }, action, $"중단({why}) — 일부 입력됐을 수 있음");
                throw new McpToolException($"{why} 입력을 중단했습니다 (일부는 이미 입력됐을 수 있음 — 화면을 캡처해 확인하고 같은 입력을 그대로 다시 보내지 말 것).");
            }
            catch (McpToolException ex)
            {
                // 좌표 오류 등 — 입력 전에 확인하므로 입력되지 않았다
                AuditLog.Write(tool, new[] { target }, action, "실행 안 함");
                throw new McpToolException(ex.Message);
            }
            catch (Exception ex)
            {
                AuditLog.Write(tool, new[] { target }, action, "실패 — 일부 입력됐을 수 있음");
                throw new McpToolException($"입력 중 오류: {ex.Message} (일부는 이미 입력됐을 수 있음 — 화면을 캡처해 확인할 것).");
            }
            AuditLog.Write(tool, new[] { target }, action, grant ? "실행(화면 조작 허용 중)" : "승인·입력");
        }
        finally
        {
            gate.Release();
            _ = approved;
        }

        var done = $"{action} 완료.";
        if (!captureAfter) return done + " 결과는 capture_screen 으로 확인하세요.";
        // 여기부터는 입력이 이미 끝났다 — 캡처가 어떤 이유로 실패해도 입력을 다시 보내지 않게 알린다
        try
        {
            if (waitMs > 0) await Task.Delay(waitMs, ct);
            if (!_tabs.Contains(tab)) return done + " 캡처 전에 탭이 닫혔습니다.";
            var width = maxWidthArg ?? (_captureGeometry.TryGetValue(tab, out var g) ? g.ImageW : 1600);
            var img = await CaptureSessionAsync(tab, width);
            return img with { Caption = done + " " + img.Caption };
        }
        catch (Exception ex)
        {
            return done + $" 화면은 다시 캡처하지 못했습니다 ({(ex is OperationCanceledException ? "요청 취소" : ex.Message)}) — capture_screen 으로 확인하세요 (입력은 이미 됨, 다시 보내지 말 것).";
        }
    }

    /// <summary>
    /// 입력하려고 RDP 탭을 띄운다 (키보드 포커스는 RDP 로 옮기지 않음). 사용자가 다른 탭에 입력하던 중이었으면
    /// 그 포커스가 숨은 탭에 남아 이어서 치는 글자가 보이지 않는 터미널로 가지 않게 Claude 패널(열려 있으면)이나 빈 곳으로 옮긴다
    /// </summary>
    private void ShowRdpTabForInput(SessionTab tab)
    {
        var previous = _tabs.FirstOrDefault(t => t.IsSelected);
        var hadFocus = previous?.View.IsKeyboardFocusWithin == true;
        ShowTabWithoutFocus(tab);
        if (!hadFocus) return;
        if (ClaudeColumn.Width.Value > 0 && _claudeView != null) _claudeView.FocusSession();
        else ReleaseHiddenSessionFocus();
    }

    private void CheckRdpTab(SessionTab tab)
    {
        if (tab.View is not RdpSessionView rdp) throw new McpToolException($"session {tab.Id} 은 RDP 탭이 아닙니다. SSH·로컬 터미널은 send_to_terminal 을 쓰세요.");
        if (tab.Status == SessionStatus.Connecting) throw new McpToolException($"session {tab.Id} 은 아직 연결 중입니다.");
        if (tab.Status != SessionStatus.Connected) throw new McpToolException($"session {tab.Id} 은 연결이 끊겨 있습니다. reconnect_session 으로 다시 연결하세요.");
        if (WindowState == WindowState.Minimized)
            throw new McpToolException("Nubis 창이 최소화되어 있어 화면을 조작할 수 없습니다. 창을 열어 달라고 사용자에게 요청하세요.");
        if (rdp.IsFullScreen)
            throw new McpToolException("RDP 탭이 전체 화면이라 조작할 수 없습니다. 전체 화면을 나와 달라고 사용자에게 요청하세요.");
    }

    /// <summary>탭 단위 화면 조작 허용 — 없거나 만료·다시 연결됐으면 승인을 받는다. 탭별 잠금 안에서 부른다</summary>
    private async Task<RdpGrant> EnsureRdpControlAsync(SessionTab tab, string tool, string action)
    {
        if (ActiveGrant(tab) is { } live) return live;
        var target = SessionAuditTarget(tab);
        var desc = L.F("승인하면 {0}분 동안 이 RDP 연결에서 Claude 의 마우스 클릭·끌기·스크롤·키 조합 입력을 다시 묻지 않습니다. 글자 입력은 매번 따로 승인을 받고, 모든 동작은 감사 로그에 남습니다. 탭 우클릭 메뉴에서 언제든 끝낼 수 있습니다.", RdpControlMinutes) +
                   "\n" + L.T("주의: 클릭·키 조합만으로도 원격에서 프로그램을 실행하거나 이전 명령을 다시 실행할 수 있고, 클립보드 공유가 켜져 있으면 이 PC 의 클립보드 내용이 원격에 붙여넣어질 수 있습니다.");
        if (!await AskApprovalAsync(tool, L.T("Claude 가 원격 데스크톱 화면 조작을 요청합니다"), desc, new[] { target }, action))
        {
            AuditLog.Write(tool, new[] { target }, action, "거부");
            throw new McpToolException("사용자가 화면 조작을 거부했습니다.");
        }
        if (!_tabs.Contains(tab) || tab.View is not RdpSessionView v)
        {
            AuditLog.Write(tool, new[] { target }, action, "승인했으나 실행 안 함(탭 닫힘)");
            throw new McpToolException("승인을 기다리는 동안 탭이 닫혀 입력하지 않았습니다.");
        }
        var grant = new RdpGrant(DateTime.UtcNow.AddMinutes(RdpControlMinutes), v.SessionGeneration);
        if (_rdpGrants.TryGetValue(tab, out var old)) { try { old.Stop.Cancel(); } catch (ObjectDisposedException) { } }
        _rdpGrants.AddOrUpdate(tab, grant);
        AuditLog.Write("rdp_control", new[] { target }, $"(화면 조작 허용 {RdpControlMinutes}분)", "승인");
        Toast.Show(L.F("{0} 화면을 Claude 가 {1}분 동안 조작할 수 있습니다. 탭 우클릭 메뉴에서 끝낼 수 있습니다.", tab.Title, RdpControlMinutes));
        return grant;
    }

    /// <summary>마지막 캡처 이미지 좌표 → RDP 입력 창 좌표 · 화면 좌표</summary>
    private ((int X, int Y) Client, (int X, int Y) Screen) MapToInput(SessionTab tab, IntPtr ih, double x, double y, int? imageWidth)
    {
        if (!_captureGeometry.TryGetValue(tab, out var g))
            throw new McpToolException($"session {tab.Id} 화면을 아직 캡처하지 않았습니다. capture_screen 으로 먼저 화면을 보고 그 이미지 좌표를 주세요.");
        if (imageWidth is { } iw && iw != g.ImageW)
            throw new McpToolException($"image_width {iw} 가 마지막 캡처 이미지 가로({g.ImageW})와 다릅니다. 가장 최근 캡처 이미지의 좌표를 주세요.");
        var view = tab.View;
        var dpi = VisualTreeHelper.GetDpi(this);
        var (curW, curH) = ((int)(view.ActualWidth * dpi.DpiScaleX), (int)(view.ActualHeight * dpi.DpiScaleY));
        if (x < 0 || y < 0 || x >= g.ImageW || y >= g.ImageH)
            throw new McpToolException($"좌표 ({x:0.#}, {y:0.#}) 가 마지막 캡처 이미지({g.ImageW}x{g.ImageH}) 밖입니다.");
        var px = RdpKeys.ImageToTabPixels(x, y, g.ImageW, g.ImageH, g.TabW, g.TabH, curW, curH)
                 ?? throw new McpToolException("마지막 캡처 뒤 탭 크기가 바뀌었습니다. capture_screen 으로 다시 캡처한 뒤 새 좌표를 주세요.");
        var screen = view.PointToScreen(new Point(px.X / dpi.DpiScaleX, px.Y / dpi.DpiScaleY));
        var (sx, sy) = ((int)Math.Round(screen.X), (int)Math.Round(screen.Y));
        var client = RdpInputSender.ScreenToInput(ih, sx, sy)
                     ?? throw new McpToolException($"좌표 ({x:0.#}, {y:0.#}) 는 원격 화면 영역 밖입니다. 다시 캡처해 확인하세요.");
        return (client, (sx, sy));
    }
}
