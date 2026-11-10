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

    /// <summary>원격 읽기: run_command 의 읽기 전용 자동 승인 설정을 따른다</summary>
    private async Task ApproveRemoteRead(string tool, HostEntry h, string what)
    {
        var target = $"[SFTP]  {h.DisplayName}  ({string.Join(" → ", h.AddressCandidates())})";
        if (SettingsStore.Current.AutoApproveReadOnly) { AuditLog.Write(tool, new[] { target }, what, "자동 승인(읽기 전용)"); return; }
        var ok = await OnUi(() => ApprovalDialog.Ask(this, "Claude 가 원격 파일 읽기를 요청합니다",
            "승인하면 서버에 SFTP 로 접속해 아래 내용을 읽어 Claude 에게 돌려줍니다 (서버는 바꾸지 않음).", new[] { target }, what));
        AuditLog.Write(tool, new[] { target }, what, ok ? "승인" : "거부");
        if (!ok) throw new McpToolException("사용자가 거부했습니다.");
    }

    private static async Task<T> WithSftp<T>(HostEntry conn, Func<Renci.SshNet.SftpClient, T> f) => await Task.Run(() =>
    {
        Renci.SshNet.SftpClient c;
        try { c = SshExec.OpenSftp(conn); }
        catch (InvalidOperationException ex) { throw new McpToolException(ex.Message); }
        using (c)
        {
            try { return f(c); }
            catch (Renci.SshNet.Common.SftpPathNotFoundException) { throw new McpToolException("원격 경로가 없습니다."); }
            catch (Renci.SshNet.Common.SftpPermissionDeniedException) { throw new McpToolException("원격 경로에 권한이 없습니다."); }
        }
    });

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
        await ApproveRemoteRead("sftp_read", h, $"파일 읽기: {path} ({(tail ? "끝 " : "앞 ")}{SftpPanel.HumanSize(max)})");
        return await WithSftp(conn, c =>
        {
            var attr = c.GetAttributes(path);
            if (attr.IsDirectory) throw new McpToolException("폴더입니다. sftp_list 를 쓰세요.");
            using var s = c.OpenRead(path);
            var size = attr.Size;
            if (tail && size > max) s.Seek(size - max, SeekOrigin.Begin);
            var buf = new byte[Math.Min(max, size > 0 ? size : max)];
            var read = 0;
            while (read < buf.Length) { var n = s.Read(buf, read, buf.Length - read); if (n <= 0) break; read += n; }
            var data = buf.AsSpan(0, read);
            if (McpHelpers.LooksBinary(data)) throw new McpToolException($"바이너리 파일로 보입니다 ({SftpPanel.HumanSize(size)}). 필요하면 sftp_download 로 받으세요.");
            var text = Encoding.UTF8.GetString(data);
            var cut = size > read ? (tail ? $" (끝 {SftpPanel.HumanSize(read)}만)" : $" (앞 {SftpPanel.HumanSize(read)}만)") : "";
            return $"[{h.DisplayName}] {path} — {SftpPanel.HumanSize(size)}{cut}\n{text}";
        });
    }

    private async Task<string> SftpDownloadAsync(JsonElement a)
    {
        var (h, conn) = await SftpTargetAsync(a);
        var path = Arg(a, "path").Trim();
        if (path.Length == 0) throw new McpToolException("path 가 필요합니다.");
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Nubis", SftpPanel.SafeLocalName(h.DisplayName));
        var target = $"[SFTP]  {h.DisplayName}  ({string.Join(" → ", h.AddressCandidates())})";
        var what = $"받기: {path}\n저장 위치: {root}";
        var ok = await OnUi(() => ApprovalDialog.Ask(this, "Claude 가 파일 다운로드를 요청합니다",
            "서버의 파일을 이 PC 로 받습니다. 기존 파일은 덮어쓰지 않습니다(이름 뒤에 번호).", new[] { target }, what));
        AuditLog.Write("sftp_download", new[] { target }, what, ok ? "승인" : "거부");
        if (!ok) throw new McpToolException("사용자가 거부했습니다.");

        return await WithSftp(conn, c =>
        {
            Directory.CreateDirectory(root);
            var attr = c.GetAttributes(path);
            var top = SftpPanel.UniquePath(Path.Combine(root, SftpPanel.SafeLocalName(McpHelpers.RemoteFileName(path))), attr.IsDirectory);
            var count = 0;
            long bytes = 0;
            var full = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            void Get(string remote, bool isDir, string local)
            {
                if (!Path.GetFullPath(local).StartsWith(full, StringComparison.OrdinalIgnoreCase)) throw new McpToolException("허용되지 않는 파일 이름이 있어 중단했습니다.");
                if (isDir)
                {
                    Directory.CreateDirectory(local);
                    foreach (var f in c.ListDirectory(remote).Where(f => f.Name is not "." and not ".."))
                        Get(f.FullName, f.IsDirectory && !f.IsSymbolicLink, Path.Combine(local, SftpPanel.SafeLocalName(f.Name)));
                    return;
                }
                if (++count > 5000) throw new McpToolException("파일이 5000개를 넘어 중단했습니다.");
                using var fs = File.Create(local);
                c.DownloadFile(remote, fs);
                bytes += fs.Length;
            }
            Get(path, attr.IsDirectory, top);
            AuditLog.Write("sftp_download", new[] { target }, path, $"완료 {count}개 {SftpPanel.HumanSize(bytes)}");
            return $"받았습니다: {count}개 ({SftpPanel.HumanSize(bytes)}) → {top}";
        });
    }

    private async Task<string> SftpUploadAsync(JsonElement a)
    {
        var (h, conn) = await SftpTargetAsync(a);
        var local = Arg(a, "local_path").Trim().Trim('"');
        var remoteDir = Arg(a, "remote_dir").Trim();
        var overwrite = BoolArg(a, "overwrite") == true;
        if (local.Length == 0 || remoteDir.Length == 0) throw new McpToolException("local_path 와 remote_dir 가 필요합니다.");
        if (!Path.IsPathFullyQualified(local)) throw new McpToolException("local_path 는 절대 경로여야 합니다.");
        if (McpHelpers.BlockedUploadReason(local, AppPaths.DataDir) is { } why) throw new McpToolException(why);
        var isDir = Directory.Exists(local);
        if (!isDir && !File.Exists(local)) throw new McpToolException($"로컬 경로가 없습니다: {local}");

        var files = isDir ? Directory.EnumerateFiles(local, "*", SearchOption.AllDirectories).ToList() : new List<string> { local };
        if (files.Count > 5000) throw new McpToolException("파일이 5000개를 넘습니다.");
        var blocked = files.Select(f => McpHelpers.BlockedUploadReason(f, AppPaths.DataDir)).FirstOrDefault(r => r != null);
        if (blocked != null) throw new McpToolException(blocked);
        var total = files.Sum(f => new FileInfo(f).Length);

        var target = $"[SFTP]  {h.DisplayName}  ({string.Join(" → ", h.AddressCandidates())})";
        var what = $"올리기: {local}{(isDir ? $" (폴더, 파일 {files.Count}개)" : "")} — {SftpPanel.HumanSize(total)}\n원격 폴더: {remoteDir}" + (overwrite ? "\n같은 이름이 있으면 덮어씀" : "");
        var ok = await OnUi(() => ApprovalDialog.Ask(this, "Claude 가 파일 업로드를 요청합니다",
            "이 PC 의 파일을 서버로 보냅니다. 보내도 되는 파일인지 확인하세요.", new[] { target }, what));
        AuditLog.Write("sftp_upload", new[] { target }, what, ok ? "승인" : "거부");
        if (!ok) throw new McpToolException("사용자가 거부했습니다.");

        return await WithSftp(conn, c =>
        {
            var name = Path.GetFileName(local.TrimEnd('\\'));
            var root = SftpPanel.Combine(remoteDir, name);
            if (c.Exists(root) && !overwrite) throw new McpToolException($"원격에 이미 있습니다: {root} (덮어쓰려면 overwrite=true)");
            if (isDir)
            {
                if (!c.Exists(root)) c.CreateDirectory(root);
                foreach (var d in Directory.EnumerateDirectories(local, "*", SearchOption.AllDirectories))
                {
                    var rd = SftpPanel.Combine(root, Path.GetRelativePath(local, d).Replace('\\', '/'));
                    if (!c.Exists(rd)) c.CreateDirectory(rd);
                }
            }
            foreach (var f in files)
            {
                var remote = isDir ? SftpPanel.Combine(root, Path.GetRelativePath(local, f).Replace('\\', '/')) : root;
                using var fs = File.OpenRead(f);
                c.UploadFile(fs, remote, true);
            }
            AuditLog.Write("sftp_upload", new[] { target }, local, $"완료 {files.Count}개 {SftpPanel.HumanSize(total)}");
            return $"올렸습니다: {files.Count}개 ({SftpPanel.HumanSize(total)}) → {root}";
        });
    }
}
