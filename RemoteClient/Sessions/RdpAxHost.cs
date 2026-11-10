using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Nubis;

/// <summary>
/// mstsc.exe 와 같은 엔진인 Windows 내장 RDP ActiveX(mstscax.dll)를 WinForms 컨트롤로 감싼다.
/// interop 어셈블리(aximp) 없이 CLSID 로 직접 생성하고, 속성은 IDispatch(dynamic)로 다룬다.
/// </summary>
public sealed class RdpAxHost : AxHost
{
    // MsRdpClient10NotSafeForScripting (Windows 10 이상 기본 포함)
    private const string Clsid = "a0c63c30-f08d-4ab4-907c-34905d770c7d";

    private readonly EventSink _sink;
    private ConnectionPointCookie? _cookie;

    public event Action? Connecting;
    public event Action? Connected;
    public event Action? LoginComplete;
    public event Action<int>? Disconnected;
    public event Action<int>? FatalError;

    public RdpAxHost() : base(Clsid)
    {
        _sink = new EventSink(this);
    }

    /// <summary>IMsRdpClient (IDispatch) — Server, AdvancedSettings9, Connect() 등</summary>
    public dynamic Ocx => GetOcx() ?? throw new InvalidOperationException(L.T("RDP 컨트롤이 아직 생성되지 않았습니다."));

    protected override void CreateSink()
    {
        try { _cookie = new ConnectionPointCookie(GetOcx()!, _sink, typeof(IMsTscAxEvents)); }
        catch { _cookie = null; }
    }

    protected override void DetachSink()
    {
        try { _cookie?.Disconnect(); } catch { }
        _cookie = null;
    }

    /// <summary>이벤트 구독 해제 (풀에 반납할 때 이전 탭과의 연결을 끊는다)</summary>
    public void ClearHandlers()
    {
        Connecting = null; Connected = null; LoginComplete = null; Disconnected = null; FatalError = null;
    }

    /// <summary>
    /// 탭을 닫을 때 정리. 이벤트 연결(COM → 관리 객체 → COM 순환 참조)을 먼저 끊고, 컨트롤을 해제한 뒤
    /// 남은 COM 참조를 모두 놓아 RDP 엔진의 스레드·핸들이 정리되게 한다.
    /// </summary>
    public void Shutdown()
    {
        object? ocx = null;
        try { ocx = GetOcx(); } catch { }
        DetachSink();
        Connecting = null; Connected = null; LoginComplete = null; Disconnected = null; FatalError = null;
        try { Dispose(); } catch { }
        try { if (ocx != null && Marshal.IsComObject(ocx)) Marshal.FinalReleaseComObject(ocx); } catch { }
    }

    [ComImport, Guid("336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    public interface IMsTscAxEvents
    {
        [DispId(1)] void OnConnecting();
        [DispId(2)] void OnConnected();
        [DispId(3)] void OnLoginComplete();
        [DispId(4)] void OnDisconnected(int discReason);
        [DispId(10)] void OnFatalError(int errorCode);
    }

    // ---------------------------------------------------------------
    // 컨트롤 풀: 연결해 본 mstscax 인스턴스를 해제할 때마다 핸들 약 60개·스레드 2~3개가 남는다
    // (Windows RDP 컨트롤 동작, Nubis 없이도 재현). 같은 인스턴스로 재연결하면 늘지 않으므로
    // 닫은 탭의 컨트롤은 연결만 끊어 보관했다가 다음 RDP 탭에서 다시 쓴다.
    // ---------------------------------------------------------------
    private const int MaxPooled = 3;
    private static readonly Stack<RdpAxHost> Pool = new();

    public static RdpAxHost Acquire()
    {
        var skipped = new List<RdpAxHost>();
        RdpAxHost? found = null;
        while (Pool.Count > 0)
        {
            var ax = Pool.Pop();
            if (ax.IsDisposed) continue;
            int state;
            try { state = (int)ax.Ocx.Connected; } catch { ax.Shutdown(); continue; }
            if (state == 0) { found = ax; break; }
            skipped.Add(ax);   // 아직 연결을 끊는 중 — 다음에 쓴다
        }
        foreach (var s in skipped) Pool.Push(s);
        return found ?? new RdpAxHost();
    }

    public static void Release(RdpAxHost ax)
    {
        ax.ClearHandlers();
        try { if ((int)ax.Ocx.Connected != 0) ax.Ocx.Disconnect(); } catch { }
        if (ax.IsDisposed || Pool.Count >= MaxPooled) { ax.Shutdown(); return; }
        Pool.Push(ax);   // 부모에서 떼어도 WinForms 주차 창이 핸들을 유지한다
    }

    [ClassInterface(ClassInterfaceType.None)]
    public sealed class EventSink : IMsTscAxEvents
    {
        private readonly RdpAxHost _owner;
        public EventSink(RdpAxHost owner) => _owner = owner;
        public void OnConnecting() => _owner.Connecting?.Invoke();
        public void OnConnected() => _owner.Connected?.Invoke();
        public void OnLoginComplete() => _owner.LoginComplete?.Invoke();
        public void OnDisconnected(int discReason) => _owner.Disconnected?.Invoke(discReason);
        public void OnFatalError(int errorCode) => _owner.FatalError?.Invoke(errorCode);
    }
}
