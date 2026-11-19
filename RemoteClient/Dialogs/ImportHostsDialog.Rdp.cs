using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;

namespace Nubis;

/// <summary>가져오기 창: .rdp 파일 출처 (여러 파일 고르기 · 창에 끌어다 놓기 · 호스트 목록에 놓아 바로 열기)</summary>
public partial class ImportHostsDialog
{
    /// <summary>마지막으로 읽은 .rdp 파일들</summary>
    private List<string> _rdpFiles = new();

    /// <summary>
    /// 끌어다 놓은 데이터에서 이름이 .rdp 로 끝나는 경로만 (없으면 빈 목록). 다른 파일은 무시.
    /// DragOver 는 마우스를 움직일 때마다 불리므로 파일 시스템은 건드리지 않는다 (수만 개 · 네트워크 파일에서 창이 멈추지 않게) —
    /// 폴더 · 없는 파일은 백그라운드에서 읽을 때(RdpFileImport.ParseFiles) 거른다. 최대 개수 + 1 개까지만 (넘으면 ParseFiles 가 경고)
    /// </summary>
    public static List<string> RdpFilesIn(IDataObject data)
    {
        string[]? files;
        try
        {
            if (!data.GetDataPresent(DataFormats.FileDrop)) return new();
            files = data.GetData(DataFormats.FileDrop) as string[];
        }
        // 가상 파일(압축 파일 안 · 메일 첨부) 등 끌어 온 쪽이 데이터를 주지 못하는 경우
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or OutOfMemoryException)
        {
            return new();
        }
        if (files == null) return new();
        return files.Where(f => !string.IsNullOrWhiteSpace(f) && f.EndsWith(".rdp", StringComparison.OrdinalIgnoreCase))
                    .Take(RdpFileImport.MaxFiles + 1).ToList();
    }

    /// <summary>.rdp 출처를 고른 상태로 열고 바로 읽는다 (호스트 목록에 파일을 놓았을 때)</summary>
    private void StartWithRdp(IReadOnlyList<string> files)
    {
        SrcRdp.IsChecked = true;   // OnSourceChanged → UpdateSourcePanel
        LoadRdp(files);
    }

    private void OnBrowseRdp(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = L.T("원격 데스크톱 파일") + " (*.rdp)|*.rdp|" + L.T("모든 파일") + "|*.*",
            CheckFileExists = true,
            Multiselect = true,
        };
        try
        {
            var dir = _rdpFiles.Count > 0 ? Path.GetDirectoryName(_rdpFiles[0]) : null;
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) dlg.InitialDirectory = dir;
        }
        catch (ArgumentException) { /* 경로에 쓸 수 없는 글자 — 기본 위치에서 연다 */ }
        if (dlg.ShowDialog(this) == true) LoadRdp(dlg.FileNames);
    }

    private void LoadRdp(IReadOnlyList<string> files)
    {
        if (files.Count == 0) { ShowStatus(L.T(".rdp 파일을 고르세요."), true); return; }
        _rdpFiles = files.ToList();
        // 파일 이름의 양방향 문자(U+202E) 등은 \u{XXXX} 로 보이게
        RdpFilesText.Text = McpHelpers.VisibleControlChars(files.Count == 1 ? files[0] : L.F("{0} 외 {1}개", Path.GetFileName(files[0]), files.Count - 1));
        RdpFilesText.ToolTip = string.Join("\n", files.Take(30).Select(f => McpHelpers.VisibleControlChars(f))) + (files.Count > 30 ? "\n" + L.F("… 외 {0}개", files.Count - 30) : "");
        var copy = _rdpFiles.ToList();
        Load(() => RdpFileImport.ParseFiles(copy), L.T(".rdp 파일"));
    }

    /// <summary>창 어디에 놓아도 .rdp 파일이면 받는다 (붙여넣기 칸 등 텍스트 상자가 파일 경로를 글자로 넣기 전에)</summary>
    private void OnRdpPreviewDragOver(object sender, DragEventArgs e)
    {
        if (RdpFilesIn(e.Data).Count == 0) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnRdpPreviewDrop(object sender, DragEventArgs e)
    {
        var files = RdpFilesIn(e.Data);
        if (files.Count == 0) return;
        e.Handled = true;
        StartWithRdp(files);
    }
}
