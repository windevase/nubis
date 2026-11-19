using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Nubis;

/// <summary>포워딩 행의 상태 표시 (점 색·문구 색)</summary>
public enum PortForwardRowState { Off, Waiting, Open, Failed }

/// <summary>
/// 포워딩 목록의 한 줄 — "127.0.0.1:15432 → db:5432" + 이름·상태 + 오른쪽 동작 버튼들.
/// SSH 포워딩(탭 창·호스트 편집)에 쓰고, 쿠버네티스 포워딩 같은 다른 종류도 같은 모양으로 쓸 수 있게 데이터 형식에 묶지 않았다.
/// 코드로 만드는 얕은 요소 (그림자·애니메이션 없음)
/// </summary>
public sealed class PortForwardRow : Border
{
    private readonly Ellipse _dot = new() { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
    private readonly TextBlock _status = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };

    /// <param name="local">이 PC 쪽 주소 (예: 127.0.0.1:15432)</param>
    /// <param name="remote">대상 (예: db:5432, pod/web:80)</param>
    /// <param name="name">이름 (선택, 상태 앞에 붙는다)</param>
    public PortForwardRow(string local, string remote, string? name = null)
    {
        var res = Application.Current.Resources;
        CornerRadius = (CornerRadius)res["RadiusMd"];
        Padding = new Thickness(10, 7, 6, 7);
        Margin = new Thickness(0, 0, 0, 4);
        Background = (Brush)res["Layer3Bg"];
        BorderBrush = (Brush)res["StrokeSubtle"];
        BorderThickness = new Thickness(1);

        var text = PortForwards.Arrow(local, remote);
        _label = name;
        var title = new TextBlock
        {
            Text = text,
            FontFamily = (FontFamily)res["MonoFont"],
            FontSize = 12,
            Foreground = (Brush)res["TextPrimary"],
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = text,
        };
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(title);
        texts.Children.Add(_status);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(texts, 1);
        Grid.SetColumn(_actions, 2);
        grid.Children.Add(_dot);
        grid.Children.Add(texts);
        grid.Children.Add(_actions);
        Child = grid;
        AutomationProperties.SetName(this, name is { Length: > 0 } ? $"{name} {text}" : text);
        SetState(PortForwardRowState.Off, "");
    }

    private readonly string? _label;

    /// <summary>상태 점·문구. 문구 앞에 이름이 있으면 "이름 · 문구"</summary>
    public void SetState(PortForwardRowState state, string text, string? toolTip = null)
    {
        var res = Application.Current.Resources;
        var brush = (Brush)res[state switch
        {
            PortForwardRowState.Open => "Success",
            PortForwardRowState.Waiting => "Warning",
            PortForwardRowState.Failed => "Danger",
            _ => "TextTertiary",
        }];
        _dot.Fill = brush;
        _status.Text = _label is { Length: > 0 } n ? (text.Length > 0 ? $"{n} · {text}" : n) : text;
        _status.Foreground = state == PortForwardRowState.Failed ? brush : (Brush)res["TextSecondary"];
        _status.ToolTip = toolTip ?? (_status.Text.Length > 0 ? _status.Text : null);
        _status.Visibility = _status.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>오른쪽에 아이콘 버튼 추가 (Segoe Fluent 글리프)</summary>
    public Button AddAction(string glyph, string toolTip, Action onClick)
    {
        var b = new Button
        {
            Style = (Style)Application.Current.Resources["IconButton"],
            Content = glyph,
            ToolTip = toolTip,
            Margin = new Thickness(2, 0, 0, 0),
        };
        b.Click += (_, _) => onClick();
        _actions.Children.Add(b);
        return b;
    }

    /// <summary>오른쪽에 켜기/끄기 스위치 추가</summary>
    public CheckBox AddToggle(bool on, string toolTip, Action<bool> onChanged)
    {
        var t = new CheckBox
        {
            Style = (Style)Application.Current.Resources["ToggleSwitch"],
            IsChecked = on,
            ToolTip = toolTip,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 2, 0),
        };
        AutomationProperties.SetName(t, toolTip);
        t.Click += (_, _) => onChanged(t.IsChecked == true);
        _actions.Children.Add(t);
        return t;
    }
}

/// <summary>
/// 포워딩 규칙 입력 칸: [로컬 포트] [대상 호스트] [대상 포트] / [이름] [추가]. 검증은 PortForwards.Parse.
/// 추가를 누르면 Submit 이 불린다 — 오류 문구를 돌려주면 아래에 보이고, null 이면 칸을 비운다
/// </summary>
public sealed class PortForwardForm : StackPanel
{
    private readonly TextBox _local = Box(L.T("예: 15432"));
    private readonly TextBox _host = Box(PortForwards.DefaultTargetHost);
    private readonly TextBox _port = Box(L.T("예: 5432"));
    private readonly TextBox _name = Box(L.T("이름 (선택)"));
    private readonly TextBlock _error = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 6, 0, 0), Visibility = Visibility.Collapsed };

    public Func<PortForwardRule, string?>? Submit { get; set; }

    public PortForwardForm(string addText)
    {
        var res = Application.Current.Resources;
        _error.Foreground = (Brush)res["Danger"];
        AutomationProperties.SetName(_local, L.T("로컬 포트"));
        AutomationProperties.SetName(_host, L.T("대상 호스트"));
        AutomationProperties.SetName(_port, L.T("대상 포트"));
        AutomationProperties.SetName(_name, L.T("이름 (선택)"));

        var row1 = new Grid();
        row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
        row1.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row1.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Add(row1, Label(L.T("로컬 포트")), 0, 0);
        var hostLabel = Label(L.T("대상 호스트 (서버에서 본)"));
        hostLabel.Margin = new Thickness(6, 0, 6, 4);
        Add(row1, hostLabel, 0, 1);
        Add(row1, Label(L.T("대상 포트")), 0, 2);
        Add(row1, _local, 1, 0);
        Add(row1, _host, 1, 1);
        Add(row1, _port, 1, 2);
        _host.Margin = new Thickness(6, 0, 6, 0);

        var row2 = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        row2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row2.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var add = new Button
        {
            Style = (Style)res["GhostButton"],
            Content = addText,
            Height = 38,
            Padding = new Thickness(14, 0, 14, 0),
            Margin = new Thickness(6, 0, 0, 0),
        };
        add.Click += (_, _) => OnAdd();
        Add(row2, _name, 0, 0);
        Add(row2, add, 0, 1);

        Children.Add(row1);
        Children.Add(row2);
        Children.Add(_error);
        // 칸에서 Enter = 추가
        foreach (var b in new[] { _local, _host, _port, _name })
            b.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; OnAdd(); } };
    }

    /// <summary>칸에 규칙 값 채우기 (편집하려고 목록에서 꺼낸 규칙)</summary>
    public void Load(PortForwardRule r)
    {
        _local.Text = r.LocalPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _host.Text = r.TargetHost == PortForwards.DefaultTargetHost ? "" : r.TargetHost;
        _port.Text = r.TargetPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _name.Text = r.Name ?? "";
        ShowError(null);
        _local.Focus();
    }

    public void Clear()
    {
        _local.Text = _host.Text = _port.Text = _name.Text = "";
        ShowError(null);
    }

    /// <summary>칸에 적다 만 값이 있는지 (편집 패널이 저장 전에 알려 주려고)</summary>
    public bool HasInput => _local.Text.Trim().Length > 0 || _host.Text.Trim().Length > 0 || _port.Text.Trim().Length > 0 || _name.Text.Trim().Length > 0;

    public void FocusFirst() => _local.Focus();

    public void ShowError(string? text)
    {
        _error.Text = text ?? "";
        _error.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnAdd()
    {
        var (rule, err) = PortForwards.Parse(_local.Text, _host.Text, _port.Text, _name.Text);
        if (rule == null) { ShowError(err); return; }
        var result = Submit?.Invoke(rule);
        if (result != null) { ShowError(result); return; }
        Clear();
        _local.Focus();
    }

    private static TextBox Box(string placeholder) => new() { Tag = placeholder };

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        Style = (Style)Application.Current.Resources["FieldLabel"],
        Margin = new Thickness(0, 0, 0, 4),
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private static void Add(Grid g, UIElement e, int row, int col)
    {
        Grid.SetRow(e, row);
        Grid.SetColumn(e, col);
        g.Children.Add(e);
    }
}
