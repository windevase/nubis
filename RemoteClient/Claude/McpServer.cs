using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
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
/// 지금 처리 중인 MCP 요청의 취소 토큰. Claude 에서 Esc 등으로 요청을 취소하면(notifications/cancelled) 취소된다.
/// 도구 핸들러 안(그 안에서 await/OnUi 로 이어지는 코드 포함)에서 읽는다. 요청 밖에서는 CancellationToken.None.
/// </summary>
public static class McpRequest
{
    internal static readonly AsyncLocal<CancellationToken> Current = new();
    public static CancellationToken Token => Current.Value;
}

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
    // 처리 중인 요청 id(JSON 원문) → 취소 토큰. notifications/cancelled 가 오면 취소한다
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inflight = new();
    // 등록 전에 도착한 취소 알림의 요청 id → 받은 시각(UTC). 요청이 등록될 때 확인하고, EarlyCancelTtl 이 지나면 버린다
    private readonly ConcurrentDictionary<string, DateTime> _earlyCancels = new();
    private static readonly TimeSpan EarlyCancelTtl = TimeSpan.FromSeconds(30);

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
            if (!TokenMatches(ctx.Request.Headers["Authorization"]))
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

            var method = req.TryGetProperty("method", out var mEl) && mEl.ValueKind == JsonValueKind.String ? mEl.GetString() ?? "" : "";
            var p = req.TryGetProperty("params", out var pEl) ? pEl : default;
            if (!req.TryGetProperty("id", out var id))
            {
                // notification (notifications/initialized 등). 취소 알림이면 해당 요청을 취소한다
                if (method == "notifications/cancelled" && p.ValueKind == JsonValueKind.Object &&
                    p.TryGetProperty("requestId", out var rid))
                    CancelRequest(rid.GetRawText());
                res.StatusCode = 202;
                return;
            }

            object reply;
            var key = id.GetRawText();
            using var cts = new CancellationTokenSource();
            _inflight[key] = cts;
            // 등록보다 먼저 도착한 취소 알림이 있으면 바로 취소한다 (등록 → 확인 순서라 CancelRequest 와 엇갈려도 놓치지 않음)
            PruneEarlyCancels();
            if (_earlyCancels.TryRemove(key, out _))
            {
                try { cts.Cancel(); } catch (ObjectDisposedException) { }
            }
            try
            {
                var result = await Dispatch(method, p, cts.Token);
                reply = new { jsonrpc = "2.0", id, result };
            }
            catch (MissingMethodException ex)
            {
                reply = new { jsonrpc = "2.0", id, error = new { code = -32601, message = ex.Message } };
            }
            finally
            {
                _inflight.TryRemove(new KeyValuePair<string, CancellationTokenSource>(key, cts));
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

    /// <summary>Authorization 헤더가 "Bearer {Token}" 과 같은지 — 비교 시간으로 토큰을 알아내지 못하게 고정 시간 비교</summary>
    private bool TokenMatches(string? header)
    {
        if (header == null) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(header), Encoding.UTF8.GetBytes($"Bearer {Token}"));
    }

    /// <summary>
    /// 요청 id 의 취소. 아직 등록되지 않은 요청(취소 알림이 요청보다 먼저 처리된 경우)을 위해 먼저 기록해 두고,
    /// 등록돼 있으면 바로 취소한다. 기록 → 확인 순서라 등록(등록 → 확인)과 엇갈려도 둘 중 한쪽은 반드시 상대를 본다.
    /// </summary>
    private void CancelRequest(string key)
    {
        PruneEarlyCancels();
        _earlyCancels[key] = DateTime.UtcNow;
        if (_inflight.TryGetValue(key, out var target))
        {
            _earlyCancels.TryRemove(key, out _);
            try { target.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>오래된(EarlyCancelTtl 초과) 선취소 기록을 지운다 — 끝난 요청에 대한 늦은 취소 알림이 쌓이지 않게</summary>
    private void PruneEarlyCancels()
    {
        var limit = DateTime.UtcNow - EarlyCancelTtl;
        foreach (var kv in _earlyCancels)
            if (kv.Value < limit) _earlyCancels.TryRemove(kv);
    }

    /// <summary>
    /// 처리 중인 모든 요청을 취소한다 (Claude 재시작·종료 때). 열려 있는 승인 창은 거부로 닫혀,
    /// 응답을 받을 Claude 가 없는데 승인되어 원격 명령이 실행되는 일을 막는다.
    /// </summary>
    public void CancelAll()
    {
        _earlyCancels.Clear();   // 이전 claude 의 요청 id 가 새 claude 의 같은 id 를 취소하지 않게
        foreach (var cts in _inflight.Values)
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    private async Task<object> Dispatch(string method, JsonElement p, CancellationToken ct)
    {
        switch (method)
        {
            case "initialize":
                var version = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("protocolVersion", out var v) ? v.GetString() : "2025-06-18";
                return new
                {
                    protocolVersion = version,
                    capabilities = new { tools = new { listChanged = false } },
                    serverInfo = new { name = ServerName, version = typeof(McpServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0" },
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
                    // 핸들러와 그 안의 await·OnUi 호출이 McpRequest.Token 으로 이 요청의 취소를 볼 수 있게 한다
                    McpRequest.Current.Value = ct;
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
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return ToolResult("요청이 취소되어 중단했습니다.", true);
                }
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
