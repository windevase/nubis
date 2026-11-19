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

    /// <summary>끌어다 놓은 데이터에서 .rdp 파일 경로만 (없으면 빈 목록). 폴더 · 다른 파일은 무시</summary>
    public static List<string> RdpFilesIn(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop) || data.GetData(DataFormats.FileDrop) is not string[] files) return new();
        return files.Where(f => f.EndsWith(".rdp", StringComparison.OrdinalIgnoreCase) && File.Exists(f)).ToList();
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
        RdpFilesText.Text = files.Count == 1 ? files[0] : L.F("{0} 외 {1}개", Path.GetFileName(files[0]), files.Count - 1);
        RdpFilesText.ToolTip = string.Join("\n", files.Take(30)) + (files.Count > 30 ? "\n" + L.F("… 외 {0}개", files.Count - 30) : "");
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
