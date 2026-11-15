using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>MCP 서버가 오래 많은 요청을 받아도 요청별 상태(처리 중 목록·선취소 기록)가 남지 않는지</summary>
public class LeakMcpTests
{
    private static McpServer NewServer(TaskCompletionSource? started = null) =>
        new(new[]
        {
            new McpTool("echo", "인자 종류를 돌려준다", new { type = "object" },
                (Func<JsonElement, Task<string>>)(a => Task.FromResult(a.ValueKind.ToString()))),
            new McpTool("wait", "취소될 때까지 기다린다", new { type = "object" }, (Func<JsonElement, Task<string>>)(async _ =>
            {
                started?.TrySetResult();
                await Task.Delay(Timeout.Infinite, McpRequest.Token);
                return "끝";
            })),
        }, "");

    private static HttpClient Client(McpServer s)
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {s.Token}");
        return c;
    }

    private static Task<HttpResponseMessage> Post(HttpClient c, McpServer s, string json) =>
        c.PostAsync(s.Url, new StringContent(json, Encoding.UTF8, "application/json"));

    private static string Call(int id, string tool, bool withArgs = true) =>
        $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"tools/call\",\"params\":{{\"name\":\"{tool}\"{(withArgs ? ",\"arguments\":{}" : "")}}}}}";

    private static string Cancel(int id) =>
        $"{{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{{\"requestId\":{id}}}}}";

    private static string ResultText(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString() ?? "";
    }

    [Fact]
    public async Task 많은_요청과_취소_뒤에도_처리_중_목록이_비어_있다()
    {
        using var s = NewServer();
        using var c = Client(s);
        // 일반 요청 (arguments 생략 포함)
        var calls = Enumerable.Range(1, 100).Select(i => Post(c, s, Call(i, "echo", withArgs: i % 2 == 0))).ToArray();
        foreach (var r in await Task.WhenAll(calls))
            Assert.Equal("Object", ResultText(await r.Content.ReadAsStringAsync()));
        // 요청보다 먼저 온 취소 → 요청이 바로 취소되고 기록이 지워진다
        for (var i = 1000; i < 1020; i++)
        {
            await Post(c, s, Cancel(i));
            var body = await (await Post(c, s, Call(i, "wait"))).Content.ReadAsStringAsync();
            Assert.Contains("요청이 취소되어 중단했습니다.", ResultText(body));
        }
        // 처리 중에 온 취소
        for (var i = 2000; i < 2010; i++)
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var s2 = NewServer(started);
            using var c2 = Client(s2);
            var call = Post(c2, s2, Call(i, "wait"));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Post(c2, s2, Cancel(i));
            await (await call).Content.ReadAsStringAsync();
            Assert.Equal(0, s2.InflightCount);
            Assert.Equal(0, s2.EarlyCancelCount);
        }
        Assert.Equal(0, s.InflightCount);
        Assert.Equal(0, s.EarlyCancelCount);
    }

    [Fact]
    public async Task 끝난_요청에_대한_늦은_취소_알림은_한도_이상_쌓이지_않는다()
    {
        using var s = NewServer();
        using var c = Client(s);
        for (var i = 0; i < McpServer.MaxEarlyCancels + 100; i++)
            Assert.Equal(HttpStatusCode.Accepted, (await Post(c, s, Cancel(5000 + i))).StatusCode);
        Assert.True(s.EarlyCancelCount <= McpServer.MaxEarlyCancels, $"선취소 기록 {s.EarlyCancelCount}개");
        // 가장 최근 알림은 남아 있어 그 id 의 요청은 여전히 취소된다
        var body = await (await Post(c, s, Call(5000 + McpServer.MaxEarlyCancels + 99, "wait"))).Content.ReadAsStringAsync();
        Assert.Contains("요청이 취소되어 중단했습니다.", ResultText(body));
    }

    [Fact]
    public async Task Dispose_는_처리_중인_요청을_취소하고_기록을_비운다()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var s = NewServer(started);
        using var c = Client(s);
        await Post(c, s, Cancel(9999));
        var call = Post(c, s, Call(1, "wait"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        s.Dispose();
        // 응답은 리스너가 닫혀 못 받을 수 있다 — 핸들러가 취소돼 처리 중 목록에서 빠지는 것만 확인
        try { await call; } catch { }
        for (var i = 0; i < 50 && s.InflightCount > 0; i++) await Task.Delay(100);
        Assert.Equal(0, s.InflightCount);
        Assert.Equal(0, s.EarlyCancelCount);
    }
}
