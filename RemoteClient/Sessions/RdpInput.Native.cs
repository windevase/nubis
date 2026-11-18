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
            throw new InvalidOperationException("탭이 닫혔거나 연결이 바뀌어 입력을 멈췄습니다.");
    }

    /// <summary>
    /// 공식 SendKeys 로 키 이벤트를 보낸다. 한 글자·한 조합(수정 키를 누른 뒤 모두 뗄 때까지)을 **한 번의 호출**로 묶는다 —
    /// 키를 하나씩 따로 보내면 컨트롤이 호출마다 수정 키 상태를 로컬 키보드에 맞춰 Shift 가 풀린다 ($ → 4, P → p 로 찍힘, tg01 실측).
    /// 실패해도 다시 보내지 않는다(일부가 이미 들어갔다면 글자가 두 번 찍힘). 실패한 묶음에서 누른 키는 뗌만 따로 보내
    /// 원격에 Shift·Win 이 눌린 채 남지 않게 하고 멈춘다. 묶음 사이 취소는 눌린 키가 없는 상태라 그대로 멈춘다
    /// </summary>
    internal static async Task SendKeysBatchedAsync(RdpTarget ih, IReadOnlyList<RdpKeyEvent> events, CancellationToken ct)
    {
        var keys = ih.Keys ?? throw new InvalidOperationException("이 RDP 컨트롤은 키 전송(SendKeys)을 지원하지 않습니다.");
        foreach (var group in RdpKeys.Chords(events))
        {
            ct.ThrowIfCancellationRequested();
            Check(ih, false);
            var ups = group.Select(e => (short)(e.Up ? -1 : 0)).ToArray();
            var data = group.Select(e => e.Scan | (e.Extended ? 0x100 : 0)).ToArray();
            try { keys.SendKeys(group.Count, ups, data); }
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
            // 원격이 따라오게 묶음 사이에 잠깐 쉰다 (실행 창·UAC 전환 직후 키를 놓치지 않게)
            await Task.Delay(15 + 4 * group.Count, ct);
        }
    }
}
