using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Nubis 아이콘: 배경 없는 구름 실루엣(남보라→시안) + 구름을 감싸는 궤도 링과 노드(앰버).
// "여러 클라우드를 누비며 연결" 을 표현. 256 기준 좌표로 그린 뒤 크기별로 렌더링한다.
static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        System.Threading.Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        var outDir = args[0];
        Directory.CreateDirectory(outDir);
        var sizes = new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        var pngs = new List<byte[]>();
        foreach (var s in sizes) pngs.Add(Png(Render(s)));
        File.WriteAllBytes(Path.Combine(outDir, "nubis.ico"), BuildIco(sizes, pngs));
        File.WriteAllBytes(Path.Combine(outDir, "nubis-256.png"), Png(Render(256)));
        File.WriteAllBytes(Path.Combine(outDir, "nubis-512.png"), Png(Render(512)));
        if (args.Length > 1) File.WriteAllBytes(args[1], Png(PreviewSheet()));
        Console.WriteLine("ok");
    }

    static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    static readonly Point Center = new(128, 140);
    const double Rx = 122, Ry = 40, Tilt = -16;

    static BitmapSource Render(int size)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) Draw(dc, size);
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        return bmp;
    }

    static void Draw(DrawingContext dc, int size)
    {
        var small = size <= 24;
        dc.PushTransform(new ScaleTransform(size / 256.0, size / 256.0));
        // 작은 크기는 여백을 줄여 조금 더 크게
        if (size <= 32) dc.PushTransform(new ScaleTransform(1.1, 1.1, 128, 124));

        var ringBrush = new LinearGradientBrush(C("#FFC857"), C("#FF8A3D"), new Point(0, 0), new Point(1, 1));
        var ringPen = new Pen(ringBrush, small ? 20 : 13) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var ring = new EllipseGeometry(Center, Rx, Ry);
        var rotate = new RotateTransform(Tilt, Center.X, Center.Y);

        // 1) 궤도 뒤쪽 절반 (구름 뒤로 지나감)
        if (!small)
        {
            dc.PushTransform(rotate);
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, 256, Center.Y)));
            dc.PushOpacity(0.55);
            dc.DrawGeometry(null, ringPen, ring);
            dc.Pop(); dc.Pop(); dc.Pop();
        }

        // 2) 구름 실루엣
        Geometry cloud = new RectangleGeometry(new Rect(30, 118, 196, 84), 42, 42);
        cloud = Geometry.Combine(cloud, new EllipseGeometry(new Point(90, 122), 50, 50), GeometryCombineMode.Union, null);
        cloud = Geometry.Combine(cloud, new EllipseGeometry(new Point(150, 96), 66, 66), GeometryCombineMode.Union, null);
        cloud = Geometry.Combine(cloud, new EllipseGeometry(new Point(196, 140), 44, 44), GeometryCombineMode.Union, null);
        var fill = new LinearGradientBrush(C("#7C5CFF"), C("#22D3EE"), new Point(0.1, 0.1), new Point(0.95, 0.95));
        dc.DrawGeometry(fill, null, cloud);
        // 윗부분 은은한 하이라이트
        var shine = new LinearGradientBrush(Color.FromArgb(70, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), new Point(0.5, 0), new Point(0.5, 0.55));
        dc.DrawGeometry(shine, null, cloud);

        // 3) 링이 구름 앞을 지나는 부분에만 어두운 테두리 (구름과 분리돼 보이게)
        var ringRot = new EllipseGeometry(Center, Rx, Ry) { Transform = rotate };
        var wide = ringRot.GetWidenedPathGeometry(new Pen(Brushes.Black, (small ? 20 : 13) + 7));
        var frontHalf = new RectangleGeometry(new Rect(0, small ? 0 : Center.Y, 256, 256)) { Transform = rotate };
        var edge = Geometry.Combine(Geometry.Combine(wide, frontHalf, GeometryCombineMode.Intersect, null), cloud, GeometryCombineMode.Intersect, null);
        dc.DrawGeometry(new SolidColorBrush(C("#33276E")), null, edge);

        // 4) 궤도 앞쪽 절반 + 노드 (구름 앞으로 지나감)
        dc.PushTransform(rotate);
        dc.PushClip(new RectangleGeometry(new Rect(0, small ? 0 : Center.Y, 256, 256)));
        dc.DrawGeometry(null, ringPen, ring);
        dc.Pop();
        var t = 58 * Math.PI / 180;
        var node = new Point(Center.X + Rx * Math.Cos(t), Center.Y + Ry * Math.Sin(t));
        var nr = small ? 26 : 19;
        dc.DrawEllipse(new SolidColorBrush(C("#FFF4D6")), new Pen(new SolidColorBrush(C("#FF8A3D")), small ? 10 : 8), node, nr, nr);
        dc.Pop();

        if (size <= 32) dc.Pop();
        dc.Pop();
    }

    static BitmapSource PreviewSheet()
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(C("#1F1F1F")), null, new Rect(0, 0, 560, 150));
            dc.DrawRectangle(new SolidColorBrush(C("#F3F3F3")), null, new Rect(0, 150, 560, 150));
            foreach (var (y, _) in new[] { (0.0, 0), (150.0, 1) })
            {
                double x = 16;
                foreach (var s in new[] { 16, 24, 32, 48, 128 })
                {
                    var img = Render(s);
                    dc.DrawImage(img, new Rect(x, y + (150 - s) / 2.0, s, s));
                    x += s + 24;
                }
            }
        }
        var bmp = new RenderTargetBitmap(560, 300, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        return bmp;
    }

    static byte[] Png(BitmapSource b)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(b));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    // PNG 압축 엔트리로 구성한 ICO (Windows Vista 이상 지원)
    static byte[] BuildIco(int[] sizes, List<byte[]> pngs)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
        var offset = 6 + 16 * sizes.Length;
        for (var i = 0; i < sizes.Length; i++)
        {
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)0); w.Write((byte)0);
            w.Write((short)1); w.Write((short)32);
            w.Write(pngs[i].Length); w.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var p in pngs) w.Write(p);
        return ms.ToArray();
    }
}
