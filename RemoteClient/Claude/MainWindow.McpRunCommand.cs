using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace Nubis;

public partial class MainWindow
{
    // =================== 여러 서버 명령 실행 (run_command) ===================
    private async Task<string> RunCommandAsync(JsonElement a)
    {
        var specs = ListArg(a, "targets");
        var command = Arg(a, "command");
        var timeout = Math.Clamp(IntArg(a, "timeout_sec") ?? 60, 5, 1800);
        if (specs.Count == 0 || command.Trim().Length == 0) throw new McpToolException("targets 와 command 가 필요합니다.");

        // 대상 해석 (UI 스레드의 호스트 목록 사용)
        var (targets, skipped) = await OnUi(() =>
        {
            var found = new List<HostEntry>();
            var skip = new List<string>();
            foreach (var spec in specs)
            {
                var r = ResolveHosts(spec);
                if (r.Count == 0) skip.Add($"{spec}: 일치하는 호스트 없음");
                if (r.Count > 1 && !spec.StartsWith("group:", StringComparison.OrdinalIgnoreCase))
                    skip.Add($"{spec}: {r.Count}개 일치 → 모두 포함");
                found.AddRange(r);
            }
            var unique = found.DistinctBy(h => h.Id).ToList();
            foreach (var h in unique.Where(h => h.IsStopped)) skip.Add($"{h.DisplayName}: 중지 상태({h.CloudState})");
            var run = unique.Where(h => h.Protocol is Protocol.Ssh or Protocol.Rdp && !h.IsStopped).Select(WithProfileDefaults).ToList();
            return (run, skip);
        });

        if (targets.Count == 0)
            throw new McpToolException("실행할 수 있는 대상이 없습니다.\n" + string.Join("\n", skipped));

        var useCmd = Arg(a, "windows_shell").Equals("cmd", StringComparison.OrdinalIgnoreCase);
        var linux = targets.Where(h => h.Protocol == Protocol.Ssh).ToList();
        var windows = targets.Where(h => h.Protocol == Protocol.Rdp).ToList();
        var winLabel = useCmd ? "WinRM cmd" : "WinRM PowerShell";
        // 시도할 주소 순서(사설 → 공인)를 승인 창에 그대로 보여준다
        string Addrs(HostEntry h, string ports) => string.Join(" → ", h.AddressCandidates().Select(a => $"{a}:{ports}"));
        var labels = linux.Select(h => $"[SSH]  {h.DisplayName}  ({Addrs(h, h.Port.ToString())})")
            .Concat(windows.Select(h => $"[{winLabel}]  {h.DisplayName}  ({Addrs(h, $"{WinRm.HttpsPort}/{WinRm.HttpPort}")})"))
            .ToList();
        var mixed = linux.Count > 0 && windows.Count > 0
            ? L.T("\n⚠ 리눅스와 Windows 서버가 섞여 있습니다. 같은 명령이 양쪽에서 동작하는지 확인하세요.") : "";
        // 설정에서 켠 경우, 대상 셸 모두에서 읽기 전용으로 판별되는 명령은 승인 창 없이 실행
        var autoApproved = SettingsStore.Current.AutoApproveReadOnly
            && (linux.Count == 0 || ReadOnlyCommands.IsReadOnly(command, CommandShell.Sh))
            && (windows.Count == 0 || ReadOnlyCommands.IsReadOnly(command, useCmd ? CommandShell.Cmd : CommandShell.PowerShell));
        if (!autoApproved)
        {
            var approved = await OnUi(() => ApprovalDialog.Ask(this, L.F("Claude 가 서버 {0}대에 명령 실행을 요청합니다", targets.Count),
                L.T("승인하면 아래 서버들에 접속해 명령을 실행하고 결과를 Claude 에게 돌려줍니다.") + mixed +
                (skipped.Count > 0 ? L.F("\n제외: {0}건 (중지·미일치)", skipped.Count) : ""), labels, command));
            if (!approved)
            {
                AuditLog.Write("run_command", labels, command, "거부");
                throw new McpToolException("사용자가 실행을 거부했습니다.");
            }
        }

        // 실행 중에는 Claude 패널 위에 "실행 중 · 취소" 줄을 보여 준다 (Claude CLI 에서 Esc 를 눌러도 앱의 실행은 계속되므로)
        using var cts = new CancellationTokenSource();
        var started = DateTime.Now;
        await OnUi(() => { BeginRunStatus(cts, L.F("서버 {0}대에서 명령 실행 중: {1}", targets.Count, FirstLine(command))); return 0; });
        List<ExecResult> results;
        try
        {
            var sshTask = SshExec.RunAsync(linux, command, timeout, cts.Token);
            var winTask = WinRm.RunAsync(windows, command, !useCmd, timeout, cts.Token);
            // 취소하면 기다리지 않고 바로 돌아온다 (아직 시작 안 한 서버는 실행하지 않고, 이미 시작한 서버는 끝까지 돌 수 있음)
            var all = Task.WhenAll(sshTask, winTask);
            try { await Task.WhenAny(all, Task.Delay(Timeout.Infinite, cts.Token)); }
            catch (OperationCanceledException) { }
            if (!cts.IsCancellationRequested)
            {
                try { await all; }
                catch (OperationCanceledException) { }
            }
            else _ = all.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            if (cts.IsCancellationRequested)
            {
                AuditLog.Write("run_command", labels, command, "승인·실행 후 사용자가 취소");
                throw new McpToolException($"사용자가 실행을 취소했습니다 ({(DateTime.Now - started).TotalSeconds:0}초 경과). 이미 실행된 서버에서는 명령이 끝까지 돌았을 수 있습니다.");
            }
            results = sshTask.Result.Concat(winTask.Result).ToList();
        }
        finally
        {
            await OnUi(() => { EndRunStatus(cts); return 0; });
        }
        var okCount = results.Count(r => r.Ok);
        AuditLog.Write("run_command", labels, command, $"{(autoApproved ? "자동 승인(읽기 전용)" : "승인")}·실행 (성공 {okCount} / 실패 {results.Count - okCount})");

        var sb = new StringBuilder($"실행 완료: 성공 {okCount} / 실패 {results.Count - okCount} (총 {results.Count}대)\n");
        if (skipped.Count > 0) sb.AppendLine("제외: " + string.Join("; ", skipped));
        // 대수가 많으면 서버당 출력을 줄여 전체를 약 60,000자 안으로 (Claude 컨텍스트 절약)
        var perHost = Math.Clamp(60_000 / Math.Max(1, results.Count), 1_000, 8_000);
        foreach (var r in results.OrderBy(r => r.Ok).ThenBy(r => r.Host.DisplayName))
        {
            sb.AppendLine();
            sb.AppendLine($"## {r.Host.DisplayName} ({r.Host.PrimaryAddress}{(r.Host.HasPublicIp ? $" / 공인 {r.Host.PublicIp}" : "")}) — {(r.ExitCode is { } c ? $"exit {c}" : "실패")}, {r.ElapsedMs / 1000.0:0.0}s");
            if (r.Output.Length > 0) sb.AppendLine(SshExec.ClipOutput(r.Output.TrimEnd(), perHost));
            if (r.Error.Length > 0) sb.AppendLine("[stderr/오류] " + SshExec.ClipOutput(r.Error.TrimEnd(), Math.Min(perHost, 4_000)));
        }
        return sb.ToString();
    }

    private static string FirstLine(string s)
    {
        var line = s.Replace("\r", "").Split('\n')[0].Trim();
        return line.Length > 60 ? line[..60] + "…" : line;
    }

    // ----- 실행 중 표시 (Claude 패널 위) -----
    private readonly List<(CancellationTokenSource Cts, string Text)> _runs = new();

    private void BeginRunStatus(CancellationTokenSource cts, string text)
    {
        _runs.Add((cts, text));
        UpdateRunBar();
    }

    private void EndRunStatus(CancellationTokenSource cts)
    {
        _runs.RemoveAll(r => r.Cts == cts);
        UpdateRunBar();
    }

    private void UpdateRunBar()
    {
        RunBar.Visibility = _runs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RunBarText.Text = _runs.Count switch
        {
            0 => "",
            1 => _runs[0].Text,
            _ => L.F("명령 {0}건 실행 중", _runs.Count),
        };
    }

    private void OnCancelRuns(object sender, RoutedEventArgs e)
    {
        foreach (var r in _runs) { try { r.Cts.Cancel(); } catch { } }
        RunBarText.Text = L.T("취소하는 중...");
    }
}
