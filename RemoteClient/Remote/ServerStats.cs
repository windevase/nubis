using System;
using System.Collections.Generic;
using System.Globalization;

namespace Nubis;

/// <summary>/proc/stat 첫 줄(cpu 합계)의 누적 시간 (jiffies). Total = 앞 8개 항목 합(guest 제외), Idle = idle + iowait</summary>
public readonly record struct CpuTimes(ulong Total, ulong Idle);

/// <summary>네트워크 인터페이스 하나의 누적 수신·송신 바이트 (/proc/net/dev)</summary>
public readonly record struct NetCounters(ulong Rx, ulong Tx);

/// <summary>한 번 읽은 서버 상태 원본 (누적 카운터 — 속도·사용률은 두 샘플의 차이로 <see cref="ServerStats.Compute"/>)</summary>
public sealed record ServerSample(
    double? Uptime, CpuTimes? Cpu, long? MemTotal, long? MemAvailable, IReadOnlyDictionary<string, NetCounters> Net);

/// <summary>화면에 보일 값. 앞 샘플이 없거나(첫 읽기) 재부팅으로 카운터가 줄었으면 CPU·네트워크는 null</summary>
public sealed record ServerStats(double? CpuPercent, long? MemTotal, long? MemUsed, double? RxPerSec, double? TxPerSec)
{
    /// <summary>
    /// 서버에서 실행할 명령 — 읽기 전용, 고정 문자열 (사용자 입력이 섞이지 않음). 쉘 문법은 ';' 하나뿐이라 sh·bash·zsh·fish·csh 모두 된다.
    /// /proc/stat 은 첫 줄(cpu 합계)만 — 코어가 많은 서버는 intr 줄이 수십 KB 라 매번 받지 않는다.
    /// head 에 파일을 여럿 주면 "==> 파일 <==" 머리줄이 붙어 구역을 나눌 수 있다. 출력은 파일마다 256KB 로 자른다 (인터페이스가 아주 많은 노드)
    /// </summary>
    public const string LinuxCommand = "head -n 1 /proc/uptime /proc/stat; head -c 262144 /proc/meminfo /proc/net/dev";

    /// <summary>받은 출력이 이보다 크면 버린다 (명령이 이미 잘라 보내므로 정상이면 넘지 않음)</summary>
    public const int MaxOutputChars = 1024 * 1024;

    /// <summary>
    /// 명령 출력 → 샘플. CPU·메모리·네트워크 중 하나도 알아보지 못하면 null (제한 셸 · /proc 없는 OS 등).
    /// 일부만 있으면(컨테이너에 /proc/net/dev 없음 등) 있는 것만 채운다
    /// </summary>
    public static ServerSample? Parse(string? output)
    {
        if (string.IsNullOrEmpty(output)) return null;
        double? uptime = null;
        CpuTimes? cpu = null;
        long? memTotal = null, memAvail = null, memFree = null, buffers = null, cached = null, reclaim = null;
        var net = new Dictionary<string, NetCounters>(StringComparer.Ordinal);
        var section = "";
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("==> ", StringComparison.Ordinal) && line.EndsWith(" <==", StringComparison.Ordinal))
            {
                section = line[4..^4];
                continue;
            }
            if (line.Length == 0) continue;
            if (section == "/proc/uptime")
            {
                var first = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (uptime == null && first.Length > 0 && double.TryParse(first[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var u) && u >= 0)
                    uptime = u;
                continue;
            }
            if (cpu == null && (line.StartsWith("cpu ", StringComparison.Ordinal) || line.StartsWith("cpu\t", StringComparison.Ordinal)))
            {
                cpu = ParseCpu(line);
                continue;
            }
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            var rest = line[(colon + 1)..];
            switch (key)
            {
                case "MemTotal": memTotal ??= Kb(rest); continue;
                case "MemAvailable": memAvail ??= Kb(rest); continue;
                case "MemFree": memFree ??= Kb(rest); continue;
                case "Buffers": buffers ??= Kb(rest); continue;
                case "Cached": cached ??= Kb(rest); continue;
                case "SReclaimable": reclaim ??= Kb(rest); continue;
            }
            // /proc/net/dev: "  eth0: 수신바이트 패킷 ... (8개) 송신바이트 ..." — 숫자가 9개 이상인 줄만 (meminfo 는 "123 kB")
            if (key.Length == 0 || key.Contains('|') || key.Contains(' ')) continue;
            var f = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 9) continue;
            if (!ulong.TryParse(f[0], NumberStyles.None, CultureInfo.InvariantCulture, out var rx)
                || !ulong.TryParse(f[8], NumberStyles.None, CultureInfo.InvariantCulture, out var tx)) continue;
            net[key] = new NetCounters(rx, tx);
        }

        // MemAvailable 이 없는 옛 커널(3.14 이전)은 free + buffers + cached (+ 회수 가능한 slab) 로 어림한다
        if (memAvail == null && memFree != null)
            memAvail = memFree + (buffers ?? 0) + (cached ?? 0) + (reclaim ?? 0);
        if (memTotal is not > 0) { memTotal = null; memAvail = null; }
        else if (memAvail != null) memAvail = Math.Clamp(memAvail.Value, 0, memTotal.Value);

        if (cpu == null && memTotal == null && net.Count == 0) return null;
        return new ServerSample(uptime, cpu, memTotal, memAvail, net);
    }

    /// <summary>"cpu  user nice system idle iowait irq softirq steal guest guest_nice" — guest 는 user 에 이미 들어 있어 앞 8개만 더한다</summary>
    private static CpuTimes? ParseCpu(string line)
    {
        var f = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (f.Length < 5) return null;
        ulong total = 0, idle = 0;
        for (var i = 1; i < f.Length && i <= 8; i++)
        {
            if (!ulong.TryParse(f[i], NumberStyles.None, CultureInfo.InvariantCulture, out var v)) return null;
            total += v;
            if (i is 4 or 5) idle += v;   // idle, iowait
        }
        return new CpuTimes(total, idle);
    }

    private static long? Kb(string rest)
    {
        var f = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (f.Length == 0 || !long.TryParse(f[0], NumberStyles.None, CultureInfo.InvariantCulture, out var kb)) return null;
        return kb > long.MaxValue / 1024 ? null : kb * 1024;
    }

    /// <summary>
    /// 합계에서 뺄 인터페이스: 루프백과, 실제 NIC 트래픽을 한 번 더 세는 가상 인터페이스 —
    /// 컨테이너·가상 브리지(docker0·br-*·br0·vmbr0·veth* — 브리지 포트인 eth0 와 이중), 본딩·팀(bond0 = 슬레이브 eth0+eth1 합),
    /// VLAN(eth0.100 — eth0 에 이미 들어 있음), 터널·VPN(tun0·wg0 — 캡슐화된 트래픽이 eth0 에 또 잡힘), VM 탭(tap*·vnet*)
    /// </summary>
    public static bool IsExcludedInterface(string name)
    {
        if (name == "lo" || name.Contains('.') || name.Contains('@')) return true;
        foreach (var p in ExcludedPrefixes)
            if (name.StartsWith(p, StringComparison.Ordinal)) return true;
        return false;
    }

    private static readonly string[] ExcludedPrefixes =
    [
        "veth", "docker", "br", "vmbr", "virbr", "fwbr", "fwpr", "fwln", "cni", "cali", "flannel", "cilium", "lxc", "kube-", "vxlan", "geneve",
        "bond", "team", "vlan", "ovs-", "tun", "tap", "vnet", "wg", "tailscale", "zt", "ppp", "ipsec", "vti", "ip_vti", "ip6", "sit", "gre",
        "erspan", "tunl", "ipip", "dummy", "ifb", "nlmon",
    ];

    /// <summary>
    /// 두 샘플 → 표시 값. 간격은 서버 uptime 차이(명령 왕복 지연과 무관), 없으면 이 PC 에서 잰 경과 시간.
    /// uptime 이 줄었거나(재부팅) CPU 카운터가 줄었으면 CPU·네트워크는 null — 다음 샘플부터 다시 계산한다.
    /// 인터페이스 하나의 카운터만 줄었으면(인터페이스 재생성 · 32비트 카운터 랩어라운드) 이번 간격에서 그 인터페이스만 뺀다
    /// </summary>
    public static ServerStats Compute(ServerSample? prev, ServerSample cur, double localElapsedSec)
    {
        long? used = cur.MemTotal is { } t && cur.MemAvailable is { } a ? t - a : null;
        if (prev == null) return new ServerStats(null, cur.MemTotal, used, null, null);

        double dt;
        var rebooted = false;
        if (prev.Uptime is { } pu && cur.Uptime is { } cu)
        {
            rebooted = cu < pu;
            dt = cu - pu;
        }
        else dt = localElapsedSec;
        if (rebooted || !(dt > 0.2)) return new ServerStats(null, cur.MemTotal, used, null, null);

        double? cpuPct = null;
        if (prev.Cpu is { } pc && cur.Cpu is { } cc)
        {
            // uptime 이 없을 때만 CPU 합계 감소로 재부팅을 판단한다 — iowait 는 커널(NO_HZ)에 따라 조금 줄어들 수 있어
            // uptime 이 있으면 합계가 조금 줄어도 재부팅이 아니다. 항목별로 줄어든 만큼은 0 으로 본다 (top·htop 과 같음)
            if (prev.Uptime == null || cur.Uptime == null)
                if (cc.Total < pc.Total) return new ServerStats(null, cur.MemTotal, used, null, null);
            var dBusy = Math.Max(0.0, (double)(cc.Total - cc.Idle) - (pc.Total - pc.Idle));
            var dIdle = Math.Max(0.0, (double)cc.Idle - pc.Idle);
            if (dBusy + dIdle > 0) cpuPct = Math.Clamp(dBusy * 100.0 / (dBusy + dIdle), 0, 100);
        }

        double? rx = null, tx = null;
        if (cur.Net.Count > 0 && prev.Net.Count > 0)
        {
            // 실제 NIC 만 더한다. 남는 것이 없으면(가상 인터페이스뿐인 특이한 구성) lo 만 빼고 모두
            if (SumNet(prev, cur, IsExcludedInterface) is not { } sum)
                sum = SumNet(prev, cur, n => n == "lo") ?? default;
            if (sum.Any) { rx = sum.Rx / dt; tx = sum.Tx / dt; }
        }
        return new ServerStats(cpuPct, cur.MemTotal, used, rx, tx);
    }

    private static (ulong Rx, ulong Tx, bool Any)? SumNet(ServerSample prev, ServerSample cur, Func<string, bool> excluded)
    {
        ulong sumRx = 0, sumTx = 0;
        var any = false;
        foreach (var (name, c) in cur.Net)
        {
            if (excluded(name) || !prev.Net.TryGetValue(name, out var p)) continue;
            // 한쪽 카운터라도 줄었으면(인터페이스 재생성 · 랩어라운드) 이번 간격에서 그 인터페이스를 통째로 뺀다
            if (c.Rx < p.Rx || c.Tx < p.Tx) continue;
            any = true;
            sumRx += c.Rx - p.Rx;
            sumTx += c.Tx - p.Tx;
        }
        return any ? (sumRx, sumTx, true) : null;
    }

    /// <summary>바이트 → "512 B" · "1.2 KB" · "3.4 GB" (1024 단위, 화면 표시용 · 숫자 형식은 고정)</summary>
    public static string FormatBytes(double bytes)
    {
        if (double.IsNaN(bytes) || bytes < 0) bytes = 0;
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        var i = 0;
        while (bytes >= 1024 && i < units.Length - 1) { bytes /= 1024; i++; }
        var fmt = i == 0 ? "0" : bytes >= 100 ? "0" : bytes >= 10 ? "0.0" : "0.00";
        return bytes.ToString(fmt, CultureInfo.InvariantCulture) + " " + units[i];
    }

    /// <summary>초당 바이트 → "12.3 KB/s"</summary>
    public static string FormatRate(double bytesPerSec) => FormatBytes(bytesPerSec) + "/s";

    /// <summary>MCP·도구 설명용 한 줄 요약 (값이 없으면 "—")</summary>
    public string Describe()
    {
        var inv = CultureInfo.InvariantCulture;
        var cpu = CpuPercent is { } c ? c.ToString("0.0", inv) + "%" : "—";
        var mem = MemTotal is { } t && MemUsed is { } u
            ? $"{FormatBytes(u)} / {FormatBytes(t)} ({(u * 100.0 / t).ToString("0", inv)}%)" : "—";
        var net = RxPerSec is { } r && TxPerSec is { } x ? $"수신 {FormatRate(r)} · 송신 {FormatRate(x)}" : "—";
        return $"CPU {cpu} · 메모리 {mem} · 네트워크 {net}";
    }
}

/// <summary>스파크라인용 최근 값 (고정 개수 — 넘치면 가장 오래된 값을 버린다. 저장하지 않는다)</summary>
public sealed class RecentValues(int capacity)
{
    private readonly double[] _buf = new double[Math.Max(1, capacity)];
    private int _start, _count;

    public int Count => _count;
    public int Capacity => _buf.Length;

    public void Add(double v)
    {
        if (_count < _buf.Length) _buf[(_start + _count++) % _buf.Length] = v;
        else { _buf[_start] = v; _start = (_start + 1) % _buf.Length; }
    }

    public void Clear() { _start = 0; _count = 0; }

    /// <summary>오래된 것부터</summary>
    public double this[int i] => _buf[(_start + i) % _buf.Length];

    public double Max()
    {
        var m = 0.0;
        for (var i = 0; i < _count; i++) m = Math.Max(m, this[i]);
        return m;
    }
}
