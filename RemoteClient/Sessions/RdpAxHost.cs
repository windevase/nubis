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
    public event Action? LeftFullScreen;
    /// <summary>네트워크가 끊겨 컨트롤이 스스로 다시 연결하는 중 (사유 코드, 시도 횟수)</summary>
    public event Action<int, int>? AutoReconnecting;
    /// <summary>자동 재연결 성공</summary>
    public event Action? AutoReconnected;

    // 지금 탭이 연결을 시작한 세션인가. 풀에서 재사용한 컨트롤에 이전 세션의 늦은 OnDisconnected 가 도착해도
    // 새 탭이 "연결 종료" 로 바뀌지 않게, 연결을 시작한 뒤의 첫 OnDisconnected 만 전달한다.
    private bool _sessionActive;

    // 반납할 때 연결을 끊었고 그 OnDisconnected 가 아직 오지 않았다. Connected 속성이 먼저 0 이 되고 이벤트가 뒤에 올 수 있어,
    // 이 사이에 다른 탭이 컨트롤을 가져가 BeginSession 하면 이전 세션의 이벤트가 새 세션의 끊김으로 처리되고
    // 새 세션의 진짜 끊김은 버려진다 → 이벤트가 올 때까지(최대 StaleWait) 풀에서 내주지 않는다.
    private bool _staleDisconnectPending;
    private DateTime _releasedAt;
    private static readonly TimeSpan StaleWait = TimeSpan.FromSeconds(10);

    /// <summary>OnFatalError 를 받은 컨트롤 — 내부 상태를 믿을 수 없으므로 다시 쓰지 않고 풀에도 넣지 않는다</summary>
    public bool Faulted { get; private set; }

    /// <summary>Connect() 직전에 부른다 (이 뒤의 OnDisconnected 만 탭에 전달)</summary>
    public void BeginSession() => _sessionActive = true;

    public RdpAxHost() : base(Clsid)
    {
        _sink = new EventSink(this);
    }

    /// <summary>IMsRdpClient (IDispatch) — Server, AdvancedSettings9, Connect() 등</summary>
    public dynamic Ocx => GetOcx() ?? throw new InvalidOperationException(L.T("RDP 컨트롤이 아직 생성되지 않았습니다."));

    /// <summary>
    /// IMsRdpExtendedSettings.Property 설정 (DesktopScaleFactor 등). 이 인터페이스는 IDispatch 로 노출되지 않아
    /// dynamic 으로는 부를 수 없으므로 QueryInterface 로 얻는다. 지원하지 않거나 값이 거부되면 false.
    /// </summary>
    public bool TrySetExtendedProperty(string name, object value)
    {
        try
        {
            if (GetOcx() is not IMsRdpExtendedSettings ext) return false;
            ext.set_Property(name, ref value);
            return true;
        }
        catch { return false; }
    }

    [ComImport, Guid("302D8188-0052-4807-806A-362B628F9AC5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMsRdpExtendedSettings
    {
        // vtable 순서: propput Property → propget Property (mstsax.idl)
        void set_Property([MarshalAs(UnmanagedType.BStr)] string bstrPropertyName, [In] ref object pValue);
        object get_Property([MarshalAs(UnmanagedType.BStr)] string bstrPropertyName);
    }

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
        Connecting = null; Connected = null; LoginComplete = null; Disconnected = null; FatalError = null; LeftFullScreen = null;
        AutoReconnecting = null; AutoReconnected = null;
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
        ClearHandlers();
        _sessionActive = false;
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
        [DispId(6)] void OnLeaveFullScreenMode();
        [DispId(10)] void OnFatalError(int errorCode);
        // mstsax.idl: OnAutoReconnecting(long disconnectReason, long attemptCount, [out] AutoReconnectContinueState*) — 0 = 자동으로 계속
        [DispId(17)] void OnAutoReconnecting(int disconnectReason, int attemptCount, out int pArcContinueStatus);
        [DispId(33)] void OnAutoReconnected();
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
            if (ax.Faulted) { ax.Shutdown(); continue; }
            int state;
            try { state = (int)ax.Ocx.Connected; } catch { ax.Shutdown(); continue; }
            // 끊기가 끝내 끝나지 않는 컨트롤은 풀 자리를 영원히 막아, 그 뒤 반납되는 컨트롤이 모두 해제(핸들·스레드 누수)된다 → 버린다
            if (IsStuck(state, DateTime.UtcNow - ax._releasedAt)) { ax.Shutdown(); continue; }
            if (state == 0 && IsReusable(ax._staleDisconnectPending, DateTime.UtcNow - ax._releasedAt))
            {
                ax._staleDisconnectPending = false;
                found = ax;
                break;
            }
            skipped.Add(ax);   // 아직 연결을 끊는 중 — 다음에 쓴다
        }
        foreach (var s in skipped) Pool.Push(s);
        return found ?? new RdpAxHost();
    }

    public static void Release(RdpAxHost ax)
    {
        // 같은 컨트롤을 두 번 반납하면 풀에 두 번 들어가 두 탭이 한 컨트롤을 나눠 갖는다 — 이미 풀에 있으면 무시
        if (Pool.Contains(ax)) return;
        ax.ClearHandlers();
        ax._sessionActive = false;   // 이제부터 오는 OnDisconnected 는 이전 세션 것 — 다음 탭에 전달하지 않는다
        try { if ((bool)ax.Ocx.FullScreen) ax.Ocx.FullScreen = false; } catch { }
        ax._releasedAt = DateTime.UtcNow;
        try
        {
            if ((int)ax.Ocx.Connected != 0)
            {
                ax._staleDisconnectPending = true;   // 이 Disconnect 의 OnDisconnected 가 올 때까지 다른 탭에 내주지 않는다
                ax.Ocx.Disconnect();
            }
        }
        catch { }
        // 풀에 보관하는 동안 이전 탭의 비밀번호가 컨트롤에 남지 않게 지운다 (다음 연결은 Connect 에서 항상 다시 설정)
        try { ax.Ocx.AdvancedSettings9.ClearTextPassword = ""; } catch { }
        if (ax.IsDisposed || ax.Faulted || Pool.Count >= MaxPooled) { ax.Shutdown(); return; }
        Pool.Push(ax);   // 부모에서 떼어도 WinForms 주차 창이 핸들을 유지한다
    }

    /// <summary>
    /// 연결이 끊긴(Connected = 0) 풀의 컨트롤을 다시 내줘도 되는가. 반납 때 끊은 연결의 OnDisconnected 를 아직 못 받았으면
    /// 기다린다 — 단 이벤트가 끝내 오지 않는 경우 풀 자리를 계속 차지하지 않게 StaleWait 가 지나면 내준다.
    /// </summary>
    internal static bool IsReusable(bool staleDisconnectPending, TimeSpan sinceRelease) =>
        !staleDisconnectPending || sinceRelease >= StaleWait;

    private static readonly TimeSpan StuckWait = TimeSpan.FromSeconds(60);

    /// <summary>반납한 지 StuckWait 가 지나도 아직 연결 상태(Connected ≠ 0)인 컨트롤 — 풀에서 빼고 해제한다</summary>
    internal static bool IsStuck(int connectedState, TimeSpan sinceRelease) =>
        connectedState != 0 && sinceRelease >= StuckWait;

    [ClassInterface(ClassInterfaceType.None)]
    public sealed class EventSink : IMsTscAxEvents
    {
        private readonly RdpAxHost _owner;
        public EventSink(RdpAxHost owner) => _owner = owner;
        public void OnConnecting() => _owner.Connecting?.Invoke();
        public void OnConnected() => _owner.Connected?.Invoke();
        public void OnLoginComplete() => _owner.LoginComplete?.Invoke();
        public void OnDisconnected(int discReason)
        {
            if (!_owner._sessionActive)
            {
                _owner._staleDisconnectPending = false;   // 반납 때 끊은 이전 세션의 이벤트 — 이제 다시 내줘도 된다
                return;
            }
            _owner._sessionActive = false;
            _owner.Disconnected?.Invoke(discReason);
        }
        public void OnAutoReconnecting(int disconnectReason, int attemptCount, out int pArcContinueStatus)
        {
            pArcContinueStatus = 0;   // autoReconnectContinueAutomatic — 컨트롤이 알아서 계속 시도 (기존 동작 그대로)
            if (_owner._sessionActive) _owner.AutoReconnecting?.Invoke(disconnectReason, attemptCount);
        }
        public void OnAutoReconnected()
        {
            if (_owner._sessionActive) _owner.AutoReconnected?.Invoke();
        }
        public void OnFatalError(int errorCode)
        {
            _owner.Faulted = true;
            _owner._sessionActive = false;
            _owner.FatalError?.Invoke(errorCode);
        }
        public void OnLeaveFullScreenMode() => _owner.LeftFullScreen?.Invoke();
    }
}
