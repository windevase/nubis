using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Nubis;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        CloudPaths.Apply();   // 사용자가 지정한 클라우드 인증 경로를 환경변수로 적용
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            LogFiles.Rotate(Path.Combine(AppPaths.DataDir, "error.log"));
            File.AppendAllText(Path.Combine(AppPaths.DataDir, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}\r\n\r\n");
        }
        catch { /* 로그 실패는 무시 */ }

        Dialog.Show(e.Exception.Message, "Nubis 오류", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
