using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Nubis;

public partial class MainWindow
{
    // =================== 접속 진단 ===================

    private async Task<string> TestConnectionAsync(JsonElement a)
    {
        var (h, conn) = await OnUi(() => { var x = ResolveOneHost(Arg(a, "host")); return (x, WithProfileDefaults(x)); });
        var timeout = TimeSpan.FromSeconds(Math.Clamp(IntArg(a, "timeout_sec") ?? 4, 1, 20));
        var custom = a.ValueKind == JsonValueKind.Object && a.TryGetProperty("ports", out var pe) && pe.ValueKind == JsonValueKind.Array
            ? McpHelpers.ParsePorts(pe.EnumerateArray().Select(x => x.ToString())) : Array.Empty<int>();
        var ports = custom.Length > 0 ? custom
            : h.Protocol == Protocol.Ssh ? new[] { h.Port }
            : new[] { h.Port, WinRm.HttpsPort, WinRm.HttpPort }.Distinct().ToArray();

        var sb = new StringBuilder($"[{h.DisplayName}] {h.ProtocolText}, 시도 순서: {string.Join(" → ", h.AddressCandidates())}" +
                                   (h.IsStopped ? $"  ⚠ 클라우드 상태: {h.CloudState}" : "") + "\n");
        foreach (var addr in h.AddressCandidates())
        {
            sb.AppendLine($"\n## {addr}");
            IPAddress[] ips;
            try
            {
                ips = IPAddress.TryParse(addr, out var ip) ? new[] { ip } : await Dns.GetHostAddressesAsync(addr).WaitAsync(timeout);
                if (!IPAddress.TryParse(addr, out _)) sb.AppendLine($"DNS: {string.Join(", ", ips.Select(i => i.ToString()))}");
            }
            catch (Exception ex) { sb.AppendLine($"DNS 실패: {ex.Message}"); continue; }

            foreach (var port in ports)
            {
                var sw = Stopwatch.StartNew();
                using var tcp = new TcpClient();
                try
                {
                    using var cts = new CancellationTokenSource(timeout);
                    await tcp.ConnectAsync(ips, port, cts.Token);
                    var line = $"  {port}/tcp 열림 ({sw.ElapsedMilliseconds}ms)";
                    if (h.Protocol == Protocol.Ssh && port == h.Port)
                    {
                        // SSH 서버는 접속하자마자 배너(SSH-2.0-...)를 보낸다 — 방화벽·다른 서비스와 구분
                        try
                        {
                            tcp.ReceiveTimeout = 3000;
                            var buf = new byte[256];
                            using var bcts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                            var n = await tcp.GetStream().ReadAsync(buf, bcts.Token);
                            var banner = Encoding.ASCII.GetString(buf, 0, n).Split('\n')[0].Trim();
                            line += banner.StartsWith("SSH-") ? $" — 배너 {banner}" : $" — SSH 배너가 아님: '{banner}'";
                        }
                        catch { line += " — 배너 없음 (SSH 가 아니거나 응답 지연)"; }
                    }
                    sb.AppendLine(line);
                }
                catch (OperationCanceledException) { sb.AppendLine($"  {port}/tcp 응답 없음 ({timeout.TotalSeconds:0}초 초과 — 방화벽·보안 그룹에서 막혔거나 서버 꺼짐)"); }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused) { sb.AppendLine($"  {port}/tcp 거부됨 (서버는 살아 있으나 이 포트에서 서비스가 안 떠 있음)"); }
                catch (Exception ex) { sb.AppendLine($"  {port}/tcp 실패: {ex.Message}"); }
            }
        }
        if (string.IsNullOrWhiteSpace(conn.Username) || (string.IsNullOrEmpty(conn.Password) && !conn.HasKey))
            sb.AppendLine("\n참고: 이 호스트에는 저장된 계정이 없어 run_command 등 비대화형 접속은 실패합니다 (호스트·Vault 에 계정 저장 필요).");
        var result = sb.ToString();
        AuditLog.Write("test_connection", new[] { h.DisplayName }, string.Join(",", ports), "조회");
        return result;
    }

    // =================== 출력 기다리기 ===================

    private async Task<string> WaitForOutputAsync(JsonElement a)
    {
        var tab = await OnUi(() => FindSession(a)) ?? throw new McpToolException("세션을 찾을 수 없습니다. list_sessions 로 확인하세요.");
        if (tab.Protocol == Protocol.Rdp) throw new McpToolException("RDP 세션은 글자로 읽을 수 없습니다. capture_screen 을 쓰세요.");
        var pattern = Arg(a, "pattern");
        var regex = BoolArg(a, "regex") == true;
        var timeout = TimeSpan.FromSeconds(Math.Clamp(IntArg(a, "timeout_sec") ?? 60, 1, 600));
        var idle = TimeSpan.FromSeconds(a.ValueKind == JsonValueKind.Object && a.TryGetProperty("idle_sec", out var ie) && ie.TryGetDouble(out var d) ? Math.Clamp(d, 0.5, 60) : 2);
        var lines = Math.Clamp(IntArg(a, "lines") ?? 60, 1, 2000);
        const int scan = 1000;   // 패턴은 마지막 1000줄에서 센다

        async Task<string> Read(int n) => await OnUiAsync(async () => await tab.Session.ReadTerminalAsync(n) ?? "");
        try { McpHelpers.CountMatches("", pattern, regex); } catch (ArgumentException ex) { throw new McpToolException(ex.Message); }

        var start = await Read(scan);
        var baseline = McpHelpers.CountMatches(start, pattern, regex);
        var last = start;
        var lastChange = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();
        string outcome;
        // 짧은 명령은 이 도구를 부르기 전에 이미 끝나 있다 — 새 출력이 없어도 화면이 셸 프롬프트면 바로 끝낸다
        // (예전에는 새 출력만 기다려 ls 같은 명령 뒤에 제한 시간(60초)까지 버텼다)
        if (pattern.Length == 0 && await OnUiAsync(async () => await tab.Session.IsAtPromptAsync()))
        {
            await Task.Delay(150);
            var again = await Read(scan);
            if (again == start)
            {
                AuditLog.Write("wait_for_output", new[] { $"session {tab.Id}: {tab.Title}" }, "(idle)", "조회");
                return $"[session {tab.Id}: {tab.Title}] 이미 끝나 셸 프롬프트로 돌아와 있습니다 ({sw.Elapsed.TotalSeconds:0.0}초)\n" +
                       string.Join("\n", start.Split('\n').TakeLast(lines));
            }
            last = again;
            lastChange = DateTime.UtcNow;
        }
        while (true)
        {
            await Task.Delay(200);
            if (!await OnUi(() => _tabs.Contains(tab))) throw new McpToolException("기다리는 동안 탭이 닫혔습니다.");
            var now = await Read(scan);
            if (now != last) { last = now; lastChange = DateTime.UtcNow; }
            if (pattern.Length > 0)
            {
                if (McpHelpers.CountMatches(now, pattern, regex) > baseline) { outcome = $"'{pattern}' 이(가) 나타났습니다"; break; }
            }
            else if (now != start && DateTime.UtcNow - lastChange >= idle) { outcome = $"출력이 {idle.TotalSeconds:0.#}초 동안 멈췄습니다"; break; }
            // 출력이 멈추고 프롬프트로 돌아왔으면 idle 시간을 다 채우지 않아도 끝
            else if (now != start && DateTime.UtcNow - lastChange >= TimeSpan.FromMilliseconds(300) &&
                     await OnUiAsync(async () => await tab.Session.IsAtPromptAsync())) { outcome = "끝나서 셸 프롬프트로 돌아왔습니다"; break; }
            if (sw.Elapsed >= timeout)
            {
                outcome = pattern.Length > 0 ? $"{timeout.TotalSeconds:0}초 안에 '{pattern}' 이(가) 나타나지 않았습니다 (시간 초과)"
                    : now == start ? $"{timeout.TotalSeconds:0}초 동안 새 출력이 없었습니다 (시간 초과)" : $"{timeout.TotalSeconds:0}초 동안 출력이 계속됐습니다 (시간 초과)";
                break;
            }
        }
        var tail = string.Join("\n", last.Split('\n').TakeLast(lines));
        AuditLog.Write("wait_for_output", new[] { $"session {tab.Id}: {tab.Title}" }, pattern.Length > 0 ? pattern : "(idle)", "조회");
        return $"[session {tab.Id}: {tab.Title}] {outcome} ({sw.Elapsed.TotalSeconds:0.0}초)\n{tail}";
    }
}
