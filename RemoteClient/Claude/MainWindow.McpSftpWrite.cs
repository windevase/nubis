using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace Nubis;

// MCP 도구: SFTP 쓰기 (새 폴더 · 이름 변경 · 삭제 · 편집 저장) — UI 에서 손으로 하는 동작을 Claude 도 할 수 있게 (BuildTools 에서 등록)
// 공통 흐름: 연결 → 승인 전 확인(서버를 바꾸지 않음: 있는지·링크인지·항목 수·이전 내용) → 승인 창 → 승인 뒤 다시 확인 → 실행 → 감사 로그.
// 감사 로그는 승인 · 거부 · 완료 · 실패를 모두 남긴다. 링크 삭제·이름 변경은 SftpRaw(REALPATH 없이)로만 — DeleteFile/RenameFile 은 링크를 따라간다
public partial class MainWindow
{
    /// <summary>sftp_write 로 쓸 수 있는 최대 크기 (UTF-8)</summary>
    private const int MaxSftpWriteBytes = 1024 * 1024;
    /// <summary>sftp_delete 가 세는 최대 항목 수 — 넘으면 지우지 않는다 (승인 창에 정확한 수를 보일 수 없음)</summary>
    private const int MaxSftpDeleteItems = 20000;

    private IEnumerable<McpTool> SftpWriteTools()
    {
        yield return new McpTool("sftp_mkdir",
            "SSH 호스트에 원격 폴더를 하나 만든다 (상위 폴더는 있어야 함). 항상 사용자 승인. path 는 절대 경로. host 또는 session_id 필요.",
            Schema(new
            {
                host = new { type = "string" },
                session_id = new { type = "integer", description = SessionIdHelp },
                path = new { type = "string", description = "만들 폴더의 절대 경로 (예: /home/user/new)" },
            }, "path"),
            a => SftpMkdirAsync(a));

        yield return new McpTool("sftp_rename",
            "원격 파일·폴더·심볼릭 링크의 이름을 같은 폴더 안에서 바꾼다 (SFTP 패널의 이름 변경과 같음, 이미 있는 이름으로는 바꾸지 않음). " +
            "링크는 링크 자체의 이름만 바뀐다. 항상 사용자 승인. host 또는 session_id 필요.",
            Schema(new
            {
                host = new { type = "string" },
                session_id = new { type = "integer", description = SessionIdHelp },
                path = new { type = "string", description = "바꿀 항목의 절대 경로" },
                new_name = new { type = "string", description = "새 이름 ('/' 없이 이름만)" },
            }, "path", "new_name"),
            a => SftpRenameAsync(a));

        yield return new McpTool("sftp_delete",
            "원격 파일·폴더·심볼릭 링크를 삭제한다. 폴더는 안의 내용까지 모두 지운다 (되돌릴 수 없음). 링크는 링크만 지우고 따라 들어가지 않는다. " +
            $"승인 창에 지울 항목 수를 보인다. 항목이 {MaxSftpDeleteItems}개를 넘거나 / · 최상위 시스템 폴더 · 홈 폴더는 지우지 않는다. " +
            $"여러 개는 paths 로 (최대 {McpSftpRules.MaxMultiPaths}개, 승인 한 번 — 서로 품는 경로는 거부, 항목 수 한도는 전체 합계). " +
            "승인 뒤 없어졌거나 종류가 바뀐 항목·지우다 실패한 항목은 건너뛰고 결과에 적는다. host 또는 session_id 필요.",
            Schema(new
            {
                host = new { type = "string" },
                session_id = new { type = "integer", description = SessionIdHelp },
                path = new { type = "string", description = "지울 항목의 절대 경로 (paths 와 둘 중 하나)" },
                paths = new { type = "array", items = new { type = "string" }, description = "지울 항목 여러 개의 절대 경로" },
            }),
            a => SftpDeleteAsync(a));

        yield return new McpTool("sftp_write",
            "원격 텍스트 파일을 content 로 저장한다 (UTF-8, 최대 1MB, 임시 파일에 쓴 뒤 바꿔치기 — 권한 유지). " +
            "있는 파일을 고치려면 먼저 sftp_read 로 읽고 그 결과의 mtime_utc · size 를 expected_mtime_utc · expected_size 로 준다 — " +
            "읽은 뒤 서버 파일이 바뀌었으면 쓰지 않는다. 새 파일은 create=true. 대상이 심볼릭 링크면 거부한다(실제 파일 경로를 지정). " +
            "승인 창에 바뀜 요약을 보인다. host 또는 session_id 필요.",
            Schema(new
            {
                host = new { type = "string" },
                session_id = new { type = "integer", description = SessionIdHelp },
                path = new { type = "string", description = "저장할 파일의 절대 경로" },
                content = new { type = "string", description = "파일 전체 내용 (텍스트)" },
                expected_mtime_utc = new { type = "string", description = "sftp_read 결과의 mtime_utc (있는 파일을 덮어쓸 때 필수)" },
                expected_size = new { type = "integer", description = "sftp_read 결과의 size (있는 파일을 덮어쓸 때 필수)" },
                create = new { type = "boolean", description = "true 면 없는 파일만 새로 만든다" },
            }, "path", "content"),
            a => SftpWriteAsync(a));
    }

    // ---------------- 공통 ----------------

    /// <summary>링크를 그대로 다루는 SftpRaw(LSTAT · REMOVE · RENAME)를 쓸 수 없으면 쓰기 도구를 쓰지 않는다 (DeleteFile/RenameFile 로 대신하지 않음)</summary>
    private static void RequireRawSftp()
    {
        if (!SftpRaw.Supported || !SftpRaw.LStatSupported || !SftpRaw.RmDirSupported)
            throw new McpToolException("이 SSH.NET 버전에서는 심볼릭 링크를 안전하게 다룰 수 없어 SFTP 쓰기 도구를 쓸 수 없습니다.");
    }

    /// <summary>경로 인자 검사 (절대 경로, '.'·'..'·제어 문자 없음) 후 마지막 이름을 패널 규칙(ValidateRemoteName)으로 검사</summary>
    private static string WritePathArg(JsonElement a, string name = "path") => WritePath(Arg(a, name), name);

    /// <summary>WritePathArg 의 값 버전 (paths 목록의 항목마다)</summary>
    private static string WritePath(string raw, string name)
    {
        if (McpSftpRules.ValidateRemotePath(raw, out var p) is { } why) throw new McpToolException($"{name}: {why}");
        if (p != "/" && McpSftpRules.ValidateName(McpHelpers.RemoteFileName(p)) is { } bad) throw new McpToolException($"{name}: {bad}");
        return p;
    }

    private static string KindText(SftpFileAttributes a) =>
        a.IsSymbolicLink ? "심볼릭 링크" : a.IsDirectory ? "폴더" : a.IsRegularFile ? "파일" : "특수 파일";

    /// <summary>
    /// 삭제 · 이름 변경 전 확인: 홈 폴더와 홈을 품은 상위 폴더(예: 홈이 /data/u 면 /data), 실제 위치가 보호 경로인 폴더는 막는다.
    /// 요청 경로만 보면 링크를 거친 경로(/srv/link → /etc)나 홈의 상위 폴더를 놓치므로 서버가 푼 실제 경로(REALPATH)도 본다.
    /// 대상이 링크 자체면 링크만 지우거나 이름을 바꾸므로 원래 경로만 본다
    /// </summary>
    private static void GuardHomeAndProtected(SftpClient c, string path, SftpFileAttributes attr, string verb)
    {
        var home = c.WorkingDirectory;
        var paths = new List<string> { path };
        if (!attr.IsSymbolicLink)
        {
            var real = c.Get(path).FullName.TrimEnd('/');
            if (real.Length == 0) real = "/";
            if (real != path)
            {
                if (McpSftpRules.ProtectedPathReason(real) is { } prot) throw new McpToolException($"{prot} (요청 경로 {path} 의 실제 위치)");
                paths.Add(real);
            }
        }
        foreach (var p in paths)
            if (McpSftpRules.ContainsHome(p, home))
                throw new McpToolException($"홈 폴더나 홈 폴더를 품은 폴더는 {verb} 수 없습니다: {p} (홈 {home})");
    }

    /// <summary>상위 폴더가 링크를 거쳐 다른 곳에 있으면 승인 창에 실제 위치를 알린다 (번역된 문구, 없으면 "")</summary>
    private static string LinkedParentNote(SftpClient c, string path)
    {
        var parent = SftpPanel.ParentPath(path);
        if (parent == "/") return "";
        string real;
        try { real = c.Get(parent).FullName; }
        catch (Renci.SshNet.Common.SftpPathNotFoundException) { throw new McpToolException($"상위 폴더가 없습니다: {parent}"); }
        return real.Length > 0 && real != parent ? L.F("\n⚠ 상위 경로가 심볼릭 링크를 거칩니다 — 실제 위치: {0}", McpHelpers.VisibleControlChars(real)) : "";
    }

    /// <summary>
    /// 쓰기 작업 공통: 연결 → inspect(서버를 바꾸지 않는 확인, 승인 창 내용) → 승인 → act → 감사 로그(승인·거부·완료·실패).
    /// inspect 가 돌려주는 What 은 승인 창의 명령 칸·감사 로그에 들어가고, Note 는 설명에 붙는다(번역됨).
    /// act 는 (Claude 에게 돌려줄 결과, 감사 로그 결과) 를 돌려준다. 클라이언트는 어떤 경우에도 닫는다
    /// </summary>
    private async Task<string> SftpWriteOpAsync<TInfo>(string tool, SftpTarget t, string title, string description,
        Func<SftpClient, CancellationToken, (TInfo Info, string What, string Note)> inspect,
        Func<SftpClient, CancellationToken, TInfo, (string Result, string Audit)> act,
        Func<string>? progress = null)
    {
        RequireRawSftp();
        var ct = McpRequest.Token;
        var targets = new[] { t.Label };
        var c = await Task.Run(() => { ct.ThrowIfCancellationRequested(); return OpenSftpClient(t); }, ct);
        try
        {
            var (info, what, note) = await Task.Run(() => MapSftpErrors(() => inspect(c, ct), ct), ct);
            var ok = await OnUiAsync(() => AskApprovalAsync(tool, title, description + note + t.Note, targets, what));
            AuditLog.Write(tool, targets, what, ok ? "승인" : "거부");
            if (!ok) throw new McpToolException("사용자가 거부했습니다.");
            try
            {
                // 승인 뒤에는 시작 자체는 취소로 막지 않는다 — act 안에서 항목마다 취소를 보고 멈춘다 (실패·취소도 아래에서 기록)
                var (result, audit) = await Task.Run(() => MapSftpErrors(() => act(c, ct, info), ct, progress), CancellationToken.None);
                AuditLog.Write(tool, targets, what, $"완료: {audit}");
                return result;
            }
            catch (McpToolException ex)
            {
                AuditLog.Write(tool, targets, what, $"실패: {ex.Message}");
                throw;
            }
            catch (Exception ex)
            {
                AuditLog.Write(tool, targets, what, $"실패: {ex.Message}");
                throw new McpToolException($"실패: {ex.Message}" + (progress?.Invoke() is { Length: > 0 } p ? $" ({p})" : ""));
            }
        }
        finally
        {
            try { c.Dispose(); } catch { }
        }
    }

    // ---------------- sftp_mkdir ----------------

    private async Task<string> SftpMkdirAsync(JsonElement a)
    {
        var path = WritePathArg(a);
        if (path == "/") throw new McpToolException("path: 만들 폴더 경로가 필요합니다.");
        var t = await SftpTargetAsync(a);
        return await SftpWriteOpAsync("sftp_mkdir", t, L.T("Claude 가 원격 폴더 만들기를 요청합니다"), L.T("승인하면 서버에 아래 폴더를 만듭니다."),
            (c, _) =>
            {
                if (TryLStat(c, path) is { } existing) throw new McpToolException($"이미 있습니다 ({KindText(existing)}): {path}");
                var note = LinkedParentNote(c, path);
                var parent = c.GetAttributes(SftpPanel.ParentPath(path));
                if (!parent.IsDirectory) throw new McpToolException($"상위 경로가 폴더가 아닙니다: {SftpPanel.ParentPath(path)}");
                return (0, $"새 폴더: {path}", note);
            },
            (c, _, _) =>
            {
                if (TryLStat(c, path) is { } existing) throw new McpToolException($"승인을 기다리는 사이 생겼습니다 ({KindText(existing)}): {path}");
                c.CreateDirectory(path);
                return ($"폴더를 만들었습니다: {path}", path);
            });
    }

    // ---------------- sftp_rename ----------------

    private async Task<string> SftpRenameAsync(JsonElement a)
    {
        var path = WritePathArg(a);
        if (McpSftpRules.ProtectedPathReason(path) is { } prot) throw new McpToolException(prot);
        var newName = Arg(a, "new_name");
        // "/" 가 들어가면 다른 폴더로 옮겨지고, ".." 등은 엉뚱한 경로가 된다 (패널의 이름 변경과 같은 규칙)
        if (McpSftpRules.ValidateName(newName) is { } bad) throw new McpToolException($"new_name: {bad}");
        var newPath = SftpPanel.Combine(SftpPanel.ParentPath(path), newName);
        if (newPath == path) throw new McpToolException("새 이름이 지금 이름과 같습니다.");
        var t = await SftpTargetAsync(a);
        return await SftpWriteOpAsync("sftp_rename", t, L.T("Claude 가 원격 이름 변경을 요청합니다"),
            L.T("승인하면 서버에서 아래 항목의 이름을 바꿉니다. 심볼릭 링크는 링크 자체의 이름만 바뀝니다."),
            (c, _) =>
            {
                var attr = TryLStat(c, path) ?? throw new McpToolException($"원격 경로가 없습니다: {path}");
                GuardHomeAndProtected(c, path, attr, "이름을 바꿀");
                if (TryLStat(c, newPath) is { } dest) throw new McpToolException($"이미 같은 이름이 있습니다 ({KindText(dest)}): {newPath}");
                return (attr.IsSymbolicLink, $"이름 변경: {path} → {newPath} ({KindText(attr)})", LinkedParentNote(c, path));
            },
            (c, _, wasLink) =>
            {
                var now = TryLStat(c, path) ?? throw new McpToolException($"승인을 기다리는 사이 없어졌습니다: {path}");
                if (now.IsSymbolicLink != wasLink) throw new McpToolException($"승인을 기다리는 사이 종류가 바뀌어 바꾸지 않았습니다: {path}");
                if (TryLStat(c, newPath) != null) throw new McpToolException($"승인을 기다리는 사이 같은 이름이 생겼습니다: {newPath}");
                // 경로를 그대로 보낸다 — RenameFile 은 REALPATH 로 링크를 따라가 원본을 옮겨 버린다
                SftpRaw.Rename(c, path, newPath);
                return ($"이름을 바꿨습니다: {path} → {newPath}", $"{path} → {newPath}");
            });
    }

    // ---------------- sftp_delete ----------------

    /// <summary>sftp_delete 의 항목 하나: 승인 때 본 종류 · 항목 수 (승인 뒤 종류가 바뀌면 지우지 않음)</summary>
    private sealed record DeleteTarget(string Path, bool IsDir, bool IsLink, int Total);

    private async Task<string> SftpDeleteAsync(JsonElement a)
    {
        if (McpSftpRules.CollectPaths("path", Arg(a, "path"), "paths", StringListArg(a, "paths"), ignoreCase: false, out var raw) is { } badArgs)
            throw new McpToolException(badArgs);
        // 경로마다 쓰기 도구 공통 검사 (절대 경로, '.'·'..'·제어 문자 없음, 이름 규칙) + 보호 경로. 정리 뒤 같아진 경로는 하나로
        var paths = new List<string>();
        foreach (var r in raw)
        {
            var p = WritePath(r, raw.Count > 1 ? "paths" : "path");
            if (McpSftpRules.ProtectedPathReason(p) is { } prot) throw new McpToolException(prot);
            if (!paths.Contains(p)) paths.Add(p);
        }
        if (McpSftpRules.NestedRemotePaths(paths) is { } nested) throw new McpToolException(nested);
        var multi = paths.Count > 1;
        var t = await SftpTargetAsync(a);
        var deleted = 0;
        var donePaths = new List<string>();
        return await SftpWriteOpAsync("sftp_delete", t, L.T("Claude 가 원격 삭제를 요청합니다"),
            multi
                ? L.F("승인하면 서버에서 아래 {0}개 항목을 삭제합니다. 폴더는 안의 내용까지 모두 지우며 되돌릴 수 없습니다. 심볼릭 링크는 링크만 지우고 가리키는 원본은 그대로 둡니다.", paths.Count)
                : L.T("승인하면 서버에서 아래 항목을 삭제합니다. 폴더는 안의 내용까지 모두 지우며 되돌릴 수 없습니다. 심볼릭 링크는 링크만 지우고 가리키는 원본은 그대로 둡니다."),
            (c, ct) =>
            {
                int files = 0, dirs = 0, links = 0;
                var capped = false;
                var targets = new List<DeleteTarget>();
                var lines = new List<string>();
                var parentNotes = new List<string>();
                foreach (var path in paths)
                {
                    var attr = TryLStat(c, path) ?? throw new McpToolException($"원격 경로가 없습니다: {path}");
                    GuardHomeAndProtected(c, path, attr, "지울");
                    int f0 = files, d0 = dirs, l0 = links;
                    var preview = new List<string>();
                    void Count(string p, bool isDir, bool isLink, int depth)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (files + dirs + links >= MaxSftpDeleteItems) { capped = true; return; }
                        if (isLink) { links++; return; }
                        if (!isDir) { files++; return; }
                        dirs++;
                        foreach (var f in c.ListDirectory(p).Where(f => f.Name is not "." and not ".."))
                        {
                            if (capped) return;
                            if (depth == 0 && preview.Count < 10)
                                preview.Add($"  {(f.IsSymbolicLink ? "link" : f.IsDirectory ? "dir" : "file")}  {McpHelpers.VisibleControlChars(f.Name)}");
                            // 링크(폴더를 가리키는 링크 포함)는 안으로 들어가지 않는다 — 패널의 삭제와 같은 규칙
                            Count(ChildPath(p, f.Name), f.IsDirectory && !f.IsSymbolicLink, f.IsSymbolicLink, depth + 1);
                        }
                    }
                    Count(path, attr.IsDirectory && !attr.IsSymbolicLink, attr.IsSymbolicLink, 0);
                    if (capped)
                        throw new McpToolException($"항목이 {MaxSftpDeleteItems}개를 넘어 지우지 않았습니다 (승인 창에 정확한 수를 보일 수 없음). 더 작은 단위로 나눠 지우세요.");
                    int pf = files - f0, pd = dirs - d0, pl = links - l0;
                    targets.Add(new DeleteTarget(path, attr.IsDirectory && !attr.IsSymbolicLink, attr.IsSymbolicLink, pf + pd + pl));
                    if (!multi)
                        lines.Add($"삭제: {path} ({KindText(attr)})\n{McpSftpRules.CountText(pf, pd, pl, false)}" +
                                  (preview.Count > 0 ? "\n안의 항목:\n" + string.Join("\n", preview) + (pf + pd + pl - 1 > preview.Count ? "\n  …" : "") : ""));
                    else
                        lines.Add($"  {path} ({KindText(attr)}{(pd + pf + pl > 1 ? $" — {McpSftpRules.CountText(pf, pd, pl, false)}" : "")})");
                    if (LinkedParentNote(c, path) is { Length: > 0 } pn && !parentNotes.Contains(pn)) parentNotes.Add(pn);
                }
                var what = multi
                    ? $"삭제 ({paths.Count}개, 모두 {McpSftpRules.CountText(files, dirs, links, false)}):\n" + string.Join("\n", lines)
                    : lines[0];
                return (targets, what, L.F("\n삭제할 항목: 파일 {0}개 · 폴더 {1}개 · 링크 {2}개", files, dirs, links) + string.Concat(parentNotes));
            },
            (c, ct, targets) =>
            {
                var total = targets.Sum(x => x.Total);
                var notDone = new List<string>();
                var capHit = false;
                foreach (var tg in targets)
                {
                    ct.ThrowIfCancellationRequested();
                    // 승인을 기다리는 사이 없어졌거나 폴더가 링크로 바뀌었으면(또는 반대) 지우지 않는다 — 링크를 따라 다른 곳의 내용을 지우지 않게.
                    // 항목이 하나면 예전처럼 오류, 여럿이면 그 항목만 빼고 결과에 적는다
                    var now = TryLStat(c, tg.Path);
                    string? why = now == null ? $"승인을 기다리는 사이 없어졌습니다: {tg.Path}"
                        : now.IsSymbolicLink != tg.IsLink || (now.IsDirectory && !now.IsSymbolicLink) != tg.IsDir ? $"승인을 기다리는 사이 종류가 바뀌어 지우지 않았습니다: {tg.Path}"
                        : null;
                    if (why != null)
                    {
                        if (!multi) throw new McpToolException(why);
                        notDone.Add(why);
                        continue;
                    }
                    var before = deleted;
                    try
                    {
                        DeleteRecursiveCounted(c, tg.Path, tg.IsDir, tg.IsLink, ct, () =>
                        {
                            // 승인 뒤 늘어난 항목까지 끝없이 지우지 않게 실제 삭제에도 한도를 둔다
                            if (deleted >= MaxSftpDeleteItems)
                            {
                                capHit = true;
                                throw new McpToolException($"지운 항목이 {MaxSftpDeleteItems}개에 이르러 멈췄습니다 (승인 때 {total}개). 남은 항목은 다시 확인하세요.");
                            }
                        }, () => deleted++);
                        donePaths.Add(tg.Path);
                    }
                    // 여러 항목이면 한 항목의 실패(권한 없음 · 그사이 없어짐 · 이상한 이름)로 나머지를 멈추지 않는다 —
                    // 한도 · 취소 · 연결 끊김 · 시간 초과 등 그 밖의 오류는 전체를 멈춘다 (끊긴 연결로 남은 항목을 하나씩 시도하지 않게)
                    catch (Exception ex) when (multi && !capHit &&
                        ex is Renci.SshNet.Common.SftpPermissionDeniedException or Renci.SshNet.Common.SftpPathNotFoundException or McpToolException)
                    {
                        var msg = ex is Renci.SshNet.Common.SftpPermissionDeniedException ? "권한 없음" : ex.Message;
                        notDone.Add($"{tg.Path}: 실패 ({msg}, 그 전까지 {deleted - before}개 지움)");
                    }
                }
                if (!multi)
                    return ($"삭제했습니다: {paths[0]} (항목 {deleted}개)", $"{deleted}개 (승인 때 {total}개)");
                var result = $"삭제했습니다: {donePaths.Count}/{paths.Count}개 경로 (항목 {deleted}개)" + McpSftpRules.ListText(donePaths, McpSftpRules.MaxMultiPaths) +
                             (notDone.Count > 0 ? $"\n지우지 못한 경로 {notDone.Count}개:" + McpSftpRules.ListText(notDone, McpSftpRules.MaxMultiPaths) : "");
                var audit = $"{deleted}개 (승인 때 {total}개), 경로 {donePaths.Count}/{paths.Count}" + (notDone.Count > 0 ? " — 지우지 못함: " + string.Join(" / ", notDone) : "");
                if (donePaths.Count == 0) throw new McpToolException("지운 경로가 없습니다:" + McpSftpRules.ListText(notDone, McpSftpRules.MaxMultiPaths));
                return (result, audit);
            },
            () => $"중단 전까지 {deleted}개 삭제됨 — 이미 지운 항목은 되돌릴 수 없음" +
                  (multi && donePaths.Count > 0 ? $", 다 지운 경로: {string.Join(", ", donePaths)}" : ""));
    }

    /// <summary>
    /// 목록 항목의 경로 — 요청 경로 아래로 직접 만든다. SftpFile.FullName 은 서버가 REALPATH 로 푼 폴더 기준이라
    /// 목록을 읽는 사이 폴더가 링크로 바뀌면 링크 너머(/etc 등)의 경로가 된다. '/' 가 든 이름(비정상 서버)은 거부
    /// </summary>
    private static string ChildPath(string dir, string name)
    {
        if (name.Contains('/')) throw new McpToolException($"서버가 '/' 가 든 이름을 돌려줘 중단했습니다: {McpHelpers.VisibleControlChars(name)}");
        return SftpPanel.Combine(dir, name);
    }

    /// <summary>
    /// SftpPanel.DeleteRecursive 와 같은 규칙: 링크는(폴더를 가리키는 링크 포함) 안으로 들어가지 않고 SftpRaw 로 링크 자체만 지운다.
    /// 일반 파일도 SftpRaw.Remove 로 경로를 그대로 지운다 (목록을 읽은 뒤 링크로 바뀌었어도 원본이 지워지지 않게).
    /// 폴더는 들어가기 직전에 LSTAT 으로 다시 보고(그사이 링크로 바뀌었으면 링크만 지움), 하위 경로는 요청 경로 아래로 만들며,
    /// 빈 폴더도 SftpRaw.RemoveDirectory 로 지운다 (DeleteDirectory 는 REALPATH 로 링크를 따라감). 항목마다 취소 · 한도(beforeDelete)를 본다
    /// </summary>
    private static void DeleteRecursiveCounted(SftpClient c, string path, bool isDir, bool isLink, CancellationToken ct, Action beforeDelete, Action deleted)
    {
        ct.ThrowIfCancellationRequested();
        if (isDir && !isLink)
        {
            SftpFileAttributes now;
            try { now = SftpRaw.LStat(c, path); }
            catch (Renci.SshNet.Common.SftpPathNotFoundException) { return; }   // 그사이 없어짐
            if (now.IsSymbolicLink || !now.IsDirectory) isDir = false;
        }
        if (isLink || !isDir)
        {
            beforeDelete();
            SftpRaw.Remove(c, path);
            deleted();
            return;
        }
        foreach (var f in c.ListDirectory(path).Where(f => f.Name is not "." and not ".."))
            DeleteRecursiveCounted(c, ChildPath(path, f.Name), f.IsDirectory && !f.IsSymbolicLink, f.IsSymbolicLink, ct, beforeDelete, deleted);
        beforeDelete();
        SftpRaw.RemoveDirectory(c, path);
        deleted();
    }

    // ---------------- sftp_write ----------------

    private async Task<string> SftpWriteAsync(JsonElement a)
    {
        var path = WritePathArg(a);
        if (path == "/") throw new McpToolException("path: 파일 경로가 필요합니다.");
        if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty("content", out var cv) || cv.ValueKind != JsonValueKind.String)
            throw new McpToolException("content 가 필요합니다 (문자열).");
        var content = cv.GetString() ?? "";
        var expectedText = Arg(a, "expected_mtime_utc");
        var expectedMtime = McpSftpRules.ParseMtime(expectedText);
        if (expectedText.Trim().Length > 0 && expectedMtime == null)
            throw new McpToolException($"expected_mtime_utc 를 해석할 수 없습니다: {expectedText} (sftp_read 결과의 mtime_utc 를 그대로 주세요)");
        var expectedSize = NumArg(a, "expected_size") is { } es ? (long?)Math.Max(-1, (long)es) : null;
        var createOnly = BoolArg(a, "create") == true;
        var newBytes = Encoding.UTF8.GetBytes(content);
        if (newBytes.Length > MaxSftpWriteBytes) throw new McpToolException($"content 가 {SftpPanel.HumanSize(MaxSftpWriteBytes)} 를 넘습니다 ({SftpPanel.HumanSize(newBytes.Length)}). sftp_upload 를 쓰세요.");
        var sensitive = McpHelpers.IsSensitiveRemotePath(path);
        var t = await SftpTargetAsync(a);
        return await SftpWriteOpAsync("sftp_write", t, L.T("Claude 가 원격 파일 쓰기를 요청합니다"),
            createOnly ? L.T("승인하면 서버에 아래 내용으로 새 파일을 만듭니다.") : L.T("승인하면 서버의 파일을 아래 내용으로 바꿉니다 (임시 파일에 쓴 뒤 바꿔치기, 권한 유지)."),
            (c, _) =>
            {
                var attr = TryLStat(c, path);
                if (McpSftpRules.LinkOverwriteRefusal(path, attr != null, attr?.IsSymbolicLink) is { } link) throw new McpToolException(link);
                if (attr is { IsDirectory: true }) throw new McpToolException($"폴더입니다: {path}");
                if (attr is { IsRegularFile: false }) throw new McpToolException($"일반 파일이 아닙니다 (장치·FIFO·소켓 등): {path}");
                // 탭의 sudo 권한으로 열었으면 승인 전이므로 지금 수정 시각 · 크기를 알려 주지 않는다 (승인 없이 다른 사용자 권한의 정보가 새지 않게)
                if (McpSftpRules.WriteConflict(attr != null, attr?.LastWriteTimeUtc, attr?.Size, expectedMtime, expectedSize, createOnly, showActual: !t.Elevated) is { } conflict)
                    throw new McpToolException(conflict);
                var note = LinkedParentNote(c, path);
                var parent = c.GetAttributes(SftpPanel.ParentPath(path));
                if (!parent.IsDirectory) throw new McpToolException($"상위 경로가 폴더가 아닙니다: {SftpPanel.ParentPath(path)}");
                // 이전 내용 (승인 창의 바뀜 요약용 — Claude 에게는 돌려주지 않음). 크거나 바이너리면 크기만 비교
                string? oldText = null;
                var bytes = newBytes;
                if (attr != null && attr.Size <= MaxSftpWriteBytes)
                {
                    using var s = c.OpenRead(path);
                    using var ms = new MemoryStream();
                    s.CopyTo(ms);
                    var old = ms.ToArray();
                    if (!McpHelpers.LooksBinary(old)) oldText = McpHelpers.DecodeUtf8Slice(old, cutStart: false, cutEnd: false);
                    // 원래 파일에 UTF-8 BOM 이 있었으면 유지한다 (sftp_read 결과에는 BOM 이 보이지 않아 말없이 지워지지 않게)
                    if (old.AsSpan().StartsWith(Utf8Bom) && !bytes.AsSpan().StartsWith(Utf8Bom)) bytes = [.. Utf8Bom, .. bytes];
                }
                var what = $"파일 쓰기: {path}\n" + McpSftpRules.DiffSummary(oldText?.TrimStart('\uFEFF'), attr?.Size ?? -1, content, bytes.Length);
                var warn = sensitive ? L.T("\n⚠ 비밀 정보나 시스템 설정이 들어 있을 수 있는 경로입니다.") : "";
                return ((Exists: attr != null, Mtime: attr?.LastWriteTimeUtc, Size: attr?.Size, Bytes: bytes), what, warn + note);
            },
            (c, ct, info) =>
            {
                // 승인을 기다리는 사이 바뀌었는지 다시 본다 (승인 창에 보인 기준과 같아야 씀)
                var now = TryLStat(c, path);
                if (McpSftpRules.LinkOverwriteRefusal(path, now != null, now?.IsSymbolicLink) is { } link) throw new McpToolException(link);
                if (now != null && !now.IsRegularFile) throw new McpToolException($"승인을 기다리는 사이 일반 파일이 아니게 됐습니다: {path}");
                if (McpSftpRules.WriteConflict(now != null, now?.LastWriteTimeUtc, now?.Size, info.Mtime, info.Size, !info.Exists) is { } changed)
                    throw new McpToolException("승인을 기다리는 사이 서버 파일이 바뀌어 쓰지 않았습니다. " + changed);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(SftpPanel.EditUploadTimeout(info.Bytes.Length));
                if (!info.Exists)
                    CreateNewRemoteFile(c, path, info.Bytes, timeout.Token);
                else
                    // 있는 내 파일은 임시 이름에 쓴 뒤 바꿔치기 (끊겨도 원래 파일은 그대로, 권한·그룹 유지) — 패널 편집 저장과 같은 경로
                    SftpPanel.UploadOneAsync(c, () => new MemoryStream(info.Bytes, writable: false), path,
                        NoProgress<UploadFileProgressReport>.Instance, timeout.Token).GetAwaiter().GetResult();
                var after = c.GetAttributes(path);
                return ($"저장했습니다: {path} ({SftpPanel.HumanSize(after.Size)})\nmtime_utc={McpSftpRules.FormatMtime(after.LastWriteTimeUtc)} size={after.Size}",
                        $"{(info.Exists ? "덮어씀" : "새 파일")} {after.Size} B");
            });
    }

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// 새 파일 만들기: "없을 때만" 연다 (SSH_FXF_CREAT|EXCL — 승인 뒤 그 이름으로 링크나 파일이 생겼으면 따라가거나 덮어쓰지 않고 실패).
    /// 쓰다가 실패 · 취소되면 방금 만든 파일을 SftpRaw.Remove 로 지운다 (DeleteFile 은 REALPATH 로 링크를 따라감)
    /// </summary>
    private static void CreateNewRemoteFile(SftpClient c, string path, byte[] bytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var s = c.Open(path, FileMode.CreateNew, FileAccess.Write);
        try
        {
            try
            {
                for (var off = 0; off < bytes.Length; off += 32 * 1024)
                {
                    ct.ThrowIfCancellationRequested();
                    s.Write(bytes, off, Math.Min(32 * 1024, bytes.Length - off));
                }
            }
            finally { s.Dispose(); }
        }
        catch
        {
            try { SftpRaw.Remove(c, path); } catch { }
            throw;
        }
    }
}
