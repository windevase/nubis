using System;
using System.Windows;
using System.Windows.Media;

namespace Nubis;

/// <summary>앱에서 쓰는 벡터 아이콘 (어떤 크기·DPI 에서도 선명)</summary>
public static class BrandIcons
{
    /// <summary>
    /// Claude 마크 — 방사형으로 뻗은 주황 광선. Claude Code 패널/버튼에 사용 (Claude 연동 표시).
    /// </summary>
    public static DrawingImage ClaudeMark { get; } = BuildClaudeMark();

    private static DrawingImage BuildClaudeMark()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x57));
        brush.Freeze();
        var group = new DrawingGroup();
        // 광선 길이를 조금씩 달리해 손으로 그린 듯한 별 모양
        double[] lengths = { 1.0, 0.80, 0.93, 0.84, 0.99, 0.86, 0.95, 0.80, 0.97, 0.85, 0.91, 0.82 };
        const double inner = 0.10, width = 0.16;
        for (var i = 0; i < lengths.Length; i++)
        {
            var a = (i * 30 - 90 + (i % 2 == 0 ? 0 : 4)) * Math.PI / 180;
            var dx = Math.Cos(a);
            var dy = Math.Sin(a);
            var pen = new Pen(brush, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            group.Children.Add(new GeometryDrawing(null, pen,
                new LineGeometry(new Point(dx * inner, dy * inner), new Point(dx * (lengths[i] - width / 2), dy * (lengths[i] - width / 2)))));
        }
        // 이미지 경계 고정 (-1..1)
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(-1, -1, 2, 2))));
        var img = new DrawingImage(group);
        img.Freeze();
        return img;
    }
}
