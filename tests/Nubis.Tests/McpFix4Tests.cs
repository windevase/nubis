using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nubis;
using Xunit;

namespace Nubis.Tests;

public class McpFix4Tests
{
    // ---------- claude 작업 폴더의 프로젝트 설정 · 옛 데이터 폴더 보호 ----------

    [Fact]
    public void claude_작업_폴더의_프로젝트_설정은_고치지_못한다()
    {
        var rules = ClaudeGuard.DenyRules(@"C:\Users\a\AppData\Roaming\Nubis\users\github_1", @"C:\Users\a\AppData\Roaming\Nubis");
        Assert.Contains("Edit(//c/Users/a/AppData/Roaming/Nubis/users/github_1/claude/.claude/**)", rules);
        Assert.Contains("Edit(//c/Users/a/AppData/Roaming/Nubis/users/github_1/claude/.mcp.json)", rules);
        Assert.Contains("Edit(//c/Users/a/AppData/Roaming/Nubis/claude/.claude/**)", rules);
        Assert.Contains("Edit(//c/Users/a/AppData/Roaming/Nubis/users/**/claude/.mcp.json)", rules);
    }

    [Fact]
    public void 옛_데이터_폴더도_같은_규칙으로_보호한다()
    {
        var rules = ClaudeGuard.DenyRules(@"C:\Users\a\AppData\Roaming\Nubis", null, @"C:\Users\a\AppData\Roaming\RemoteClient");
        Assert.Contains("Read(//c/Users/a/AppData/Roaming/RemoteClient/hosts.json*)", rules);
        Assert.Contains("Read(//c/Users/a/AppData/Roaming/RemoteClient/vault.json*)", rules);
        Assert.Contains("Edit(//c/Users/a/AppData/Roaming/RemoteClient/settings.json*)", rules);
        Assert.Contains("Edit(//c/Users/a/AppData/Roaming/RemoteClient/claude/.claude/**)", rules);
        // 생략하면 넣지 않는다 (기존 호출 그대로)
        Assert.DoesNotContain(ClaudeGuard.DenyRules(@"C:\Users\a\AppData\Roaming\Nubis"), r => r.Contains("/RemoteClient/"));
        // settings JSON 에도 같은 규칙이 들어간다
        using var doc = JsonDocument.Parse(ClaudeGuard.BuildSettingsJson(@"C:\Users\a\AppData\Roaming\Nubis", null, null, @"C:\Users\a\AppData\Roaming\RemoteClient"));
        Assert.Equal(rules.Count, doc.RootElement.GetProperty("permissions").GetProperty("deny").GetArrayLength());
        Assert.EndsWith(@"\RemoteClient", ClaudeGuard.LegacyDataDir);
    }

    // ---------- MCP 서버: 토큰 · 취소 ----------

    private static McpServer NewServer(TaskCompletionSource started) =>
        new(new[]
        {
            new McpTool("wait", "취소될 때까지 기다린다", new { type = "object" }, (Func<JsonElement, Task<string>>)(async _ =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, McpRequest.Token);
                return "끝";
            })),
        }, "");

    private static HttpClient Client(McpServer s, string? token = null)
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {token ?? s.Token}");
        return c;
    }

    private static Task<HttpResponseMessage> Post(HttpClient c, McpServer s, string json) =>
        c.PostAsync(s.Url, new StringContent(json, Encoding.UTF8, "application/json"));

    // 응답 JSON 은 한글을 \uXXXX 로 이스케이프하므로 파싱해서 도구 결과 텍스트를 꺼낸다
    private static string ResultText(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString() ?? "";
    }

    private const string CallWait = "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{\"name\":\"wait\",\"arguments\":{}}}";

    [Fact]
    public async Task 토큰이_다르면_401()
    {
        using var s = NewServer(new TaskCompletionSource());
        using var bad = Client(s, "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(bad, s, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}")).StatusCode);
        using var noHeader = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(noHeader, s, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}")).StatusCode);
        using var ok = Client(s);
        Assert.Equal(HttpStatusCode.OK, (await Post(ok, s, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}")).StatusCode);
    }

    [Fact]
    public async Task 요청보다_먼저_온_취소_알림도_반영한다()
    {
        using var s = NewServer(new TaskCompletionSource());
        using var c = Client(s);
        var n = await Post(c, s, "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/cancelled\",\"params\":{\"requestId\":7}}");
        Assert.Equal(HttpStatusCode.Accepted, n.StatusCode);
        var body = await (await Post(c, s, CallWait)).Content.ReadAsStringAsync();
        Assert.Contains("요청이 취소되어 중단했습니다.", ResultText(body));
    }

    [Fact]
    public async Task CancelAll_은_처리_중인_요청을_모두_취소한다()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var s = NewServer(started);
        using var c = Client(s);
        var call = Post(c, s, CallWait);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        s.CancelAll();
        var body = await (await call).Content.ReadAsStringAsync();
        Assert.Contains("요청이 취소되어 중단했습니다.", ResultText(body));
    }
}
