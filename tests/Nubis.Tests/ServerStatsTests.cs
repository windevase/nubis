using System;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>SSH 탭 서버 모니터: /proc 출력 파싱 · 두 샘플 차이 계산 · 표시 형식 (ServerStats, 연결 없이)</summary>
public class ServerStatsTests
{
    /// <summary>LinuxCommand 의 실제 출력 모양 (head 가 파일마다 "==> 파일 <==" 머리줄을 붙임)</summary>
    private static string Output(double uptime, ulong user, ulong system, ulong idle, ulong iowait,
        ulong ethRx, ulong ethTx, ulong loRx = 5000, long memTotalKb = 4_000_000, long memAvailKb = 3_000_000,
        string? extraNet = null) =>
        "==> /proc/uptime <==\n" +
        $"{uptime.ToString(System.Globalization.CultureInfo.InvariantCulture)} 1234.56\n" +
        "\n==> /proc/stat <==\n" +
        $"cpu  {user} 0 {system} {idle} {iowait} 0 0 0 0 0\n" +
        "==> /proc/meminfo <==\n" +
        $"MemTotal:       {memTotalKb} kB\n" +
        "MemFree:          100000 kB\n" +
        $"MemAvailable:   {memAvailKb} kB\n" +
        "Buffers:           20000 kB\n" +
        "HugePages_Total:       0\n" +
        "\n==> /proc/net/dev <==\n" +
        "Inter-|   Receive                                                |  Transmit\n" +
        " face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed\n" +
        $"    lo: {loRx} 10 0 0 0 0 0 0 {loRx} 10 0 0 0 0 0 0\n" +
        $"  eth0: {ethRx} 100 0 0 0 0 0 0 {ethTx} 90 0 0 0 0 0 0\n" +
        (extraNet ?? "");

    [Fact]
    public void 정상_출력_파싱()
    {
        var s = ServerStats.Parse(Output(1000.5, 100, 50, 800, 50, 10_000, 2_000));
        Assert.NotNull(s);
        Assert.Equal(1000.5, s!.Uptime);
        Assert.Equal(new CpuTimes(1000, 850), s.Cpu);
        Assert.Equal(4_000_000L * 1024, s.MemTotal);
        Assert.Equal(3_000_000L * 1024, s.MemAvailable);
        Assert.Equal(2, s.Net.Count);
        Assert.Equal(new NetCounters(10_000, 2_000), s.Net["eth0"]);
    }

    [Fact]
    public void 첫_샘플은_메모리만()
    {
        var s = ServerStats.Compute(null, ServerStats.Parse(Output(10, 1, 1, 1, 0, 1, 1))!, 0);
        Assert.Null(s.CpuPercent);
        Assert.Null(s.RxPerSec);
        Assert.Equal(1_000_000L * 1024, s.MemUsed);
        Assert.Equal(4_000_000L * 1024, s.MemTotal);
    }

    [Fact]
    public void 두_샘플_차이로_CPU와_속도()
    {
        var a = ServerStats.Parse(Output(100, 100, 100, 700, 100, 10_000, 5_000))!;
        // 2초 뒤: 전체 +200 (user +30, system +20, idle +140, iowait +10) → 사용 50/200 = 25%
        var b = ServerStats.Parse(Output(102, 130, 120, 840, 110, 30_000, 9_000, loRx: 999_999_999))!;
        var s = ServerStats.Compute(a, b, localElapsedSec: 5);   // 간격은 서버 uptime 차이(2초)를 쓴다
        Assert.Equal(25, s.CpuPercent!.Value, 3);
        Assert.Equal(10_000, s.RxPerSec!.Value, 3);   // lo 는 제외
        Assert.Equal(2_000, s.TxPerSec!.Value, 3);
    }

    [Fact]
    public void uptime_이_없으면_이_PC_경과_시간()
    {
        var a = ServerStats.Parse(Output(100, 100, 100, 700, 100, 0, 0).Replace("==> /proc/uptime <==\n100 1234.56\n", ""))!;
        var b = ServerStats.Parse(Output(100, 150, 150, 750, 150, 4_000, 0).Replace("==> /proc/uptime <==\n100 1234.56\n", ""))!;
        Assert.Null(a.Uptime);
        var s = ServerStats.Compute(a, b, localElapsedSec: 4);
        Assert.Equal(1_000, s.RxPerSec!.Value, 3);
        Assert.Equal(50, s.CpuPercent!.Value, 3);
    }

    [Fact]
    public void 재부팅으로_카운터가_줄면_값을_내지_않는다()
    {
        var a = ServerStats.Parse(Output(50_000, 9_000, 9_000, 90_000, 100, 9_000_000, 9_000_000))!;
        var b = ServerStats.Parse(Output(30, 10, 10, 100, 1, 1_000, 1_000))!;
        var s = ServerStats.Compute(a, b, 2);
        Assert.Null(s.CpuPercent);
        Assert.Null(s.RxPerSec);
        Assert.NotNull(s.MemUsed);   // 메모리는 그 자리 값이라 그대로

        // uptime 이 없어도 CPU 카운터 감소로 재부팅을 알아본다
        var a2 = a with { Uptime = null };
        var b2 = b with { Uptime = null };
        var s2 = ServerStats.Compute(a2, b2, 2);
        Assert.Null(s2.CpuPercent);
        Assert.Null(s2.RxPerSec);
    }

    [Fact]
    public void 인터페이스_하나의_카운터만_줄면_그_인터페이스만_뺀다()
    {
        // eth1 이 다시 만들어졌거나 32비트 카운터가 넘침 — 큰 음수/거대한 값이 나오지 않게 그 간격에서만 뺀다
        var a = ServerStats.Parse(Output(100, 1, 1, 1, 0, 1_000, 1_000, extraNet: "  eth1: 4294967000 1 0 0 0 0 0 0 4294967000 1 0 0 0 0 0 0\n"))!;
        var b = ServerStats.Parse(Output(101, 2, 2, 2, 0, 3_000, 2_000, extraNet: "  eth1: 500 1 0 0 0 0 0 0 600 1 0 0 0 0 0 0\n"))!;
        var s = ServerStats.Compute(a, b, 1);
        Assert.Equal(2_000, s.RxPerSec!.Value, 3);
        Assert.Equal(1_000, s.TxPerSec!.Value, 3);
    }

    [Fact]
    public void 새로_생긴_인터페이스와_컨테이너_가상_인터페이스는_세지_않는다()
    {
        var a = ServerStats.Parse(Output(100, 1, 1, 1, 0, 1_000, 1_000))!;
        var b = ServerStats.Parse(Output(101, 2, 2, 2, 0, 1_000, 1_000,
            extraNet: "  wg0: 50000 1 0 0 0 0 0 0 50000 1 0 0 0 0 0 0\n" +
                      "docker0: 7000 1 0 0 0 0 0 0 7000 1 0 0 0 0 0 0\n" +
                      "vethab12: 7000 1 0 0 0 0 0 0 7000 1 0 0 0 0 0 0\n"))!;
        var s = ServerStats.Compute(a, b, 1);
        Assert.Equal(0, s.RxPerSec!.Value);
        Assert.True(ServerStats.IsExcludedInterface("lo"));
        Assert.True(ServerStats.IsExcludedInterface("br-1a2b"));
        Assert.True(ServerStats.IsExcludedInterface("cali1234"));
        Assert.False(ServerStats.IsExcludedInterface("eth0"));
        Assert.False(ServerStats.IsExcludedInterface("ens5"));
        Assert.False(ServerStats.IsExcludedInterface("bond0"));
    }

    [Fact]
    public void 네트워크_정보가_없으면_속도는_비고_나머지는_있다()
    {
        var text = "==> /proc/uptime <==\n5 5\n==> /proc/stat <==\ncpu  1 0 1 10 0 0 0 0\n==> /proc/meminfo <==\nMemTotal: 1000 kB\nMemAvailable: 400 kB\n" +
                   "head: cannot open '/proc/net/dev' for reading: No such file or directory\n";
        var a = ServerStats.Parse(text)!;
        var b = ServerStats.Parse(text.Replace("5 5", "7 5").Replace("cpu  1 0 1 10", "cpu  3 0 1 12"))!;
        Assert.Empty(b.Net);
        var s = ServerStats.Compute(a, b, 2);
        Assert.Null(s.RxPerSec);
        Assert.Equal(50, s.CpuPercent!.Value, 3);
        Assert.Equal(600L * 1024, s.MemUsed);
    }

    [Fact]
    public void MemAvailable_없는_옛_커널은_어림한다()
    {
        var s = ServerStats.Parse("MemTotal: 1000 kB\nMemFree: 100 kB\nBuffers: 50 kB\nCached: 250 kB\n")!;
        Assert.Equal(400L * 1024, s.MemAvailable);
        Assert.Null(s.Cpu);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Welcome to Rebex Virtual Shell!\r\nUnknown command 'head'.\r\n")]
    [InlineData("head: cannot open '/proc/uptime' for reading: No such file or directory\n")]
    [InlineData("cpu  abc def\nMemTotal: x kB\n  eth0: 1 2 3\n")]
    [InlineData("\0\0\0garbage==> <==\n:::\n")]
    public void 알아볼_수_없는_출력은_null(string? text) => Assert.Null(ServerStats.Parse(text));

    [Fact]
    public void 잘린_줄과_CRLF_도_견딘다()
    {
        var text = Output(10, 1, 1, 1, 0, 100, 100).Replace("\n", "\r\n") + "  eth9: 12 3";   // 마지막 줄이 잘림 (head -c 256KB)
        var s = ServerStats.Parse(text)!;
        Assert.Equal(10, s.Uptime);
        Assert.False(s.Net.ContainsKey("eth9"));
        Assert.True(s.Net.ContainsKey("eth0"));
    }

    [Fact]
    public void 사용률은_0에서_100_사이()
    {
        // idle 증가가 전체 증가보다 큰 이상한 카운터 (iowait 보정 등)
        var a = new ServerSample(1, new CpuTimes(100, 50), null, null, new System.Collections.Generic.Dictionary<string, NetCounters>());
        var b = a with { Uptime = 3, Cpu = new CpuTimes(110, 70) };
        Assert.Equal(0, ServerStats.Compute(a, b, 2).CpuPercent);
        // 간격이 너무 짧으면 값을 내지 않는다
        Assert.Null(ServerStats.Compute(a, a with { Uptime = 1.1, Cpu = new CpuTimes(120, 60) }, 0.1).CpuPercent);
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1536, "1.50 KB")]
    [InlineData(12_345, "12.1 KB")]
    [InlineData(500_000, "488 KB")]
    [InlineData(5L * 1024 * 1024 * 1024, "5.00 GB")]
    [InlineData(-5, "0 B")]
    public void 바이트_표시(long bytes, string expected) => Assert.Equal(expected, ServerStats.FormatBytes(bytes));

    [Fact]
    public void 속도_표시와_요약()
    {
        Assert.Equal("1.00 MB/s", ServerStats.FormatRate(1024 * 1024));
        var d = new ServerStats(12.34, 2048, 1024, 100, 2048).Describe();
        Assert.Contains("CPU 12.3%", d);
        Assert.Contains("1.00 KB / 2.00 KB (50%)", d);
        Assert.Contains("송신 2.00 KB/s", d);
        Assert.Contains("CPU —", new ServerStats(null, null, null, null, null).Describe());
    }

    [Fact]
    public void 명령은_고정된_읽기_전용()
    {
        // 사용자 입력이 섞이지 않는 상수 — /proc 읽기만. 리다이렉트·치환·파이프가 없다
        Assert.StartsWith("head ", ServerStats.LinuxCommand);
        Assert.DoesNotContain(">", ServerStats.LinuxCommand);
        Assert.DoesNotContain("|", ServerStats.LinuxCommand);
        Assert.DoesNotContain("$", ServerStats.LinuxCommand);
        Assert.DoesNotContain("`", ServerStats.LinuxCommand);
    }

    [Fact]
    public void 최근_값은_정해진_개수만()
    {
        var r = new RecentValues(3);
        for (var i = 1; i <= 5; i++) r.Add(i);
        Assert.Equal(3, r.Count);
        Assert.Equal(3, r[0]);
        Assert.Equal(5, r[2]);
        Assert.Equal(5, r.Max());
        r.Clear();
        Assert.Equal(0, r.Count);
        Assert.Equal(0, r.Max());
    }
}
