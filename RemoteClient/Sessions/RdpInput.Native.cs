using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Nubis;

/// <summary>
/// RDP 키 입력 — RDP 컨트롤의 공식 IMsRdpClientNonScriptable.SendKeys (창 메시지가 아니라 RDP 프로토콜로 바로 보냄).
/// 실서버(tg01)에서: 창 메시지 키는 포커스가 있어도 창 모드의 Win 키가 로컬에 잡히지만, SendKeys 는 Win 키까지 원격으로 넘어간다
/// </summary>
internal static partial class RdpInputSender
{
    [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static void Check(RdpTarget ih, bool release)
    {
        if (!IsWindow(ih.Hwnd) || !(release ? ih.CanRelease() : ih.Alive()))
            throw new InvalidOperationException("탭이 닫혔거나, 다른 탭이 선택됐거나, 연결이 바뀌어 입력을 멈췄습니다.");
    }

    /// <summary>
    /// 공식 SendKeys 로 키 이벤트를 보낸다. 한 글자·한 조합(수정 키를 누른 뒤 모두 뗄 때까지)을 **한 번의 호출**로 묶는다 —
    /// 키를 하나씩 따로 보내면 컨트롤이 호출마다 수정 키 상태를 로컬 키보드에 맞춰 Shift 가 풀린다 ($ → 4, P → p 로 찍힘, tg01 실측).
    /// 일시 거절(E_UNEXPECTED, 포커스가 빠진 E_FAIL)만 다시 보낸다 — 그 밖의 실패는 일부가 이미 들어갔다면 글자가 두 번 찍히므로 멈춘다. 실패한 묶음에서 누른 키는 뗌만 따로 보내
    /// 원격에 Shift·Win 이 눌린 채 남지 않게 하고 멈춘다. 묶음 사이 취소는 눌린 키가 없는 상태라 그대로 멈춘다
    /// </summary>
    internal static async Task SendKeysBatchedAsync(RdpTarget ih, IReadOnlyList<RdpKeyEvent> events, CancellationToken ct)
    {
        var keys = ih.Keys ?? throw new InvalidOperationException("이 RDP 컨트롤은 키 전송(SendKeys)을 지원하지 않습니다.");
        var sent = 0;   // 이미 보낸 묶음 수 — 실패 메시지에 넣어 Claude 가 남은 부분만 다시 보내게 (글자 입력은 한 글자 = 한 묶음)
        try
        {
            foreach (var group in RdpKeys.Chords(events))
            {
                ct.ThrowIfCancellationRequested();
                Check(ih, false);
                // SendKeys 는 호출마다 로컬 수정 키 상태를 따라간다 — 입력 도중 사용자가 다른 앱에서 Ctrl 등을 누르고 있으면(그 앱이 앞이라 차단되지 않음)
                // 남은 글자가 원격에 단축키(Ctrl+W 등)로 들어가므로 멈춘다. SendKeys 는 로컬 키 상태를 바꾸지 않아 Claude 의 조합 키와 헷갈리지 않는다
                if (RdpKeyboardGuard.ModifierHeld())
                    throw new InvalidOperationException("입력 도중 Ctrl·Shift·Alt·Win 키가 눌려 남은 입력을 멈췄습니다 (보낸 글자가 단축키로 바뀌지 않게).");
                var ups = group.Select(e => (short)(e.Up ? -1 : 0)).ToArray();
                var data = group.Select(e => e.Scan | (e.Extended ? 0x100 : 0)).ToArray();
                try { await SendChordAsync(ih, keys, group.Count, ups, data, ct); }
                catch
                {
                    // 이 묶음에서 누른 키를 역순으로 뗀다 (토큰·재시도 없이 한 번)
                    var downs = group.Where(e => !e.Up).Reverse().ToArray();
                    if (downs.Length > 0)
                    {
                        try
                        {
                            Check(ih, true);
                            keys.SendKeys(downs.Length, downs.Select(_ => (short)-1).ToArray(), downs.Select(e => e.Scan | (e.Extended ? 0x100 : 0)).ToArray());
                        }
                        catch { }
                    }
                    throw;
                }
                sent++;
                // 원격이 따라오게 묶음 사이에 잠깐 쉰다 (실행 창·UAC 전환 직후 키를 놓치지 않게)
                await Task.Delay(15 + 4 * group.Count, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException && sent > 0)
        {
            throw new InvalidOperationException($"{Describe(ex)} — 앞 {sent}개 키 묶음(글자 입력이면 앞 {sent}글자)까지 입력됐고, {Tail(ex)} 화면으로 확인한 뒤 남은 부분만 보낼 것.", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not RdpNothingSentException)   // 첫 묶음에서 멈춤 (sent == 0)
        {
            if (!Partial(ex)) throw new RdpNothingSentException($"{Describe(ex)} — 아무것도 입력되지 않았습니다.", ex);
            throw new InvalidOperationException($"{Describe(ex)} — 첫 묶음(한 글자·한 조합)이 일부 들어갔을 수 있고 그 뒤는 입력되지 않았습니다.", ex);
        }
    }

    private static string Describe(Exception ex) => ex is RdpKeySendException r ? r.Message
        : ex is COMException c ? $"RDP 키 전송 오류 (0x{c.HResult:X8})" : ex.Message;

    // 실패한 묶음이 일부라도 들어갔을 수 있는가 — 보내기 전 확인에서 멈췄거나 E_UNEXPECTED(묶음 전체 거절)면 아니고, 그 밖의 전송 오류(E_FAIL 포함)는 그럴 수 있다
    private static bool Partial(Exception ex) => ex is RdpKeySendException r ? r.MaybePartial : ex is COMException;

    private static string Tail(Exception ex) => Partial(ex)
        ? "실패한 묶음(한 글자·한 조합)은 일부 들어갔을 수 있고 그 뒤는 입력되지 않았습니다."
        : "그 뒤는 입력되지 않았습니다.";

    /// <summary>키 입력이 하나도 원격에 가지 않고 멈춤 — 호출하는 쪽이 "일부 입력됐을 수 있음" 대신 "입력하지 않음" 으로 보고하게</summary>
    internal sealed class RdpNothingSentException(string message, Exception inner) : InvalidOperationException(message, inner);

    private const int EUnexpected = unchecked((int)0x8000FFFF);
    private const int EFail = unchecked((int)0x80004005);

    /// <summary>키 전송 실패 — 메시지에 HRESULT·포커스 상태, MaybePartial = 실패한 묶음이 일부 들어갔을 수 있음</summary>
    private sealed class RdpKeySendException(string message, bool maybePartial, Exception inner) : InvalidOperationException(message, inner)
    {
        public bool MaybePartial { get; } = maybePartial;
    }

    /// <summary>
    /// 묶음 하나를 보낸다. 일시 거절로 보이는 실패만 잠깐 뒤 두 번까지 다시 보낸다:
    /// · E_UNEXPECTED — tg01 실측: Nubis 를 켠 뒤 첫 글자 입력에서 몇 글자 만에 실패, 그 묶음의 글자는 하나도 찍히지 않음(묶음 전체 거절)
    /// · E_FAIL 이면서 RDP 컨트롤에 포커스가 없을 때 — 포커스를 다시 준 뒤 (tg01 에서 같은 상황의 E_FAIL 도 묶음 전체 거절이었음)
    /// 포커스가 있는데 난 E_FAIL 등 그 밖의 오류는 일부가 들어갔을 수 있어 다시 보내지 않는다 (두 번 찍힘 방지)
    /// </summary>
    private static async Task SendChordAsync(RdpTarget ih, RdpAxHost.IMsRdpClientNonScriptable keys, int n, short[] ups, int[] data, CancellationToken ct)
    {
        var sawFail = false;
        for (var attempt = 0; ; attempt++)
        {
            try { keys.SendKeys(n, ups, data); return; }
            catch (COMException ex) when (ex.HResult is EUnexpected or EFail)
            {
                var hadFocus = ih.HasFocus?.Invoke() ?? true;
                var focusText = hadFocus ? "있음" : "없음";
                sawFail |= ex.HResult == EFail;
                if (ex.HResult == EFail && hadFocus)
                    throw new RdpKeySendException($"RDP 키 전송 오류 (0x{ex.HResult:X8}, RDP 포커스 {focusText})", true, ex);
                if (attempt >= 2)
                    throw new RdpKeySendException($"RDP 컨트롤이 키 전송을 계속 거절했습니다 (0x{ex.HResult:X8}, 3번 시도, RDP 포커스 {focusText})", sawFail, ex);
                try
                {
                    // 기다린 사이 탭·연결이 바뀌었거나 사용자가 수정 키를 눌렀으면 다시 보내지 않는다
                    Check(ih, false);
                    if (!hadFocus) ih.Refocus?.Invoke();   // 포커스가 빠져 거절됐으면 다시 준다 (사용자 키는 입력 동안 막혀 있음)
                    await Task.Delay(200, ct);
                    Check(ih, false);
                    if (RdpKeyboardGuard.ModifierHeld())
                        throw new InvalidOperationException("Ctrl·Shift·Alt·Win 키가 눌려 있음 (보낸 글자가 단축키로 바뀌지 않게)");
                }
                catch (InvalidOperationException stop)
                {
                    throw new RdpKeySendException($"키 전송이 거절돼(0x{ex.HResult:X8}) 다시 보내려다 멈췄습니다: {stop.Message}", sawFail, stop);
                }
            }
        }
    }
}
