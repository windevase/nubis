using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>SSH 로컬 포트 포워딩(ssh -L): 규칙 검증·표시 (PortForwards) · 탭의 포워딩 묶음 상태 (SshPortForwardSet, 연결 없이)</summary>
public class PortForwardTests
{
    private static PortForwardRule R(int local, string host, int port, bool on = true, string? name = null) =>
        new() { LocalPort = local, TargetHost = host, TargetPort = port, Enabled = on, Name = name };

    [Theory]
    [InlineData("localhost")]
    [InlineData("db")]
    [InlineData("db.internal.example.com")]
    [InlineData("10.0.0.5")]
    [InlineData("my_host-01")]
    [InlineData("::1")]
    [InlineData("[fe80::1]")]
    [InlineData("  db  ")]
    public void 대상_호스트_허용(string host) => Assert.Null(PortForwards.CheckTargetHost(host));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("db server")]
    [InlineData("user@db")]
    [InlineData("db:5432")]
    [InlineData("db/admin")]
    [InlineData("-oProxyCommand=x")]
    [InlineData("db.")]
    [InlineData("a..b")]
    [InlineData("db\n")]
    [InlineData("d‮b")]
    [InlineData("1.2.3.4:80")]
    [InlineData("[::1]:80")]
    [InlineData("fe80::1%eth0")]
    [InlineData("::g")]
    [InlineData("호스트")]
    public void 대상_호스트_거부(string host) => Assert.NotNull(PortForwards.CheckTargetHost(host));

    [Fact]
    public void 대상_호스트_길이_제한() =>
        Assert.NotNull(PortForwards.CheckTargetHost(new string('a', PortForwards.MaxHostLength + 1)));

    [Theory]
    [InlineData("1", 1)]
    [InlineData("65535", 65535)]
    [InlineData(" 15432 ", 15432)]
    public void 포트_허용(string text, int expected)
    {
        Assert.Null(PortForwards.ParsePort(text, out var p, local: true));
        Assert.Equal(expected, p);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-1")]
    [InlineData("+80")]
    [InlineData("80a")]
    [InlineData("8 0")]
    [InlineData("99999999999")]
    public void 포트_거부(string text) => Assert.NotNull(PortForwards.ParsePort(text, out _, local: false));

    [Fact]
    public void 입력칸_파싱_대상_비우면_localhost_이름_정리()
    {
        var (r, err) = PortForwards.Parse("15432", "", "5432", "  prod-db ");
        Assert.Null(err);
        Assert.Equal(15432, r!.LocalPort);
        Assert.Equal("localhost", r.TargetHost);
        Assert.Equal(5432, r.TargetPort);
        Assert.Equal("prod-db", r.Name);
        Assert.True(r.Enabled);

        (r, _) = PortForwards.Parse("8080", " [::1] ", "80", " ");
        Assert.Equal("::1", r!.TargetHost);
        Assert.Null(r.Name);
    }

    [Fact]
    public void 입력칸_파싱_오류()
    {
        Assert.NotNull(PortForwards.Parse("", "db", "5432", null).Error);
        Assert.NotNull(PortForwards.Parse("15432", "db x", "5432", null).Error);
        Assert.NotNull(PortForwards.Parse("15432", "db", "0", null).Error);
        Assert.NotNull(PortForwards.Parse("15432", "db", "5432", new string('n', PortForwards.MaxNameLength + 1)).Error);
        Assert.NotNull(PortForwards.Parse("15432", "db", "5432", "a\u0007b").Error);
    }

    [Fact]
    public void 목록_켜진_규칙끼리_로컬_포트_중복_거부()
    {
        Assert.NotNull(PortForwards.CheckList(new[] { R(15432, "db", 5432), R(15432, "db2", 5432) }));
        // 꺼 둔 규칙은 같은 포트를 써도 된다 (번갈아 켜기)
        Assert.Null(PortForwards.CheckList(new[] { R(15432, "db", 5432), R(15432, "db2", 5432, on: false) }));
        Assert.Null(PortForwards.CheckList(new[] { R(15432, "db", 5432), R(13306, "db", 3306) }));
        Assert.Null(PortForwards.CheckList(Array.Empty<PortForwardRule>()));
    }

    [Fact]
    public void 목록_규칙마다_검사_개수_제한()
    {
        Assert.NotNull(PortForwards.CheckList(new[] { R(0, "db", 5432) }));
        Assert.NotNull(PortForwards.CheckList(new[] { R(1, "db x", 5432) }));
        var many = Enumerable.Range(0, PortForwards.MaxRules + 1).Select(i => R(20000 + i, "db", 80)).ToList();
        Assert.NotNull(PortForwards.CheckList(many));
        Assert.Null(PortForwards.CheckList(many.Take(PortForwards.MaxRules).ToList()));
    }

    [Fact]
    public void 표시_모양()
    {
        Assert.Equal("127.0.0.1:15432 → db:5432", PortForwards.Describe(R(15432, "db", 5432)));
        Assert.Equal("127.0.0.1:8080 → [::1]:80", PortForwards.Describe(R(8080, "::1", 80)));
        Assert.Equal("localhost:8080 → pod/web:80", PortForwards.Arrow("localhost:8080", "pod/web:80"));
    }

    [Fact]
    public void 바인드는_루프백만() => Assert.Equal("127.0.0.1", PortForwards.BindAddress);

    [Theory]
    [InlineData(18080, 8080, null, "http")]
    [InlineData(18443, 443, null, "https")]
    [InlineData(15432, 5432, null, null)]
    [InlineData(13306, 3306, "admin web", "http")]
    [InlineData(19000, 9999, "grafana https", "https")]
    [InlineData(8080, 22, null, "http")]
    public void 브라우저로_열_수_있어_보이는지(int local, int port, string? name, string? scheme)
    {
        var r = R(local, "localhost", port, name: name);
        Assert.Equal(scheme, PortForwards.GuessScheme(r));
        Assert.Equal(scheme == null ? null : $"{scheme}://127.0.0.1:{local}/", PortForwards.BrowserUrl(r));
    }

    [Fact]
    public void 호스트_복제는_규칙을_깊게_복사()
    {
        var h = new HostEntry { PortForwards = new() { R(15432, "db", 5432) } };
        var c = h.Clone();
        c.PortForwards![0].LocalPort = 1;
        c.PortForwards.Add(R(2, "x", 2));
        Assert.Equal(15432, h.PortForwards[0].LocalPort);
        Assert.Single(h.PortForwards);
        Assert.Null(new HostEntry().Clone().PortForwards);
    }

    [Fact]
    public void 호스트_JSON_왕복()
    {
        var h = new HostEntry { Label = "x", PortForwards = new() { R(15432, "db", 5432, on: false, name: "prod") } };
        var back = JsonSerializer.Deserialize<HostEntry>(JsonSerializer.Serialize(h))!;
        var r = Assert.Single(back.PortForwards!);
        Assert.Equal((15432, "db", 5432, false, "prod"), (r.LocalPort, r.TargetHost, r.TargetPort, r.Enabled, r.Name));
        // 예전 파일(필드 없음)은 null
        Assert.Null(JsonSerializer.Deserialize<HostEntry>("{\"Label\":\"a\"}")!.PortForwards);
    }

    [Fact]
    public void 스냅숏은_켜기와_이름도_구분()
    {
        var a = new[] { R(1, "db", 2) };
        Assert.NotEqual(PortForwards.Snapshot(a), PortForwards.Snapshot(new[] { R(1, "db", 2, on: false) }));
        Assert.NotEqual(PortForwards.Snapshot(a), PortForwards.Snapshot(new[] { R(1, "db", 2, name: "n") }));
        Assert.Equal(PortForwards.Snapshot(a), PortForwards.Snapshot(new[] { R(1, "db", 2) }));
        Assert.Equal("", PortForwards.Snapshot(null));
        Assert.Null(PortForwards.CloneList(new List<PortForwardRule>()));
    }

    [Fact]
    public void 열기_실패_사유()
    {
        Assert.Contains("15432", SshPortForwardSet.OpenError(new SocketException((int)SocketError.AddressAlreadyInUse), 15432));
        Assert.Contains("15432", SshPortForwardSet.OpenError(new SocketException((int)SocketError.AccessDenied), 15432));
        Assert.Equal("boom", SshPortForwardSet.OpenError(new InvalidOperationException("boom"), 1));
    }

    // ---------- SshPortForwardSet (연결 없이) ----------

    [Fact]
    public void 연결_전에는_켜진_규칙이_대기()
    {
        var set = new SshPortForwardSet(new[] { R(15432, "db", 5432), R(13306, "db", 3306, on: false) });
        var items = set.Items;
        Assert.Equal(ForwardState.Waiting, items[0].State);
        Assert.Equal(ForwardState.Off, items[1].State);
        Assert.False(set.IsAttached);
        Assert.Equal(0, set.OpenCount);
    }

    [Fact]
    public void 저장된_규칙을_다시_반영해도_탭의_켜기와_임시_규칙은_유지()
    {
        var set = new SshPortForwardSet(new[] { R(15432, "db", 5432), R(13306, "db", 3306) });
        var first = set.Items[0];
        Assert.Null(set.SetEnabled(first, false));
        var (temp, err) = set.AddTemporary(R(18080, "web", 80));
        Assert.Null(err);
        Assert.True(temp!.Temporary);

        // 호스트 편집에서 이름을 바꾸고 3306 규칙을 지우고 새 규칙을 추가함
        set.SetSavedRules(new[] { R(15432, "db", 5432, name: "renamed"), R(16379, "redis", 6379) });
        var items = set.Items;
        Assert.Equal(3, items.Count);
        Assert.Same(first, items[0]);
        Assert.False(items[0].Enabled);   // 탭에서 끈 값 유지
        Assert.Equal("renamed", items[0].Rule.Name);
        Assert.Equal(16379, items[1].Rule.LocalPort);
        Assert.Same(temp, items[2]);
        Assert.DoesNotContain(items, f => f.Rule.LocalPort == 13306);
    }

    [Fact]
    public void 임시_추가는_검사하고_켜진_로컬_포트와_겹치면_거부()
    {
        var set = new SshPortForwardSet(new[] { R(15432, "db", 5432) });
        Assert.NotNull(set.AddTemporary(R(15432, "other", 1)).Error);
        Assert.NotNull(set.AddTemporary(R(0, "db", 1)).Error);
        Assert.NotNull(set.AddTemporary(R(1, "db x", 1)).Error);
        // 저장된 규칙을 끄면 그 포트를 임시로 쓸 수 있다
        Assert.Null(set.SetEnabled(set.Items[0], false));
        var (f, err) = set.AddTemporary(R(15432, "other", 1, on: false));
        Assert.Null(err);
        Assert.True(f!.Enabled);   // 임시 규칙은 늘 켜진 채로 추가
        // 이제 저장된 규칙을 다시 켜면 겹친다
        Assert.NotNull(set.SetEnabled(set.Items[0], true));
    }

    [Fact]
    public void 지우기는_임시_규칙만()
    {
        var set = new SshPortForwardSet(new[] { R(15432, "db", 5432) });
        Assert.False(set.Remove(set.Items[0]));
        var (f, _) = set.AddTemporary(R(18080, "web", 80));
        var changed = 0;
        set.Changed += () => changed++;
        Assert.True(set.Remove(f!));
        Assert.Single(set.Items);
        Assert.Equal(1, changed);
        Assert.Null(set.Find(f!.Id));
    }

    [Fact]
    public void 포워딩_id는_탭_안에서_겹치지_않는다()
    {
        var set = new SshPortForwardSet(new[] { R(1, "a", 1), R(2, "a", 2) });
        set.AddTemporary(R(3, "a", 3));
        set.SetSavedRules(new[] { R(4, "a", 4) });
        Assert.Equal(set.Items.Count, set.Items.Select(f => f.Id).Distinct().Count());
        Assert.Equal(new[] { 4, 3 }, set.Items.Select(f => f.Rule.LocalPort));
        Assert.DoesNotContain(set.Items, f => f.Id is 1 or 2);
    }

    [Fact]
    public void 연결_없이_끊기는_아무것도_하지_않는다()
    {
        var set = new SshPortForwardSet(new[] { R(1, "a", 1) });
        var changed = 0;
        set.Changed += () => changed++;
        set.Detach();
        Assert.Equal(0, changed);
        Assert.Equal(ForwardState.Waiting, set.Items[0].State);
    }
}
