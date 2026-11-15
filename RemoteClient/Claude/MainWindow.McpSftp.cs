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

    /// <summary>SFTP 대상 호스트 (SSH 만, 중지 서버 제외) — 접속용 사본</summary>
    private async Task<(HostEntry Saved, HostEntry Conn)> SftpTargetAsync(JsonElement a) => await OnUi(() =>
    {
        var h = ResolveOneHost(Arg(a, "host"));
        if (h.Protocol != Protocol.Ssh) throw new McpToolException($"'{h.DisplayName}' 는 RDP(Windows) 호스트입니다. SFTP 는 SSH 호스트만 됩니다.");
        if (h.IsStopped) throw new McpToolException($"'{h.DisplayName}' 는 중지 상태입니다 ({h.CloudState}).");
        return (h, WithProfileDefaults(h));
    });

    /// <summary>
    /// 원격 읽기: run_command 의 읽기 전용 자동 승인 설정을 따른다. 단 sensitive(개인키·/etc/shadow·.env 등 비밀이 든 경로)면 항상 승인 창.
    /// 승인 창을 띄웠으면 true, 자동 승인이면 false (거부면 McpToolException)
    /// </summary>
    private async Task<bool> ApproveRemoteRead(string tool, HostEntry h, string what, bool sensitive = false)
    {
        var target = $"[SFTP]  {McpHelpers.VisibleControlChars(h.DisplayName)}  ({string.Join(" → ", h.AddressCandidates())})";
        if (SettingsStore.Current.AutoApproveReadOnly && !sensitive) { AuditLog.Write(tool, new[] { target }, what, "자동 승인(읽기 전용)"); return false; }
        var ok = await OnUiAsync(() => AskApprovalAsync(tool, L.T("Claude 가 원격 파일 읽기를 요청합니다"),
            L.T("승인하면 서버에 SFTP 로 접속해 아래 내용을 읽어 Claude 에게 돌려줍니다 (서버는 바꾸지 않음).") +
            (sensitive ? L.T("\n⚠ 개인키·비밀번호 해시·환경 변수 파일처럼 비밀 정보가 들어 있을 수 있는 경로입니다. 읽은 내용은 Claude 에게 전달됩니다.") : ""),
            new[] { target }, what));
        AuditLog.Write(tool, new[] { target }, what, ok ? "승인" : "거부");
        if (!ok) throw new McpToolException("사용자가 거부했습니다.");
        return true;
    }

    /// <summary>SFTP 작업 하나(요청)의 응답 제한 시간 — 서버가 멈춰도 MCP 요청이 끝없이 걸려 있지 않게</summary>
    private static readonly TimeSpan SftpOperationTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// SFTP 로 접속해 f 실행. Claude 요청이 취소되면(McpRequest.Token) 다음 파일·다음 읽기/쓰기에서 멈춘다.
    /// progress 를 주면 실패·취소 메시지에 진행 상황("파일 10개 중 3개 올림")을 붙인다.
    /// </summary>
    private static async Task<T> WithSftp<T>(HostEntry conn, Func<Renci.SshNet.SftpClient, CancellationToken, T> f, Func<string>? progress = null)
    {
        var ct = McpRequest.Token;
        string P() => progress?.Invoke() is { Length: > 0 } s ? $" ({s})" : "";
        return await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            Renci.SshNet.SftpClient c;
            try { c = SshExec.OpenSftp(conn); }
            catch (InvalidOperationException ex) { throw new McpToolException(ex.Message); }
            using (c)
            {
                c.OperationTimeout = SftpOperationTimeout;
                try { return f(c, ct); }
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
        }, ct);
    }

    private static Task<T> WithSftp<T>(HostEntry conn, Func<Renci.SshNet.SftpClient, T> f) => WithSftp(conn, (c, _) => f(c));

    private async Task<string> SftpListAsync(JsonElement a)
    {
        var (h, conn) = await SftpTargetAsync(a);
        var path = Arg(a, "path");
        await ApproveRemoteRead("sftp_list", h, $"폴더 목록: {(path.Length > 0 ? path : "(홈)")}");
        return await WithSftp(conn, c =>
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
        var (h, conn) = await SftpTargetAsync(a);
        var path = Arg(a, "path");
        var max = Math.Clamp(IntArg(a, "max_bytes") ?? 65536, 1024, 1048576);
        var tail = BoolArg(a, "tail") == true;
        var what = $"파일 읽기: {path} ({(tail ? "끝 " : "앞 ")}{SftpPanel.HumanSize(max)})";
        // 개인키·/etc/shadow·.env 처럼 비밀이 든 경로는 자동 승인 설정이 켜져 있어도 승인 창
        var asked = await ApproveRemoteRead("sftp_read", h, what, McpHelpers.IsSensitiveRemotePath(path));
        return await WithSftp(conn, c =>
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
                    ApproveRemoteRead("sftp_read", h, $"{what}\n실제 경로: {real}", sensitive: true).GetAwaiter().GetResult();
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
            return $"[{h.DisplayName}] {path} — {SftpPanel.HumanSize(size)}{cut}\n{text}";
        });
    }

    /// <summary>sftp_download 한 번에 받을 수 있는 전체 크기</summary>
    private const long MaxDownloadBytes = 20L * 1024 * 1024 * 1024;
    /// <summary>받은 뒤에도 남겨 둘 디스크 여유 공간</summary>
    private const long DownloadDiskReserve = 1L * 1024 * 1024 * 1024;

    /// <summary>폴더가 있는 드라이브의 여유 공간 (알 수 없으면 null)</summary>
    private static long? FreeSpace(string dir)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!).AvailableFreeSpace; }
        catch { return null; }
    }

    private async Task<string> SftpDownloadAsync(JsonElement a)
    {
        var (h, conn) = await SftpTargetAsync(a);
        var path = Arg(a, "path").Trim();
        if (path.Length == 0) throw new McpToolException("path 가 필요합니다.");
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Nubis", SftpPanel.SafeLocalName(h.DisplayName));
        var target = $"[SFTP]  {h.DisplayName}  ({string.Join(" → ", h.AddressCandidates())})";
        var what = $"받기: {path}\n저장 위치: {root}";
        var ok = await OnUiAsync(() => AskApprovalAsync("sftp_download", L.T("Claude 가 파일 다운로드를 요청합니다"),
            L.T("서버의 파일을 이 PC 로 받습니다. 기존 파일은 덮어쓰지 않습니다(이름 뒤에 번호)."), new[] { target }, what));
        AuditLog.Write("sftp_download", new[] { target }, what, ok ? "승인" : "거부");
        if (!ok) throw new McpToolException("사용자가 거부했습니다.");

        var count = 0;
        long bytes = 0;
        try
        {
            return await WithSftp(conn, (c, ct) => DownloadCore(c, ct),
                () => $"중단 전까지 파일 {count}개 받음 ({SftpPanel.HumanSize(bytes)}), 받던 파일은 일부만 저장됐을 수 있음 — 저장 위치 {root}");
        }
        catch (McpToolException ex)
        {
            AuditLog.Write("sftp_download", new[] { target }, path, $"실패 (파일 {count}개 받음): {ex.Message}");
            throw;
        }
        catch (OperationCanceledException)
        {
            // 접속 전 취소(Task.Run 의 토큰, WithSftp 첫 ThrowIfCancellationRequested)는 McpToolException 으로 바뀌지 않는다 — 취소 기록을 남긴다
            AuditLog.Write("sftp_download", new[] { target }, path, $"요청 취소로 중단 (파일 {count}개 받음)");
            throw new McpToolException($"요청이 취소되어 중단했습니다 (파일 {count}개 받음).");
        }
        catch (Exception ex)
        {
            // 접속·인증 실패 등도 '승인' 기록만 남지 않게 결과를 적는다
            AuditLog.Write("sftp_download", new[] { target }, path, $"실패 (파일 {count}개 받음): {ex.Message}");
            throw;
        }

        string DownloadCore(Renci.SshNet.SftpClient c, CancellationToken ct)
        {
            Directory.CreateDirectory(root);
            var attr = c.GetAttributes(path);
            if (!attr.IsDirectory && !attr.IsRegularFile) throw new McpToolException("일반 파일이 아닙니다 (장치·FIFO·소켓 등).");
            var top = SftpPanel.UniquePath(Path.Combine(root, SftpPanel.SafeLocalName(McpHelpers.RemoteFileName(path))), attr.IsDirectory);
            var full = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            var skipped = new List<string>();
            var renamed = new List<string>();
            void Get(string remote, bool isDir, string local, long size)
            {
                if (!Path.GetFullPath(local).StartsWith(full, StringComparison.OrdinalIgnoreCase)) throw new McpToolException("허용되지 않는 파일 이름이 있어 중단했습니다.");
                if (isDir)
                {
                    Directory.CreateDirectory(local);
                    foreach (var f in c.ListDirectory(remote).Where(f => f.Name is not "." and not ".."))
                    {
                        // Windows 는 대소문자를 구분하지 않고 SafeLocalName 이 다른 이름을 같게 만들 수 있다 (A 와 a, a:b 와 a_b) —
                        // 먼저 받은 파일을 덮어쓰지 않게 이미 있으면 번호를 붙인다 (받는 최상위 폴더는 새로 만든 빈 폴더라 있는 것은 이번에 받은 것)
                        var wanted = Path.Combine(local, SftpPanel.SafeLocalName(f.Name));
                        var childLocal = SftpPanel.UniquePath(wanted, f.IsDirectory);
                        if (childLocal != wanted) renamed.Add($"{f.FullName} → {Path.GetFileName(childLocal)}");
                        // 장치·FIFO·소켓은 받지 않는다 (/dev/zero 류는 끝없이 이어지고, FIFO 는 멈춘다)
                        if (!f.IsSymbolicLink && !f.IsDirectory && !f.IsRegularFile) { skipped.Add($"{f.FullName} (일반 파일이 아님)"); continue; }
                        if (!f.IsSymbolicLink) { Get(f.FullName, f.IsDirectory, childLocal, f.Length); continue; }
                        // 심볼릭 링크: 대상을 stat 해서 일반 파일이면 내용을 받고, 폴더 링크는 따라가지 않는다
                        // (순환·받는 폴더 밖(/ 등)으로 이어져 끝없이 받는 일을 막음). 깨진 링크도 건너뛰고 결과에 적는다
                        ct.ThrowIfCancellationRequested();
                        Renci.SshNet.Sftp.SftpFileAttributes? linkTarget;
                        try { linkTarget = c.GetAttributes(f.FullName); }
                        catch (Renci.SshNet.Common.SftpPathNotFoundException) { linkTarget = null; }
                        catch (Renci.SshNet.Common.SftpPermissionDeniedException) { linkTarget = null; }
                        if (linkTarget == null) skipped.Add($"{f.FullName} (깨진 링크 또는 대상 권한 없음)");
                        else if (linkTarget.IsDirectory) skipped.Add($"{f.FullName} (폴더 링크 — 따라가지 않음)");
                        else if (!linkTarget.IsRegularFile) skipped.Add($"{f.FullName} (일반 파일이 아닌 링크 대상)");
                        else Get(f.FullName, false, childLocal, linkTarget.Size);
                    }
                    return;
                }
                if (count >= 5000) throw new McpToolException("파일이 5000개를 넘어 중단했습니다.");
                // 전체 크기 상한과 디스크 여유 공간 (서버의 큰 폴더·로그를 통째로 받아 이 PC 디스크를 채우지 않게)
                if (bytes + size > MaxDownloadBytes)
                    throw new McpToolException($"받을 전체 크기가 {SftpPanel.HumanSize(MaxDownloadBytes)} 를 넘어 중단했습니다. 필요한 파일만 골라 받으세요.");
                if (FreeSpace(root) is { } free && free < size + DownloadDiskReserve)
                    throw new McpToolException($"이 PC 디스크 여유 공간이 부족해 중단했습니다 (여유 {SftpPanel.HumanSize(free)}, 받을 파일 {SftpPanel.HumanSize(size)}).");
                ct.ThrowIfCancellationRequested();
                // 목록의 크기는 받는 동안 늘 수 있다 (자라는 로그) — 쓰는 동안에도 남은 한도·여유 공간을 넘으면 멈춘다
                var allowed = MaxDownloadBytes - bytes;
                if (FreeSpace(root) is { } freeNow) allowed = Math.Min(allowed, freeNow - DownloadDiskReserve);
                using (var fs = File.Create(local))
                {
                    c.DownloadFile(remote, new LimitedWriteStream(new CancellableStream(fs, ct), allowed, () =>
                        $"받는 중 크기 한도(전체 {SftpPanel.HumanSize(MaxDownloadBytes)}) 또는 디스크 여유 공간(1GB 남김)을 넘어 중단했습니다: {remote}"));
                    bytes += fs.Length;
                }
                count++;   // 다 받은 파일만 센다 (실패 메시지의 진행 상황)
            }
            Get(path, attr.IsDirectory, top, attr.Size);
            var note = skipped.Count == 0 ? "" : $", 심볼릭 링크 {skipped.Count}개 건너뜀";
            AuditLog.Write("sftp_download", new[] { target }, path, $"완료 {count}개 {SftpPanel.HumanSize(bytes)}{note}");
            return $"받았습니다: {count}개 ({SftpPanel.HumanSize(bytes)}) → {top}" +
                   (skipped.Count == 0 ? "" : $"\n건너뛴 심볼릭 링크 {skipped.Count}개:\n" + string.Join("\n", skipped.Take(50)) +
                                              (skipped.Count > 50 ? $"\n… 외 {skipped.Count - 50}개" : "")) +
                   (renamed.Count == 0 ? "" : $"\n이 PC 에서 이름이 겹쳐(대소문자·쓸 수 없는 글자) 번호를 붙인 {renamed.Count}개:\n" + string.Join("\n", renamed.Take(50)) +
                                              (renamed.Count > 50 ? $"\n… 외 {renamed.Count - 50}개" : ""));
        }
    }

    private async Task<string> SftpUploadAsync(JsonElement a)
    {
        var (h, conn) = await SftpTargetAsync(a);
        var local = Arg(a, "local_path").Trim().Trim('"');
        var remoteDir = Arg(a, "remote_dir").Trim();
        var overwrite = BoolArg(a, "overwrite") == true;
        if (local.Length == 0 || remoteDir.Length == 0) throw new McpToolException("local_path 와 remote_dir 가 필요합니다.");
        if (!Path.IsPathFullyQualified(local)) throw new McpToolException("local_path 는 절대 경로여야 합니다.");
        if (McpHelpers.BlockedUploadReason(local, AppPaths.DataDir, AppPaths.RootDir) is { } why) throw new McpToolException(why);
        var isDir = Directory.Exists(local);
        if (!isDir && !File.Exists(local)) throw new McpToolException($"로컬 경로가 없습니다: {local}");

        var files = isDir ? Directory.EnumerateFiles(local, "*", SearchOption.AllDirectories).ToList() : new List<string> { local };
        if (files.Count > 5000) throw new McpToolException("파일이 5000개를 넘습니다.");
        var blocked = files.Select(f => McpHelpers.BlockedUploadReason(f, AppPaths.DataDir, AppPaths.RootDir)).FirstOrDefault(r => r != null);
        if (blocked != null) throw new McpToolException(blocked);
        var total = files.Sum(f => new FileInfo(f).Length);

        var target = $"[SFTP]  {h.DisplayName}  ({string.Join(" → ", h.AddressCandidates())})";
        var what = $"올리기: {local}{(isDir ? $" (폴더, 파일 {files.Count}개)" : "")} — {SftpPanel.HumanSize(total)}\n원격 폴더: {remoteDir}" + (overwrite ? "\n같은 이름이 있으면 덮어씀" : "");
        var ok = await OnUiAsync(() => AskApprovalAsync("sftp_upload", L.T("Claude 가 파일 업로드를 요청합니다"),
            L.T("이 PC 의 파일을 서버로 보냅니다. 보내도 되는 파일인지 확인하세요."), new[] { target }, what));
        AuditLog.Write("sftp_upload", new[] { target }, what, ok ? "승인" : "거부");
        if (!ok) throw new McpToolException("사용자가 거부했습니다.");

        var done = 0;
        try
        {
            return await WithSftp(conn, (c, ct) => UploadCore(c, ct), () => $"파일 {files.Count}개 중 {done}개 올림, 올리던 파일은 원격에 일부만 남았을 수 있음");
        }
        catch (McpToolException ex)
        {
            AuditLog.Write("sftp_upload", new[] { target }, local, $"실패 ({files.Count}개 중 {done}개 올림): {ex.Message}");
            throw;
        }
        catch (OperationCanceledException)
        {
            AuditLog.Write("sftp_upload", new[] { target }, local, $"요청 취소로 중단 ({files.Count}개 중 {done}개 올림)");
            throw new McpToolException($"요청이 취소되어 중단했습니다 ({files.Count}개 중 {done}개 올림).");
        }
        catch (Exception ex)
        {
            AuditLog.Write("sftp_upload", new[] { target }, local, $"실패 ({files.Count}개 중 {done}개 올림): {ex.Message}");
            throw;
        }

        string UploadCore(Renci.SshNet.SftpClient c, CancellationToken ct)
        {
            // 승인 대기 중 최상위 경로가 링크·정션으로 바뀌었을 수 있다 — 접속 뒤 쓰기 전에 다시 검사 (파일별 핸들 검사는 아래)
            if (McpHelpers.BlockedUploadReason(local, AppPaths.DataDir, AppPaths.RootDir) is { } again) throw new McpToolException(again);
            var name = Path.GetFileName(local.TrimEnd('\\'));
            var root = SftpPanel.Combine(remoteDir, name);
            if (c.Exists(root) && !overwrite) throw new McpToolException($"원격에 이미 있습니다: {root} (덮어쓰려면 overwrite=true)");
            if (isDir)
            {
                if (!c.Exists(root)) c.CreateDirectory(root);
                foreach (var d in Directory.EnumerateDirectories(local, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    var rd = SftpPanel.Combine(root, Path.GetRelativePath(local, d).Replace('\\', '/'));
                    if (!c.Exists(rd)) c.CreateDirectory(rd);
                }
            }
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                var remote = isDir ? SftpPanel.Combine(root, Path.GetRelativePath(local, f).Replace('\\', '/')) : root;
                // 승인 뒤 읽기 직전에 다시 검사한다: 파일을 열고 그 핸들의 실제 경로·하드 링크 수로 판단한 뒤 같은 핸들로 읽는다
                // (승인 대기 중 링크로 바꿔치기·id_rsa 하드 링크 차단)
                FileStream fs;
                try { fs = McpHelpers.OpenUploadFile(f, AppPaths.DataDir, AppPaths.RootDir); }
                catch (InvalidOperationException ex) { throw new McpToolException($"{ex.Message} — {f}"); }
                using (fs) c.UploadFile(new CancellableStream(fs, ct), remote, true);
                done++;
            }
            AuditLog.Write("sftp_upload", new[] { target }, local, $"완료 {files.Count}개 {SftpPanel.HumanSize(total)}");
            return $"올렸습니다: {files.Count}개 ({SftpPanel.HumanSize(total)}) → {root}";
        }
    }

    /// <summary>
    /// 쓰기 한도가 있는 스트림 (sftp_download): limit 바이트를 넘겨 쓰려 하면 McpToolException.
    /// 목록에 보인 크기와 달리 받는 동안 자라는 파일·끝없는 파일이 이 PC 디스크를 채우지 않게
    /// </summary>
    private sealed class LimitedWriteStream(Stream inner, long limit, Func<string> message) : Stream
    {
        private long _written;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_written + count > limit) throw new McpToolException(message());
            inner.Write(buffer, offset, count);
            _written += count;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
