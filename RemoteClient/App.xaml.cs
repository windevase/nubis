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

        // 화면 언어: 처음 실행이면 고르게 하고(한국어 기본), 앱 전체 문구에 적용 (바꾸면 다시 시작해야 적용)
        LanguageWindow.EnsureChosen();
        L.Init(SettingsStore.Global.Language);

        // 시작 화면 로그인 (설정한 경우). 로그인에 실패·취소하면 메인 창을 열지 않고 종료
        if (!LoginWindow.EnsureAtStartup(out var signedIn))
        {
            Shutdown();
            return;
        }

        // 계정별 프로필: 로그인한 계정(또는 로그인을 껐을 때 소유자)의 폴더로 데이터 위치를 바꾼다.
        // 소유자가 처음 로그인하면 로그인을 켜기 전 공통 폴더의 데이터를 넘겨받는다.
        var cfg = SettingsStore.Global.Login;
        // 소유자 기능이 생기기 전에 로그인을 설정한 경우: 업그레이드 후 처음 로그인한 계정을 소유자로 (기존 데이터를 넘겨받음)
        if (cfg.Enabled && cfg.OwnerKey.Length == 0 && signedIn != null)
        {
            SettingsStore.UpdateGlobal(s => s.Login.OwnerKey = signedIn);
            cfg = SettingsStore.Global.Login;
        }
        if (Profiles.Choose(cfg, signedIn, Profiles.Exists) is { } key)
        {
            var dir = Profiles.Dir(key);
            var owner = key == cfg.OwnerKey;
            if (owner) Profiles.ClaimOwner(cfg, dir, AppPaths.RootDir, AppPaths.UsersDir);
            AppPaths.UseDataDir(dir);
            SettingsStore.UseProfile(inheritGlobal: owner);
        }

        var main = new MainWindow();
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        main.Show();
    }

    /// <summary>앱을 다시 시작 (언어·로그인 설정을 바꾼 뒤). 같은 환경변수(NUBIS_DATA_DIR 등)로 새로 띄우고 지금 것은 끈다</summary>
    public static void Restart()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe != null) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false });
        }
        catch { }
        Current.Shutdown();
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
