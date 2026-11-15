using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Nubis;

/// <summary>
/// Win32 창을 품는 WPF 컨트롤(HwndHost — WebView2 · WindowsFormsHost)을 닫을 때 쓰는 정리 도우미.
///
/// HwndHost 는 키보드 입력을 받으려고 부모 창(HwndSource)의 "키보드 입력 사이트" 목록에 자신을 등록한다.
/// 시각 트리에서 빼기 전에 Dispose 하면 이 등록이 풀리지 않아, 메인 창이 닫힌 탭의 컨트롤을 영구히 붙잡는다
/// (그 컨트롤에 UI 자동화 피어가 붙어 있으면 피어 → 부모 화면(SshSessionView 등) → 탭 전체까지 남는다).
/// 2026-11-15 테스트베드에서 SSH 탭 50번 열고 닫기 뒤 강제 GC 에도 SshSessionView · XtermView · WebView2 가 52개 남은 원인.
/// Dispose 전에 이 함수로 등록을 풀고 트리에서 뺀다.
/// </summary>
public static class HwndHostRelease
{
    public static void Detach(HwndHost? host)
    {
        if (host == null) return;
        // 1) 부모 HwndSource 의 키보드 입력 사이트 목록에서 뺀다 (Unregister 가 목록에서 제거)
        try
        {
            var sink = (IKeyboardInputSink)host;
            sink.KeyboardInputSite?.Unregister();
            sink.KeyboardInputSite = null;
        }
        catch { }
        // 2) 시각·논리 트리에서 뺀다 — 정상 경로(OnSourceChanged)로도 등록이 풀리고, 부모 화면과의 연결이 끊긴다
        try
        {
            switch (VisualTreeHelper.GetParent(host) ?? LogicalTreeHelper.GetParent(host))
            {
                case Panel p: p.Children.Remove(host); break;
                case ContentControl c when ReferenceEquals(c.Content, host): c.Content = null; break;
                case Decorator d when ReferenceEquals(d.Child, host): d.Child = null; break;
                case ContentPresenter cp when ReferenceEquals(cp.Content, host): cp.Content = null; break;
            }
        }
        catch { }
    }
}
