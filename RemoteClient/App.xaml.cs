using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Nubis;

public partial class App : Application
{
    /// <summary>App.Restart 로 띄운 새 프로세스: 이전 프로세스가 끝날 때까지 기다린 뒤 시작</summary>
    private const string WaitPreviousArg = "--wait-previous";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;

        // 같은 데이터 폴더에는 한 인스턴스만 — 두 개가 각자 들고 있는 목록으로 저장하면 서로의 변경을 덮어쓴다.
        // 이미 실행 중이면 명령줄(--connect · --claude)을 그쪽에 넘기고(창을 앞으로) 종료
        var restarted = Array.Exists(e.Args, a => a.Equals(WaitPreviousArg, StringComparison.OrdinalIgnoreCase));
        if (!SingleInstance.TryAcquire(AppPaths.RootDir, restarted ? RestartWaitMs : 0))
        {
            L.Init(SettingsStore.Global.Language);
            if (restarted)
            {
                // 다시 시작: 끝나 가는 이전 프로세스에 명령줄을 넘기면 "넘김 성공" 으로 이쪽이 끝나고 곧 이전 것도 끝나 앱이 사라진다.
                // 그래서 넘기지 않고, 이전 프로세스가 끝나지 않으면 알리고 끝낸다
                Dialog.Show(L.T("이전 Nubis 가 끝나지 않아 다시 시작하지 못했습니다.\n작업 관리자에서 남아 있는 Nubis.exe 가 끝났는지 확인한 뒤 직접 실행하세요."),
                    "Nubis", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                switch (SingleInstance.Forward(AppPaths.RootDir, Environment.GetCommandLineArgs()))
                {
                    case ForwardResult.Delivered: break;
                    case ForwardResult.NoResponse:
                        Dialog.Show(L.T("Nubis 가 응답하지 않습니다 (이미 실행 중인 프로세스가 멈췄거나 창 없이 남아 있습니다).\n작업 관리자에서 남아 있는 Nubis.exe 를 끝낸 뒤 다시 실행하세요."),
                            "Nubis", MessageBoxButton.OK, MessageBoxImage.Warning);
                        break;
                    default:
                        Dialog.Show(L.T("Nubis 가 이미 실행 중입니다. 실행 중인 창을 사용하세요."), "Nubis", MessageBoxButton.OK, MessageBoxImage.Information);
                        break;
                }
            }
            Shutdown();
            return;
        }
        SingleInstance.StartServer(AppPaths.RootDir, AcceptForwardedArgs);

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
            // 예전 소유자 프로필을 옮기지 못했으면 이번 실행은 예전 폴더를 그대로 쓴다 (빈 새 폴더로 시작해 데이터가 가려지지 않게).
            // PreviousOwnerKey 가 남아 있어 다음 실행 때 다시 옮긴다
            if (owner && !Profiles.ClaimOwner(cfg, dir, AppPaths.RootDir, AppPaths.UsersDir))
                dir = Profiles.DirForRun(cfg, dir, AppPaths.UsersDir, claimed: false);
            AppPaths.UseDataDir(dir);
            SettingsStore.UseProfile(inheritGlobal: owner);
        }

        var main = new MainWindow();
        MainWindow = main;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        main.Show();
        _startupDone = true;
    }

    /// <summary>재시작한 새 프로세스가 이전 프로세스가 끝나기를 기다리는 최대 시간</summary>
    private const int RestartWaitMs = 60000;

    /// <summary>시작 과정(언어·로그인 창, 메인 창 만들기)이 끝났는지 — 그 전에는 보이는 창이 없어도 응답하는 것으로 본다</summary>
    private volatile bool _startupDone;

    /// <summary>
    /// 파이프 서버 스레드에서 부른다: UI 스레드가 명령을 받아 처리하기 시작하면 true (응답 바이트 1).
    /// UI 스레드가 멈췄거나(4초 안에 실행되지 않음) 시작이 끝났는데 보이는 창이 없으면(창 없이 남은 프로세스) false —
    /// 새로 띄운 쪽이 "응답하지 않음" 안내를 보여준다. 처리 자체(접속·대화 상자)는 기다리지 않는다.
    /// </summary>
    private bool AcceptForwardedArgs(string[] args)
    {
        var accepted = false;
        var ready = new System.Threading.ManualResetEventSlim();   // 시간 초과 뒤 UI 스레드가 Set 할 수 있어 Dispose 하지 않는다
        try
        {
            Dispatcher.InvokeAsync(() =>
            {
                accepted = !_startupDone || HasVisibleWindow();
                ready.Set();
                if (accepted) OnForwardedArgs(args);
            });
        }
        catch { return false; }   // Dispatcher 가 이미 종료됨
        return ready.Wait(4000) && accepted;
    }

    private bool HasVisibleWindow()
    {
        foreach (Window w in Windows)
            if (w.IsVisible) return true;
        return false;
    }

    /// <summary>다른 인스턴스가 넘긴 명령줄: 메인 창이 있으면 앞으로 가져와 처리, 아직 로그인 화면이면 그 창만 앞으로</summary>
    private void OnForwardedArgs(string[] args)
    {
        if (MainWindow is Nubis.MainWindow main && main.IsLoaded) { main.HandleForwardedArgs(args); return; }
        foreach (Window w in Windows)
        {
            if (!w.IsVisible) continue;
            if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
            w.Activate();
            break;
        }
    }

    /// <summary>앱을 다시 시작 (언어·로그인 설정을 바꾼 뒤). 같은 환경변수(NUBIS_DATA_DIR 등)로 새로 띄우고 지금 것은 끈다</summary>
    public static void Restart()
    {
        try
        {
            var exe = Environment.ProcessPath;
            // 새 프로세스는 이 프로세스가 끝나 단일 인스턴스 잠금이 풀릴 때까지 기다렸다가 시작한다
            if (exe != null) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, WaitPreviousArg) { UseShellExecute = false });
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

        Dialog.Show(e.Exception.Message, L.T("Nubis 오류"), MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}

/// <summary>
/// 데이터 폴더(AppPaths.RootDir)당 한 인스턴스: 이름 있는 Mutex(폴더 경로 해시) + 명령줄을 넘기는 이름 있는 파이프.
/// NUBIS_DATA_DIR 로 다른 폴더를 쓰는 테스트 인스턴스는 이름이 달라 같이 뜬다. 계정별 프로필은 RootDir 아래라 함께 잠긴다.
/// 파이프는 같은 Windows 사용자만 연결할 수 있다 (CurrentUserOnly).
/// </summary>
internal static class SingleInstance
{
    private static System.Threading.Mutex? _mutex;   // 프로세스가 끝날 때까지 잡고 있는다 (끝나면 OS 가 풀어 준다)
    private const int MaxMessage = 64 * 1024;

    /// <summary>폴더 경로 → 이름 (대소문자·끝 구분자 무시)</summary>
    internal static string Id(string rootDir)
    {
        var norm = Path.GetFullPath(rootDir).TrimEnd('\\', '/').ToUpperInvariant();
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(norm));
        return "Nubis-" + Convert.ToHexString(hash, 0, 12);
    }

    /// <summary>
    /// 이 프로세스가 그 폴더의 주인이 되면 true. waitMs 동안 이전 인스턴스가 끝나기를 기다린다 (재시작).
    /// Global\ 이름: 같은 Windows 사용자가 콘솔과 RDP 등 다른 세션에서 같은 폴더로 두 개를 띄우지 못하게 (파이프 이름도 세션 구분이 없다).
    /// 뮤텍스는 일반 사용자도 Global\ 에 만들 수 있지만, 막힌 환경이면 Local\ 로 물러선다.
    /// </summary>
    public static bool TryAcquire(string rootDir, int waitMs)
    {
        System.Threading.Mutex m;
        try { m = new System.Threading.Mutex(false, @"Global\" + Id(rootDir)); }
        catch (UnauthorizedAccessException) { m = new System.Threading.Mutex(false, @"Local\" + Id(rootDir)); }
        bool owned;
        try { owned = m.WaitOne(waitMs); }
        catch (System.Threading.AbandonedMutexException) { owned = true; }   // 이전 인스턴스가 비정상 종료 — 잠금은 넘겨받음
        if (!owned) { m.Dispose(); return false; }
        _mutex = m;
        return true;
    }

    /// <summary>받는 쪽이 처리를 시작했다고 알릴 때까지 기다리는 시간</summary>
    private const int AckTimeoutMs = 5000;

    /// <summary>
    /// 실행 중인 인스턴스에 명령줄을 넘긴다. 형식: 길이(4바이트, little-endian) + UTF-8 본문('\0' 구분) → 받는 쪽이 응답 1바이트(1 = 받아서 처리 시작).
    /// 응답이 없거나 0 이면 NoResponse (멈췄거나 창 없이 남은 프로세스) — 조용히 "성공" 으로 끝나지 않게.
    /// </summary>
    public static ForwardResult Forward(string rootDir, string[] args)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(string.Join('\0', args));
        if (bytes.Length > MaxMessage) return ForwardResult.NoServer;
        System.IO.Pipes.NamedPipeClientStream client;
        try
        {
            client = new System.IO.Pipes.NamedPipeClientStream(".", Id(rootDir) + "-args", System.IO.Pipes.PipeDirection.InOut,
                System.IO.Pipes.PipeOptions.CurrentUserOnly | System.IO.Pipes.PipeOptions.Asynchronous);
        }
        catch { return ForwardResult.NoServer; }
        using (client)
        {
            // 파이프가 없을 때도 시간 초과로 끝나 "없음" 과 "바쁨" 을 구분할 수 없다 — 둘 다 NoServer (실행 중 안내).
            // 멈춘 프로세스도 파이프 스레드는 살아 있어 연결은 되므로, 그 경우는 아래 응답 대기에서 NoResponse 가 된다
            try { client.Connect(3000); }
            catch { return ForwardResult.NoServer; }
            try
            {
                // 받는 쪽이 창을 앞으로 가져올 수 있게 (백그라운드 프로세스는 포그라운드를 가져올 수 없다)
                AllowSetForegroundWindow(-1);
                using var cts = new System.Threading.CancellationTokenSource(AckTimeoutMs);
                client.WriteAsync(BitConverter.GetBytes(bytes.Length), cts.Token).AsTask().GetAwaiter().GetResult();
                client.WriteAsync(bytes, cts.Token).AsTask().GetAwaiter().GetResult();
                client.FlushAsync(cts.Token).GetAwaiter().GetResult();
                var ack = new byte[1];
                var n = client.ReadAsync(ack, cts.Token).AsTask().GetAwaiter().GetResult();
                return n == 1 && ack[0] == 1 ? ForwardResult.Delivered : ForwardResult.NoResponse;
            }
            catch { return ForwardResult.NoResponse; }
        }
    }

    private static bool ReadExactly(Stream s, byte[] buf, int count)
    {
        var read = 0;
        while (read < count)
        {
            var n = s.Read(buf, read, count - read);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

    /// <summary>
    /// 다른 인스턴스가 넘긴 명령줄을 받는다 (백그라운드 스레드에서 onArgs 호출).
    /// onArgs 가 true(받아서 처리 시작)면 응답 1, 아니면 0 을 돌려준다.
    /// </summary>
    public static void StartServer(string rootDir, Func<string[], bool> onArgs)
    {
        var name = Id(rootDir) + "-args";
        var thread = new System.Threading.Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.InOut, 1,
                        System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.CurrentUserOnly);
                    server.WaitForConnection();
                    var head = new byte[4];
                    if (!ReadExactly(server, head, 4)) continue;
                    var len = BitConverter.ToInt32(head, 0);
                    if (len < 0 || len > MaxMessage) continue;
                    var body = new byte[len];
                    if (!ReadExactly(server, body, len)) continue;
                    var text = System.Text.Encoding.UTF8.GetString(body);
                    var ok = false;
                    try { ok = onArgs(text.Length == 0 ? Array.Empty<string>() : text.Split('\0')); } catch { }
                    server.WriteByte(ok ? (byte)1 : (byte)0);
                    server.Flush();
                    try { server.WaitForPipeDrain(); } catch { }
                }
                catch
                {
                    System.Threading.Thread.Sleep(1000);   // 파이프를 만들 수 없는 상태에서 헛돌지 않게
                }
            }
        })
        { IsBackground = true, Name = "Nubis single instance" };
        thread.Start();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}

/// <summary>SingleInstance.Forward 결과</summary>
internal enum ForwardResult
{
    /// <summary>실행 중인 인스턴스가 받아서 처리하기 시작함</summary>
    Delivered,
    /// <summary>파이프에 연결하지 못함 (받는 쪽이 아직 서버를 열지 않았거나 없음)</summary>
    NoServer,
    /// <summary>연결은 됐지만(또는 잠금은 잡혀 있는데) 응답이 없음 — 멈췄거나 창 없이 남은 프로세스</summary>
    NoResponse,
}
