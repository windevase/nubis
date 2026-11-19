using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using Renci.SshNet;

namespace Nubis;

/// <summary>포워딩 하나의 지금 상태</summary>
public enum ForwardState
{
    /// <summary>꺼 둠</summary>
    Off,
    /// <summary>켜져 있지만 SSH 가 연결되지 않음 — 연결되면 연다</summary>
    Waiting,
    /// <summary>127.0.0.1:로컬포트 에서 받는 중</summary>
    Open,
    /// <summary>열지 못함 (로컬 포트 사용 중 등) — Error 에 사유</summary>
    Failed,
}

/// <summary>SSH 탭에 걸린 포워딩 하나 (저장된 규칙 또는 이 탭에서만 임시로 추가한 규칙)</summary>
public sealed class ActiveForward
{
    internal ActiveForward(int id, PortForwardRule rule, bool temporary)
    {
        Id = id;
        Rule = rule;
        Temporary = temporary;
        Enabled = rule.Enabled;
    }

    /// <summary>탭 안에서의 번호 (MCP 가 가리킬 때)</summary>
    public int Id { get; }
    /// <summary>규칙 사본 (저장된 호스트의 값과 따로)</summary>
    public PortForwardRule Rule { get; private set; }
    /// <summary>이 탭에서만 임시로 추가함 (호스트에 저장 안 됨)</summary>
    public bool Temporary { get; }
    /// <summary>이 탭에서 켜 둠 (저장된 규칙의 켜기 값으로 시작, 탭에서 바꾸면 이 탭에만)</summary>
    public bool Enabled { get; internal set; }
    public ForwardState State { get; internal set; }
    /// <summary>열기 실패 사유 또는 마지막 연결(채널) 오류 — 서버가 포워딩을 막았거나 대상에 닿지 못한 경우 등</summary>
    public string? Error { get; internal set; }
    /// <summary>지금까지 받은 연결 수</summary>
    public int Connections { get; internal set; }

    internal ForwardedPortLocal? Port;

    internal void ReplaceRule(PortForwardRule rule) => Rule = rule;

    public string Describe => PortForwards.Describe(Rule);
    public string LocalAddress => PortForwards.LocalAddress(Rule.LocalPort);
}

/// <summary>
/// SSH 탭 하나의 로컬 포트 포워딩 묶음. 탭의 SSH 연결(SshClient)에 ForwardedPortLocal 을 붙인다 — 새로 로그인하지 않는다 (OTP 서버도).
/// 연결되면 켜진 규칙을 열고(Attach), 끊기거나 탭을 닫으면 모두 닫는다(Detach). 다시 연결되면 다시 연다.
/// 로컬 포트가 이미 쓰이면 그 규칙만 Failed 로 두고 나머지는 연다. 바인드는 127.0.0.1 만.
/// 메서드는 어느 스레드에서 불러도 된다 (내부 잠금). 포트 닫기(Stop)는 열린 채널을 기다릴 수 있어 백그라운드에서 한다.
/// </summary>
public sealed class SshPortForwardSet
{
    private readonly object _lock = new();
    private readonly List<ActiveForward> _items = new();
    private SshClient? _client;
    private int _nextId = 1;

    /// <summary>목록·상태가 바뀜 (아무 스레드에서나 온다)</summary>
    public event Action? Changed;

    public SshPortForwardSet(IEnumerable<PortForwardRule>? saved = null) => SetSavedRules(saved);

    public IReadOnlyList<ActiveForward> Items { get { lock (_lock) return _items.ToList(); } }
    public int OpenCount { get { lock (_lock) return _items.Count(f => f.State == ForwardState.Open); } }
    public bool IsAttached { get { lock (_lock) return _client != null; } }

    /// <summary>
    /// 저장된 규칙 반영 (탭을 열 때·다시 연결할 때 최신 호스트 값으로). 같은 규칙(로컬 포트·대상)은 이 탭에서 바꾼 켜기 값을 그대로 두고,
    /// 새 규칙은 추가, 사라진 규칙은 닫는다. 임시 규칙은 그대로.
    /// </summary>
    public void SetSavedRules(IEnumerable<PortForwardRule>? saved)
    {
        var stop = new List<ForwardedPortLocal>();
        var client = default(SshClient);
        lock (_lock)
        {
            var want = (saved ?? Enumerable.Empty<PortForwardRule>()).Select(r => r.Clone()).ToList();
            var keep = new List<ActiveForward>();
            foreach (var r in want)
            {
                if (_items.FirstOrDefault(f => !f.Temporary && f.Rule.Key == r.Key && !keep.Contains(f)) is { } old)
                {
                    old.ReplaceRule(r);   // 이름이 바뀌었을 수 있다 (켜기 값은 탭의 것을 유지)
                    keep.Add(old);
                }
                else keep.Add(new ActiveForward(_nextId++, r, temporary: false));
            }
            foreach (var gone in _items.Where(f => !f.Temporary && !keep.Contains(f)))
                if (gone.Port != null) { stop.Add(gone.Port); gone.Port = null; }
            var temps = _items.Where(f => f.Temporary).ToList();
            _items.Clear();
            _items.AddRange(keep);
            _items.AddRange(temps);
            client = _client;
            foreach (var f in _items)
                if (f.Port == null) f.State = !f.Enabled ? ForwardState.Off : client == null ? ForwardState.Waiting : f.State;
            if (client != null)
                foreach (var f in _items.Where(f => f.Enabled && f.Port == null)) OpenLocked(f, client);
        }
        StopInBackground(stop, client);
        Changed?.Invoke();
    }

    /// <summary>SSH 연결이 준비됨 — 켜진 규칙을 모두 연다 (하나가 실패해도 나머지는 연다)</summary>
    public void Attach(SshClient client)
    {
        List<ForwardedPortLocal> stop;
        SshClient? oldClient;
        lock (_lock)
        {
            oldClient = _client;
            stop = TakePortsLocked();
            _client = client;
            foreach (var f in _items)
            {
                f.Error = null;
                if (f.Enabled) OpenLocked(f, client);
                else f.State = ForwardState.Off;
            }
        }
        StopInBackground(stop, oldClient);
        Changed?.Invoke();
    }

    /// <summary>연결이 끊김·탭 닫힘 — 모두 닫는다. 켜진 규칙은 "연결되면 열림" 으로 남는다</summary>
    public void Detach()
    {
        List<ForwardedPortLocal> stop;
        SshClient? client;
        lock (_lock)
        {
            if (_client == null && _items.All(f => f.Port == null)) return;
            client = _client;
            _client = null;
            stop = TakePortsLocked();
            foreach (var f in _items) f.State = f.Enabled ? ForwardState.Waiting : ForwardState.Off;
        }
        StopInBackground(stop, client);
        Changed?.Invoke();
    }

    /// <summary>이 탭에서 켜기/끄기. 켤 때 로컬 포트를 켜 둔 다른 규칙이 있으면 오류 문구</summary>
    public string? SetEnabled(ActiveForward f, bool on)
    {
        ForwardedPortLocal? stop = null;
        SshClient? client;
        lock (_lock)
        {
            if (!_items.Contains(f)) return L.T("이미 목록에서 사라진 포워딩입니다.");
            if (f.Enabled == on) return null;
            if (on && _items.Any(x => x != f && x.Enabled && x.Rule.LocalPort == f.Rule.LocalPort))
                return L.F("로컬 포트 {0} 을(를) 켜 둔 규칙이 둘 이상입니다.", f.Rule.LocalPort);
            f.Enabled = on;
            f.Error = null;
            client = _client;
            if (on)
            {
                if (client != null) OpenLocked(f, client);
                else f.State = ForwardState.Waiting;
            }
            else
            {
                stop = f.Port;
                f.Port = null;
                f.State = ForwardState.Off;
            }
        }
        if (stop != null) StopInBackground(new() { stop }, client);
        Changed?.Invoke();
        return null;
    }

    /// <summary>이 탭에서만 임시 추가 (호스트에 저장하지 않음). 연결돼 있으면 바로 연다. 규칙이 잘못됐거나 로컬 포트가 겹치면 (null, 오류)</summary>
    public (ActiveForward? Forward, string? Error) AddTemporary(PortForwardRule rule)
    {
        if (PortForwards.Check(rule) is { } err) return (null, err);
        ActiveForward f;
        lock (_lock)
        {
            if (_items.Count >= PortForwards.MaxRules) return (null, L.F("포트 포워딩은 호스트마다 {0}개까지입니다.", PortForwards.MaxRules));
            if (_items.Any(x => x.Enabled && x.Rule.LocalPort == rule.LocalPort))
                return (null, L.F("로컬 포트 {0} 은(는) 이 탭의 다른 포워딩이 이미 씁니다.", rule.LocalPort));
            var r = rule.Clone();
            r.Enabled = true;
            r.TargetHost = PortForwards.NormalizeHost(r.TargetHost);
            f = new ActiveForward(_nextId++, r, temporary: true);
            _items.Add(f);
            if (_client != null) OpenLocked(f, _client);
            else f.State = ForwardState.Waiting;
        }
        Changed?.Invoke();
        return (f, null);
    }

    /// <summary>임시 규칙 지우기 (저장된 규칙은 끄기만 — 지우려면 호스트 편집)</summary>
    public bool Remove(ActiveForward f)
    {
        ForwardedPortLocal? stop;
        SshClient? client;
        lock (_lock)
        {
            if (!f.Temporary || !_items.Remove(f)) return false;
            stop = f.Port;
            f.Port = null;
            f.State = ForwardState.Off;
            client = _client;
        }
        if (stop != null) StopInBackground(new() { stop }, client);
        Changed?.Invoke();
        return true;
    }

    public ActiveForward? Find(int id) { lock (_lock) return _items.FirstOrDefault(f => f.Id == id); }

    // ---------- 내부 ----------

    private void OpenLocked(ActiveForward f, SshClient client)
    {
        if (f.Port != null) return;
        var r = f.Rule;
        ForwardedPortLocal? port = null;
        try
        {
            // 127.0.0.1 에만 — 다른 PC 가 이 터널로 서버 뒤에 들어오지 못하게
            port = new ForwardedPortLocal(PortForwards.BindAddress, (uint)r.LocalPort, r.TargetHost, (uint)r.TargetPort);
            port.RequestReceived += (_, _) => { lock (_lock) f.Connections++; Changed?.Invoke(); };
            // 연결(채널)마다의 오류 — 서버가 포워딩을 막음(AllowTcpForwarding no), 대상에 닿지 못함 등. 리스너는 계속 받는다.
            // 탭의 SSH 연결 오류(ErrorOccurred)로 번지지 않는다 (ForwardedPort 자체 이벤트)
            port.Exception += (_, e) =>
            {
                lock (_lock) { if (f.Port == port) f.Error = e.Exception.Message; }
                Changed?.Invoke();
            };
            client.AddForwardedPort(port);
            port.Start();
            f.Port = port;
            f.State = ForwardState.Open;
            f.Error = null;
        }
        catch (Exception ex)
        {
            if (port != null)
            {
                try { client.RemoveForwardedPort(port); } catch { }
                try { port.Dispose(); } catch { }
            }
            f.Port = null;
            f.State = ForwardState.Failed;
            f.Error = OpenError(ex, r.LocalPort);
        }
    }

    /// <summary>열기 실패 사유 (로컬 포트 사용 중·권한 없음은 알아보기 쉽게)</summary>
    internal static string OpenError(Exception ex, int localPort)
    {
        var sock = ex as SocketException ?? ex.InnerException as SocketException;
        return sock?.SocketErrorCode switch
        {
            SocketError.AddressAlreadyInUse => L.F("로컬 포트 {0} 이(가) 이미 사용 중입니다.", localPort),
            // Windows 가 예약한 포트 범위(Hyper-V·WSL 등)는 WSAEACCES 로 거부된다
            SocketError.AccessDenied => L.F("로컬 포트 {0} 을(를) 열 권한이 없습니다 (Windows 가 예약한 포트일 수 있음).", localPort),
            _ => ex.Message,
        };
    }

    private List<ForwardedPortLocal> TakePortsLocked()
    {
        var list = new List<ForwardedPortLocal>();
        foreach (var f in _items)
            if (f.Port != null) { list.Add(f.Port); f.Port = null; }
        return list;
    }

    /// <summary>
    /// 리스너 소켓은 바로 닫고(곧바로 다시 연결해 같은 포트를 열어도 겹치지 않게, 새 연결도 받지 않게),
    /// 열린 채널까지 정리하는 Stop 은 기다릴 수 있어 백그라운드에서
    /// </summary>
    private static void StopInBackground(List<ForwardedPortLocal> ports, SshClient? client)
    {
        if (ports.Count == 0) return;
        foreach (var p in ports) JumpTunnel.CloseListener(p);
        _ = Task.Run(() =>
        {
            foreach (var p in ports)
            {
                try { if (client != null) client.RemoveForwardedPort(p); else p.Stop(); } catch { }
                try { p.Dispose(); } catch { }
            }
        });
    }
}
