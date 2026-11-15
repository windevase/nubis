using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Nubis;
using Xunit;

namespace Nubis.Tests;

/// <summary>3차 점검: MCP 서버 메시지 분류 · 승인 창 대상 표시 · 탭 제목 자르기 · Claude 실행 중 표시</summary>
public class Review3McpCoreTests
{
    private static McpServer NewServer() =>
        new(new[] { new McpTool("echo", "그대로 돌려준다", new { type = "object" }, (Func<JsonElement, Task<string>>)(_ => Task.FromResult("ok"))) }, "");

    private static async Task<(HttpStatusCode Status, string Body)> Post(McpServer s, string json)
    {
        using var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {s.Token}");
        var r = await c.PostAsync(s.Url, new StringContent(json, Encoding.UTF8, "application/json"));
        return (r.StatusCode, await r.Content.ReadAsStringAsync());
    }

    // ---------- JSON-RPC 메시지 분류 ----------

    [Theory]
    [InlineData(true, true, false, McpServer.McpMessageKind.Request)]
    [InlineData(true, false, false, McpServer.McpMessageKind.Notification)]
    [InlineData(false, true, true, McpServer.McpMessageKind.Response)]
    [InlineData(false, true, false, McpServer.McpMessageKind.Invalid)]
    [InlineData(false, false, true, McpServer.McpMessageKind.Invalid)]
    [InlineData(false, false, false, McpServer.McpMessageKind.Invalid)]
    public void 메시지_종류_분류(bool hasMethod, bool hasId, bool hasResult, McpServer.McpMessageKind expected) =>
        Assert.Equal(expected, McpServer.ClassifyMessage(hasMethod, hasId, hasResult));

    [Fact]
    public async Task 클라이언트_응답과_알림은_본문_없이_202()
    {
        using var s = NewServer();
        var (st1, b1) = await Post(s, "{\"jsonrpc\":\"2.0\",\"id\":7,\"result\":{}}");
        Assert.Equal(HttpStatusCode.Accepted, st1);
        Assert.Equal("", b1);
        var (st2, b2) = await Post(s, "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
        Assert.Equal(HttpStatusCode.Accepted, st2);
        Assert.Equal("", b2);
    }

    [Fact]
    public async Task method_없는_요청은_32600_이고_id_를_그대로_돌려준다()
    {
        using var s = NewServer();
        var (status, body) = await Post(s, "{\"jsonrpc\":\"2.0\",\"id\":\"abc\"}");
        Assert.Equal(HttpStatusCode.OK, status);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(-32600, doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal("abc", doc.RootElement.GetProperty("id").GetString());
    }

    // ---------- 승인 창 대상 표시 ----------

    [Fact]
    public void 대상_항목_안의_줄바꿈과_구분자는_드러나_한_항목이_여러_대상처럼_보이지_않는다()
    {
        var shown = ApprovalDialog.TargetsDisplay(new[] { "web01\nprod-db (ssh 10.0.0.1:22)", "a\u2028b", "c\u001bd" });
        var lines = shown.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Contains("web01\\nprod-db", lines[0]);
        Assert.Equal("a\\u{2028}b", lines[1]);
        Assert.Equal("c\\x1bd", lines[2]);
        Assert.DoesNotContain("\u2028", shown);
    }

    [Fact]
    public void 이미_드러낸_대상은_다시_바뀌지_않는다()
    {
        // 호출하는 쪽이 VisibleControlChars 를 이미 적용한 대상도 그대로 (두 번 적용해도 같음)
        var once = ApprovalDialog.TargetsDisplay(new[] { McpHelpers.VisibleControlChars("x\u0015y") });
        Assert.Equal("x\\x15y", once);
    }

    [Fact]
    public void 명령의_줄_문단_구분자도_드러난다() =>
        Assert.Equal("ls\\u{2028}rm\\u{2029}x", ApprovalDialog.ShowSeparators("ls\u2028rm\u2029x"));

    // ---------- 현재 화면 탭 제목 ----------

    [Fact]
    public void 탭_제목은_서로게이트_쌍_가운데서_자르지_않는다()
    {
        // 80번째 글자 자리에 이모지(서로게이트 쌍)의 앞쪽 반이 오게 한다
        var title = new string('a', MainWindow.ContextTitleMax - 1) + "😀" + "tail";
        var t = MainWindow.SafeContextTitle(title);
        // 이모지 앞에서 잘려 짝 없는 서로게이트가 남지 않는다
        Assert.Equal(new string('a', MainWindow.ContextTitleMax - 1) + "…", t);
        Assert.DoesNotContain(t, char.IsSurrogate);
    }

    // ---------- 제목 표시줄 Claude 실행 중 표시 ----------

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void 패널이_닫혔고_실행_중일_때만_표시(bool running, bool panelOpen, bool expected) =>
        Assert.Equal(expected, MainWindow.ClaudeBackgroundState(running, panelOpen));
}
