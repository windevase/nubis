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

    private readonly HttpListener _listener;
    private readonly Dictionary<string, McpTool> _tools;
    private readonly string _instructions;
    // 처리 중인 요청 id(JSON 원문) → 취소 토큰. notifications/cancelled 가 오면 취소한다
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inflight = new();
    // 등록 전에 도착한 취소 알림의 요청 id → 받은 시각(UTC). 요청이 등록될 때 확인하고, EarlyCancelTtl 이 지나면 버린다
    private readonly ConcurrentDictionary<string, DateTime> _earlyCancels = new();
    private static readonly TimeSpan EarlyCancelTtl = TimeSpan.FromSeconds(30);
    // 선취소 기록의 최대 개수 — 새 요청이 오지 않아 시간 기준 정리가 돌지 않아도 이 이상 쌓이지 않는다
    // (끝난 요청에 대한 늦은 취소 알림이 몰려와도 메모리가 일정)
    internal const int MaxEarlyCancels = 256;
    // arguments 가 없는 tools/call 에 넘길 빈 객체 — 요청마다 JsonDocument 를 만들고 버리지 않게 하나를 같이 쓴다 (Clone 은 Dispose 불필요)
    private static readonly JsonElement EmptyArgs = CreateEmptyArgs();

    private static JsonElement CreateEmptyArgs()
    {
        using var d = JsonDocument.Parse("{}");
        return d.RootElement.Clone();
    }

    /// <summary>처리 중인 요청 수 (누수 점검·테스트용)</summary>
    internal int InflightCount => _inflight.Count;
    /// <summary>남아 있는 선취소 기록 수 (누수 점검·테스트용)</summary>
    internal int EarlyCancelCount => _earlyCancels.Count;

    public int Port { get; }
    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
    public string Url => $"http://127.0.0.1:{Port}/mcp";

    /// <summary>지원하는 MCP 프로토콜 버전 (최신이 앞). initialize 의 요청 버전이 여기 없으면 첫 번째(최신)로 답한다</summary>
    public static readonly string[] SupportedProtocolVersions = { "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05" };

    public McpServer(IEnumerable<McpTool> tools, string instructions)
    {
        _tools = tools.ToDictionary(t => t.Name);
        _instructions = instructions;
        // 빈 포트를 고른 뒤 실제로 열기까지 사이에 다른 프로그램이 그 포트를 잡을 수 있다 (TOCTOU) — 몇 번 다시 고른다
        for (var attempt = 1; ; attempt++)
        {
            var port = FreePort();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                _listener = listener;
                Port = port;
                break;
            }
            catch (HttpListenerException) when (attempt < 5)
            {
                try { listener.Close(); } catch { }
            }
        }
        _ = AcceptLoop();
    }

    /// <summary>initialize 의 protocolVersion 협상: 지원하는 버전이면 그대로, 아니면(없음·문자열 아님 포함) 지원하는 최신 버전</summary>
    public static string NegotiateProtocolVersion(JsonElement p) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty("protocolVersion", out var v) && v.ValueKind == JsonValueKind.String &&
        Array.IndexOf(SupportedProtocolVersions, v.GetString()) >= 0
            ? v.GetString()!
            : SupportedProtocolVersions[0];

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

    /// <summary>앱 종료 때 토큰이 든 클라이언트 설정 파일을 지운다 (서버가 닫힌 뒤에는 쓸모없는 비밀 값)</summary>
    public static void DeleteClientConfig(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
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

            // 본문 크기 제한: 인증된 클라이언트라도 잘못된(또는 악의적인) 거대 본문으로 메모리를 끝없이 쓰지 않게 한다.
            // Content-Length 가 한도를 넘으면 읽지 않고 413, 길이를 모르는(chunked) 본문은 한도까지만 읽는다
            if (ctx.Request.ContentLength64 > MaxRequestBytes)
            {
                res.StatusCode = 413;
                return;
            }
            var body = await ReadBodyAsync(ctx.Request.InputStream, MaxRequestBytes);
            if (body == null)
            {
                res.StatusCode = 413;
                return;
            }
            JsonDocument doc;
            try { doc = JsonDocument.Parse(body); }
            catch (JsonException)
            {
                // JSON-RPC: 파싱 실패는 HTTP 500 이 아니라 -32700 오류 응답 (id 는 알 수 없으므로 null)
                await WriteJson(res, ErrorReply(null, -32700, "Parse error"));
                return;
            }
            using var _doc = doc;
            var req = doc.RootElement;
            if (req.ValueKind != JsonValueKind.Object)
            {
                // 배치(배열)·기타 값은 지원하지 않는다 → -32600 Invalid Request
                await WriteJson(res, ErrorReply(null, -32600, "Invalid Request"));
                return;
            }

            var hasMethod = req.TryGetProperty("method", out var mEl) && mEl.ValueKind == JsonValueKind.String;
            var method = hasMethod ? mEl.GetString() ?? "" : "";
            var p = req.TryGetProperty("params", out var pEl) ? pEl : default;
            var hasId = req.TryGetProperty("id", out var id);
            switch (ClassifyMessage(hasMethod, hasId, req.TryGetProperty("result", out _) || req.TryGetProperty("error", out _)))
            {
                case McpMessageKind.Response:
                    // 클라이언트가 보낸 응답 (이 서버는 클라이언트에 요청을 보내지 않으므로 받기만 한다) — Streamable HTTP 규약대로 본문 없이 202
                    res.StatusCode = 202;
                    return;
                case McpMessageKind.Invalid:
                    // method 가 없는 요청 → -32600 (id 가 있으면 그대로 돌려준다)
                    await WriteJson(res, ErrorReply(hasId ? id : null, -32600, "Invalid Request"));
                    return;
            }
            if (!hasId)
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
                reply = ErrorReply(id, -32601, ex.Message);
            }
            catch (Exception ex)
            {
                // 도구 오류는 Dispatch 안에서 도구 결과(isError)로 바뀐다. 여기까지 온 것은 요청 처리 자체의 오류 → -32603
                Log($"MCP 요청 처리 오류 ({method}): {ex}");
                reply = ErrorReply(id, -32603, "Internal error");
            }
            finally
            {
                _inflight.TryRemove(new KeyValuePair<string, CancellationTokenSource>(key, cts));
            }

            await WriteJson(res, reply);
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

    /// <summary>요청 본문 최대 크기 (바이트). 가장 큰 정상 요청(import_hosts 의 표 붙여넣기 등)보다 넉넉하게</summary>
    internal const int MaxRequestBytes = 16 * 1024 * 1024;

    /// <summary>본문을 UTF-8 문자열로 읽는다 (BOM 은 버림). maxBytes 를 넘으면 더 읽지 않고 null</summary>
    internal static async Task<string?> ReadBodyAsync(Stream input, int maxBytes)
    {
        using var ms = new MemoryStream();
        var buf = new byte[16 * 1024];
        int n;
        while ((n = await input.ReadAsync(buf)) > 0)
        {
            if (ms.Length + n > maxBytes) return null;
            ms.Write(buf, 0, n);
        }
        var span = ms.GetBuffer().AsSpan(0, (int)ms.Length);
        if (span.StartsWith(Encoding.UTF8.Preamble)) span = span[Encoding.UTF8.Preamble.Length..];
        return Encoding.UTF8.GetString(span);
    }

    public enum McpMessageKind { Request, Notification, Response, Invalid }

    /// <summary>
    /// JSON-RPC 메시지 종류: method 가 있으면 id 유무로 요청/알림, method 없이 result·error 가 있으면 클라이언트의 응답, 그 밖은 잘못된 요청.
    /// 알림·응답에는 본문 없이 202 로 답한다 (JSON-RPC 는 알림에 응답하지 않음)
    /// </summary>
    public static McpMessageKind ClassifyMessage(bool hasMethod, bool hasId, bool hasResultOrError) =>
        hasMethod ? (hasId ? McpMessageKind.Request : McpMessageKind.Notification)
        : hasResultOrError && hasId ? McpMessageKind.Response
        : McpMessageKind.Invalid;

    private static object ErrorReply(JsonElement? id, int code, string message) =>
        new { jsonrpc = "2.0", id, error = new { code, message } };

    private static async Task WriteJson(HttpListenerResponse res, object reply)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(reply);
        res.ContentType = "application/json";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
    }

    /// <summary>Authorization 헤더가 "Bearer {Token}" 과 같은지 — 비교 시간으로 토큰을 알아내지 못하게 고정 시간 비교</summary>
    private bool TokenMatches(string? header)
    {
        if (header == null) return false;
        _expectedAuth ??= Encoding.UTF8.GetBytes($"Bearer {Token}");
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(header), _expectedAuth);
    }

    // "Bearer {Token}" 의 UTF-8 바이트 (요청마다 만들지 않게 한 번만)
    private byte[]? _expectedAuth;

    /// <summary>
    /// 요청 id 의 취소. 아직 등록되지 않은 요청(취소 알림이 요청보다 먼저 처리된 경우)을 위해 먼저 기록해 두고,
    /// 등록돼 있으면 바로 취소한다. 기록 → 확인 순서라 등록(등록 → 확인)과 엇갈려도 둘 중 한쪽은 반드시 상대를 본다.
    /// </summary>
    private void CancelRequest(string key)
    {
        PruneEarlyCancels();
        // 한도에 닿으면 가장 오래된 기록부터 버린다 (드문 경로라 정렬 비용은 문제없음)
        if (_earlyCancels.Count >= MaxEarlyCancels)
            foreach (var old in _earlyCancels.OrderBy(kv => kv.Value).Take(_earlyCancels.Count - MaxEarlyCancels + 1).ToList())
                _earlyCancels.TryRemove(old);
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
        if (_earlyCancels.IsEmpty) return;   // 대부분의 요청은 선취소가 없다 — 열거 비용 없이 바로 끝낸다
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
                return new
                {
                    protocolVersion = NegotiateProtocolVersion(p),
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
                var args = p.TryGetProperty("arguments", out var a) ? a.Clone() : EmptyArgs;
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
        // 처리 중인 도구(승인 대기·명령 실행 포함)를 멈추고 기록을 비운다 — 서버를 닫은 뒤 핸들러·토큰이 남아 돌지 않게
        CancelAll();
        _earlyCancels.Clear();
        try { _listener.Stop(); _listener.Close(); } catch { }
    }
}
