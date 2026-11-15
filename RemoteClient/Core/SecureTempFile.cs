using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Nubis;

/// <summary>
/// 비밀이 든 임시 파일 (kubectl 이 읽을 복호화한 kubeconfig). 현재 사용자만 접근하도록 상속을 끊은 ACL 로 만들고,
/// 쓰는 동안 읽기 공유로 열어 두어(다른 Nubis 의 정리가 지우지 못하게) Dispose 때 지운다.
/// 앱이 비정상 종료해 남은 파일은 다음 시작 때 CleanupStale 이 지운다.
/// </summary>
public sealed class SecureTempFile : IDisposable
{
    /// <summary>쿠버네티스 임시 kubeconfig 폴더 (데이터 폴더 아래 — ClaudeGuard 가 이 폴더 읽기를 막는다)</summary>
    public static string K8sDir => System.IO.Path.Combine(AppPaths.DataDir, "k8s-run");

    public string Path { get; }
    private FileStream? _hold;

    private SecureTempFile(string path, FileStream hold)
    {
        Path = path;
        _hold = hold;
    }

    public static SecureTempFile Create(string dir, string content, string extension = ".yaml")
    {
        EnsureDir(dir);
        var path = System.IO.Path.Combine(dir, Guid.NewGuid().ToString("N") + extension);
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(CurrentUser, FileSystemRights.FullControl, AccessControlType.Allow));
        var bytes = new UTF8Encoding(false).GetBytes(content);
        using (var fs = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, security))
            fs.Write(bytes, 0, bytes.Length);
        try
        {
            // 읽기만 공유 — 다른 프로세스(kubectl)는 읽을 수 있고, 지우기는 이 핸들이 닫힐 때까지 실패한다
            var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return new SecureTempFile(path, hold);
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    private static SecurityIdentifier CurrentUser
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return id.User ?? throw new InvalidOperationException("current user SID");
        }
    }

    private static void EnsureDir(string dir)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(CurrentUser, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        var info = new DirectoryInfo(dir);
        if (!info.Exists) info.Create(security);
        else
        {
            try { info.SetAccessControl(security); } catch { /* 권한을 바꿀 수 없어도 파일 자체 ACL 은 따로 건다 */ }
        }
    }

    /// <summary>
    /// 남은 임시 파일을 지운다 (앱 시작 때). 다른 Nubis 가 쓰는 중인 파일은 열려 있어 지워지지 않고 남는다
    /// </summary>
    public static int CleanupStale(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        var n = 0;
        foreach (var f in Directory.EnumerateFiles(dir))
            if (TryDelete(f)) n++;
        return n;
    }

    private static bool TryDelete(string path)
    {
        try { File.Delete(path); return !File.Exists(path); }
        catch { return false; }
    }

    public void Dispose()
    {
        var hold = _hold;
        _hold = null;
        if (hold == null) return;
        hold.Dispose();
        TryDelete(Path);
    }
}
