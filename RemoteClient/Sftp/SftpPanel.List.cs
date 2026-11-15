using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Renci.SshNet;

namespace Nubis;

/// <summary>SFTP 목록 정렬 기준</summary>
public enum SftpSortKey { Name, Size, Modified }

public partial class SftpPanel
{
    // ===================== 목록 보기: 정렬 · 숨김 파일 =====================

    /// <summary>지금 폴더에서 읽은 전체 항목 (숨김 파일 포함, 정렬 전)</summary>
    private List<SftpItem> _allItems = [];
    private SftpSortKey _sortKey = SftpSortKey.Name;
    private bool _sortDesc;
    /// <summary>점(.)으로 시작하는 파일 표시 — 기본은 지금까지처럼 보인다 (Ctrl+H 로 전환)</summary>
    private bool _showHidden = true;

    /// <summary>새로 읽은 목록을 보인다. 같은 폴더를 다시 읽은 것이면(새로고침·업로드 뒤) 선택을 유지한다</summary>
    private void SetItems(List<SftpItem> items, bool keepSelection)
    {
        _allItems = items;
        ApplyView(keepSelection);
    }

    private void ApplyView(bool keepSelection)
    {
        var selected = keepSelection
            ? FileList.SelectedItems.Cast<SftpItem>().Select(i => i.FullName).ToHashSet(StringComparer.Ordinal)
            : null;
        var view = SortItems(_allItems.Where(i => _showHidden || !IsHiddenName(i.Name)), _sortKey, _sortDesc);
        FileList.ItemsSource = view;
        if (selected is { Count: > 0 })
            foreach (var i in view)
                if (selected.Contains(i.FullName)) FileList.SelectedItems.Add(i);
        if (view.Count > 0) ShowMessage(null);
        else if (_allItems.Count > 0) ShowMessage(L.T("숨김 파일만 있습니다.\nCtrl+H 를 누르면 숨김 파일을 표시합니다."), GlyphHiddenOnly);
        else ShowMessage(L.T("빈 폴더입니다.\n파일을 끌어다 놓으면 업로드합니다."), GlyphEmptyFolder);
        UpdateSortHeaders();
    }

    /// <summary>폴더가 먼저, 그 안에서 고른 기준으로. 같으면 이름순 (대소문자 무시 → 구분)</summary>
    public static List<SftpItem> SortItems(IEnumerable<SftpItem> items, SftpSortKey key, bool desc)
    {
        var o = items.OrderByDescending(i => i.IsDir);
        o = key switch
        {
            SftpSortKey.Size => desc ? o.ThenByDescending(i => i.IsDir ? 0 : i.Size) : o.ThenBy(i => i.IsDir ? 0 : i.Size),
            SftpSortKey.Modified => desc ? o.ThenByDescending(i => i.Modified) : o.ThenBy(i => i.Modified),
            _ => desc ? o.ThenByDescending(i => i.Name, StringComparer.OrdinalIgnoreCase) : o.ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
        };
        return o.ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ThenBy(i => i.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>유닉스 숨김 파일 (점으로 시작)</summary>
    public static bool IsHiddenName(string name) => name.StartsWith('.');

    private void OnSortHeader(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string tag || !Enum.TryParse<SftpSortKey>(tag, out var key)) return;
        // 같은 열을 다시 누르면 방향만 바꾼다. 크기·날짜는 처음 누를 때 큰 것·최신이 위로
        if (key == _sortKey) _sortDesc = !_sortDesc;
        else { _sortKey = key; _sortDesc = key != SftpSortKey.Name; }
        ApplyView(keepSelection: true);
        e.Handled = true;
    }

    /// <summary>머리글: 정렬 중인 열만 글자를 한 단계 밝게 + 방향 화살표(글리프). 나머지는 3차 글자색, 화살표 숨김</summary>
    private void UpdateSortHeaders()
    {
        void Set(TextBlock label, TextBlock arrow, SftpSortKey k)
        {
            var on = k == _sortKey;
            arrow.Text = _sortDesc ? "" : "";   // ChevronDown / ChevronUp
            arrow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            label.SetResourceReference(TextBlock.ForegroundProperty, on ? "TextSecondary" : "TextTertiary");
        }
        Set(NameHeader, NameArrow, SftpSortKey.Name);
        Set(SizeHeader, SizeArrow, SftpSortKey.Size);
        Set(ModifiedHeader, ModifiedArrow, SftpSortKey.Modified);
    }

    private void OnToggleHidden(object sender, RoutedEventArgs e) => ToggleHidden();

    private void ToggleHidden()
    {
        _showHidden = !_showHidden;
        HiddenMenuItem.IsChecked = _showHidden;
        ApplyView(keepSelection: true);
    }

    // ===================== 경로 입력 · 이름 검사 =====================

    /// <summary>
    /// 경로 칸 입력을 절대 경로로: "~" · "~/x" 는 홈 기준, "/" 로 시작하지 않으면 현재 폴더 기준,
    /// "." · ".." · 겹친 "/" 는 글자 그대로 정리한다 (셸의 cd 처럼 — 링크는 풀지 않음)
    /// </summary>
    public static string ResolvePathInput(string input, string cwd, string home)
    {
        var p = input.Trim();
        // Windows OpenSSH 서버: 탐색기에서 복사한 "C:\Users\me" · "C:/x" 는 SFTP 경로 "/C:/Users/me" 로 (현재 폴더 기준 상대 경로로 붙지 않게)
        if (p.Length >= 2 && char.IsAsciiLetter(p[0]) && p[1] == ':' && (p.Length == 2 || p[2] is '\\' or '/'))
            p = "/" + p.Replace('\\', '/');
        if (p == "~") p = home;
        else if (p.StartsWith("~/", StringComparison.Ordinal)) p = Combine(home, p[2..]);
        else if (!p.StartsWith('/')) p = Combine(cwd.Length > 0 ? cwd : home, p);
        var parts = new List<string>();
        foreach (var seg in p.Split('/'))
        {
            if (seg is "" or ".") continue;
            if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(seg);
        }
        return "/" + string.Join("/", parts);
    }

    /// <summary>새 폴더·이름 바꾸기에 쓸 수 있는 이름인가 — 안 되면 사용자에게 보일 사유, 되면 null</summary>
    public static string? ValidateRemoteName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return L.T("이름을 입력하세요.");
        if (name is "." or "..") return L.T("'.' 과 '..' 은 이름으로 쓸 수 없습니다.");
        // "/" 는 다른 폴더로 옮기는 경로가 되고, 제어 문자(줄바꿈 등)는 거의 항상 잘못 붙여 넣은 것이다
        if (name.Any(ch => ch == '/' || ch < 32 || ch == 127)) return L.T("이름에 '/' 나 제어 문자는 쓸 수 없습니다.");
        return null;
    }

    /// <summary>원격 폴더의 이름 목록 (폴더가 없으면 null, 그 밖의 오류는 예외 — RemoteNameCache 가 하나씩 묻기로 넘어간다)</summary>
    private static IEnumerable<string>? ListNames(SftpClient c, string dir)
    {
        try { return c.ListDirectory(dir).Select(f => f.Name).ToList(); }
        catch (Renci.SshNet.Common.SftpPathNotFoundException) { return null; }
    }
}

/// <summary>
/// 업로드 전 "서버에 이미 있나" 확인을 폴더 목록 한 번으로 — 파일마다 묻는(Exists) 왕복을 없앤다.
/// 폴더 목록을 못 읽으면(권한 없음 등) 그 폴더는 null(모름)을 돌려주고, 부르는 쪽이 하나씩 묻는다
/// </summary>
public sealed class RemoteNameCache(Func<string, IEnumerable<string>?> list)
{
    /// <summary>이 개수 이상일 때만 쓴다 — 몇 개만 올릴 때 수만 개짜리 폴더 목록을 읽지 않게</summary>
    public const int BatchThreshold = 20;

    /// <summary>
    /// 폴더 → 이름들 (null = 폴더가 없음). 대소문자를 무시하는 집합 — 정확히 같은 이름은 Exact 로 따로 본다.
    /// (Windows OpenSSH 처럼 대소문자를 가리지 않는 서버에서 "Foo.txt" 를 "없음"으로 판단하면, 덮어쓰기 확인 없이
    /// "foo.txt" 에 바로 쓰고 실패 때 새 파일로 알고 지워 버린다 — 대소문자만 다른 이름은 "모름"으로 돌려 서버에 직접 묻게)
    /// </summary>
    private readonly Dictionary<string, (HashSet<string> Exact, HashSet<string> Folded)?> _dirs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unknown = new(StringComparer.Ordinal);

    /// <summary>있으면 true, 없으면 false, 폴더 목록을 못 읽었거나 대소문자만 다른 이름이 있으면 null (부르는 쪽이 서버에 직접 묻는다)</summary>
    public bool? Exists(string path)
    {
        var (parent, name) = Split(path);
        if (_unknown.Contains(parent)) return null;
        if (!_dirs.TryGetValue(parent, out var set))
        {
            try { set = list(parent) is { } names ? MakeSet(names) : null; }
            catch { _unknown.Add(parent); return null; }
            _dirs[parent] = set;
        }
        if (set is not { } s) return false;
        if (s.Exact.Contains(name)) return true;
        return s.Folded.Contains(name) ? null : false;
    }

    /// <summary>방금 만든 폴더 — 부모 목록에 넣고, 자신은 빈 폴더로 기억</summary>
    public void Created(string dir)
    {
        var (parent, name) = Split(dir);
        if (_dirs.TryGetValue(parent, out var set))
        {
            var s = set ?? MakeSet([]);
            s.Exact.Add(name);
            s.Folded.Add(name);
            _dirs[parent] = s;
        }
        _dirs[dir.TrimEnd('/')] = MakeSet([]);
        _unknown.Remove(dir.TrimEnd('/'));
    }

    private static (HashSet<string> Exact, HashSet<string> Folded) MakeSet(IEnumerable<string> names)
    {
        var exact = new HashSet<string>(names, StringComparer.Ordinal);
        return (exact, new HashSet<string>(exact, StringComparer.OrdinalIgnoreCase));
    }

    private static (string Parent, string Name) Split(string path)
    {
        var p = path.TrimEnd('/');
        return (SftpPanel.ParentPath(p), p[(p.LastIndexOf('/') + 1)..]);
    }
}

/// <summary>전송 진행 문구 (받은 양 / 전체 · 속도 · 남은 시간). 화면 갱신은 0.25초에 한 번만</summary>
public sealed class TransferMeter(long total)
{
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private long _lastMs = -1000;

    public long Total { get; } = total;

    /// <summary>문구를 다시 그릴 때가 됐나 (진행 보고가 초당 수백 번 와도 화면은 0.25초마다)</summary>
    public bool Due()
    {
        var ms = _sw.ElapsedMilliseconds;
        if (ms - _lastMs < 250) return false;
        _lastMs = ms;
        return true;
    }

    public string Detail(long done) => Format(done, Total, _sw.Elapsed);

    /// <summary>"1.2 MB / 10 MB · 3.4 MB/s · 남은 시간 0:03" — 1초가 지나기 전에는 양만</summary>
    public static string Format(long done, long total, TimeSpan elapsed)
    {
        var sizes = $"{SftpPanel.HumanSize(done)} / {SftpPanel.HumanSize(total)}";
        if (elapsed.TotalSeconds < 1 || done <= 0) return sizes;
        var rate = done / elapsed.TotalSeconds;
        var remain = total > done ? Math.Min((total - done) / rate, 99 * 3600 + 59 * 60 + 59) : 0;
        return L.F("{0} · {1}/s · 남은 시간 {2}", sizes, SftpPanel.HumanSize((long)rate), FormatEta(TimeSpan.FromSeconds(remain)));
    }

    /// <summary>남은 시간: 한 시간 미만이면 m:ss, 이상이면 h:mm:ss</summary>
    public static string FormatEta(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
}

/// <summary>
/// 진행 보고를 화면 스레드로 넘기는 횟수를 줄인다. Progress&lt;T&gt; 는 SSH.NET 이 보고할 때마다(수십 KB 마다)
/// UI 스레드에 작업을 올려, 빠른 전송에서는 초당 수천 번 화면 스레드를 깨웠다 (보고 처리 안에서 거르는 것으로는 그 비용이 그대로).
/// 마지막 값만 기억해 두고 interval 에 한 번, 동시에 하나만 올린다. 그 사이 값은 버린다 —
/// 부르는 쪽은 파일 하나가 끝날 때 진행률을 직접 맞춘다.
/// </summary>
public sealed class ThrottledProgress<T> : IProgress<T>
{
    private readonly Action<Action> _post;
    private readonly Action<T> _apply;
    private readonly long _intervalMs;
    private readonly Func<long> _clock;
    private readonly object _gate = new();
    private T _latest = default!;
    private long _lastPost = long.MinValue / 2;
    private bool _pending;

    /// <param name="post">apply 를 화면 스레드로 넘기는 방법 (테스트에서는 바로 실행)</param>
    /// <param name="clock">밀리초 시계 (기본 Environment.TickCount64)</param>
    public ThrottledProgress(Action<Action> post, Action<T> apply, int intervalMs = 250, Func<long>? clock = null)
    {
        _post = post;
        _apply = apply;
        _intervalMs = intervalMs;
        _clock = clock ?? (() => Environment.TickCount64);
    }

    /// <summary>WPF 화면 스레드로 — 입력·그리기보다 낮은 우선순위라 전송 중에도 화면이 버벅이지 않는다</summary>
    public static ThrottledProgress<T> OnDispatcher(System.Windows.Threading.Dispatcher dispatcher, Action<T> apply) =>
        new(a => dispatcher.BeginInvoke(a, System.Windows.Threading.DispatcherPriority.Background), apply);

    public void Report(T value)
    {
        lock (_gate)
        {
            _latest = value;
            if (_pending) return;   // 이미 올려 둔 것이 최신 값을 가져간다
            var now = _clock();
            if (now - _lastPost < _intervalMs) return;
            _pending = true;
            _lastPost = now;
        }
        _post(Flush);
    }

    private void Flush()
    {
        T v;
        lock (_gate)
        {
            v = _latest;
            _pending = false;
        }
        _apply(v);
    }
}

/// <summary>진행 보고를 받지 않음 — new Progress&lt;T&gt;(_ =&gt; { }) 는 빈 처리기라도 보고마다 UI 스레드에 작업을 올린다</summary>
public sealed class NoProgress<T> : IProgress<T>
{
    public static readonly NoProgress<T> Instance = new();
    private NoProgress() { }
    public void Report(T value) { }
}
