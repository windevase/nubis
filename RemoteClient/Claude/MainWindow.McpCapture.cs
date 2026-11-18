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

    /// <summary>마지막 캡처의 이미지 크기 · 그때 탭 화면의 물리 픽셀 크기 · RDP 연결 번호(다시 연결 뒤에는 그 좌표를 쓰지 않게)</summary>
    private sealed record CaptureGeometry(int ImageW, int ImageH, int TabW, int TabH, int Generation);

    // 탭별 마지막 캡처 — 약한 참조라 닫힌 탭은 함께 사라진다 (누수 없음)
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<SessionTab, CaptureGeometry> _captureGeometry = new();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetGuiResources(IntPtr hProcess, int uiFlags);   // 0 = GDI, 1 = USER 객체 수

    /// <summary>
    /// 탭 영역을 PrintWindow(전체 렌더링 포함)로 캡처. RDP ActiveX·WebView2 처럼 자체 창에 그리는 화면도 담긴다.
    /// UI 스레드에서 호출.
    /// </summary>
    private async Task<McpImage> CaptureSessionAsync(SessionTab? tab, int maxWidth, bool holdsInputSection = false)
    {
        if (tab == null) throw new McpToolException("세션을 찾을 수 없습니다. list_sessions 로 확인하세요.");
        if (WindowState == WindowState.Minimized)
            throw new McpToolException("Nubis 창이 최소화되어 있어 화면을 캡처할 수 없습니다. 창을 열어 달라고 사용자에게 요청하세요.");
        // 전체 화면 RDP 는 별도 창에 그려져 탭 영역 캡처에 원격 화면이 담기지 않는다 (좌표 기준도 어긋남)
        if (tab.View is RdpSessionView { IsFullScreen: true })
            throw new McpToolException("RDP 탭이 전체 화면이라 캡처할 수 없습니다. 전체 화면을 나와 달라고 사용자에게 요청하세요.");

        // 탭을 바꿔야 하는 캡처는 RDP 입력 구간 잠금을 잡는다 — 다른 탭에 Claude 가 입력하는 중이면 그 탭을 바꿔 입력을 끊지 않게
        // (rdp_* 의 입력 뒤 캡처는 이미 잠금을 쥐고 부른다)
        var acquired = false;
        if (!tab.IsSelected && !holdsInputSection)
        {
            try { await _rdpInputSection.WaitAsync(McpRequest.Token); }
            catch (OperationCanceledException) { throw new McpToolException("Claude 쪽에서 요청이 취소되어 캡처하지 않았습니다."); }
            acquired = true;
            if (!_tabs.Contains(tab)) { _rdpInputSection.Release(); throw new McpToolException("탭이 닫혀 캡처하지 않았습니다."); }
            // 기다리는 사이(승인 창이 떠 있던 동안 등) 전체 화면이 됐을 수 있다 — 원격 화면이 빠진 이미지를 정상 결과로 돌려주지 않게
            if (tab.View is RdpSessionView { IsFullScreen: true })
            {
                _rdpInputSection.Release();
                throw new McpToolException("RDP 탭이 전체 화면이라 캡처할 수 없습니다. 전체 화면을 나와 달라고 사용자에게 요청하세요.");
            }
        }
        var switched = !tab.IsSelected;
        // 캡처가 끝나면 사용자가 보던 화면(탭 또는 홈)으로 되돌린다
        var previous = _tabs.FirstOrDefault(t => t.IsSelected);
        // 사용자가 그 탭에 입력하던 중이었는지 — 되돌릴 때만 그 탭에 키보드 포커스를 돌려준다
        var previousHadFocus = previous?.View.IsKeyboardFocusWithin == true;
        try
        {
            return await CaptureCoreAsync(tab, maxWidth, switched);
        }
        finally
        {
            try
            {
                // 캡처하는 동안(0.7초 대기 포함) 사용자가 다른 탭·홈으로 옮겼으면 그 선택을 존중하고 되돌리지 않는다.
                // 캡처가 바꿔 놓은 탭이 아직 선택돼 있을 때만 원래 화면으로 돌아간다
                if (switched && tab.IsSelected && _tabs.Contains(tab))
                {
                    if (previous != null && _tabs.Contains(previous))
                    {
                        ShowTabWithoutFocus(previous);
                        if (previousHadFocus) previous.Session.FocusSession();
                    }
                    else SelectHome();
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("캡처 뒤 화면 되돌리기 실패: " + ex.Message); }
            // 되돌리기가 실패해도 잠금은 반드시 푼다 — 남으면 이후 모든 RDP 입력·승인 창이 멈춘다
            finally { if (acquired) _rdpInputSection.Release(); }
        }
    }

    /// <summary>
    /// 캡처용 탭 전환: SelectTab 과 같지만 키보드 포커스를 옮기지 않는다.
    /// SelectTab 은 고른 탭에 포커스를 주므로, 사용자가 Claude 패널이나 다른 탭에 입력하던 중이면
    /// 그 0.7초 사이의 키 입력(명령·비밀번호)이 캡처 대상 서버의 터미널·RDP 로 들어갔다
    /// </summary>
    private void ShowTabWithoutFocus(SessionTab tab)
    {
        foreach (var t in _tabs)
        {
            t.IsSelected = t == tab;
            t.View.Visibility = t == tab ? Visibility.Visible : Visibility.Collapsed;
        }
        HomeView.Visibility = Visibility.Collapsed;
        SessionsHost.Visibility = Visibility.Visible;
        HomeTab.Background = Brushes.Transparent;
        UpdateActiveContext();
    }

    private async Task<McpImage> CaptureCoreAsync(SessionTab tab, int maxWidth, bool switched)
    {
        if (switched)
        {
            // 가려진 RDP 화면은 갱신되지 않으므로 보이게 한 뒤 그려질 시간을 준다 (포커스는 옮기지 않음)
            ShowTabWithoutFocus(tab);
            await Task.Delay(700);
        }
        // 기다리는 동안 창이 최소화됐으면(사용자·set_window_state) 빈 화면을 정상 결과로 돌려주지 않는다
        if (WindowState == WindowState.Minimized)
            throw new McpToolException("캡처하는 동안 Nubis 창이 최소화되어 화면을 캡처하지 못했습니다. 창을 열어 달라고 사용자에게 요청하세요.");

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

        // PrintWindow(창 메시지)만 UI 스레드에서 하고, 축소·PNG/JPEG 인코딩(큰 화면은 수백 ms)은 백그라운드에서 —
        // 그동안 UI(터미널 입력·스크롤)가 멈추지 않게. 비트맵은 어느 경로로 끝나든 Dispose(GDI+ 핸들) 한다
        var full = new System.Drawing.Bitmap(clientW, clientH);
        try
        {
            using (var g = System.Drawing.Graphics.FromImage(full))
            {
                var hdc = g.GetHdc();
                try { PrintWindow(hwnd, hdc, 0x1 | 0x2); }   // PW_CLIENTONLY | PW_RENDERFULLCONTENT
                finally { g.ReleaseHdc(hdc); }
            }
        }
        catch
        {
            full.Dispose();
            throw;
        }
        crop.Intersect(new System.Drawing.Rectangle(0, 0, clientW, clientH));
        if (crop.Width < 1 || crop.Height < 1)
        {
            full.Dispose();
            throw new McpToolException("탭 화면 영역을 캡처하지 못했습니다.");
        }
        var (data, mime, outW, outH) = await Task.Run(() => EncodeCapture(full, crop, maxWidth));
        // RDP 화면 조작(rdp_click 등)은 Claude 가 본 마지막 이미지의 좌표를 쓴다 — 이미지 크기와 그때의 탭 크기를 기억
        _captureGeometry.AddOrUpdate(tab, new CaptureGeometry(outW, outH, crop.Width, crop.Height, (tab.View as RdpSessionView)?.SessionGeneration ?? 0));

        var target = $"session {tab.Id}: {tab.Title}" + (tab.Host != null ? $" ({tab.Host.Address})" : "");
        AuditLog.Write("capture_screen", new[] { target }, $"({outW}x{outH} {mime})", "조회");
        var kind = tab.Protocol switch { Protocol.Rdp => "RDP", Protocol.Ssh => "SSH", _ => "로컬" };
        return new McpImage(data, mime,
            $"[{target}] {kind} 화면 {crop.Width}x{crop.Height}" + (outW != crop.Width ? $" (축소 {outW}x{outH})" : "") +
            (switched ? " — 캡처를 위해 잠깐 이 탭으로 전환했다가 사용자가 보던 화면으로 되돌렸습니다." : ""));
    }

    /// <summary>JPEG 인코더 (한 번만 찾는다 — GetImageEncoders 는 부를 때마다 배열을 새로 만든다)</summary>
    private static readonly Lazy<System.Drawing.Imaging.ImageCodecInfo> JpegEncoder =
        new(() => System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().First(e => e.MimeType == "image/jpeg"));

    /// <summary>
    /// 캡처 비트맵을 잘라 축소하고 PNG(크면 JPEG)로 인코딩한다 (백그라운드 스레드). full 은 여기서 Dispose 한다.
    /// </summary>
    private static (byte[] Data, string Mime, int Width, int Height) EncodeCapture(System.Drawing.Bitmap full, System.Drawing.Rectangle crop, int maxWidth)
    {
        using (full)
        {
            var scale = Math.Min(1.0, Math.Clamp(maxWidth, 320, 3840) / (double)crop.Width);
            var outW = Math.Max(1, (int)(crop.Width * scale));
            var outH = Math.Max(1, (int)(crop.Height * scale));
            using var shot = new System.Drawing.Bitmap(outW, outH);
            using (var g = System.Drawing.Graphics.FromImage(shot))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(full, new System.Drawing.Rectangle(0, 0, outW, outH), crop, System.Drawing.GraphicsUnit.Pixel);
            }

            // 대부분 PNG, 사진·배경화면처럼 큰 화면은 JPEG 로 (토큰·전송량 절약)
            using var ms = new MemoryStream();
            shot.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            if (ms.Length <= 1_500_000) return (ms.ToArray(), "image/png", outW, outH);
            using var jms = new MemoryStream();
            using var p = new System.Drawing.Imaging.EncoderParameters(1);
            p.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
            shot.Save(jms, JpegEncoder.Value, p);
            return (jms.ToArray(), "image/jpeg", outW, outH);
        }
    }
}
