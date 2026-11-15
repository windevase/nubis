using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace Nubis;

/// <summary>
/// Windows 11 Mica 배경 (DWM 이 합성 — GPU 비용 거의 없음).
/// 창의 SourceInitialized 에서 Apply 를 부른다. Windows 11(빌드 22000) 미만이거나 DWM 호출이 실패하면 아무것도 바꾸지 않고 false
/// → XAML 의 단색 배경(TitleBarBackdrop · SidebarBackdrop · ContentBackdrop 기본값)이 그대로 대체 화면이 된다.
///
/// 켜지면:
///  · WindowChrome 의 유리 프레임을 창 전체로 넓히고(GlassFrameThickness = -1) 창 · HwndSource 배경을 투명으로
///  · 창 리소스의 TitleBarBackdrop/SidebarBackdrop/ContentBackdrop 을 반투명 색조 · 투명으로 덮어써 그 영역에 Mica 가 비친다
///    (XAML 에서 반드시 DynamicResource 로 참조해야 바뀐다)
///  · nativeCaptionButtons=true 면 Windows 기본 캡션 버튼(최소화/최대화/닫기, Snap Layouts 포함)을 쓴다.
///    유리 프레임을 넓히면 DWM 이 기본 캡션 버튼을 어차피 그리므로, 직접 만든 버튼과 겹치지 않게 호출한 쪽이 자기 버튼을 숨겨야 한다.
/// AllowsTransparency=True 인 창(대화상자들)에는 쓸 수 없다 (레이어드 창은 Mica 를 받지 못함) — 그런 창이면 false.
/// </summary>
internal static class WindowBackdrop
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_CAPTION_BUTTON_BOUNDS = 5;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;   // 22621(22H2)+
    private const int DWMWA_MICA_EFFECT = 1029;          // 22000~22620 (문서화되지 않은 이전 값)
    private const int DWMSBT_MAINWINDOW = 2;             // Mica

    /// <summary>Windows 11 이상인지 (Mica · 둥근 모서리 · 캡션 버튼 영역 조회 가능)</summary>
    public static bool IsWindows11 => Environment.OSVersion.Version.Major >= 10 && Environment.OSVersion.Version.Build >= 22000;

    /// <summary>Mica 를 켠다. 성공하면 true (호출 쪽은 이때만 기본 캡션 버튼으로 바꾼다)</summary>
    public static bool Apply(Window window, bool nativeCaptionButtons)
    {
        if (!IsWindows11 || window.AllowsTransparency) return false;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            var on = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));   // 어두운 Mica 색조 · 흰 캡션 글리프
            int hr;
            if (Environment.OSVersion.Version.Build >= 22621)
            {
                var type = DWMSBT_MAINWINDOW;
                hr = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int));
            }
            else
            {
                hr = DwmSetWindowAttribute(hwnd, DWMWA_MICA_EFFECT, ref on, sizeof(int));
            }
            if (hr != 0) return false;

            if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: { } target }) target.BackgroundColor = Colors.Transparent;

            // 유리 프레임을 창 전체로 — 투명하게 그린 곳에 DWM 배경(Mica)이 비친다
            var chrome = WindowChrome.GetWindowChrome(window);
            if (chrome != null)
            {
                if (chrome.IsFrozen) chrome = (WindowChrome)chrome.Clone();
                chrome.GlassFrameThickness = new Thickness(-1);
                chrome.UseAeroCaptionButtons = nativeCaptionButtons;
                WindowChrome.SetWindowChrome(window, chrome);
            }
            window.Background = Brushes.Transparent;
            window.Resources["TitleBarBackdrop"] = window.TryFindResource("MicaTitleBarTint") ?? Brushes.Transparent;
            window.Resources["SidebarBackdrop"] = window.TryFindResource("MicaSidebarTint") ?? Brushes.Transparent;
            window.Resources["ContentBackdrop"] = Brushes.Transparent;
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException)
        {
            return false;
        }
    }

    /// <summary>
    /// 기본 캡션 버튼(최소화/최대화/닫기)이 차지하는 폭 (DIP). 타이틀바 오른쪽 끝 여백으로 쓴다. 못 구하면 null
    /// </summary>
    public static double? CaptionButtonsWidth(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return null;
        try
        {
            if (DwmGetWindowAttribute(hwnd, DWMWA_CAPTION_BUTTON_BOUNDS, out var r, Marshal.SizeOf<RECT>()) != 0) return null;
            var px = r.Right - r.Left;
            if (px <= 0) return null;
            return px / VisualTreeHelper.GetDpi(window).DpiScaleX;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// 팝업 등 작은 최상위 창의 모서리를 DWM 으로 둥글게 하고 테두리 색을 입힌다 (Windows 11 전용, 실패는 무시).
    /// AllowsTransparency 없이 둥근 모양을 내는 방법 — 레이어드 창(소프트웨어 렌더링)을 피한다.
    /// </summary>
    public static bool RoundCorners(IntPtr hwnd, Color? border)
    {
        if (!IsWindows11 || hwnd == IntPtr.Zero) return false;
        try
        {
            var pref = 3;   // DWMWCP_ROUNDSMALL
            var ok = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int)) == 0;
            if (ok && border is { } c)
            {
                var colorref = c.R | (c.G << 8) | (c.B << 16);   // COLORREF = 0x00BBGGRR
                DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorref, sizeof(int));
            }
            return ok;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);
}
