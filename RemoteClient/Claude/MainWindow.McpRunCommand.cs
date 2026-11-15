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
    /// <summary>대상 호스트별로 연결된 SSH 탭의 로그인된 연결 (UI 스레드에서 호출). 같은 호스트 탭이 여럿이면 앞의 것</summary>
    private Dictionary<Guid, (Renci.SshNet.SshClient Client, string User)> OpenSshConnections(IEnumerable<HostEntry> hosts)
    {
        var ids = hosts.Select(h => h.Id).ToHashSet();
        var map = new Dictionary<Guid, (Renci.SshNet.SshClient, string)>();
        foreach (var tab in _tabs)
            if (tab.Host is { } th && ids.Contains(th.Id) && !map.ContainsKey(th.Id)
                && tab.View is SshSessionView v && v.SharedConnection is { } c)
                map[th.Id] = c;
        return map;
    }

    // =================== 여러 서버 명령 실행 (run_command) ===================
    private async Task<string> RunCommandAsync(JsonElement a)
    {
        var specs = ListArg(a, "targets");
        var command = Arg(a, "command");
        var timeout = Math.Clamp(IntArg(a, "timeout_sec") ?? 60, 5, 1800);
        if (specs.Count == 0 || command.Trim().Length == 0) throw new McpToolException("targets 와 command 가 필요합니다.");

        // 대상 해석 (UI 스레드의 호스트 목록 사용).
        // skipped = 실제로 빠진 대상(미일치·중지), notes = 안내(여러 호스트와 일치해 모두 포함) — 승인 창의 '제외 N건' 에 안내를 세지 않는다
        var (targets, skipped, notes, accounts, auditAccounts) = await OnUi(() =>
        {
            var found = new List<HostEntry>();
            var skip = new List<string>();
            var note = new List<string>();
            foreach (var spec in specs)
            {
                var r = ResolveHosts(spec);
                if (r.Count == 0) skip.Add($"{spec}: 일치하는 호스트 없음");
                if (r.Count > 1 && !spec.StartsWith("group:", StringComparison.OrdinalIgnoreCase))
                    note.Add($"{spec}: {r.Count}개 일치 → 모두 포함");
                found.AddRange(r);
            }
            var unique = found.DistinctBy(h => h.Id).ToList();
            foreach (var h in unique.Where(h => h.IsStopped)) skip.Add($"{h.DisplayName}: 중지 상태({h.CloudState})");
            var saved = unique.Where(h => h.Protocol is Protocol.Ssh or Protocol.Rdp && !h.IsStopped).ToList();
            var run = saved.Select(WithProfileDefaults).ToList();
            // 승인 창에 보일 서버별 계정 (사용자명 + 출처: Vault·호스트·프로필 기본값). 비밀번호는 넣지 않는다.
            // 감사 로그용은 번역하지 않은 한국어로 따로 만든다 (영어 화면에서도 감사 로그는 한국어 원문)
            CloudProfile? Prof(HostEntry s) => _profiles.FirstOrDefault(p => p.Id == s.CloudProfileId);
            var acct = saved.Zip(run, (s, c) => (s.Id, Text: McpHelpers.AccountLabel(c.Username, s.CredentialSourceText(Prof(s)))))
                .ToDictionary(x => x.Id, x => x.Text);
            var auditAcct = saved.Zip(run, (s, c) => (s.Id, Text: McpHelpers.AccountLabel(c.Username, McpHelpers.CredentialSourceForAudit(s, Prof(s)), translate: false)))
                .ToDictionary(x => x.Id, x => x.Text);
            return (run, skip, note, acct, auditAcct);
        });

        if (targets.Count == 0)
            throw new McpToolException("실행할 수 있는 대상이 없습니다.\n" + string.Join("\n", skipped));

        var useCmd = Arg(a, "windows_shell").Equals("cmd", StringComparison.OrdinalIgnoreCase);
        var linux = targets.Where(h => h.Protocol == Protocol.Ssh).ToList();
        var windows = targets.Where(h => h.Protocol == Protocol.Rdp).ToList();
        var winLabel = useCmd ? "WinRM cmd" : "WinRM PowerShell";
        // 이 호스트의 SSH 탭이 열려 있으면 그 로그인된 연결로 실행한다 (OTP 서버도 다시 인증하지 않음).
        // 승인 창에 보여 준 것과 같은 연결을 쓰도록 승인 전에 정한다 — 승인을 기다리는 동안 새로 연 탭은 쓰지 않고, 그사이 끊긴 탭은 새 접속(아래 계정)으로
        var shared = await OnUi(() => OpenSshConnections(linux));
        // 시도할 주소 순서(사설 → 공인)와 계정을 승인 창에 그대로 보여준다. 이름의 보이지 않는 제어·양방향 문자는 드러낸다
        string Addrs(HostEntry h, string ports) => string.Join(" → ", h.AddressCandidates().Select(a => $"{a}:{ports}"));
        string Name(HostEntry h) => McpHelpers.VisibleControlChars(h.DisplayName);
        // ui=true: 승인 창용(번역), ui=false: 감사 로그용(번역하지 않은 한국어). 사용자명은 보이지 않는 문자를 드러낸다
        string Account(HostEntry h, bool ui) => (ui ? accounts : auditAccounts).TryGetValue(h.Id, out var t) ? t : "";
        string SshLabel(HostEntry h, bool ui)
        {
            var label = $"[SSH]  {Name(h)}  ({Addrs(h, h.Port.ToString())})  {Account(h, ui)}";
            if (!shared.TryGetValue(h.Id, out var s)) return label;
            // 열린 탭의 연결로 실행 — 탭에 로그인한 계정이 호스트 설정 계정과 다르면 분명히 알린다
            var tabUser = McpHelpers.VisibleControlChars(s.User);
            var hostUser = h.Username.Length > 0 ? McpHelpers.VisibleControlChars(h.Username) : "-";
            var same = string.Equals(s.User, h.Username, StringComparison.Ordinal);
            return label + "\n      " + (ui
                ? same ? L.F("→ 열린 터미널 탭의 연결로 실행 (계정 {0})", tabUser)
                       : L.F("→ 열린 터미널 탭의 연결로 실행 — ⚠ 탭 로그인 계정 {0} (호스트 설정 계정 {1} 과 다름)", tabUser, hostUser)
                : same ? $"→ 열린 터미널 탭의 연결로 실행 (계정 {tabUser})"
                       : $"→ 열린 터미널 탭의 연결로 실행 — ⚠ 탭 로그인 계정 {tabUser} (호스트 설정 계정 {hostUser} 과 다름)");
        }
        List<string> Labels(bool ui) => linux.Select(h => SshLabel(h, ui))
            .Concat(windows.Select(h => $"[{winLabel}]  {Name(h)}  ({Addrs(h, $"{WinRm.HttpsPort}/{WinRm.HttpPort}")})  {Account(h, ui)}"))
            .ToList();
        var labels = Labels(ui: true);
        var auditLabels = Labels(ui: false);
        var mixed = linux.Count > 0 && windows.Count > 0
            ? L.T("\n⚠ 리눅스와 Windows 서버가 섞여 있습니다. 같은 명령이 양쪽에서 동작하는지 확인하세요.") : "";
        var multi = notes.Count > 0 ? L.F("\n⚠ 여러 호스트와 일치해 모두 포함: {0}", string.Join(", ", notes.Select(McpHelpers.VisibleControlChars))) : "";
        // 설정에서 켠 경우, 대상 셸 모두에서 읽기 전용으로 판별되는 명령은 승인 창 없이 실행.
        // 단 OS 가 Windows 로 보이거나 OS 를 모르는(직접 추가한 호스트 등) SSH 대상은 Windows OpenSSH 서버(기본 셸 cmd/PowerShell)일 수 있어
        // sh 기준 판정이 맞지 않으므로 항상 승인 (echo 'x & net user a P1 /add & ' 는 sh 에선 문자열이지만 cmd 에선 net user 가 실행된다)
        var autoApproved = SettingsStore.Current.AutoApproveReadOnly
            && !linux.Any(h => McpHelpers.ShellNotKnownSh(h.Os, h.SshBannerOs))
            && (linux.Count == 0 || ReadOnlyCommands.IsReadOnly(command, CommandShell.Sh))
            && (windows.Count == 0 || ReadOnlyCommands.IsReadOnly(command, useCmd ? CommandShell.Cmd : CommandShell.PowerShell));
        if (!autoApproved)
        {
            var approved = await OnUiAsync(() => AskApprovalAsync("run_command", L.F("Claude 가 서버 {0}대에 명령 실행을 요청합니다", targets.Count),
                L.T("승인하면 아래 서버들에 접속해 명령을 실행하고 결과를 Claude 에게 돌려줍니다.") + mixed + multi +
                (skipped.Count > 0 ? L.F("\n제외: {0}건 (중지·미일치)", skipped.Count) : "") +
                L.F("\n제한 시간: 서버당 {0}초 (넘으면 중단)", timeout), labels, command));
            if (!approved)
            {
                AuditLog.Write("run_command", auditLabels, command, "거부");
                throw new McpToolException("사용자가 실행을 거부했습니다.");
            }
        }
        // 실행 전에 먼저 기록한다 — 앱이 실행 중에 끝나거나 멈춰도 '무엇을 어디에 실행했는지' 는 남게
        AuditLog.Write("run_command", auditLabels, command, $"{(autoApproved ? "자동 승인(읽기 전용)" : "승인")}·실행 시작 (제한 {timeout}초)");

        // 실행 중에는 Claude 패널 위에 "실행 중 · 취소" 줄을 보여 준다 (Claude CLI 에서 Esc 를 눌러도 앱의 실행은 계속되므로)
        using var cts = new CancellationTokenSource();
        // Claude 에서 요청을 취소하면(Esc) 실행 중 표시줄의 취소와 같게 처리한다
        using var reqCancel = McpRequest.Token.Register(() => { try { cts.Cancel(); } catch (ObjectDisposedException) { } });
        var started = DateTime.Now;
        await OnUi(() => { BeginRunStatus(cts, L.F("서버 {0}대에서 명령 실행 중: {1}", targets.Count, FirstLine(command))); return 0; });
        List<ExecResult> results;
        try
        {
            var sshTask = SshExec.RunAsync(linux, command, timeout, cts.Token, shared);
            var winTask = WinRm.RunAsync(windows, command, !useCmd, timeout, cts.Token);
            // 취소하면 기다리지 않고 바로 돌아온다 (아직 시작 안 한 서버는 실행하지 않고, 실행 중인 명령에는 SSH 는 SIGTERM · WinRM 은 셸 삭제로 중단을 시도)
            var all = Task.WhenAll(sshTask, winTask);
            // WaitAsync 는 끝나면 취소 등록을 바로 풀어 준다 (예전 Task.Delay(Infinite, token) 은 실행이 끝난 뒤에도 등록이 남았다)
            // 여기서는 끝나기(또는 취소)만 기다린다 — 실행 오류는 아래 await all 에서 올린다 (예전 WhenAny 와 같은 동작:
            // 오류와 취소가 겹쳐도 취소 안내가 나가게)
            try { await all.WaitAsync(cts.Token); }
            catch { }
            // 취소가 모든 실행이 끝난 직후에 들어왔으면(실행 중 표시줄의 취소와 완료가 겹침) 실제로 실행된 결과를 '취소' 로 버리지 않고 돌려준다
            if (all.Status != TaskStatus.RanToCompletion && cts.IsCancellationRequested)
            {
                _ = all.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                AuditLog.Write("run_command", auditLabels, command, "승인·실행 후 사용자가 취소");
                throw new McpToolException($"사용자가 실행을 취소했습니다 ({(DateTime.Now - started).TotalSeconds:0}초 경과). " +
                    "아직 시작하지 않은 서버에는 보내지 않았고, 실행 중이던 명령에는 중단 신호(SSH SIGTERM · WinRM 셸 삭제)를 보냈지만 " +
                    "서버가 신호를 무시하거나 이미 일부를 실행했을 수 있습니다.");
            }
            // 실행 오류(서버별 오류는 결과에 담기므로 여기 오는 건 예상 밖 예외)는 감사 로그에 남기고 그대로 올린다
            try { await all; }
            catch (Exception ex)
            {
                AuditLog.Write("run_command", auditLabels, command, $"승인·실행 중 오류: {CloudProviders.FirstLine(ex.Message)}");
                throw;
            }
            results = sshTask.Result.Concat(winTask.Result).ToList();
        }
        finally
        {
            await OnUi(() => { EndRunStatus(cts); return 0; });
        }
        var okCount = results.Count(r => r.Ok);
        AuditLog.Write("run_command", auditLabels, command, $"{(autoApproved ? "자동 승인(읽기 전용)" : "승인")}·실행 (성공 {okCount} / 실패 {results.Count - okCount})");

        var sb = new StringBuilder($"실행 완료: 성공 {okCount} / 실패 {results.Count - okCount} (총 {results.Count}대)\n");
        if (skipped.Count > 0) sb.AppendLine("제외: " + string.Join("; ", skipped));
        if (notes.Count > 0) sb.AppendLine("참고: " + string.Join("; ", notes));
        // 대수가 많으면 서버당 출력을 줄여 전체를 약 60,000자 안으로 (Claude 컨텍스트 절약)
        var perHost = Math.Clamp(60_000 / Math.Max(1, results.Count), 1_000, 8_000);
        foreach (var r in results.OrderBy(r => r.Ok).ThenBy(r => r.Host.DisplayName))
        {
            sb.AppendLine();
            sb.AppendLine($"## {r.Host.DisplayName} ({r.Host.PrimaryAddress}{(r.Host.HasPublicIp ? $" / 공인 {r.Host.PublicIp}" : "")}) — {(r.ExitCode is { } c ? $"exit {c}" : "실패")}, {r.ElapsedMs / 1000.0:0.0}s");
            if (r.Note is { Length: > 0 } note) sb.AppendLine($"[{note}]");
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
