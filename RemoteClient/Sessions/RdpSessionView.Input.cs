using System;

namespace Nubis;

/// <summary>Claude 의 RDP 화면 조작(rdp_click 등)이 입력을 보낼 창을 찾는다 (입력 자체는 RdpInputSender)</summary>
public sealed partial class RdpSessionView
{
    // 연결을 시작할 때마다 1 씩 늘어난다 — 화면 조작 허용을 연결 하나에 묶는다 (끊겼다 다시 로그인한 세션은 새로 승인)
    private int _sessionGeneration;

    /// <summary>지금 연결의 번호 (Connect 마다 바뀜). UI 스레드에서 읽는다</summary>
    internal int SessionGeneration => _sessionGeneration;

    /// <summary>
    /// 원격 세션에 입력을 보낼 RDP 컨트롤 안의 입력 창. 연결 전·끊김·안내 화면(오버레이) 표시 중·닫힌 탭이면 Zero.
    /// 매번 찾는다 — 다시 연결하거나 치명적 오류로 컨트롤을 바꾸면 창이 달라진다. UI 스레드에서 호출.
    /// </summary>
    internal IntPtr FindInputWindow()
    {
        if (_closed || _status != SessionStatus.Connected || _wfh.Visibility != System.Windows.Visibility.Visible) return IntPtr.Zero;
        try { return _rdp.IsHandleCreated ? RdpInputSender.FindInputWindow(_rdp.Handle) : IntPtr.Zero; }
        catch { return IntPtr.Zero; }
    }

    /// <summary>RDP 컨트롤에 키보드 포커스를 준다 (입력 방식 실험: focus_post · sendkeys · sendinput). UI 스레드에서 호출</summary>
    internal void FocusRdpControl()
    {
        if (_closed) return;
        try { _rdp.Focus(); } catch { }
    }

    /// <summary>공식 키 전송 인터페이스 (sendkeys 방식)</summary>
    internal RdpAxHost.IMsRdpClientNonScriptable? NonScriptable => _closed ? null : _rdp.NonScriptable;

    /// <summary>전체 화면 중인가 — 전체 화면은 별도 창에 그려져 탭 캡처·좌표가 맞지 않는다</summary>
    internal bool IsFullScreen
    {
        get
        {
            if (_closed) return false;
            try { return (bool)_rdp.Ocx.FullScreen; } catch { return false; }
        }
    }
}
