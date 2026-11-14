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
        // 인자는 기다리기 전에 모두 읽는다 (잘못된 값은 기본값, 예외 없음)
        var ct = McpRequest.Token;
        var pattern = Arg(a, "pattern");
        var regex = BoolArg(a, "regex") == true;
        var timeout = TimeSpan.FromSeconds(Math.Clamp(NumArg(a, "timeout_sec") ?? 60, 1, 600));
        var idleArg = NumArg(a, "idle_sec");
        var idle = TimeSpan.FromSeconds(Math.Clamp(idleArg ?? 2, 0.5, 60));
        var lines = Math.Clamp(IntArg(a, "lines") ?? 60, 1, 2000);
        const int scan = 1000;   // 패턴은 마지막 1000줄에서 센다
        try { McpHelpers.CountMatches("", pattern, regex); } catch (ArgumentException ex) { throw new McpToolException(ex.Message); }

        var tab = await OnUi(() => FindSession(a)) ?? throw new McpToolException("세션을 찾을 수 없습니다. list_sessions 로 확인하세요.");
        if (tab.Protocol == Protocol.Rdp) throw new McpToolException("RDP 세션은 글자로 읽을 수 없습니다. capture_screen 을 쓰세요.");
        var label = $"session {tab.Id}: {tab.Title}";
        string Tail(string s) => string.Join("\n", s.Split('\n').TakeLast(lines));
        string Done(string outcome, string screen, TimeSpan elapsed)
        {
            AuditLog.Write("wait_for_output", new[] { label }, pattern.Length > 0 ? pattern : "(idle)", "조회");
            return $"[{label}] {outcome} ({elapsed.TotalSeconds:0.0}초)\n{Tail(screen)}";
        }

        async Task<string> Read(int n) => await OnUiAsync(async () => await tab.Session.ReadTerminalAsync(n) ?? "");
        Task<bool> AtPrompt() => OnUiAsync(async () => await tab.Session.IsAtPromptAsync());

        var start = await Read(scan);
        var baseline = McpHelpers.CountMatches(start, pattern, regex);
        var last = start;
        var prev = start;
        var lastChange = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();
        if (await OnUi(() => tab.Status) == SessionStatus.Disconnected) return Done("연결이 끊겨 있습니다", start, sw.Elapsed);

        var atPromptStart = await AtPrompt();
        // 패턴이 이미 나와 있는 경우: send_to_terminal 이 '아직 실행 중' 으로 돌아온 뒤 명령이 끝나며 패턴이 나왔으면 baseline 에 포함돼
        // 제한 시간까지 기다리게 된다 — 셸 프롬프트로 돌아와 있고(명령이 끝남) 마지막 명령 이후 출력(+지금 프롬프트 줄)에 패턴이 있으면 바로 끝낸다.
        // 아직 실행 중이면(tail -f 의 첫 줄에 남은 옛 ERROR, 알아보지 못한 프롬프트 안이라 화면 전체가 '실행 중 출력' 인 경우 등)
        // 이미 있는 것은 세지 않고 새로 나타날 때만 끝낸다 (아래 반복의 baseline 비교)
        if (pattern.Length > 0 && McpHelpers.AlreadyPresentAtPrompt(start, pattern, regex, atPromptStart))
            return Done($"'{pattern}' 이(가) 이미 나와 있고 셸 프롬프트로 돌아와 있습니다", start, sw.Elapsed);

        // 짧은 명령은 이 도구를 부르기 전에 이미 끝나 있다 — 새 출력이 없어도 화면이 셸 프롬프트면 바로 끝낸다
        // (예전에는 새 출력만 기다려 ls 같은 명령 뒤에 제한 시간(60초)까지 버텼다)
        if (pattern.Length == 0 && atPromptStart)
        {
            await Task.Delay(150, ct);
            var again = await Read(scan);
            if (again == start) return Done("이미 끝나 셸 프롬프트로 돌아와 있습니다", start, sw.Elapsed);
            last = again;
            lastChange = DateTime.UtcNow;
        }
        // 실행 중인 명령이 앱이 알아보는 프롬프트에서 입력됐으면 끝나면 프롬프트로 돌아온 것을 알 수 있다 →
        // idle_sec 를 따로 주지 않은 한 '출력이 잠깐 멈춤' 으로 끝내지 않는다 (apt·docker pull·curl 처럼 중간에 조용한 명령).
        // 화면의 '마지막' 프롬프트+명령 줄이 지금 명령일 때만 (그 뒤에 mysql>·>>>·Password: 같은 다른 프롬프트가 보이면 아님 — 출력 멈춤 규칙).
        // 이때는 '출력이 N초 멈춤' 상한 없이 프롬프트 복귀 또는 timeout 까지 기다린다 — sleep 30·rsync -q·mysqldump 처럼
        // 오래 조용한 명령을 10초 만에 끝난 것처럼 돌려주던 문제. 비밀번호·[Y/n] 같은 입력 대기는 RunningCommandFromKnownPrompt 가 걸러 출력 멈춤 규칙을 쓴다
        var promptKnown = pattern.Length == 0 && idleArg == null && McpHelpers.RunningCommandFromKnownPrompt(start);
        string outcome;
        while (true)
        {
            await Task.Delay(200, ct);
            var (open, status) = await OnUi(() => (_tabs.Contains(tab), tab.Status));
            if (!open) throw new McpToolException("기다리는 동안 탭이 닫혔습니다.");
            var now = await Read(scan);
            if (status == SessionStatus.Disconnected) { last = now; outcome = "연결이 끊겼습니다"; break; }
            if (now != last) { last = now; lastChange = DateTime.UtcNow; }
            if (pattern.Length > 0)
            {
                // 전체 개수가 늘었으면 확실히 새로 나온 것. 1000줄 창이 꽉 차 오래된 줄이 밀려나면 전체 개수로는 놓치므로
                // 지난번 화면 뒤에 새로 붙은 부분만 따로 센다
                if (McpHelpers.CountMatches(now, pattern, regex) > baseline ||
                    now.Split('\n').Length >= scan && McpHelpers.CountNewMatches(prev, now, pattern, regex) > 0)
                { outcome = $"'{pattern}' 이(가) 나타났습니다"; break; }
                prev = now;
            }
            else if (!promptKnown && now != start && DateTime.UtcNow - lastChange >= idle) { outcome = $"출력이 {idle.TotalSeconds:0.#}초 동안 멈췄습니다"; break; }
            // 출력이 멈추고 프롬프트로 돌아왔으면 idle 시간을 다 채우지 않아도 끝
            else if (now != start && DateTime.UtcNow - lastChange >= TimeSpan.FromMilliseconds(300) && await AtPrompt()) { outcome = "끝나서 셸 프롬프트로 돌아왔습니다"; break; }
            if (sw.Elapsed >= timeout)
            {
                outcome = pattern.Length > 0 ? $"{timeout.TotalSeconds:0}초 안에 '{pattern}' 이(가) 나타나지 않았습니다 (시간 초과)"
                    : now == start ? $"{timeout.TotalSeconds:0}초 동안 새 출력이 없었습니다 (시간 초과)"
                    : promptKnown ? $"{timeout.TotalSeconds:0}초 안에 셸 프롬프트로 돌아오지 않았습니다 (아직 실행 중이거나 입력을 기다리는 중, 시간 초과)"
                    : $"{timeout.TotalSeconds:0}초 동안 출력이 계속됐습니다 (시간 초과)";
                break;
            }
        }
        return Done(outcome, last, sw.Elapsed);
    }
}
