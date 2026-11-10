using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;

namespace Nubis;

/// <summary>로그 파일이 너무 커지지 않도록 일정 크기를 넘으면 .1 로 넘긴다 (최근 2개 유지)</summary>
public static class LogFiles
{
    public const long MaxBytes = 5 * 1024 * 1024;

    public static void Rotate(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length < MaxBytes) return;
            File.Move(path, path + ".1", overwrite: true);
        }
        catch { }
    }
}

/// <summary>Claude 가 요청한 실행 기록 (claude_audit.log)</summary>
public static class AuditLog
{
    private static readonly object Gate = new();
    public static string FilePath => Path.Combine(AppPaths.DataDir, "claude_audit.log");

    public static void Write(string tool, IEnumerable<string> targets, string command, string outcome)
    {
        lock (Gate)
        {
            try
            {
                LogFiles.Rotate(FilePath);
                File.AppendAllText(FilePath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {tool} | {outcome}\r\n" +
                    $"  대상: {string.Join(", ", targets)}\r\n" +
                    $"  명령: {command.Replace("\n", "\n        ")}\r\n\r\n");
            }
            catch { }
        }
    }
}
