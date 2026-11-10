using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace Nubis;

public partial class MainWindow
{
    // =================== 화면 캡처 (capture_screen) ===================

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetGuiResources(IntPtr hProcess, int uiFlags);   // 0 = GDI, 1 = USER 객체 수

    /// <summary>
    /// 탭 영역을 PrintWindow(전체 렌더링 포함)로 캡처. RDP ActiveX·WebView2 처럼 자체 창에 그리는 화면도 담긴다.
    /// UI 스레드에서 호출.
    /// </summary>
    private async Task<McpImage> CaptureSessionAsync(SessionTab? tab, int maxWidth)
    {
        if (tab == null) throw new McpToolException("세션을 찾을 수 없습니다. list_sessions 로 확인하세요.");
        if (WindowState == WindowState.Minimized)
            throw new McpToolException("Nubis 창이 최소화되어 있어 화면을 캡처할 수 없습니다. 창을 열어 달라고 사용자에게 요청하세요.");

        var switched = !tab.IsSelected;
        if (switched)
        {
            // 가려진 RDP 화면은 갱신되지 않으므로 보이게 한 뒤 그려질 시간을 준다
            SelectTab(tab);
            await Task.Delay(700);
        }

        var view = tab.View;
        if (view.ActualWidth < 10 || view.ActualHeight < 10) throw new McpToolException("탭 화면 크기를 알 수 없습니다.");
        var dpi = VisualTreeHelper.GetDpi(this);
        var origin = view.TransformToAncestor(this).Transform(new System.Windows.Point(0, 0));
        var crop = new System.Drawing.Rectangle(
            (int)(origin.X * dpi.DpiScaleX), (int)(origin.Y * dpi.DpiScaleY),
            (int)(view.ActualWidth * dpi.DpiScaleX), (int)(view.ActualHeight * dpi.DpiScaleY));

        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var clientW = (int)(ActualWidth * dpi.DpiScaleX) + 32;
        var clientH = (int)(ActualHeight * dpi.DpiScaleY) + 32;

        byte[] data;
        string mime;
        int outW, outH;
        using (var full = new System.Drawing.Bitmap(clientW, clientH))
        {
            using (var g = System.Drawing.Graphics.FromImage(full))
            {
                var hdc = g.GetHdc();
                try { PrintWindow(hwnd, hdc, 0x1 | 0x2); }   // PW_CLIENTONLY | PW_RENDERFULLCONTENT
                finally { g.ReleaseHdc(hdc); }
            }
            crop.Intersect(new System.Drawing.Rectangle(0, 0, full.Width, full.Height));

            var scale = Math.Min(1.0, Math.Clamp(maxWidth, 320, 3840) / (double)crop.Width);
            outW = Math.Max(1, (int)(crop.Width * scale));
            outH = Math.Max(1, (int)(crop.Height * scale));
            using var shot = new System.Drawing.Bitmap(outW, outH);
            using (var g = System.Drawing.Graphics.FromImage(shot))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(full, new System.Drawing.Rectangle(0, 0, outW, outH), crop, System.Drawing.GraphicsUnit.Pixel);
            }

            // 대부분 PNG, 사진·배경화면처럼 큰 화면은 JPEG 로 (토큰·전송량 절약)
            using var ms = new MemoryStream();
            shot.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            if (ms.Length <= 1_500_000) { data = ms.ToArray(); mime = "image/png"; }
            else
            {
                using var jms = new MemoryStream();
                var jpeg = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().First(e => e.MimeType == "image/jpeg");
                using var p = new System.Drawing.Imaging.EncoderParameters(1);
                p.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
                shot.Save(jms, jpeg, p);
                data = jms.ToArray();
                mime = "image/jpeg";
            }
        }

        var target = $"session {tab.Id}: {tab.Title}" + (tab.Host != null ? $" ({tab.Host.Address})" : "");
        AuditLog.Write("capture_screen", new[] { target }, $"({outW}x{outH} {mime})", "조회");
        var kind = tab.Protocol switch { Protocol.Rdp => "RDP", Protocol.Ssh => "SSH", _ => "로컬" };
        return new McpImage(data, mime,
            $"[{target}] {kind} 화면 {crop.Width}x{crop.Height}" + (outW != crop.Width ? $" (축소 {outW}x{outH})" : "") +
            (switched ? " — 캡처를 위해 이 탭으로 전환했습니다." : ""));
    }
}
