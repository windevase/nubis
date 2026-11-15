using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Nubis;

// MCP 도구: SFTP 전송 (sftp_download · sftp_upload) — 등록은 MainWindow.McpSftp.cs 의 SftpTools
public partial class MainWindow
{
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
        var conn = await SftpTargetAsync(a);
        var h = conn.Host;
        var path = Arg(a, "path").Trim();
        if (path.Length == 0) throw new McpToolException("path 가 필요합니다.");
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Nubis", SftpPanel.SafeLocalName(h.DisplayName));
        var target = conn.Label;
        var what = $"받기: {path}\n저장 위치: {root}";
        var ok = await OnUiAsync(() => AskApprovalAsync("sftp_download", L.T("Claude 가 파일 다운로드를 요청합니다"),
            L.T("서버의 파일을 이 PC 로 받습니다. 기존 파일은 덮어쓰지 않습니다(이름 뒤에 번호).") + conn.Note, new[] { target }, what));
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
        var conn = await SftpTargetAsync(a);
        var local = Arg(a, "local_path").Trim().Trim('"');
        var remoteDir = Arg(a, "remote_dir").Trim();
        var overwrite = BoolArg(a, "overwrite") == true;
        if (local.Length == 0 || remoteDir.Length == 0) throw new McpToolException("local_path 와 remote_dir 가 필요합니다.");
        if (!Path.IsPathFullyQualified(local)) throw new McpToolException("local_path 는 절대 경로여야 합니다.");
        // 링크(끊긴 링크 포함)를 알아볼 수 없으면 승인 창을 띄우기 전에 거부한다 (OverwriteLinkRefusal 이 어차피 모든 대상을 거부)
        if (!SftpRaw.LStatSupported) throw new McpToolException("이 SSH.NET 버전에서는 심볼릭 링크를 확인할 수 없어 sftp_upload 를 쓸 수 없습니다.");
        if (McpHelpers.BlockedUploadReason(local, AppPaths.DataDir, AppPaths.RootDir) is { } why) throw new McpToolException(why);
        var isDir = Directory.Exists(local);
        if (!isDir && !File.Exists(local)) throw new McpToolException($"로컬 경로가 없습니다: {local}");

        var files = isDir ? Directory.EnumerateFiles(local, "*", SearchOption.AllDirectories).ToList() : new List<string> { local };
        if (files.Count > 5000) throw new McpToolException("파일이 5000개를 넘습니다.");
        var blocked = files.Select(f => McpHelpers.BlockedUploadReason(f, AppPaths.DataDir, AppPaths.RootDir)).FirstOrDefault(r => r != null);
        if (blocked != null) throw new McpToolException(blocked);
        var total = files.Sum(f => new FileInfo(f).Length);

        var target = conn.Label;
        var what = $"올리기: {local}{(isDir ? $" (폴더, 파일 {files.Count}개)" : "")} — {SftpPanel.HumanSize(total)}\n원격 폴더: {remoteDir}" + (overwrite ? "\n같은 이름이 있으면 덮어씀 (심볼릭 링크는 덮어쓰지 않음)" : "");
        var ok = await OnUiAsync(() => AskApprovalAsync("sftp_upload", L.T("Claude 가 파일 업로드를 요청합니다"),
            L.T("이 PC 의 파일을 서버로 보냅니다. 보내도 되는 파일인지 확인하세요.") + conn.Note, new[] { target }, what));
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
            // 덮어쓸 대상이 심볼릭 링크면 거부한다 — UploadFile(덮어쓰기)은 링크를 따라가 링크가 가리키는 파일(폴더 밖 /etc 등)에 쓴다.
            // 끊긴 링크도 LSTAT 으로 잡는다 (Exists 는 못 보지만 열기(O_CREAT)는 링크 대상 파일을 새로 만든다)
            if (OverwriteLinkRefusal(c, root) is { } rootLink) throw new McpToolException(rootLink);
            var rootExisted = c.Exists(root);
            if (rootExisted && !overwrite) throw new McpToolException($"원격에 이미 있습니다: {root} (덮어쓰려면 overwrite=true)");
            string RemoteOf(string f) => isDir ? SftpPanel.Combine(root, Path.GetRelativePath(local, f).Replace('\\', '/')) : root;
            // 이미 있던 폴더에 덮어쓰면 올리기 전에 모든 대상(하위 폴더·파일)의 링크 여부를 먼저 본다 — 중간에 멈춰 반쯤 올린 채 남지 않게
            if (isDir && rootExisted)
            {
                foreach (var d in Directory.EnumerateDirectories(local, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    if (OverwriteLinkRefusal(c, RemoteOf(d)) is { } dirLink) throw new McpToolException(dirLink);
                }
                foreach (var f in files)
                {
                    ct.ThrowIfCancellationRequested();
                    if (OverwriteLinkRefusal(c, RemoteOf(f)) is { } fileLink) throw new McpToolException(fileLink);
                }
            }
            if (isDir)
            {
                if (!rootExisted) c.CreateDirectory(root);
                foreach (var d in Directory.EnumerateDirectories(local, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    var rd = RemoteOf(d);
                    if (!c.Exists(rd)) c.CreateDirectory(rd);
                }
            }
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                var remote = RemoteOf(f);
                // 쓰기 직전에 한 번 더 (위 확인과 쓰기 사이에 링크로 바뀐 경우를 좁힌다 — 그 사이 경쟁은 SFTP 로는 완전히 막을 수 없음)
                if ((rootExisted || !isDir) && OverwriteLinkRefusal(c, remote) is { } link) throw new McpToolException(link);
                // 승인 뒤 읽기 직전에 다시 검사한다: 파일을 열고 그 핸들의 실제 경로·하드 링크 수로 판단한 뒤 같은 핸들로 읽는다
                // (승인 대기 중 링크로 바꿔치기·id_rsa 하드 링크 차단)
                FileStream fs;
                try { fs = McpHelpers.OpenUploadFile(f, AppPaths.DataDir, AppPaths.RootDir); }
                catch (InvalidOperationException ex) { throw new McpToolException($"{ex.Message} — {f}"); }
                // 새로 만드는 대상(없던 최상위 폴더 아래 · 없던 파일)은 "없을 때만" 만든다 (CREAT|EXCL) — 확인 뒤 그 이름에 링크가 생겨도 따라가지 않음
                using (fs) c.UploadFile(new CancellableStream(fs, ct), remote, rootExisted);
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
