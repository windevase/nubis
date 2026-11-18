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
    // =================== SFTP ===================

    private const string SessionIdHelp =
        "열린 SSH 탭의 session_id (list_sessions). 주면 그 탭의 로그인된 연결로 SFTP 를 연다 — 다시 인증하지 않아 OTP 서버도 되고, " +
        "탭의 SFTP 패널이 sudo 로 다른 사용자(root 등)로 전환돼 있으면 그 권한으로 연다. host 와 함께 주면 같은 호스트여야 한다.";

    /// <summary>SFTP 읽기·전송 도구 등록 (BuildTools 에서). host 또는 session_id 중 하나가 필요</summary>
    private IEnumerable<McpTool> SftpTools()
    {
        yield return new McpTool("sftp_list",
            "SSH 호스트의 원격 폴더 목록 (이름·종류·크기·수정 시각·권한). 승인 규칙은 run_command 와 같다(설정에 따라 읽기 전용 자동 승인, " +
            "단 탭의 sudo 권한으로 열 때는 항상 승인). host 또는 session_id 필요.",
            Schema(new
            {
                host = new { type = "string" },
                session_id = new { type = "integer", description = SessionIdHelp },
                path = new { type = "string", description = "원격 경로 (생략 시 홈)" },
            }),
            a => SftpListAsync(a));

        yield return new McpTool("sftp_read",
            "SSH 호스트의 원격 텍스트 파일을 읽는다 (로그·설정 확인용, 기본 64KB, 최대 1MB). tail=true 면 끝부분. 바이너리 파일은 거부. " +
            "결과 둘째 줄의 mtime_utc · size 는 sftp_write 의 expected_mtime_utc · expected_size 로 쓴다. 승인 규칙은 run_command 와 같다.",
            Schema(new
            {
                host = new { type = "string" },
                session_id = new { type = "integer", description = SessionIdHelp },
                path = new { type = "string" },
                max_bytes = new { type = "integer", description = "읽을 최대 바이트 (기본 65536, 최대 1048576)" },
                tail = new { type = "boolean", description = "파일 끝부분을 읽기 (로그)" },
            }, "path"),
            a => SftpReadAsync(a));

        yield return new McpTool("sftp_download",
            "SSH 호스트의 원격 파일·폴더를 이 PC 의 다운로드\\Nubis\\<호스트> 폴더로 받는다 (경로는 앱이 정함, 기존 파일은 덮어쓰지 않음). 항상 사용자 승인. " +
            $"여러 개는 paths 로 한 번에 (승인 한 번, 최대 {McpSftpRules.MaxMultiPaths}개) — 없는 경로 등 받지 못한 항목은 결과에 따로 적는다.",
            Schema(new
            {
                host = new { type = "string" },
                session_id = new { type = "integer", description = SessionIdHelp },
                path = new { type = "string", description = "원격 파일 또는 폴더 (paths 와 둘 중 하나)" },
                paths = new { type = "array", items = new { type = "string" }, description = "원격 파일·폴더 여러 개 (path 와 둘 중 하나)" },
            }),
            a => SftpDownloadAsync(a));

        yield return new McpTool("sftp_upload",
            "이 PC 의 파일·폴더를 SSH 호스트의 원격 폴더로 올린다 (폴더는 하위까지, 원격에 같은 폴더가 있으면 병합). " +
            "같은 이름 파일 처리는 on_conflict: overwrite = 덮어씀, skip = 건너뜀, fail(기본) = 하나라도 있으면 아무것도 올리지 않음 — SFTP 패널의 덮어쓰기/건너뛰기와 같다. " +
            "예전 인자 overwrite=true 는 on_conflict=overwrite 와 같다. 대상(또는 폴더 자리)이 심볼릭 링크면 링크가 가리키는 곳에 쓰게 되므로 쓰지 않는다 (skip 이면 건너뜀, 아니면 거부). " +
            $"여러 개는 local_paths 로 (최대 {McpSftpRules.MaxMultiPaths}개, 승인 한 번). 항상 사용자 승인. Nubis 데이터 폴더와 ~/.ssh, 개인키 파일은 올릴 수 없다.",
            Schema(new
            {
                host = new { type = "string" },
                session_id = new { type = "integer", description = SessionIdHelp },
                local_path = new { type = "string", description = "이 PC 의 파일 또는 폴더 (절대 경로, local_paths 와 둘 중 하나)" },
                local_paths = new { type = "array", items = new { type = "string" }, description = "이 PC 의 파일·폴더 여러 개 (절대 경로)" },
                remote_dir = new { type = "string", description = "올릴 원격 폴더" },
                on_conflict = new { type = "string", @enum = McpSftpRules.UploadConflictModes, description = "같은 이름이 있을 때: overwrite / skip / fail (기본 fail)" },
                overwrite = new { type = "boolean", description = "예전 인자 — true 는 on_conflict=overwrite" },
            }, "remote_dir"),
            a => SftpUploadAsync(a));

        yield return new McpTool("sftp_panel_navigate",
            "SSH 탭 왼쪽 SFTP 패널이 보여 주는 폴더를 바꾼다 (패널 경로 칸에 입력하는 것과 같음 — 사용자 화면만 바뀌고 목록은 Claude 에게 돌려주지 않는다, " +
            "목록이 필요하면 sftp_list). 승인 규칙은 sftp_list 와 같다 (패널이 sudo 권한이면 항상 승인). 패널이 열려 있어야 한다 (toggle_sftp_panel). path 는 절대 경로. session_id 생략 시 현재 탭.",
            Schema(new
            {
                session_id = new { type = "integer" },
                path = new { type = "string", description = "표시할 원격 폴더의 절대 경로" },
            }, "path"),
            a => OnUiAsync(() => SftpPanelNavigateAsync(a)));
    }

    /// <summary>
    /// 문자열 목록 인자 (paths · local_paths). 없거나 null 이면 null, 배열이 아니거나 문자열이 아닌 항목이 있으면 오류
    /// (잘못된 값을 말없이 빼고 나머지만 처리하지 않게)
    /// </summary>
    private static List<string>? StringListArg(JsonElement a, string name)
    {
        if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind != JsonValueKind.Array) throw new McpToolException($"{name} 는 문자열 배열이어야 합니다.");
        var list = new List<string>();
        foreach (var x in v.EnumerateArray())
        {
            if (x.ValueKind != JsonValueKind.String) throw new McpToolException($"{name} 의 항목은 모두 문자열이어야 합니다.");
            list.Add(x.GetString() ?? "");
        }
        return list;
    }

    /// <summary>sftp_panel_navigate: 패널의 터미널 따라가기와 같은 경로(FollowAsync)로 이동 — 전송 중이면 끝난 뒤 이동</summary>
    private async Task<string> SftpPanelNavigateAsync(JsonElement a)
    {
        if (McpSftpRules.ValidateRemotePath(Arg(a, "path"), out var path) is { } why) throw new McpToolException($"path: {why}");
        var tab = RequireSession(a, allowCurrent: true);
        if (tab.View is not SshSessionView ssh) throw new McpToolException($"session {tab.Id} 은 SSH 탭이 아닙니다 (SFTP 패널은 SSH 탭에만 있음).");
        var panel = FindSftpPanels(ssh).FirstOrDefault(p => p.Visibility == System.Windows.Visibility.Visible);
        if (panel == null || !panel.IsOpen)
            throw new McpToolException($"session {tab.Id} 의 SFTP 패널이 열려 있지 않습니다. toggle_sftp_panel 로 먼저 여세요.");
        // 패널 목록은 capture_screen 으로 읽을 수 있으므로 sftp_list 와 같은 승인 규칙: 읽기 전용 자동 승인 설정이 꺼져 있거나,
        // 패널이 sudo 권한이거나, 비밀이 든 경로면 승인 창
        var command = $"(SFTP 패널 폴더 이동: {path})";
        var targets = new[] { SessionAuditTarget(tab) };
        if (McpSftpRules.PanelNavigateNeedsApproval(SettingsStore.Current.AutoApproveReadOnly, panel.RunAs != null, McpHelpers.IsSensitiveRemotePath(path)))
        {
            var ok = await AskApprovalAsync("sftp_panel_navigate", L.T("Claude 가 SFTP 패널 폴더 이동을 요청합니다"),
                L.T("승인하면 SFTP 패널이 아래 폴더의 목록을 보여 줍니다. 화면 캡처로 Claude 가 그 목록을 볼 수 있습니다.") +
                (panel.RunAs is { } ra ? L.F("\n⚠ 이 탭의 SFTP 가 sudo 로 {0} 권한으로 열려 있어, 이 작업도 {0} 권한으로 실행됩니다.", McpHelpers.VisibleControlChars(ra)) : ""),
                targets, command);
            AuditLog.Write("sftp_panel_navigate", targets, command, ok ? "승인" : "거부");
            if (!ok) throw new McpToolException("사용자가 거부했습니다.");
            if (!_tabs.Contains(tab) || !panel.IsOpen) throw new McpToolException($"승인을 기다리는 사이 session {tab.Id} 탭이나 SFTP 패널이 닫혔습니다.");
        }
        else AuditLog.Write("sftp_panel_navigate", targets, command, "자동 승인(읽기 전용)");
        // 패널이 sudo 로 다른 사용자(root 등)로 열려 있으면 이동 성공 여부도 알려 주지 않는다 —
        // 승인 없이 그 권한으로만 보이는 폴더가 있는지 Claude 가 알아내지 않게 (목록은 sftp_list 의 승인을 거친다)
        var elevated = panel.RunAs != null;
        await panel.FollowAsync(path);
        if (elevated || panel.RunAs != null)
            return $"session {tab.Id} 의 SFTP 패널에 {path} 이동을 요청했습니다 (sudo 권한 패널이라 결과는 알려 주지 않음 — 사용자 화면에 표시됨).";
        var now = panel.PathBox.Text;
        return now == path
            ? $"session {tab.Id} 의 SFTP 패널에 {path} 를 표시했습니다."
            : $"session {tab.Id} 의 SFTP 패널이 {path} 로 가지 못했습니다 (지금 표시: {now}). 없는 폴더·권한 없음이거나 전송 중이면 끝난 뒤 이동합니다 — 패널에 오류가 표시됩니다.";
    }

    /// <summary>
    /// SFTP 대상: 호스트 + 여는 방법. Label 은 승인 창·감사 로그의 대상 줄(번역할 말 없음), Note 는 승인 창 설명에 붙일 안내(번역됨),
    /// Open 은 백그라운드 스레드에서 연결을 연다 (새 접속 또는 열린 SSH 탭의 연결 — OperationTimeout 은 Connect 전에 정해짐)
    /// </summary>
    private sealed record SftpTarget(HostEntry Host, string Label, string Note, bool Elevated, Func<Renci.SshNet.SftpClient> Open);

    /// <summary>
    /// SFTP 대상 (SSH 만, 중지 서버 제외). session_id 가 있으면 그 SSH 탭의 로그인된 연결 위에 SFTP 채널을 연다
    /// (다시 인증하지 않음 — OTP 서버도 됨, 탭의 SFTP 가 sudo 로 다른 사용자면 같은 권한). 여는 방법은 여기(승인 전)에서 정한다 —
    /// 승인을 기다리는 사이 탭이 끊기면 새 접속으로 넘어가지 않고 실패한다 (승인 창에 보인 것과 다른 연결·계정을 쓰지 않게)
    /// </summary>
    private async Task<SftpTarget> SftpTargetAsync(JsonElement a)
    {
        var sid = IntArg(a, "session_id");
        var spec = Arg(a, "host");
        return await OnUi(() =>
        {
            string Addrs(HostEntry h) => string.Join(" → ", h.AddressCandidates());
            if (sid == null)
            {
                if (spec.Trim().Length == 0) throw new McpToolException("host 또는 session_id 가 필요합니다.");
                var h = ResolveOneHost(spec);
                CheckSftpHost(h);
                var conn = WithProfileDefaults(h);
                return new SftpTarget(h, $"[SFTP]  {McpHelpers.VisibleControlChars(h.DisplayName)}  ({Addrs(h)})", "", false, () => SshExec.OpenSftp(conn));
            }
            var tab = _tabs.FirstOrDefault(t => t.Id == sid) ?? throw new McpToolException($"session {sid} 탭이 없습니다. list_sessions 로 확인하세요.");
            if (tab.View is not SshSessionView v || tab.Host is not { } th) throw new McpToolException($"session {sid} 는 SSH 탭이 아닙니다.");
            if (spec.Trim().Length > 0 && ResolveOneHost(spec).Id != th.Id)
                throw new McpToolException($"host '{spec}' 와 session {sid} 의 호스트({th.DisplayName})가 다릅니다. 하나만 지정하세요.");
            var saved = _hosts.FirstOrDefault(x => x.Id == th.Id) ?? th;
            if (saved.Protocol != Protocol.Ssh) throw new McpToolException($"session {sid} 는 SSH 탭이 아닙니다.");
            var access = v.McpSftpAccess() ?? throw new McpToolException($"session {sid} 탭이 연결된 상태가 아닙니다 (연결 중이거나 끊김).");
            var user = McpHelpers.VisibleControlChars(access.User);
            var runAs = access.RunAs is { } r ? McpHelpers.VisibleControlChars(r) : null;
            var label = $"[SFTP]  {McpHelpers.VisibleControlChars(saved.DisplayName)}  (session {sid}: {user}{(runAs != null ? $" → sudo {runAs}" : "")})";
            var note = L.F("\n열린 터미널 탭(session {0})의 로그인된 연결을 씁니다 (다시 인증하지 않음, 계정 {1}).", sid, user) +
                       (runAs != null ? L.F("\n⚠ 이 탭의 SFTP 가 sudo 로 {0} 권한으로 열려 있어, 이 작업도 {0} 권한으로 실행됩니다.", runAs) : "");
            return new SftpTarget(saved, label, note, runAs != null, access.Open);
        });
    }

    private static void CheckSftpHost(HostEntry h)
    {
        if (h.Protocol != Protocol.Ssh) throw new McpToolException($"'{h.DisplayName}' 는 RDP(Windows) 호스트입니다. SFTP 는 SSH 호스트만 됩니다.");
        if (h.IsStopped) throw new McpToolException($"'{h.DisplayName}' 는 중지 상태입니다 ({h.CloudState}).");
    }

    /// <summary>
    /// 원격 읽기: run_command 의 읽기 전용 자동 승인 설정을 따른다. 단 sensitive(개인키·/etc/shadow·.env 등 비밀이 든 경로)면 항상 승인 창.
    /// 탭의 sudo 권한으로 읽는 경우도 항상 승인 창 (로그인 계정으로는 못 읽는 파일을 읽을 수 있음).
    /// 승인 창을 띄웠으면 true, 자동 승인이면 false (거부면 McpToolException)
    /// </summary>
    private async Task<bool> ApproveRemoteRead(string tool, SftpTarget t, string what, bool sensitive = false)
    {
        var target = t.Label;
        if (SettingsStore.Current.AutoApproveReadOnly && !sensitive && !t.Elevated) { AuditLog.Write(tool, new[] { target }, what, "자동 승인(읽기 전용)"); return false; }
        var ok = await OnUiAsync(() => AskApprovalAsync(tool, L.T("Claude 가 원격 파일 읽기를 요청합니다"),
            L.T("승인하면 서버에 SFTP 로 접속해 아래 내용을 읽어 Claude 에게 돌려줍니다 (서버는 바꾸지 않음).") +
            (sensitive ? L.T("\n⚠ 개인키·비밀번호 해시·환경 변수 파일처럼 비밀 정보가 들어 있을 수 있는 경로입니다. 읽은 내용은 Claude 에게 전달됩니다.") : "") +
            t.Note,
            new[] { target }, what));
        AuditLog.Write(tool, new[] { target }, what, ok ? "승인" : "거부");
        if (!ok) throw new McpToolException("사용자가 거부했습니다.");
        return true;
    }

    /// <summary>SFTP 작업 하나(요청)의 응답 제한 시간 — 서버가 멈춰도 MCP 요청이 끝없이 걸려 있지 않게 (SharedSftpClient.RequestTimeout 과 같은 값)</summary>
    private static readonly TimeSpan SftpOperationTimeout = SharedSftpClient.RequestTimeout;

    /// <summary>연결 열기 (백그라운드 스레드). 열기 실패는 McpToolException 으로 — 비밀번호는 어떤 메시지에도 들어가지 않는다</summary>
    private static Renci.SshNet.SftpClient OpenSftpClient(SftpTarget t)
    {
        try { return t.Open(); }
        catch (InvalidOperationException ex) { throw new McpToolException(ex.Message); }
        catch (SudoSftp.SudoException ex) { throw new McpToolException($"탭의 sudo 권한으로 SFTP 를 열지 못했습니다: {ex.Message}"); }
        catch (SharedSftpClient.ServerCommandException ex) { throw new McpToolException($"탭의 연결에서 SFTP 를 열지 못했습니다: {ex.Message}"); }
        catch (Renci.SshNet.Common.SshException ex) { throw new McpToolException($"SFTP 를 열지 못했습니다: {ex.Message}"); }
    }

    /// <summary>SFTP 예외를 MCP 오류 문구로 (progress 를 주면 진행 상황을 붙인다)</summary>
    private static T MapSftpErrors<T>(Func<T> f, CancellationToken ct, Func<string>? progress = null)
    {
        string P() => progress?.Invoke() is { Length: > 0 } s ? $" ({s})" : "";
        try { return f(); }
        catch (Renci.SshNet.Common.SftpPathNotFoundException) { throw new McpToolException("원격 경로가 없습니다." + P()); }
        catch (Renci.SshNet.Common.SftpPermissionDeniedException) { throw new McpToolException("원격 경로에 권한이 없습니다." + P()); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw new McpToolException("요청이 취소되어 중단했습니다." + P()); }
        catch (Renci.SshNet.Common.SshOperationTimeoutException)
        {
            throw new McpToolException($"SFTP 서버가 {SftpOperationTimeout.TotalSeconds:0}초 동안 응답하지 않아 중단했습니다." + P());
        }
        catch (McpToolException ex) when (progress != null) { throw new McpToolException(ex.Message + P()); }
        catch (Exception ex) when (progress != null) { throw new McpToolException($"전송 중 오류: {ex.Message}" + P()); }
    }

    /// <summary>
    /// SFTP 로 접속해 f 실행. Claude 요청이 취소되면(McpRequest.Token) 다음 파일·다음 읽기/쓰기에서 멈춘다.
    /// progress 를 주면 실패·취소 메시지에 진행 상황("파일 10개 중 3개 올림")을 붙인다.
    /// </summary>
    private static async Task<T> WithSftp<T>(SftpTarget t, Func<Renci.SshNet.SftpClient, CancellationToken, T> f, Func<string>? progress = null)
    {
        var ct = McpRequest.Token;
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            using var c = OpenSftpClient(t);
            return MapSftpErrors(() => f(c, ct), ct, progress);
        }, ct);
    }

    private static Task<T> WithSftp<T>(SftpTarget t, Func<Renci.SshNet.SftpClient, T> f) => WithSftp(t, (c, _) => f(c));

    /// <summary>링크를 따라가지 않는 속성 (SSH_FXP_LSTAT) — 없으면 null. 상대 경로는 서버 작업 폴더 기준으로 붙인다</summary>
    private static Renci.SshNet.Sftp.SftpFileAttributes? TryLStat(Renci.SshNet.SftpClient c, string path)
    {
        var abs = path.StartsWith('/') ? path : SftpPanel.Combine(c.WorkingDirectory, path);
        try { return SftpRaw.LStat(c, abs); }
        catch (Renci.SshNet.Common.SftpPathNotFoundException) { return null; }
    }

    /// <summary>
    /// 덮어쓰기 전 링크 확인 (sftp_upload · sftp_write): 거부 사유 또는 null. LSTAT 을 못 쓰면 언제나 거부 —
    /// Exists 는 REALPATH·STAT 이라 끊긴 링크를 "없음"으로 보고, 그 경로에 쓰면 링크가 가리키는 파일이 새로 만들어진다
    /// </summary>
    private static string? OverwriteLinkRefusal(Renci.SshNet.SftpClient c, string path)
    {
        if (!SftpRaw.LStatSupported) return McpSftpRules.LinkOverwriteRefusal(path, true, null);
        var attr = TryLStat(c, path);
        return McpSftpRules.LinkOverwriteRefusal(path, attr != null, attr?.IsSymbolicLink);
    }

    private async Task<string> SftpListAsync(JsonElement a)
    {
        var t = await SftpTargetAsync(a);
        var h = t.Host;
        var path = Arg(a, "path");
        await ApproveRemoteRead("sftp_list", t, $"폴더 목록: {(path.Length > 0 ? path : "(홈)")}");
        return await WithSftp(t, c =>
        {
            var dir = path.Length > 0 ? path : c.WorkingDirectory;
            var items = c.ListDirectory(dir).Where(f => f.Name is not "." and not "..")
                .OrderByDescending(f => f.IsDirectory).ThenBy(f => f.Name, StringComparer.Ordinal).ToList();
            var sb = new StringBuilder($"[{h.DisplayName}] {dir} — {items.Count}개{(items.Count > 500 ? " (앞 500개)" : "")}\n종류 | 권한 | 크기 | 수정 시각 | 이름\n");
            foreach (var f in items.Take(500))
                sb.AppendLine($"{(f.IsDirectory ? "dir" : f.IsSymbolicLink ? "link" : "file")} | {Perms(f)} | {(f.IsDirectory ? "-" : SftpPanel.HumanSize(f.Length))} | {f.LastWriteTime:yyyy-MM-dd HH:mm} | {f.Name}");
            return sb.ToString();
        });
    }

    private static string Perms(Renci.SshNet.Sftp.ISftpFile f) =>
        (f.OwnerCanRead ? "r" : "-") + (f.OwnerCanWrite ? "w" : "-") + (f.OwnerCanExecute ? "x" : "-") +
        (f.GroupCanRead ? "r" : "-") + (f.GroupCanWrite ? "w" : "-") + (f.GroupCanExecute ? "x" : "-") +
        (f.OthersCanRead ? "r" : "-") + (f.OthersCanWrite ? "w" : "-") + (f.OthersCanExecute ? "x" : "-");

    private async Task<string> SftpReadAsync(JsonElement a)
    {
        var t = await SftpTargetAsync(a);
        var h = t.Host;
        var path = Arg(a, "path");
        // 빈 경로는 승인 창을 띄운 뒤 홈 폴더로 풀려 '폴더입니다' 로 끝난다 — 승인 전에 거른다
        if (path.Trim().Length == 0) throw new McpToolException("path 가 필요합니다 (읽을 원격 파일 경로).");
        var max = Math.Clamp(IntArg(a, "max_bytes") ?? 65536, 1024, 1048576);
        var tail = BoolArg(a, "tail") == true;
        var what = $"파일 읽기: {path} ({(tail ? "끝 " : "앞 ")}{SftpPanel.HumanSize(max)})";
        // 개인키·/etc/shadow·.env 처럼 비밀이 든 경로는 자동 승인 설정이 켜져 있어도 승인 창
        var asked = await ApproveRemoteRead("sftp_read", t, what, McpHelpers.IsSensitiveRemotePath(path));
        return await WithSftp(t, c =>
        {
            // 자동 승인됐으면 심볼릭 링크를 따라간 실제 경로로 한 번 더 본다 (/tmp/x → /etc/shadow 같은 우회 방지).
            // Get 은 서버의 realpath 로 경로를 풀어 준다 (OpenSSH). 비밀 경로면 이제라도 승인을 받는다.
            // 그리고 확인한 그 실제 경로를 연다 — 확인 뒤 원래 경로의 링크를 바꿔치기해도 확인하지 않은 파일을 읽지 않게.
            // 한계: realpath 를 풀어 주지 않는 SFTP 서버, 하드 링크, 실제 경로의 상위 폴더를 그사이 링크로 바꾸는 경우는 막지 못한다
            var readPath = path;
            if (!asked)
            {
                var real = c.Get(path).FullName;
                if (real != path && McpHelpers.IsSensitiveRemotePath(real))
                    ApproveRemoteRead("sftp_read", t, $"{what}\n실제 경로: {real}", sensitive: true).GetAwaiter().GetResult();
                if (real.Length > 0) readPath = real;
            }
            var attr = c.GetAttributes(readPath);
            if (attr.IsDirectory) throw new McpToolException("폴더입니다. sftp_list 를 쓰세요.");
            // 장치·FIFO·소켓은 읽지 않는다 (FIFO 는 쓰는 쪽이 없으면 응답 제한 시간까지 멈춘다)
            if (!attr.IsRegularFile) throw new McpToolException("일반 파일이 아닙니다 (장치·FIFO·소켓 등).");
            using var s = c.OpenRead(readPath);
            var size = attr.Size;
            var fromMiddle = tail && size > max;
            if (fromMiddle) s.Seek(size - max, SeekOrigin.Begin);
            var buf = new byte[Math.Min(max, size > 0 ? size : max)];
            var read = 0;
            while (read < buf.Length) { var n = s.Read(buf, read, buf.Length - read); if (n <= 0) break; read += n; }
            var data = buf.AsSpan(0, read);
            if (McpHelpers.LooksBinary(data)) throw new McpToolException($"바이너리 파일로 보입니다 ({SftpPanel.HumanSize(size)}). 필요하면 sftp_download 로 받으세요.");
            // 파일 중간에서 시작하거나(끝 읽기) 중간에서 끊겼으면(앞 읽기) 경계에서 잘린 UTF-8 글자를 버린다
            var text = McpHelpers.DecodeUtf8Slice(data, cutStart: fromMiddle, cutEnd: !tail && size > read);
            var cut = size > read ? (tail ? $" (끝 {SftpPanel.HumanSize(read)}만)" : $" (앞 {SftpPanel.HumanSize(read)}만)") : "";
            // 수정 시각·크기: sftp_write 로 고칠 때 expected_mtime_utc · expected_size 로 그대로 넘긴다 (읽은 뒤 바뀌었는지 확인)
            return $"[{h.DisplayName}] {path} — {SftpPanel.HumanSize(size)}{cut}\n" +
                   $"mtime_utc={McpSftpRules.FormatMtime(attr.LastWriteTimeUtc)} size={size}\n{text}";
        });
    }
}
