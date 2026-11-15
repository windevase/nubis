using System.Windows;
using System.Windows.Media;

namespace Nubis;

/// <summary>
/// 공통 컨트롤 템플릿(App.xaml)이 읽는 첨부 속성. 스타일 기본값을 화면에서 바꿀 때 쓴다.
/// 예: &lt;Button Style="{StaticResource GhostButton}" l:Ui.HoverBrush="{StaticResource AccentSubtle}" l:Ui.CornerRadius="16"/&gt;
/// (예전처럼 Tag 에 hover 색을 넣는 방식은 TitleButton 만 남아 있다)
/// </summary>
public static class Ui
{
    /// <summary>마우스를 올렸을 때 덧칠하는 색 (버튼 계열 템플릿)</summary>
    public static readonly DependencyProperty HoverBrushProperty = DependencyProperty.RegisterAttached(
        "HoverBrush", typeof(Brush), typeof(Ui), new FrameworkPropertyMetadata(null));

    public static Brush? GetHoverBrush(DependencyObject o) => (Brush?)o.GetValue(HoverBrushProperty);
    public static void SetHoverBrush(DependencyObject o, Brush? value) => o.SetValue(HoverBrushProperty, value);

    /// <summary>눌렀을 때 덧칠하는 색 (기본: 검정 15% — 어느 바탕이든 살짝 어두워진다)</summary>
    public static readonly DependencyProperty PressedBrushProperty = DependencyProperty.RegisterAttached(
        "PressedBrush", typeof(Brush), typeof(Ui), new FrameworkPropertyMetadata(null));

    public static Brush? GetPressedBrush(DependencyObject o) => (Brush?)o.GetValue(PressedBrushProperty);
    public static void SetPressedBrush(DependencyObject o, Brush? value) => o.SetValue(PressedBrushProperty, value);

    /// <summary>버튼 · 입력란 · 목록 행 템플릿의 모서리 반경</summary>
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.RegisterAttached(
        "CornerRadius", typeof(CornerRadius), typeof(Ui), new FrameworkPropertyMetadata(new CornerRadius(6)));

    public static CornerRadius GetCornerRadius(DependencyObject o) => (CornerRadius)o.GetValue(CornerRadiusProperty);
    public static void SetCornerRadius(DependencyObject o, CornerRadius value) => o.SetValue(CornerRadiusProperty, value);
}
