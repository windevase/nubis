using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Nubis;

/// <summary>MCP 도구 정의. Handler 는 결과 텍스트를 반환하고, 실패는 McpToolException 으로 알린다.</summary>
public sealed record McpTool(string Name, string Description, object InputSchema, Func<JsonElement, Task<object>> Handler)
{
    /// <summary>텍스트를 돌려주는 도구</summary>
    public McpTool(string name, string description, object inputSchema, Func<JsonElement, Task<string>> handler)
        : this(name, description, inputSchema, async a => (object)await handler(a)) { }
}

/// <summary>이미지를 돌려주는 도구 결과 (MCP image content)</summary>
public sealed record McpImage(byte[] Data, string MimeType, string Caption);

public sealed class McpToolException(string message) : Exception(message);

/// <summary>
/// 최소 구현 MCP 서버 (Streamable HTTP, JSON 응답만 사용).
/// 127.0.0.1 에서만 열고 Bearer 토큰으로 인증한다. Claude Code 가 --mcp-config 로 접속한다.
/// </summary>
public sealed class McpServer : IDisposable
{
    public const string ServerName = "nubis";

    private readonly HttpListener _listener = new();
    private readonly Dictionary<string, McpTool> _tools;
    private readonly string _instructions;

    public int Port { get; }
    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
    public string Url => $"http://127.0.0.1:{Port}/mcp";

    public McpServer(IEnumerable<McpTool> tools, string instructions)
    {
        _tools = tools.ToDictionary(t => t.Name);
        _instructions = instructions;
        Port = FreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = AcceptLoop();
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>claude --mcp-config 에 넘길 설정 JSON 파일을 쓰고 경로를 반환</summary>
    public string WriteClientConfig(string path)
    {
        var cfg = new Dictionary<string, object>
        {
            ["mcpServers"] = new Dictionary<string, object>
            {
                [ServerName] = new { type = "http", url = Url, headers = new Dictionary<string, string> { ["Authorization"] = $"Bearer {Token}" } },
            },
        };
        File.WriteAllText(path, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    private async Task AcceptLoop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private async Task Handle(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        try
        {
            if (ctx.Request.Headers["Authorization"] != $"Bearer {Token}")
            {
                res.StatusCode = 401;
                return;
            }
            if (ctx.Request.HttpMethod != "POST")
            {
                res.StatusCode = 405;   // SSE 스트림은 제공하지 않음
                return;
            }

            string body;
            using (var r = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) body = await r.ReadToEndAsync();
            using var doc = JsonDocument.Parse(body);
            var req = doc.RootElement;

            if (!req.TryGetProperty("id", out var id))
            {
                res.StatusCode = 202;   // notification (notifications/initialized 등)
                return;
            }

            object reply;
            try
            {
                var method = req.TryGetProperty("method", out var mEl) && mEl.ValueKind == JsonValueKind.String ? mEl.GetString() ?? "" : "";
                var result = await Dispatch(method, req.TryGetProperty("params", out var p) ? p : default);
                reply = new { jsonrpc = "2.0", id, result };
            }
            catch (MissingMethodException ex)
            {
                reply = new { jsonrpc = "2.0", id, error = new { code = -32601, message = ex.Message } };
            }

            var bytes = JsonSerializer.SerializeToUtf8Bytes(reply);
            res.ContentType = "application/json";
            res.ContentLength64 = bytes.Length;
            await res.OutputStream.WriteAsync(bytes);
        }
        catch (Exception ex)
        {
            try { res.StatusCode = 500; Log($"MCP 처리 오류: {ex}"); } catch { }
        }
        finally
        {
            try { res.Close(); } catch { }
        }
    }

    private async Task<object> Dispatch(string method, JsonElement p)
    {
        switch (method)
        {
            case "initialize":
                var version = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("protocolVersion", out var v) ? v.GetString() : "2025-06-18";
                return new
                {
                    protocolVersion = version,
                    capabilities = new { tools = new { listChanged = false } },
                    serverInfo = new { name = ServerName, version = "0.1.0" },
                    instructions = _instructions,
                };

            case "ping":
                return new { };

            case "tools/list":
                return new
                {
                    tools = _tools.Values.Select(t => new { name = t.Name, description = t.Description, inputSchema = t.InputSchema }),
                };

            case "tools/call":
                if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
                    return ToolResult("tools/call 에 name 이 없습니다.", true);
                var name = nameEl.GetString() ?? "";
                var args = p.TryGetProperty("arguments", out var a) ? a.Clone() : JsonDocument.Parse("{}").RootElement;
                if (!_tools.TryGetValue(name, out var tool))
                    return ToolResult($"알 수 없는 도구: {name}", true);
                try
                {
                    var result = await tool.Handler(args);
                    return result is McpImage img
                        ? new
                        {
                            content = new object[]
                            {
                                new { type = "image", data = Convert.ToBase64String(img.Data), mimeType = img.MimeType },
                                new { type = "text", text = img.Caption },
                            },
                            isError = false,
                        }
                        : ToolResult(result as string ?? "", false);
                }
                catch (McpToolException ex) { return ToolResult(ex.Message, true); }
                catch (Exception ex)
                {
                    Log($"도구 {name} 오류: {ex}");
                    return ToolResult($"도구 실행 중 오류: {ex.Message}", true);
                }

            default:
                throw new MissingMethodException($"지원하지 않는 메서드: {method}");
        }
    }

    private static object ToolResult(string text, bool isError) =>
        new { content = new[] { new { type = "text", text } }, isError };

    private static void Log(string msg)
    {
        try { LogFiles.Rotate(Path.Combine(AppPaths.DataDir, "error.log"));
            File.AppendAllText(Path.Combine(AppPaths.DataDir, "error.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\r\n"); } catch { }
    }

    public void Dispose()
    {
        try { _listener.Stop(); _listener.Close(); } catch { }
    }
}
