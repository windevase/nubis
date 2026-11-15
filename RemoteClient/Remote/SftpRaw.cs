using System;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Nubis;

/// <summary>
/// 경로를 서버에서 해석(REALPATH)하지 않고 그대로 보내는 SFTP 삭제·이름 바꾸기.
///
/// SSH.NET 의 SftpClient.DeleteFile / RenameFile 은 먼저 GetCanonicalPath(SSH_FXP_REALPATH)를 부르는데,
/// OpenSSH 의 realpath 는 심볼릭 링크를 따라간다 → 링크를 지우면 링크가 가리키는 원본이 지워지고,
/// 링크 이름을 바꾸면 원본이 옮겨진다. 링크 자체를 다루려면 SSH_FXP_REMOVE / SSH_FXP_RENAME 에 경로를 그대로 넣어야 한다.
///
/// SSH.NET 은 이 API 를 공개하지 않아 SftpClient 의 internal 속성 SftpSession(ISftpSession)을 리플렉션으로 꺼내
/// RequestRemove / RequestRename 을 직접 부른다. SharedSftpClient · SudoSftp 가 만드는 클라이언트도 모두 SftpClient 라 그대로 된다.
/// SSH.NET 이 바뀌어 찾지 못하면 <see cref="Supported"/> 가 false 이고, 링크 작업은 거부한다
/// (DeleteFile / RenameFile 로 대신하면 원본이 지워지므로 절대 대신하지 않는다).
/// </summary>
public static class SftpRaw
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private static readonly PropertyInfo? SessionProperty = typeof(SftpClient).GetProperty("SftpSession", Any);
    private static readonly FieldInfo? SessionField = typeof(SftpClient).GetField("_sftpSession", Any);
    private static readonly Type? SessionType = typeof(SftpClient).Assembly.GetType("Renci.SshNet.Sftp.ISftpSession");
    private static readonly MethodInfo? RemoveMethod = SessionType?.GetMethod("RequestRemove", new[] { typeof(string) });
    private static readonly MethodInfo? RenameMethod = SessionType?.GetMethod("RequestRename", new[] { typeof(string), typeof(string) });

    /// <summary>리플렉션 대상이 모두 있는가 (SSH.NET 업데이트로 내부 구조가 바뀌었는지 테스트가 확인)</summary>
    public static bool Supported =>
        (SessionProperty?.GetMethod != null || SessionField != null) && RemoveMethod != null && RenameMethod != null;

    /// <summary>경로를 그대로 SSH_FXP_REMOVE — 링크면 링크 자체만 지운다 (원본은 그대로)</summary>
    public static void Remove(SftpClient client, string path)
    {
        RequireAbsolute(path);
        Invoke(RemoveMethod, Session(client), path);
    }

    /// <summary>경로를 그대로 SSH_FXP_RENAME — 링크면 링크 자체의 이름만 바꾼다 (원본은 그대로)</summary>
    public static void Rename(SftpClient client, string oldPath, string newPath)
    {
        RequireAbsolute(oldPath);
        RequireAbsolute(newPath);
        Invoke(RenameMethod, Session(client), oldPath, newPath);
    }

    private static object Session(SftpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (!Supported) throw new NotSupportedException(L.T("이 버전에서는 심볼릭 링크 자체를 지우거나 이름을 바꿀 수 없어 작업하지 않았습니다 (링크가 가리키는 원본이 대신 바뀌는 것을 막기 위함)."));
        var session = SessionProperty?.GetMethod != null ? SessionProperty.GetValue(client) : SessionField!.GetValue(client);
        return session ?? throw new SshConnectionException("Client not connected.");
    }

    // 상대 경로는 서버의 작업 폴더 기준이 되어 의도와 다른 곳을 가리킬 수 있다 — 패널은 항상 절대 경로를 쓴다
    private static void RequireAbsolute(string path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '/')
            throw new ArgumentException("SFTP path must be absolute: " + path, nameof(path));
    }

    private static void Invoke(MethodInfo? method, object session, params object[] args)
    {
        try { method!.Invoke(session, args); }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            // SftpPathNotFoundException · SftpPermissionDeniedException 등 원래 예외를 그대로 올린다
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}
