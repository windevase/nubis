using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace Nubis;

public enum PortForwardState { Starting, Active, Stopped, Failed }

/// <summary>
/// 포트 포워딩 목록의 행 하나 — 무엇이 포워딩하는지(kubectl · SSH 등)와 무관하게 같은 모양으로 보인다.
/// Label 은 "localhost:8080 → svc/web:80" 모양
/// </summary>
public interface IPortForwardRow : INotifyPropertyChanged
{
    string Label { get; }
    /// <summary>두 번째 줄 앞부분 (네임스페이스 · 호스트 등, 없으면 빈 문자열)</summary>
    string Detail { get; }
    PortForwardState State { get; }
    string StatusText { get; }
    /// <summary>복사할 주소 (127.0.0.1:8080)</summary>
    string LocalAddress { get; }
    /// <summary>웹 포트로 보이면 브라우저 주소, 아니면 null</summary>
    string? BrowserUrl { get; }
    bool CanRestart { get; }
    void Stop();
    void Restart();
}

/// <summary>
/// 열린 포트 포워딩 목록 (상태 점 · 문구 · 주소 복사 · 브라우저로 열기 · 다시 시작 · 중지/목록에서 지우기).
/// 행이 없으면 접힌다. 행은 소유자(클러스터 탭)가 Rows 에 넣고, 멈춘 행의 ✕ 는 목록에서 지운다
/// </summary>
public partial class PortForwardList : UserControl
{
    public ObservableCollection<IPortForwardRow> Rows { get; } = new();

    public PortForwardList()
    {
        InitializeComponent();
        RowList.ItemsSource = Rows;
        Rows.CollectionChanged += OnRowsChanged;
        UpdateHeader();
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateHeader();

    private void UpdateHeader()
    {
        Visibility = Rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        HeaderText.Text = L.F("포트 포워딩 ({0})", Rows.Count);
    }

    private static IPortForwardRow? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as IPortForwardRow;

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } r) return;
        try
        {
            Clipboard.SetText(r.LocalAddress);
            Toast.Show(L.F("{0} 을(를) 복사했습니다.", r.LocalAddress));
        }
        catch { }
    }

    private void OnOpenBrowser(object sender, RoutedEventArgs e)
    {
        // 이 목록이 만든 127.0.0.1 주소만 연다 (http/https)
        if (RowOf(sender)?.BrowserUrl is not { } url || !Uri.TryCreate(url, UriKind.Absolute, out var u)
            || u.Scheme is not ("http" or "https") || !u.IsLoopback) return;
        try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception ex) { Toast.Show(ex.Message, ToastKind.Warning); }
    }

    private void OnRestart(object sender, RoutedEventArgs e) => RowOf(sender)?.Restart();

    /// <summary>실행 중이면 중지, 멈춘 행이면 목록에서 지운다</summary>
    private void OnStopOrRemove(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } r) return;
        if (r.State is PortForwardState.Starting or PortForwardState.Active) r.Stop();
        else Rows.Remove(r);
    }

    /// <summary>모두 중지 (탭을 닫을 때)</summary>
    public void StopAll()
    {
        foreach (var r in Rows) { try { r.Stop(); } catch { } }
    }
}
