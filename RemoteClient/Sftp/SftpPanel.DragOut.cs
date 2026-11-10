using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace Nubis;

public partial class SftpPanel
{
    // ===================== 탐색기로 끌어내기 (drag-out) =====================
    // 끄는 동안에는 받지 않고, 마우스를 놓아 대상이 파일을 요청하는 순간(GetData) 임시 폴더로 받아 그 경로를 넘긴다.
    // 탐색기는 같은 드라이브면 임시 폴더에서 옮기고, 다른 드라이브면 복사한다.

    private readonly string _panelId = Guid.NewGuid().ToString("N");
    private Point? _dragStart;
    private SftpItem? _pressedItem;
    private bool _deferSelect;

    private static string DragTempRoot => Path.Combine(Path.GetTempPath(), "Nubis", "sftp-drag");

    private void OnListMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = null;
        _deferSelect = false;
        _pressedItem = (e.OriginalSource as FrameworkElement)?.DataContext as SftpItem;
        if (_pressedItem == null || e.ClickCount != 1) return;
        _dragStart = e.GetPosition(FileList);
        // 여러 개 선택한 상태에서 선택된 항목을 눌러 끌기 시작하면 선택이 풀리지 않게, 선택 변경은 버튼을 뗄 때로 미룬다
        if (FileList.SelectedItems.Contains(_pressedItem) && FileList.SelectedItems.Count > 1 && Keyboard.Modifiers == ModifierKeys.None)
        {
            _deferSelect = true;
            e.Handled = true;
        }
    }

    private void OnListMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_deferSelect && _pressedItem != null)
        {
            FileList.SelectedItems.Clear();
            FileList.SelectedItem = _pressedItem;
        }
        _deferSelect = false;
        _dragStart = null;
    }

    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(FileList) - start;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragStart = null;
        _deferSelect = false;
        var items = Selected();
        if (items.Count == 0 || _busy || _cwd.Length == 0) return;

        CleanupDragTemp();
        var dir = Path.Combine(DragTempRoot, Guid.NewGuid().ToString("N")[..12]);
        var data = new SftpDragData(this, items, DragTargets(items.Select(i => i.Name), dir), dir);
        try { DragDrop.DoDragDrop(FileList, data, DragDropEffects.Copy | DragDropEffects.Move); }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    /// <summary>끌어낼 항목들의 임시 폴더 안 로컬 경로 (Windows 이름으로 바꾼 뒤 겹치면 "이름 (1).확장자")</summary>
    public static List<string> DragTargets(IEnumerable<string> remoteNames, string dir)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var name in remoteNames)
        {
            var safe = SafeLocalName(name);
            var candidate = safe;
            for (var i = 1; !used.Add(candidate); i++)
                candidate = $"{Path.GetFileNameWithoutExtension(safe)} ({i}){Path.GetExtension(safe)}";
            result.Add(Path.Combine(dir, candidate));
        }
        return result;
    }

    /// <summary>
    /// 놓는 순간 호출: 임시 폴더로 받는 동안 메시지를 돌려 진행률 표시·취소 버튼이 동작하게 한다.
    /// 성공하면 true (모든 대상 경로가 만들어짐).
    /// </summary>
    internal bool DownloadForDrag(List<SftpItem> items, List<string> targets, string dir)
    {
        if (_busy) return false;
        Directory.CreateDirectory(dir);
        var task = DownloadCoreAsync(items.Zip(targets, (i, t) => (i, t)).ToList(), dir, "끌어다 놓은 위치");
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => Dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        return task.Result != null;
    }

    /// <summary>하루 지난 끌어내기 임시 폴더 정리 (다른 드라이브로 복사된 경우 남는다)</summary>
    private static void CleanupDragTemp()
    {
        try
        {
            if (!Directory.Exists(DragTempRoot)) return;
            foreach (var d in Directory.GetDirectories(DragTempRoot))
                if (Directory.GetCreationTimeUtc(d) < DateTime.UtcNow.AddDays(-1))
                    try { Directory.Delete(d, true); } catch { }
        }
        catch { }
    }

    /// <summary>
    /// 지연 렌더링 데이터 개체. 끄는 동안(마우스 버튼이 눌린 동안)은 받을 예정 경로만 돌려주고
    /// (탐색기가 같은 드라이브인지 판단할 때 경로만 본다), 버튼을 놓은 뒤 요청이 오면 그때 실제로 받는다.
    /// </summary>
    private sealed class SftpDragData : IDataObject
    {
        /// <summary>끌어낸 패널 식별 (같은 패널에 다시 놓기 방지)</summary>
        public const string SourceFormat = "Nubis.SftpDragSource";

        private readonly SftpPanel _panel;
        private readonly List<SftpItem> _items;
        private readonly List<string> _targets;
        private readonly string _dir;
        private readonly Dictionary<string, object> _extra = new(StringComparer.OrdinalIgnoreCase);
        private string[]? _result;
        private bool _done, _downloading;

        public SftpDragData(SftpPanel panel, List<SftpItem> items, List<string> targets, string dir)
        {
            _panel = panel; _items = items; _targets = targets; _dir = dir;
        }

        private static bool Is(string format, string name) => string.Equals(format, name, StringComparison.OrdinalIgnoreCase);

        private string[]? Files()
        {
            if (_done) return _result;
            if (_downloading) return null;
            if (System.Windows.Forms.Control.MouseButtons != System.Windows.Forms.MouseButtons.None) return _targets.ToArray();
            _downloading = true;
            try { _result = _panel.DownloadForDrag(_items, _targets, _dir) ? _targets.ToArray() : null; }
            finally { _downloading = false; _done = true; }
            return _result;
        }

        public object? GetData(string format, bool autoConvert)
        {
            if (Is(format, DataFormats.FileDrop)) return Files();
            if (Is(format, SourceFormat)) return _panel._panelId;
            return _extra.TryGetValue(format, out var v) ? v : null;
        }
        public object? GetData(string format) => GetData(format, true);
        public object? GetData(Type format) => GetData(format.FullName ?? "", true);

        public bool GetDataPresent(string format, bool autoConvert) =>
            (Is(format, DataFormats.FileDrop) && !(_done && _result == null)) || Is(format, SourceFormat) || _extra.ContainsKey(format);
        public bool GetDataPresent(string format) => GetDataPresent(format, true);
        public bool GetDataPresent(Type format) => GetDataPresent(format.FullName ?? "", true);

        public string[] GetFormats(bool autoConvert) => new[] { DataFormats.FileDrop, SourceFormat }.Concat(_extra.Keys).ToArray();
        public string[] GetFormats() => GetFormats(true);

        // 탐색기가 "Preferred DropEffect" 등을 써 넣는다
        public void SetData(string format, object data, bool autoConvert) => _extra[format] = data;
        public void SetData(string format, object data) => _extra[format] = data;
        public void SetData(Type format, object data) => _extra[format.FullName ?? format.Name] = data;
        public void SetData(object data) => _extra[data.GetType().FullName ?? "object"] = data;
    }

    private static void Plan(SftpClient c, string remote, bool isDir, string local,
        List<(string, string, long)> files, List<string> dirs)
    {
        if (!isDir)
        {
            files.Add((remote, local, c.GetAttributes(remote).Size));
            return;
        }
        dirs.Add(local);
        foreach (var f in c.ListDirectory(remote).Where(f => f.Name is not "." and not ".."))
            Plan(c, f.FullName, f.IsDirectory && !f.IsSymbolicLink, Path.Combine(local, SafeLocalName(f.Name)), files, dirs);
    }

    /// <summary>
    /// 원격 이름을 Windows 파일 이름으로: 경로 구분자·예약 문자(\ / : * ? " &lt; &gt; |)·제어 문자를 _ 로,
    /// "." / ".." 와 끝의 점·공백 제거, 예약 장치 이름(CON, NUL 등) 회피. 콜론은 NTFS 대체 스트림이 되는 것을 막기 위해 치환.
    /// </summary>
    public static string SafeLocalName(string name)
    {
        var chars = name.Select(ch => ch < 32 || "\\/:*?\"<>|".Contains(ch) ? '_' : ch).ToArray();
        var s = new string(chars).TrimEnd('.', ' ');
        if (s.Length == 0 || s is "." or "..") s = "_";
        var stem = s.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3])))
            s = "_" + s;
        return s;
    }

    /// <summary>같은 이름이 이미 있으면 "이름 (1).확장자" 형태로 (기존 로컬 파일 덮어쓰기 방지)</summary>
    public static string UniquePath(string path, bool isDir)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = isDir ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
        var ext = isDir ? "" : Path.GetExtension(path);
        for (var i = 1; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }
}
