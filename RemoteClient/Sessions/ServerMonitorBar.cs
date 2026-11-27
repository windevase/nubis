using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Nubis;

/// <summary>
/// SSH 탭 터미널 위의 서버 모니터 띠: CPU % · 메모리 사용/전체 · 네트워크 수신/송신 속도 + 짧은 스파크라인.
/// 스파크라인은 최근 <see cref="HistoryLength"/>개 값만 메모리에 들고 있다 (저장·기록 없음). 값이 올 때만 다시 그린다 (반복 애니메이션 없음)
/// </summary>
public sealed class ServerMonitorBar : Border
{
    public const int HistoryLength = 30;
    private const double SparkWidth = 56, SparkHeight = 14;

    private readonly TextBlock _cpu, _mem, _rx, _tx, _status, _error;
    private readonly WrapPanel _items;
    private readonly Polyline _cpuLine, _memLine, _rxLine, _txLine;
    private readonly RecentValues _cpuHist = new(HistoryLength), _memHist = new(HistoryLength),
        _rxHist = new(HistoryLength), _txHist = new(HistoryLength);

    /// <summary>띠의 닫기 버튼 (도구 막대의 모니터 버튼과 같은 동작)</summary>
    public event Action? CloseRequested;

    public ServerMonitorBar()
    {
        var res = Application.Current.Resources;
        Background = (Brush)res["SidebarBg"];
        BorderBrush = (Brush)res["Line"];
        BorderThickness = new Thickness(0, 0, 0, 1);
        Padding = new Thickness(10, 3, 4, 3);
        AutomationPropertiesName(this, L.T("서버 모니터"));

        _cpu = Value(52);
        _mem = Value(150);
        _rx = Value(84);
        _tx = Value(84);
        _cpuLine = Spark((Brush)res["Accent"]);
        _memLine = Spark((Brush)res["Info"]);
        _rxLine = Spark((Brush)res["Success"]);
        _txLine = Spark((Brush)res["Warning"]);

        var items = _items = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        items.Children.Add(Group("CPU",[_cpu], [_cpuLine], L.T("CPU 사용률 (모든 코어 평균)")));
        items.Children.Add(Group(L.T("메모리"), [_mem], [_memLine], L.T("사용 중 / 전체 메모리 (캐시·버퍼 제외)")));
        items.Children.Add(Group(L.T("네트워크"), [Arrow("↓", (Brush)res["Success"]), _rx, Arrow("↑", (Brush)res["Warning"]), _tx], [_rxLine, _txLine],
            L.T("초당 수신 ↓ / 송신 ↑ (lo · 컨테이너 가상 인터페이스 제외)")));

        // 상태 글 (읽는 중 · 일시 정지) 은 값 뒤에 이어 붙고, 오류는 값 자리를 대신한다 (좁으면 말줄임)
        _status = new TextBlock
        {
            Foreground = (Brush)res["TextTertiary"],
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 1, 8, 1),
        };
        items.Children.Add(_status);
        _error = new TextBlock
        {
            Foreground = (Brush)res["Warning"],
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Visibility = Visibility.Collapsed,
        };
        var close = new Button
        {
            Style = (Style)res["IconButton"],
            Content = "",
            FontSize = 10,
            Width = 22,
            Height = 22,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = L.T("서버 모니터 끄기"),
        };
        close.Click += (_, _) => CloseRequested?.Invoke();

        // [값 (좁으면 다음 줄로) 또는 오류] [닫기]
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(close, 1);
        grid.Children.Add(items);
        grid.Children.Add(_error);
        grid.Children.Add(close);
        Child = grid;
        ShowStatus(L.T("읽는 중…"));
    }

    private static void AutomationPropertiesName(DependencyObject o, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(o, name);

    private static TextBlock Value(double minWidth) => new()
    {
        FontFamily = (FontFamily)Application.Current.Resources["MonoFont"],
        FontSize = 12,
        Foreground = (Brush)Application.Current.Resources["Text"],
        MinWidth = minWidth,
        VerticalAlignment = VerticalAlignment.Center,
        Text = "—",
    };

    private static TextBlock Arrow(string glyph, Brush brush) => new()
    {
        Text = glyph,
        FontSize = 12,
        Foreground = brush,
        Margin = new Thickness(0, 0, 3, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static Polyline Spark(Brush stroke) => new()
    {
        Stroke = stroke,
        StrokeThickness = 1.2,
        StrokeLineJoin = PenLineJoin.Round,
        SnapsToDevicePixels = true,
    };

    private static FrameworkElement Group(string label, FrameworkElement[] values, Polyline[] lines, string tip)
    {
        var res = Application.Current.Resources;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 16, 1), ToolTip = tip, Background = Brushes.Transparent };
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11.5,
            Foreground = (Brush)res["SubText"],
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        foreach (var v in values) panel.Children.Add(v);
        var canvas = new Canvas
        {
            Width = SparkWidth,
            Height = SparkHeight,
            Margin = new Thickness(6, 0, 0, 0),
            ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Center,
            Background = (Brush)res["ControlFill"],
        };
        foreach (var l in lines) canvas.Children.Add(l);
        panel.Children.Add(canvas);
        return panel;
    }

    /// <summary>상태 글 (읽는 중 · 일시 정지 · 오류). null/빈 글이면 숨김. 오류면 값 자리를 접고 사유만 보인다</summary>
    public void ShowStatus(string? text, bool error = false)
    {
        _items.Visibility = error ? Visibility.Collapsed : Visibility.Visible;
        _error.Visibility = error ? Visibility.Visible : Visibility.Collapsed;
        _error.Text = error ? text ?? "" : "";
        _error.ToolTip = error && !string.IsNullOrEmpty(text) ? text : null;
        _status.Text = error ? "" : text ?? "";
        _status.Visibility = string.IsNullOrEmpty(_status.Text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>값을 모두 비운다 (다시 연결 · 오류 — 다른 서버/세션의 앞 값과 이어 그리지 않게)</summary>
    public void Reset()
    {
        _cpu.Text = _mem.Text = _rx.Text = _tx.Text = "—";
        _cpuHist.Clear(); _memHist.Clear(); _rxHist.Clear(); _txHist.Clear();
        _cpuLine.Points = new PointCollection();
        _memLine.Points = new PointCollection();
        _rxLine.Points = new PointCollection();
        _txLine.Points = new PointCollection();
    }

    public void Show(ServerStats s)
    {
        var inv = CultureInfo.InvariantCulture;
        if (s.CpuPercent is { } cpu)
        {
            _cpu.Text = cpu.ToString(cpu >= 10 ? "0" : "0.0", inv) + "%";
            _cpuHist.Add(cpu);
        }
        if (s.MemTotal is { } total && s.MemUsed is { } used)
        {
            var pct = used * 100.0 / total;
            _mem.Text = $"{ServerStats.FormatBytes(used)} / {ServerStats.FormatBytes(total)} ({pct.ToString("0", inv)}%)";
            _memHist.Add(pct);
        }
        else _mem.Text = "—";
        if (s.RxPerSec is { } rx && s.TxPerSec is { } tx)
        {
            _rx.Text = ServerStats.FormatRate(rx);
            _tx.Text = ServerStats.FormatRate(tx);
            _rxHist.Add(rx);
            _txHist.Add(tx);
        }
        Draw(_cpuLine, _cpuHist, 100);
        Draw(_memLine, _memHist, 100);
        // 네트워크는 두 선을 같은 눈금으로 (최소 1 KB/s — 거의 쉬는 서버에서 잡음이 꽉 찬 그래프로 보이지 않게)
        var netMax = Math.Max(1024, Math.Max(_rxHist.Max(), _txHist.Max()));
        Draw(_rxLine, _rxHist, netMax);
        Draw(_txLine, _txHist, netMax);
    }

    private static void Draw(Polyline line, RecentValues values, double max)
    {
        var pts = new PointCollection(values.Count);
        var step = SparkWidth / (HistoryLength - 1);
        // 오른쪽 끝이 가장 최근 값 — 값이 모자라면 왼쪽이 빈다
        var x0 = SparkWidth - (values.Count - 1) * step;
        for (var i = 0; i < values.Count; i++)
        {
            var v = Math.Clamp(values[i] / max, 0, 1);
            pts.Add(new Point(x0 + i * step, SparkHeight - 1 - v * (SparkHeight - 2)));
        }
        pts.Freeze();
        line.Points = pts;
    }
}
