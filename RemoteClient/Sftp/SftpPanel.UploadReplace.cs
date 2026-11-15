using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace Nubis;

// 파일 하나 올리기 (임시 파일에 쓴 뒤 바꿔치기) — 패널 업로드 · 편집 저장 · MCP sftp_write 가 같이 쓴다
public partial class SftpPanel
{
    /// <summary>
    /// 파일 하나 올리기. 취소·실패 때 서버에 반쯤 쓴 파일이 남지 않게:
    /// - 새 파일: 그대로 올리고, 중간에 멈추면 지운다
    /// - 이미 있는 내 파일: 임시 이름으로 올린 뒤 원래 파일의 권한·그룹을 옮기고 이름을 바꿔 교체한다. 멈추면 원래 파일은 그대로
    /// 임시 파일 방식이 안 되는 경우는 원래 파일에 바로 덮어쓴다 (예전 방식):
    /// - 심볼릭 링크 (이름을 바꾸면 링크가 일반 파일로 바뀜 → 링크가 가리키는 파일에 써야 함)
    /// - 파일은 쓸 수 있지만 폴더에 새 파일을 못 만드는 곳 (예: /etc/app/config.yml), 서버가 EXCL 만들기를 지원하지 않음
    /// - 내 소유가 아닌 파일 (그룹 쓰기 권한으로 고치는 www-data 파일 등 — 바꿔치기하면 소유자가 바뀌고 하드링크·ACL 이 끊김)
    /// - 이름 바꾸기 실패 — 원본을 먼저 지우지 않는다
    /// </summary>
    /// <param name="open">올릴 내용 (부를 때마다 처음부터 읽는 새 스트림 — 교체 실패 때 다시 올린다)</param>
    /// <param name="knownMissing">방금 폴더 목록으로 없다고 확인함 — 다시 묻지 않는다</param>
    internal static async Task UploadOneAsync(SftpClient c, Func<Stream> open, string remote, IProgress<Renci.SshNet.UploadFileProgressReport> progress, CancellationToken ct,
        bool knownMissing = false)
    {
        var old = knownMissing ? null : await Task.Run(() => TryGetAttributes(c, remote), ct);
        if (old == null)
        {
            try { await UploadToAsync(c, open, remote, progress, ct); }
            catch { await Task.Run(() => { RemoveQuietly(c, remote); }); throw; }
            return;
        }
        // GetAttributes 는 경로를 REALPATH 로 풀어 링크 대상의 속성을 돌려주므로, 여기서 링크로 보이는 것은 대상이 없는(끊긴) 링크뿐이다.
        // 살아 있는 링크는 아래 바꿔치기에서 RenameFile 도 경로를 풀어 대상 파일을 교체하므로 링크는 그대로 남는다
        if (old.IsSymbolicLink) { await UploadToAsync(c, open, remote, progress, ct); return; }

        // 임시 이름은 예측할 수 없게, 이미 있으면 실패하게(심어 둔 링크를 따라가지 않음), 내용을 쓰기 전에 0600 으로
        var temp = UploadTempName(remote);
        var created = false;
        try
        {
            try
            {
                await Task.Run(() => CreatePrivate(c, temp), ct);
                created = true;
            }
            catch (SftpException)
            {
                // 폴더에 임시 파일을 만들 수 없음(권한 없음) · 서버가 "없을 때만 만들기"(EXCL)를 지원하지 않음(SSH_FX_FAILURE·OP_UNSUPPORTED 등)
                // → 아래에서 원래 파일에 바로 덮어쓴다. 만들기가 실패했으므로 그 이름의 파일은 지우지 않는다 (남의 것일 수 있음)
            }
            // 내 파일이 아니면(예: www-data 소유 664 파일을 그룹 쓰기로 편집) 바꿔치기하면 소유자가 나로 바뀌고
            // 하드링크·ACL 이 끊긴다 → 빈 임시 파일만 지우고 원래 파일에 바로 덮어쓴다 (예전 방식)
            if (created && !await Task.Run(() => TryMatchOwner(c, temp, old), ct))
            {
                created = false;
                await Task.Run(() => { RemoveQuietly(c, temp); });
            }
            if (created) await UploadToAsync(c, open, temp, progress, ct);
        }
        catch (OperationCanceledException)
        {
            if (created) await Task.Run(() => { RemoveQuietly(c, temp); });
            throw;
        }
        catch
        {
            // 직접 만든 임시 파일만 지운다 (만들기에 실패했다면 그 이름의 파일은 남의 것)
            if (created) await Task.Run(() => { RemoveQuietly(c, temp); });
            throw;
        }
        if (!created)
        {
            // 임시 파일을 못 만들었거나 남의 파일 → 원래 파일에 바로 덮어쓰기 (실패해도 다시 시도하지 않음)
            await UploadToAsync(c, open, remote, progress, ct);
            return;
        }

        var replaced = await Task.Run(() =>
        {
            try
            {
                var a = c.GetAttributes(temp);
                a.OwnerCanRead = old.OwnerCanRead; a.OwnerCanWrite = old.OwnerCanWrite; a.OwnerCanExecute = old.OwnerCanExecute;
                a.GroupCanRead = old.GroupCanRead; a.GroupCanWrite = old.GroupCanWrite; a.GroupCanExecute = old.GroupCanExecute;
                a.OthersCanRead = old.OthersCanRead; a.OthersCanWrite = old.OthersCanWrite; a.OthersCanExecute = old.OthersCanExecute;
                c.SetAttributes(temp, a);
            }
            catch { /* 권한을 못 옮겨도 교체는 진행 (소유자·그룹은 TryMatchOwner 에서 이미 맞춤) */ }
            try { c.RenameFile(temp, remote, true); return true; }   // posix-rename: 원자적 교체 (OpenSSH)
            catch { return false; }
        }, CancellationToken.None);
        if (replaced) return;

        // posix-rename 미지원 등으로 교체 실패: 원본은 건드리지 않았으니 원본에 바로 덮어쓰고 임시 파일을 지운다
        try { await UploadToAsync(c, open, remote, progress, ct); }
        finally { await Task.Run(() => { RemoveQuietly(c, temp); }); }
    }

    /// <summary>
    /// 실패 · 취소 뒤 정리용 삭제 (오류 무시). 경로를 그대로 지운다(SftpRaw.Remove) — DeleteFile 은 REALPATH 로 링크를 따라가,
    /// 그사이 그 이름이 링크로 바뀌었으면 링크가 가리키는 다른 파일을 지운다. SftpRaw 를 못 쓰는 버전에서만 예전처럼 DeleteFile
    /// </summary>
    private static void RemoveQuietly(SftpClient c, string path)
    {
        try
        {
            if (SftpRaw.Supported && path.StartsWith('/')) SftpRaw.Remove(c, path);
            else c.DeleteFile(path);
        }
        catch { }
    }

    private static async Task UploadToAsync(SftpClient c, Func<Stream> open, string path, IProgress<Renci.SshNet.UploadFileProgressReport> progress, CancellationToken ct)
    {
        await using var fs = open();
        await c.UploadFileAsync(fs, path, true, progress, ct);
    }

    /// <summary>
    /// 바꿔치기 업로드를 해도 되는가 — 방금 만든 임시 파일(소유자 = 접속한 사용자)의 소유자·그룹이 원래 파일과 같을 때만.
    /// 다르면(남의 파일, 그룹을 못 맞춤) 바꿔치기가 소유자를 바꾸고 하드링크·ACL 을 끊으므로 원래 파일에 바로 덮어쓴다
    /// </summary>
    public static bool CanSwapReplace(int oldUserId, int oldGroupId, int tempUserId, int tempGroupId) =>
        oldUserId == tempUserId && oldGroupId == tempGroupId;

    /// <summary>
    /// 빈 임시 파일의 그룹을 원래 파일에 맞춰 보고(내 파일이면 내가 속한 그룹으로는 바꿀 수 있다), 바꿔치기해도 되는지 돌려준다.
    /// 소유자는 바꾸려 하지 않는다 — 남의 파일이면 바로 덮어쓰기로 간다. 속성을 못 읽으면 false (안전하게 덮어쓰기)
    /// </summary>
    private static bool TryMatchOwner(SftpClient c, string temp, SftpFileAttributes old)
    {
        try
        {
            var a = c.GetAttributes(temp);
            if (a.UserId != old.UserId) return false;
            if (a.GroupId != old.GroupId)
            {
                try { a.GroupId = old.GroupId; c.SetAttributes(temp, a); } catch { }
                a = c.GetAttributes(temp);
            }
            return CanSwapReplace(old.UserId, old.GroupId, a.UserId, a.GroupId);
        }
        catch (SftpException) { return false; }
    }

    /// <summary>교체 업로드용 임시 이름 — 같은 폴더, 매번 다른 임의 접미사 (world-writable 폴더에 미리 심어 둔 이름을 쓰지 않게)</summary>
    public static string UploadTempName(string remote) =>
        $"{remote}.{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)).ToLowerInvariant()}.nubis-upload";

    /// <summary>
    /// 빈 파일을 "없을 때만" 만들고(O_CREAT|O_EXCL — 이미 있거나 링크면 실패) 소유자만 읽고 쓰게(0600) 한다.
    /// 내용은 그 뒤에 쓰므로 umask 권한(0644)으로 내용이 보이는 순간이 없다. 권한 변경 실패는 무시 (파일은 이미 내 것)
    /// </summary>
    private static void CreatePrivate(SftpClient c, string path)
    {
        using (c.Open(path, FileMode.CreateNew, FileAccess.Write)) { }
        try
        {
            var a = c.GetAttributes(path);
            a.OwnerCanRead = true; a.OwnerCanWrite = true; a.OwnerCanExecute = false;
            a.GroupCanRead = false; a.GroupCanWrite = false; a.GroupCanExecute = false;
            a.OthersCanRead = false; a.OthersCanWrite = false; a.OthersCanExecute = false;
            c.SetAttributes(path, a);
        }
        catch { }
    }
}
