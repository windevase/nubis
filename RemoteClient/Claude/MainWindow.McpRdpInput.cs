using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Nubis;

// MCP 도구: RDP 화면 조작 (rdp_click · rdp_drag · rdp_scroll · rdp_key · rdp_type) — capture_screen 으로 본 화면에 마우스·키 입력을 보낸다.
// 보안 원칙:
// - 원격 서버에 영향을 주는 입력이므로 승인 + 감사 로그. 클릭·끌기·스크롤·키 조합은 탭마다 한 번 승인하면 RdpControlMinutes 분 동안 묻지 않고(모두 감사 로그),
//   글자 입력(rdp_type)은 매번 그 글자를 보여 주고 승인받는다. 글자 키 단독·붙여넣기·화상 키보드·AltGr 조합은 rdp_key 로 못 보내게 막는다(RdpKeys.ParseCombo).
//   다만 클릭·키 조합만으로도 원격에서 프로그램 실행·명령 기록 재실행·(클립보드 공유 시) 붙여넣기가 가능하므로 허용 승인 창에 그대로 알린다.
// - 허용은 그 탭의 그 연결에만 (다시 연결하면 새로 승인). 탭 메뉴에서 끝내면 진행 중인 입력도 멈춘다.
// - 한 탭의 입력은 한 번에 하나씩 (탭별 잠금) — 동시에 온 요청의 키가 섞여 Ctrl+글자 같은 다른 입력이 되지 않게.
// - 입력은 RDP 컨트롤로 (클릭은 입력 창 메시지, 키는 공식 SendKeys) — 실제 마우스 커서는 움직이지 않는다. 컨트롤은 포커스가 있어야 입력을 넘기므로
//   입력하는 동안만 포커스를 주고 사용자의 실제 키 입력을 막은 뒤(RdpKeyboardGuard), 끝나면 원래 포커스로 되돌린다.
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
        /// <summary>허용이 끝나 입력이 멈췄을 때 결과·감사 로그에 쓸 사유 (사용자 해제 / Claude 반납)</summary>
        public string? StopReason { get; set; }
    }

    // 탭별 허용 · 입력 잠금 — 약한 참조라 닫힌 탭은 함께 사라진다
    private readonly ConditionalWeakTable<SessionTab, RdpGrant> _rdpGrants = new();
    private readonly ConditionalWeakTable<SessionTab, SemaphoreSlim> _rdpInputLocks = new();
    // 지금 입력 중인 요청의 멈춤 토큰 (탭 메뉴 "Claude 입력 멈추기"·허용 끝내기에서 취소)
    private readonly ConditionalWeakTable<SessionTab, CancellationTokenSource> _rdpActiveInput = new();

    /// <summary>이 탭에 Claude 가 지금 입력하는 중인가 (탭 메뉴에 "Claude 입력 멈추기" 표시용)</summary>
    internal bool IsRdpInputRunning(SessionTab tab) => _rdpActiveInput.TryGetValue(tab, out _);

    // 탭별 사용자 멈춤 상태 — 멈추기·Esc 마다 번호를 올리고 "확인 필요" 를 켜고, 확인용 stop_id 를 새로 뽑는다 (Esc 는 스레드 풀에서 오므로 lock).
    // 번호(Epoch): 요청이 들어올 때 기억했다가 잠금을 얻은 뒤 바뀌었으면 입력하지 않는다 (동시에 와 있던 요청).
    // 확인 필요: Claude Code 는 한 메시지의 도구를 하나씩 이어서 보내므로, 멈춘 뒤 도착한 다음 rdp_* 도 after_user_stop=<stop_id> 없이는
    // 받지 않는다 — 결과를 읽은 Claude 가 사용자에게 확인하고 붙여 보내야 이어진다. stop_id 는 무작위라 다음 멈춤의 값을 미리 맞힐 수 없다
    private sealed class RdpStopState
    {
        public readonly object Sync = new();
        public int Epoch; public bool Pending; public int StopId;
        public int ReadEpoch() { lock (Sync) return Epoch; }
        public int ReadStopId() { lock (Sync) return StopId; }
        public void Bump() { lock (Sync) { Epoch++; Pending = true; StopId = System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, int.MaxValue); } }
        /// <summary>입력해도 되는가 — 도착 뒤 멈춤이 없고, 확인 필요면 지금 stop_id 로 확인받았을 때. clear 면 통과와 함께 확인 필요를 끈다 (한 번에)</summary>
        public bool Pass(int arrivedEpoch, int? ackId, bool clear)
        {
            lock (Sync)
            {
                if (Epoch != arrivedEpoch) return false;
                if (Pending && ackId != StopId) return false;
                if (clear) Pending = false;
                return true;
            }
        }
    }
    private readonly ConditionalWeakTable<SessionTab, RdpStopState> _rdpStopEpoch = new();
    private RdpStopState RdpStopEpoch(SessionTab tab) => _rdpStopEpoch.GetValue(tab, _ => new());
    private void BumpRdpStopEpoch(SessionTab tab) => RdpStopEpoch(tab).Bump();

    /// <summary>진행 중인 Claude 입력을 멈춘다 (누른 키·버튼은 떼고 끝남)</summary>
    internal void StopRdpInput(SessionTab tab)
    {
        if (!_rdpActiveInput.TryGetValue(tab, out var cts)) return;
        BumpRdpStopEpoch(tab);
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        AuditLog.Write("rdp_control", new[] { SessionAuditTarget(tab) }, "(진행 중인 입력 멈춤)", "사용자 중단");
    }

    private RdpGrant? ActiveGrant(SessionTab tab) =>
        _rdpGrants.TryGetValue(tab, out var g) && g.Until > DateTime.UtcNow && !g.Stop.IsCancellationRequested &&
        tab.View is RdpSessionView v && v.SessionGeneration == g.Generation ? g : null;

    /// <summary>이 탭에 Claude 의 화면 조작(클릭·키 조합)이 허용돼 있는가 (탭 메뉴에서 끝내기 표시용)</summary>
    internal bool HasRdpControl(SessionTab tab) => ActiveGrant(tab) != null;

    /// <summary>
    /// 화면 조작 허용을 바로 끝낸다 (탭 우클릭 메뉴, byClaude = MCP rdp_release_control 로 Claude 가 스스로 반납).
    /// 사용자가 끝내면 진행 중인 입력을 모두 멈추고, Claude 반납은 허용으로 하던 클릭·키 조합 입력만 멈춘다.
    /// 끝낼 허용이 있었으면 true
    /// </summary>
    internal bool RevokeRdpControl(SessionTab tab, bool byClaude = false)
    {
        // 사용자가 끝내면 진행 중인 입력(허용과 무관한 rdp_type 포함)도 멈춘다. Claude 반납은 허용으로 하던 입력만 멈춘다 (허용 토큰 취소)
        if (!byClaude && _rdpActiveInput.TryGetValue(tab, out var running)) { BumpRdpStopEpoch(tab); try { running.Cancel(); } catch (ObjectDisposedException) { } }
        if (!_rdpGrants.TryGetValue(tab, out var g)) return false;
        var wasActive = ActiveGrant(tab) != null;
        _rdpGrants.Remove(tab);
        if (byClaude) g.StopReason = "Claude 가 화면 조작 허용을 반납해";   // 사용자 해제는 입력 멈춤(userStop)이 먼저 잡혀 "사용자가 Claude 입력을 멈춰" 로 기록된다
        try { g.Stop.Cancel(); } catch (ObjectDisposedException) { }
        if (!wasActive)
        {
            // 이미 만료됐거나 다시 연결돼 쓸 수 없던 허용 — 기록만 지운다 (끝냈다는 알림은 띄우지 않음)
            AuditLog.Write(byClaude ? "rdp_release_control" : "rdp_control", new[] { SessionAuditTarget(tab) }, "(만료된 화면 조작 허용 기록 지움)", "정리");
            return true;
        }
        AuditLog.Write(byClaude ? "rdp_release_control" : "rdp_control", new[] { SessionAuditTarget(tab) }, "(화면 조작 허용 끝냄)", byClaude ? "Claude 반납" : "사용자 해제");
        Toast.Show(byClaude ? L.F("Claude 가 {0} 화면의 조작 허용을 반납했습니다.", tab.Title) : L.F("{0} 화면의 Claude 조작 허용을 끝냈습니다.", tab.Title));
        return true;
    }

    private static readonly object AfterUserStopArg = new { type = "integer", description = "사용자가 이 탭의 Claude 입력을 멈춘(Esc·멈추기) 뒤 이어서 입력할 때만, 멈춤 오류에 적힌 stop_id 를 그대로 넣는다 — 사용자에게 계속할지 확인하고 계속하라고 한 경우에만. 그 뒤 사용자가 다시 멈추면 번호가 바뀌어 예전 번호는 통하지 않는다" };
    private static readonly object CaptureArgs = new { type = "boolean", description = "동작 뒤 화면을 다시 캡처해 함께 돌려준다 (기본 true). 여러 동작을 이어서 할 때 중간 것은 false 로 토큰 절약" };
    private static readonly object WaitArgs = new { type = "integer", description = "동작 뒤 캡처까지 기다릴 ms (기본 800, 0~10000) — 창이 뜨는 데 오래 걸리면 늘린다" };
    private static readonly object WidthArgs = new { type = "integer", description = "캡처 이미지 최대 가로 (기본: 마지막 캡처와 같은 크기 — 좌표 기준이 그대로 유지됨)" };
    private static readonly object ImageWidthArg = new { type = "integer", description = "선택: x·y 를 읽은 이미지의 가로 픽셀. 주면 앱이 기억하는 마지막 캡처와 다를 때 거부한다 (다른 이미지 기준 좌표로 엉뚱한 곳을 누르지 않게)" };

    private const string CoordNote = "x·y 는 이 탭의 마지막 capture_screen(또는 rdp_* 결과) 이미지의 픽셀 좌표. 그 뒤 탭 크기가 바뀌었으면 다시 캡처해야 한다. ";
    private const string GrantNote = "탭마다 처음 한 번 사용자 승인을 받으면 그 연결에서 10분 동안 클릭·끌기·스크롤·키 조합은 다시 묻지 않는다 (모두 감사 로그). ";

    private IEnumerable<McpTool> RdpInputTools()
    {
        yield return new McpTool("rdp_release_control",
            "RDP 탭에서 받아 둔 화면 조작 허용(클릭·끌기·스크롤·키 조합을 다시 묻지 않는 10분)을 Claude 가 스스로 끝낸다 — " +
            "허용(10분)으로 하던 클릭·끌기·스크롤·키 조합 입력은 멈추지만, 따로 승인받은 글자 입력(rdp_type)은 멈추지 않는다. 화면 작업을 마쳤으면 반납해 둔다. 다시 조작하려면 사용자 승인을 다시 받는다.",
            Schema(new { session_id = new { type = "integer", description = "생략 시 현재 탭" } }),
            a => OnUi(() =>
            {
                var tab = RequireSession(a, allowCurrent: true);
                if (tab.View is not RdpSessionView) throw new McpToolException($"session {tab.Id} 은 RDP 탭이 아닙니다.");
                var active = HasRdpControl(tab);
                var released = RevokeRdpControl(tab, byClaude: true);
                if (!released)
                {
                    AuditLog.Write("rdp_release_control", new[] { SessionAuditTarget(tab) }, "(화면 조작 허용 반납)", "허용 없음");
                    return $"session {tab.Id} 에는 반납할 화면 조작 허용이 없습니다.";
                }
                return active
                    ? $"session {tab.Id} 의 화면 조작 허용을 반납했습니다. 다시 조작하려면 사용자 승인이 필요합니다."
                    : $"session {tab.Id} 의 (이미 만료됐거나 연결이 바뀐) 화면 조작 허용 기록을 지웠습니다.";
            }));

        yield return new McpTool("rdp_click",
            "RDP(원격 데스크톱) 탭 화면을 클릭한다. " + CoordNote + GrantNote +
            "사용자의 실제 마우스 커서는 움직이지 않는다 (입력하는 동안만 키보드 포커스가 RDP 로 가고 사용자 키 입력은 막힘). 결과는 동작 뒤 캡처로 확인. " +
            "PowerShell 을 열 때는 시작 메뉴를 찾아 누르기보다 rdp_key 'win+r' → rdp_type 'powershell' 이 빠르고 정확하다. " +
            "콘솔 창(PowerShell·cmd)에서 오른쪽 클릭은 붙여넣기이므로 쓰지 말 것.",
            Schema(new
            {
                session_id = new { type = "integer", description = "생략 시 현재 탭" },
                x = new { type = "number" },
                y = new { type = "number" },
                button = new { type = "string", @enum = new[] { "left", "right", "middle" }, description = "기본 left" },
                @double = new { type = "boolean", description = "더블 클릭" },
                image_width = ImageWidthArg, capture_after = CaptureArgs, wait_ms = WaitArgs, max_width = WidthArgs, after_user_stop = AfterUserStopArg,
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
                image_width = ImageWidthArg, capture_after = CaptureArgs, wait_ms = WaitArgs, max_width = WidthArgs, after_user_stop = AfterUserStopArg,
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
                image_width = ImageWidthArg, capture_after = CaptureArgs, wait_ms = WaitArgs, max_width = WidthArgs, after_user_stop = AfterUserStopArg,
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
            "win 조합(win+r 등)도 원격에 전달된다 (RDP 컨트롤의 키 전송 기능). 입력하는 동안 사용자의 실제 키 누름은 막히고, 사용자가 Esc 를 누르면 멈춘다.",
            Schema(new
            {
                session_id = new { type = "integer" },
                keys = new { type = "array", items = new { type = "string" }, description = $"차례로 누를 키 조합 (최대 {RdpKeys.MaxCombos}개)" },
                capture_after = CaptureArgs, wait_ms = WaitArgs, max_width = WidthArgs, after_user_stop = AfterUserStopArg,
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
                capture_after = CaptureArgs, wait_ms = WaitArgs, max_width = WidthArgs, after_user_stop = AfterUserStopArg,
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

    /// <summary>Claude 입력 전 키보드 포커스 상태 (끝나면 그대로 되돌린다)</summary>
    private sealed record RdpFocusState(bool RdpHadFocus, bool ClaudeHadFocus, IInputElement? Element);

    private RdpFocusState CaptureFocusForRdpInput(RdpSessionView view) =>
        new(view.HasInputFocus, _claudeView?.IsKeyboardFocusWithin == true, Keyboard.FocusedElement);

    /// <summary>
    /// 입력 뒤 포커스 되돌리기: 원래 RDP 화면에 있었으면 그대로, 입력하는 사이 사용자가 다른 곳(다른 탭 등)을 골랐으면 그대로,
    /// 아니면 Claude 패널·원래 요소로 — 마지막에 RDP 컨트롤에 포커스가 남아 있으면 창 자체로 빼낸다 (이후 키가 원격으로 가지 않게)
    /// </summary>
    private void RestoreFocusAfterRdpInput(RdpSessionView view, RdpFocusState s)
    {
        if (s.RdpHadFocus || !view.HasInputFocus) return;
        if (s.ClaudeHadFocus && _claudeView != null) _claudeView.FocusSession();
        else if (s.Element is DependencyObject d && PresentationSource.FromDependencyObject(d) != null && s.Element.Focusable)
            Keyboard.Focus(s.Element);
        if (!view.HasInputFocus) return;
        Keyboard.ClearFocus();
        Focus();
    }

    // 여러 RDP 탭의 입력 구간(포커스 주기 → 키 차단 → 입력 → 되돌리기)은 창 전체에서 한 번에 하나 — 포커스는 하나라 동시에 하면 서로의 키가 버려진다
    private readonly SemaphoreSlim _rdpInputSection = new(1, 1);

    /// <summary>
    /// Claude 가 탭·화면을 바꾸거나 포커스를 옮기는 동작을 RDP 입력 구간 잠금을 쥔 채 실행한다 — 진행 중인 RDP 입력(·입력 뒤 캡처·승인 창)이
    /// 끝날 때까지 기다린 뒤 하고, 하는 동안 새 RDP 입력이 끼어들지 못한다 (입력 중 탭이 바뀌면 남은 입력이 끊긴다).
    /// "기다렸다 놓고 바로 전환" 은 WPF 가 await 뒤 코드를 다시 예약해 그 틈에 줄 서 있던 입력이 먼저 시작될 수 있어 쓰지 않는다.
    /// action 은 동기여야 하고 안에서 이 잠금·승인을 다시 기다리면 안 된다. 동기 모달 창은 닫힐 때까지 잠금을 쥔 채 기다리게 되고,
    /// 나중으로 미룬 모달 창이 이어지는 RDP 입력과 겹치면 RdpActAsync 가 IsThreadModal 로 입력을 거부한다
    /// </summary>
    private async Task<T> UnderRdpInputSectionAsync<T>(Func<T> action)
    {
        try { await _rdpInputSection.WaitAsync(McpRequest.Token); }
        catch (OperationCanceledException) { throw new McpToolException("Claude 쪽에서 요청이 취소되어 실행하지 않았습니다."); }
        try { return action(); }
        finally { _rdpInputSection.Release(); }
    }

    private Task UnderRdpInputSectionAsync(Action action) => UnderRdpInputSectionAsync(() => { action(); return 0; });

    private static double RequireNum(JsonElement a, string name) =>
        NumArg(a, name) is { } v && double.IsFinite(v) ? v : throw new McpToolException($"{name} 좌표(숫자)가 필요합니다.");

    /// <summary>
    /// RDP 조작 공통: 탭 확인 → 탭별 잠금 → 승인(grant = 탭 단위 시간 허용, 아니면 approval 로 매번) → 탭 표시 → 입력 → 감사 로그 → (선택) 다시 캡처.
    /// action 은 감사 로그·결과 문구(언어 무관 표기). 인자 오류는 모두 승인 전에 던진다.
    /// </summary>
    private async Task<object> RdpActAsync(JsonElement a, string tool, string action, bool grant,
        Func<SessionTab, RdpTarget, CancellationToken, Task> act, (string Title, string Description, string Shown)? approval = null)
    {
        var ct = McpRequest.Token;
        var captureAfter = BoolArg(a, "capture_after") != false;
        var waitMs = Math.Clamp(IntArg(a, "wait_ms") ?? 800, 0, 10000);
        var maxWidthArg = IntArg(a, "max_width");
        // 실서버(tg01)에서 확인된 방식: RDP 컨트롤은 포커스가 있어야 입력을 원격으로 넘기고(포커스 없는 창 메시지는 버려짐),
        // 창 모드의 Win 키는 공식 SendKeys 로만 넘어간다 → 키는 SendKeys, 마우스는 포커스를 준 뒤 PostMessage
        var method = tool is "rdp_key" or "rdp_type" ? RdpInputMethod.SendKeys : RdpInputMethod.FocusPost;
        var tab = RequireSession(a, allowCurrent: true);
        CheckRdpTab(tab);
        var target = SessionAuditTarget(tab);

        // 한 탭의 입력은 한 번에 하나씩 — 승인 대기까지 포함해 잠근다 (첫 허용 승인 창이 두 번 뜨지 않고, 승인한 글자 사이에 다른 키가 끼지 않게)
        var gate = _rdpInputLocks.GetValue(tab, _ => new SemaphoreSlim(1, 1));
        var stopBox = RdpStopEpoch(tab);
        var epoch = stopBox.ReadEpoch();   // 이 요청이 들어온 시점의 멈춤 번호 — 이후 사용자가 멈췄으면 입력하지 않는다
        var ackId = IntArg(a, "after_user_stop");   // 사용자 확인을 받은 멈춤 번호 — 지금 번호와 같을 때만 그 멈춤을 확인한 것
        bool StoppedSince() => !stopBox.Pass(epoch, ackId, clear: false);
        string StopRefusal(string head) => $"{head} 사용자에게 계속할지 확인한 뒤, 계속하라고 하면 after_user_stop={stopBox.ReadStopId()} (stop_id) 를 붙여 다시 요청하세요.";
        try { await gate.WaitAsync(ct); }
        catch (OperationCanceledException) { throw new McpToolException("Claude 쪽에서 요청이 취소되어 실행하지 않았습니다."); }
        if (StoppedSince())
        {
            gate.Release();
            AuditLog.Write(tool, new[] { target }, action, "실행 안 함(사용자가 앞선 입력을 멈춤)");
            throw new McpToolException(StopRefusal("사용자가 이 탭의 Claude 입력을 멈춰 이 요청은 입력하지 않았습니다."));
        }
        // 사용자가 탭 메뉴 "Claude 입력 멈추기"·허용 끝내기로 멈출 수 있게 이 입력의 토큰을 탭에 등록한다 (허용 여부와 무관)
        using var userStop = new CancellationTokenSource();
        var entered = false;            // 창 전체 입력 구간 잠금(_rdpInputSection)을 잡았는가
        var holdForCapture = false;     // 입력 뒤 캡처까지 잠금을 쥐고 있다가 캡처가 끝나면 푼다 (캡처의 탭 전환이 다른 탭 입력을 끊지 않게)
        try
        {
            // 승인을 묻기 전 연결 번호 — 승인 창이 떠 있는 사이 다시 연결됐으면 승인하지 않은 새 연결에 입력하지 않는다
            var view = (RdpSessionView)tab.View;
            var gen = view.SessionGeneration;
            RdpGrant? grantObj = null;
            CancellationToken grantStop = default;
            if (grant) { grantObj = await EnsureRdpControlAsync(tab, tool, action, gen); grantStop = grantObj.Stop.Token; }
            else if (approval is { } ap && !await AskApprovalAsync(tool, ap.Title, ap.Description, new[] { target }, ap.Shown))
            {
                AuditLog.Write(tool, new[] { target }, action, "거부");
                throw new McpToolException("사용자가 입력을 거부했습니다.");
            }

            // 승인 뒤부터는 탭 메뉴 '멈추기'·허용 끝내기가 통하게 — 다른 탭 입력이 끝나기를 기다리는 동안에도
            _rdpActiveInput.AddOrUpdate(tab, userStop);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, grantStop, userStop.Token);
            string StopReason() => userStop.IsCancellationRequested ? "사용자가 Claude 입력을 멈춰"
                : grantStop.IsCancellationRequested ? grantObj?.StopReason ?? "화면 조작 허용이 끝나"
                : "요청이 취소되어";

            // 창 전체 입력 구간 잠금은 승인 직후, 탭을 바꾸기 전에 잡는다 — 다른 탭 요청이 진행 중인 입력의 탭을 바꿔 끊지 않게
            RdpTarget ih;
            var refocused = false;   // 키 전송이 포커스 없음으로 거절돼 RDP 에 포커스를 다시 줬는가 — 결과에 남긴다 (사용자가 고른 곳으로 되돌리지 못했을 수 있음)
            try
            {
                await _rdpInputSection.WaitAsync(linked.Token);
                entered = true;
                // 승인을 기다리는 동안 탭이 닫혔거나 끊겼거나 다시 연결됐을 수 있다
                if (!_tabs.Contains(tab)) throw new McpToolException("승인을 기다리는 동안 탭이 닫혀 입력하지 않았습니다.");
                CheckRdpTab(tab);
                if (view.SessionGeneration != gen) throw new McpToolException("승인을 기다리는 동안 RDP 가 다시 연결되어 입력하지 않았습니다. 화면을 다시 캡처한 뒤 요청하세요.");
                // 다른 승인 창 등을 기다리는 사이 허용 10분이 지났거나(만료는 토큰으로 오지 않음) 사용자가 멈췄을 수 있다
                if (grant && !ReferenceEquals(ActiveGrant(tab), grantObj))
                    throw new McpToolException("기다리는 동안 화면 조작 허용이 끝나 입력하지 않았습니다. 다시 요청하면 사용자에게 허용을 다시 묻습니다.");
                // 확인과 "확인 필요" 끄기를 한 번에 (사이에 새 멈춤이 끼어 그 표시를 지우지 않게) — 지금 멈춤을 확인받은 요청이 실제 입력에 들어간다
                if (!stopBox.Pass(epoch, ackId, clear: true))
                    throw new McpToolException(StopRefusal("사용자가 이 탭의 Claude 입력을 멈춰 입력하지 않았습니다."));
                // 앱에 대화상자(계정 입력·호스트키·안내 창 등)가 떠 있으면 입력하지 않는다 — 다른 도구가 나중으로 미룬 창이 이 입력과 겹치면
                // 사용자가 그 창에 치는 비밀번호가 키 차단에 막히고, 포커스도 서로 빼앗는다
                if (System.Windows.Interop.ComponentDispatcher.IsThreadModal)
                    throw new McpToolException("앱에 대화상자가 떠 있어 입력하지 않았습니다. 사용자가 그 창을 닫은 뒤 다시 요청하세요.");
                // 조작하는 모습이 보이게 그 탭을 띄운다 (숨은 탭은 크기가 0 이라 좌표를 맞출 수 없음)
                if (!tab.IsSelected)
                {
                    ShowRdpTabForInput(tab);
                    await Task.Delay(400, linked.Token);
                    if (!_tabs.Contains(tab)) throw new McpToolException("탭이 닫혀 입력하지 않았습니다.");
                }
                var hwnd = view.FindInputWindow();
                if (hwnd == IntPtr.Zero) throw new McpToolException("RDP 입력 창을 찾지 못했습니다 (연결 중이거나 끊김). list_sessions 로 상태를 확인하세요.");
                // 메시지마다 확인: 같은 탭이 열려 있고 · 선택돼 있고 · 같은 연결이고 · 입력 창이 그대로인가
                // (닫힌 탭의 컨트롤이 다른 탭에 재사용돼도 거기로 가지 않게, 사용자가 다른 탭을 고르면 "완료" 로 보고하지 않고 멈추게)
                ih = new RdpTarget(hwnd,
                    () => _tabs.Contains(tab) && tab.IsSelected && view.SessionGeneration == gen && view.FindInputWindow() == hwnd &&
                          !System.Windows.Interop.ComponentDispatcher.IsThreadModal,   // 입력 도중 대화상자가 뜨면 멈춘다
                    // 떼기는 같은 탭·같은 연결이면 보낸다 (자동 재연결 중에는 FindInputWindow 가 비어도 눌린 키가 남지 않게)
                    () => _tabs.Contains(tab) && view.SessionGeneration == gen)
                { Method = method, Keys = method == RdpInputMethod.SendKeys ? view.NonScriptable : null, HasFocus = () => view.HasInputFocus, Refocus = () => { refocused = true; view.FocusRdpControl(); } };
            }
            catch (Exception ex) when (ex is McpToolException or OperationCanceledException)
            {
                var why = ex is OperationCanceledException ? $"({StopReason()})" : ": " + ex.Message;
                AuditLog.Write(tool, new[] { target }, action, "승인했으나 실행 안 함" + why);
                if (ex is OperationCanceledException) throw new McpToolException($"{StopReason()} 입력하지 않았습니다 (아무것도 입력되지 않음)." + (userStop.IsCancellationRequested ? " " + StopRefusal("이 탭의 다음 입력은 사용자 확인 전까지 거부됩니다.") : ""));
                throw;
            }

            // RDP 컨트롤에 포커스를 주고, 그동안 사용자의 실제 키 누름은 막는다 (포커스가 RDP 에 있는 사이 Claude 패널 등에 치던 키가
            // 원격으로 새어 승인하지 않은 입력이 되지 않게). 끝나면 원래 포커스로 되돌린다 — 예외·취소에도 (finally)
            // 입력 중 Esc 를 누르면(키 차단이 받아) 멈춘다 — 키보드로 멈출 수 있는 길 (나머지 키는 막힘)
            RdpKeyboardGuard? guard = null;
            var started = false;        // act 를 시작했는가 — 시작 전 중단은 "아무것도 입력되지 않음"
            var focusLost = false;
            var stoppedAtEnd = false;
            try
            {
                var focus = CaptureFocusForRdpInput(view);
                var focused = false;
                try
                {
                    guard = new RdpKeyboardGuard(() => { BumpRdpStopEpoch(tab); try { userStop.Cancel(); } catch (ObjectDisposedException) { } });
                    if (!guard.Active)
                        throw new McpToolException("입력 중 사용자 키 입력을 막는 장치(키보드 훅)를 걸지 못해 입력하지 않았습니다 — 사용자 키가 원격으로 섞일 수 있어서입니다.");
                    // Ctrl·Shift·Alt·Win 이 실제로 눌린 채면 SendKeys 가 그 상태를 따라 Claude 의 글자가 조합키가 된다 — 뗄 때까지 잠깐 기다린다
                    for (var i = 0; i < 20 && RdpKeyboardGuard.ModifierHeld(); i++) await Task.Delay(100, linked.Token);
                    if (RdpKeyboardGuard.ModifierHeld())
                        throw new McpToolException("Ctrl·Shift·Alt·Win 키가 눌려 있어 입력하지 않았습니다 (눌린 채 입력하면 글자가 조합키로 바뀜). 키에서 손을 뗀 뒤 다시 요청하세요.");
                    // RDP 는 포커스를 받을 때 로컬 CapsLock 상태를 원격에 맞춘다 — 켜져 있으면 글자의 대소문자가 뒤집힌다
                    if (tool == "rdp_type" && RdpKeyboardGuard.CapsLockOn())
                        throw new McpToolException("이 PC 의 CapsLock 이 켜져 있어 입력하지 않았습니다 (켜진 채 입력하면 대소문자가 뒤집힘). 사용자에게 CapsLock 을 꺼 달라고 요청한 뒤 다시 보내세요.");
                    if (!tab.IsSelected) throw new McpToolException("그 사이 다른 탭이 선택되어 입력하지 않았습니다.");
                    if (System.Windows.Interop.ComponentDispatcher.IsThreadModal)
                        throw new McpToolException("앱에 대화상자가 떠 있어 입력하지 않았습니다. 사용자가 그 창을 닫은 뒤 다시 요청하세요.");
                    view.FocusRdpControl();
                    focused = true;
                    await Task.Delay(120, linked.Token);   // 포커스가 컨트롤 안 입력 창(별도 스레드)까지 닿을 시간
                    started = true;
                    await act(tab, ih, linked.Token);
                    // 입력하는 사이 포커스가 빠졌으면(사용자가 다른 곳을 클릭 등) 그 뒤 마우스 입력은 컨트롤이 버렸을 수 있다
                    focusLost = !view.HasInputFocus;
                }
                finally
                {
                    if (focused)
                    {
                        // 마지막 버튼 뗌 등이 입력 창 스레드에서 처리될 시간을 준 뒤 포커스를 빼낸다 (포커스가 없으면 그 메시지가 버려짐)
                        try { await Task.Delay(80); } catch { }
                        try { RestoreFocusAfterRdpInput(view, focus); } catch { }
                    }
                    guard?.Dispose();
                    // 입력이 막 끝난 순간(뗌 처리 대기 80ms 포함) Esc·멈추기가 들어오면 취소를 볼 곳이 없다 — 결과에 남겨 Claude 가 이어서 하지 않게.
                    // Esc 콜백은 스레드 풀에서 늦게 돌 수 있어 번호도 함께 본다
                    stoppedAtEnd = userStop.IsCancellationRequested || stopBox.ReadEpoch() != epoch;
                    if (guard is { Blocked: > 0 } g)
                        Toast.Show(L.F("Claude 가 원격에 입력하는 동안 누른 키 {0}개는 원격으로 가지 않게 막았습니다.", g.Blocked), ToastKind.Warning);
                }
            }
            catch (OperationCanceledException)
            {
                if (!started)
                {
                    AuditLog.Write(tool, new[] { target }, action, $"승인했으나 실행 안 함({StopReason()})");
                    throw new McpToolException($"{StopReason()} 입력하지 않았습니다 (아무것도 입력되지 않음)." + (userStop.IsCancellationRequested ? " " + StopRefusal("이 탭의 다음 입력은 사용자 확인 전까지 거부됩니다.") : ""));
                }
                AuditLog.Write(tool, new[] { target }, action, $"중단({StopReason()}) — 일부 입력됐을 수 있음");
                throw new McpToolException($"{StopReason()} 입력을 중단했습니다 (일부는 이미 입력됐을 수 있음 — 화면을 캡처해 확인하고 같은 입력을 그대로 다시 보내지 말 것)." +
                    (userStop.IsCancellationRequested ? " " + StopRefusal("이 탭의 다음 입력은 사용자 확인 전까지 거부됩니다.") : ""));
            }
            catch (McpToolException ex)
            {
                // 입력 전 확인(좌표·키 차단·수정 키·CapsLock·탭 선택)에서 멈춤 — 입력되지 않았다
                AuditLog.Write(tool, new[] { target }, action, "승인했으나 실행 안 함: " + ex.Message);
                throw new McpToolException(ex.Message);
            }
            catch (RdpInputSender.RdpNothingSentException ex)
            {
                // 첫 키 묶음부터 멈춰 원격에는 아무것도 가지 않았다 (시작은 했지만 "일부 입력됐을 수 있음" 이 아님)
                AuditLog.Write(tool, new[] { target }, action, "승인했으나 실행 안 함: " + ex.Message);
                throw new McpToolException($"입력하지 않았습니다: {ex.Message}");
            }
            catch (Exception ex)
            {
                AuditLog.Write(tool, new[] { target }, action, (started ? "중단 — 일부 입력됐을 수 있음: " : "승인했으나 실행 안 함: ") + ex.Message);
                throw new McpToolException(started
                    ? $"입력을 멈췄습니다: {ex.Message} (일부는 이미 입력됐을 수 있음 — 화면을 캡처해 확인할 것)."
                    : $"입력하지 않았습니다: {ex.Message}");
            }
            AuditLog.Write(tool, new[] { target }, action,
                (grant ? "실행(화면 조작 허용 중)" : "승인·입력") + (focusLost ? " — 입력 중 포커스가 빠져 일부가 버려졌을 수 있음" : "") +
                (stoppedAtEnd ? " — 입력이 끝나는 순간 사용자가 멈춤" : "") + (refocused ? " — 입력 중 포커스가 빠져 다시 줌" : ""));
            if (focusLost) action += " (입력 중 RDP 포커스가 빠져 일부 입력이 버려졌을 수 있음 — 화면으로 확인할 것)";
            if (refocused) action += " (입력 중 RDP 포커스가 빠져 다시 준 뒤 이어서 입력함 — 사용자가 입력 중 다른 곳을 눌렀다면 끝난 뒤 포커스는 입력 전 위치로 돌아감)";
            if (stoppedAtEnd) action += $" (입력은 끝났지만 사용자가 그 순간 Claude 입력을 멈췄습니다 — 이어서 입력하지 말고 사용자에게 계속할지 확인할 것. 계속하라고 하면 다음 요청에 after_user_stop={stopBox.ReadStopId()})";
            holdForCapture = captureAfter;
        }
        finally
        {
            if (_rdpActiveInput.TryGetValue(tab, out var cur) && ReferenceEquals(cur, userStop)) _rdpActiveInput.Remove(tab);
            if (entered && !holdForCapture) _rdpInputSection.Release();
            gate.Release();
        }

        var done = $"{action} 완료.";
        if (!captureAfter) return done + " 결과는 capture_screen 으로 확인하세요.";
        // 여기부터는 입력이 이미 끝났다 — 캡처가 어떤 이유로 실패해도 입력을 다시 보내지 않게 알린다.
        // 입력 구간 잠금은 캡처가 끝날 때까지 쥐고 있다 (탭을 바꾸는 캡처가 다른 탭의 입력을 끊지 않게)
        try
        {
            if (waitMs > 0) await Task.Delay(waitMs, ct);
            if (!_tabs.Contains(tab)) return done + " 캡처 전에 탭이 닫혔습니다.";
            var width = maxWidthArg ?? (_captureGeometry.TryGetValue(tab, out var g) ? g.ImageW : 1600);
            var img = await CaptureSessionAsync(tab, width, holdsInputSection: true);
            return img with { Caption = done + " " + img.Caption };
        }
        catch (Exception ex)
        {
            return done + $" 화면은 다시 캡처하지 못했습니다 ({(ex is OperationCanceledException ? "요청 취소" : ex.Message)}) — capture_screen 으로 확인하세요 (입력은 이미 됨, 다시 보내지 말 것).";
        }
        finally
        {
            if (holdForCapture) _rdpInputSection.Release();
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
    private async Task<RdpGrant> EnsureRdpControlAsync(SessionTab tab, string tool, string action, int gen)
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
        if (!_tabs.Contains(tab) || tab.View is not RdpSessionView v || v.SessionGeneration != gen)
        {
            AuditLog.Write(tool, new[] { target }, action, "승인했으나 실행 안 함(탭 닫힘·다시 연결)");
            throw new McpToolException("승인을 기다리는 동안 탭이 닫혔거나 RDP 가 다시 연결되어 입력하지 않았습니다. 화면을 다시 캡처한 뒤 요청하세요.");
        }
        var grant = new RdpGrant(DateTime.UtcNow.AddMinutes(RdpControlMinutes), gen);
        if (_rdpGrants.TryGetValue(tab, out var old)) { try { old.Stop.Cancel(); } catch (ObjectDisposedException) { } }
        _rdpGrants.AddOrUpdate(tab, grant);
        AuditLog.Write("rdp_control", new[] { target }, $"(화면 조작 허용 {RdpControlMinutes}분)", "승인");
        Toast.Show(L.F("{0} 화면을 Claude 가 {1}분 동안 조작할 수 있습니다. 탭 우클릭 메뉴에서 끝낼 수 있습니다.", tab.Title, RdpControlMinutes));
        return grant;
    }

    /// <summary>마지막 캡처 이미지 좌표 → RDP 입력 창 좌표 · 화면 좌표</summary>
    private ((int X, int Y) Client, (int X, int Y) Screen) MapToInput(SessionTab tab, RdpTarget ih, double x, double y, int? imageWidth)
    {
        if (!_captureGeometry.TryGetValue(tab, out var g))
            throw new McpToolException($"session {tab.Id} 화면을 아직 캡처하지 않았습니다. capture_screen 으로 먼저 화면을 보고 그 이미지 좌표를 주세요.");
        if (imageWidth is { } iw && iw != g.ImageW)
            throw new McpToolException($"image_width {iw} 가 마지막 캡처 이미지 가로({g.ImageW})와 다릅니다. 가장 최근 캡처 이미지의 좌표를 주세요.");
        if (tab.View is RdpSessionView rv && rv.SessionGeneration != g.Generation)
            throw new McpToolException("마지막 캡처 뒤 RDP 가 다시 연결되어 화면이 바뀌었을 수 있습니다. capture_screen 으로 다시 캡처한 뒤 새 좌표를 주세요.");
        var view = tab.View;
        var dpi = VisualTreeHelper.GetDpi(this);
        var (curW, curH) = ((int)(view.ActualWidth * dpi.DpiScaleX), (int)(view.ActualHeight * dpi.DpiScaleY));
        if (x < 0 || y < 0 || x >= g.ImageW || y >= g.ImageH)
            throw new McpToolException($"좌표 ({x:0.#}, {y:0.#}) 가 마지막 캡처 이미지({g.ImageW}x{g.ImageH}) 밖입니다.");
        var px = RdpKeys.ImageToTabPixels(x, y, g.ImageW, g.ImageH, g.TabW, g.TabH, curW, curH)
                 ?? throw new McpToolException("마지막 캡처 뒤 탭 크기가 바뀌었습니다. capture_screen 으로 다시 캡처한 뒤 새 좌표를 주세요.");
        var screen = view.PointToScreen(new Point(px.X / dpi.DpiScaleX, px.Y / dpi.DpiScaleY));
        var (sx, sy) = ((int)Math.Round(screen.X), (int)Math.Round(screen.Y));
        var client = RdpInputSender.ScreenToInput(ih.Hwnd, sx, sy)
                     ?? throw new McpToolException($"좌표 ({x:0.#}, {y:0.#}) 는 원격 화면 영역 밖입니다. 다시 캡처해 확인하세요.");
        return (client, (sx, sy));
    }
}
