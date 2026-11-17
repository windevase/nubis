using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Nubis;

/// <summary>kubeconfig 에서 context 를 골라 클러스터로 가져오는 창</summary>
public partial class KubeconfigImportDialog : Window
{
    /// <summary>목록의 context 한 줄</summary>
    public sealed class Row : INotifyPropertyChanged
    {
        public required KubeContext Context { get; init; }
        public string Name => Context.Name;
        public required string Badge { get; init; }
        public required string Detail { get; init; }
        public string Note { get; init; } = "";
        public bool CanImport { get; init; } = true;
        private bool _selected;
        public bool Selected { get => _selected; set { _selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>한 번 읽은 결과 (목록과 가져오기가 같은 내용을 쓰게 함께 둔다). Doc 이 null 이면 읽지 못한 것</summary>
    private sealed record LoadResult(string Yaml, string? BaseDir, KubeconfigDoc? Doc, List<Row> Rows, string? Message);

    private LoadResult _loaded = new("", null, null, new(), null);
    private List<Row> _rows = new();
    private List<K8sCluster>? _result;

    /// <summary>"파일..." 로 고른 파일 (다른 곳을 봤다가 돌아오면 다시 읽는다)</summary>
    private string? _pickedFile;
    private RadioButton? _curSource, _prevSource;
    /// <summary>같은 원본을 다시 읽을 때(붙여넣기 수정) 유지할 체크 선택. 원본을 바꾸면 null</summary>
    private HashSet<string>? _remembered;

    /// <summary>붙여넣기 디바운스 — 글자마다 전체를 다시 읽지 않게</summary>
    private readonly DispatcherTimer _pasteTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    /// <summary>마지막 읽기 요청 번호 (늦게 끝난 이전 요청 결과는 버린다)</summary>
    private int _loadGen;
    private Func<LoadResult>? _pendingWork;
    private bool _closed;

    // 원본 읽기·context 정보·클러스터 만들기는 MCP import_k8s_clusters 와 공용 (KubeconfigImport)
    private static string HomeConfig => KubeconfigImport.HomeConfig;

    private static string? EnvKubeconfig => KubeconfigImport.EnvKubeconfig;

    /// <summary>기본 원본: KUBECONFIG 의 파일들 (없으면 ~/.kube/config)</summary>
    private static List<string> DefaultPaths => KubeconfigImport.DefaultPaths;

    private KubeconfigImportDialog()
    {
        InitializeComponent();
        _curSource = SrcDefault;
        var paths = DefaultPaths;
        if (!string.IsNullOrWhiteSpace(EnvKubeconfig) && !(paths.Count == 1 && paths[0] == HomeConfig))
        {
            SrcDefault.Content = paths.Count == 1 ? "KUBECONFIG" : L.F("KUBECONFIG ({0}개 파일)", paths.Count);
            SrcDefault.ToolTip = string.Join("\n", paths);
        }
        _pasteTimer.Tick += (_, _) =>
        {
            _pasteTimer.Stop();
            if (SrcPaste.IsChecked == true) StartLoad(PasteWork());
        };
        Loaded += (_, _) => LoadDefault();
        Closed += (_, _) => { _closed = true; _pasteTimer.Stop(); _loadGen++; };
    }

    /// <summary>가져올 클러스터 (취소하면 null)</summary>
    public static List<K8sCluster>? Ask(Window owner)
    {
        var dlg = new KubeconfigImportDialog { Owner = owner };
        return dlg.ShowDialog() == true ? dlg._result : null;
    }

    private void OnSourceChanged(object sender, RoutedEventArgs e)
    {
        if (PasteBox == null || sender is not RadioButton src) return;   // InitializeComponent 중
        if (src == _curSource) return;
        _prevSource = _curSource;
        _curSource = src;
        _pasteTimer.Stop();
        ResetRows("");
        PasteBox.Visibility = SrcPaste.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (src == SrcDefault) LoadDefault();
        else if (src == SrcFile)
        {
            // 파일 선택 창은 Click 에서 연다 (이미 선택된 상태에서 다시 눌러도 열리게)
            if (_pickedFile != null) LoadFile(_pickedFile);
            else { SourceText.Text = ""; ResetRows(L.T("'파일...' 을 눌러 kubeconfig 파일을 고르세요.")); }
        }
        else
        {
            SourceText.Text = L.T("kubeconfig 내용을 아래에 붙여 넣으세요. 인증서를 파일 경로로 가리키면 파일로 가져오세요.");
            StartLoad(PasteWork());
            PasteBox.Focus();
        }
    }

    private void OnFileClick(object sender, RoutedEventArgs e)
    {
        var initial = _pickedFile != null ? Path.GetDirectoryName(_pickedFile) : Path.GetDirectoryName(HomeConfig);
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = L.T("kubeconfig 파일 선택"),
            Filter = L.T("모든 파일|*.*|YAML (*.yaml;*.yml)|*.yaml;*.yml"),
            InitialDirectory = initial != null && Directory.Exists(initial) ? initial : "",
        };
        if (dlg.ShowDialog(this) == true)
        {
            _pickedFile = dlg.FileName;
            ResetRows("");
            LoadFile(_pickedFile);
        }
        // 취소: 이미 고른 파일이 있으면 그대로, 없으면 이전 원본으로 돌아간다
        else if (_pickedFile == null) (_prevSource ?? SrcDefault).IsChecked = true;
    }

    private void OnPasteChanged(object sender, TextChangedEventArgs e)
    {
        if (SrcPaste.IsChecked != true) return;
        _pasteTimer.Stop();
        _pasteTimer.Start();
    }

    private Func<LoadResult> PasteWork()
    {
        var text = PasteBox.Text;
        return () => Build(text, null);
    }

    private void LoadDefault()
    {
        var paths = DefaultPaths;
        if (paths.Count == 1) { LoadFile(paths[0]); return; }
        SourceText.Text = string.Join(";  ", paths);
        // 없는 파일은 kubectl 처럼 건너뛰고 나머지를 합친다
        StartLoad(() => FromSource(KubeconfigImport.ReadDefault(paths)));
    }

    private void LoadFile(string path)
    {
        SourceText.Text = path;
        StartLoad(() => FromSource(KubeconfigImport.ReadPath(path)));
    }

    private static LoadResult FromSource(KubeconfigImport.Source s) => s.Error != null ? Empty(s.Error) : Build(s.Yaml, s.BaseDir);

    private static LoadResult Empty(string message) => new("", null, null, new(), message);

    /// <summary>읽어서 목록 줄을 만든다 (백그라운드 — 화면 개체는 건드리지 않는다)</summary>
    private static LoadResult Build(string yaml, string? baseDir)
    {
        if (string.IsNullOrWhiteSpace(yaml)) return new(yaml, baseDir, null, new(), L.T("가져올 context 가 없습니다."));
        try
        {
            var doc = Kubeconfig.Load(yaml);
            // 같은 API 서버 + context 이름이 이미 있으면 다시 가져오지 않는다 (KubeconfigImport.Describe)
            var rows = KubeconfigImport.Describe(doc, baseDir, K8sStore.All()).Select(i =>
            {
                var c = i.Context;
                return new Row
                {
                    Context = c,
                    Badge = AuthBadge(i),
                    Detail = string.Join("  ·  ", new[] { c.Server, c.Namespace is { Length: > 0 } ns ? "ns " + ns : null, c.User }.Where(s => !string.IsNullOrEmpty(s))),
                    Note = i.AlreadyImported ? L.T("이미 있음") : "",
                    CanImport = !i.AlreadyImported,
                    Selected = i.DefaultSelected,
                };
            }).ToList();
            return new(yaml, baseDir, doc, rows, rows.Count == 0 ? L.T("가져올 context 가 없습니다.") : null);
        }
        catch (FormatException ex) { return new(yaml, baseDir, null, new(), ex.Message); }
    }

    /// <summary>context 의 인증 방식 배지 (잘라 보지 않고 원본 사용자 항목으로 — 인증서 파일은 읽지 않는다)</summary>
    private static string AuthBadge(KubeconfigImport.ContextInfo i)
    {
        var badge = new K8sCluster { AuthKind = i.Auth }.AuthBadge;
        if (i.ExecCommand is { } cmd && !Kubectl.CommandExists(cmd)) badge += " · " + L.F("{0} 없음", Path.GetFileName(cmd));
        return badge;
    }

    /// <summary>읽기를 백그라운드로 돌리고, 마지막 요청의 결과만 목록에 반영한다</summary>
    private async void StartLoad(Func<LoadResult> work)
    {
        var gen = ++_loadGen;
        _pendingWork = work;
        LoadResult r;
        try { r = await Task.Run(work); }
        catch (Exception ex) { r = Empty(ex.Message); }
        if (_closed || gen != _loadGen) return;
        _pendingWork = null;
        Apply(r);
    }

    private void Apply(LoadResult r)
    {
        // 같은 원본을 다시 읽은 것이면(붙여넣기 수정) 체크 선택을 이름으로 유지한다
        if (_rows.Count > 0) _remembered = _rows.Where(x => x.Selected).Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        if (_remembered != null)
            foreach (var row in r.Rows) row.Selected = row.CanImport && _remembered.Contains(row.Name);
        _loaded = r;
        ShowList(r.Rows, r.Message);
    }

    /// <summary>원본을 바꿀 때: 목록·선택 기억을 비운다 (진행 중인 읽기 결과도 버린다)</summary>
    private void ResetRows(string message)
    {
        _loadGen++;
        _pendingWork = null;
        _remembered = null;
        _loaded = Empty(message);
        ShowList(new List<Row>(), message);
    }

    private void ShowList(List<Row> rows, string? empty)
    {
        _rows = rows;
        ContextList.ItemsSource = rows;
        EmptyText.Text = empty ?? "";
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        // 아직 반영되지 않은 붙여넣기·읽기가 있으면 지금 내용으로 마저 읽는다 (보이는 목록과 가져올 내용이 어긋나지 않게).
        // 체크 선택은 이름으로 유지되고, 새로 생긴 context 는 고르지 않은 상태라 아래에서 다시 고르라고 알린다
        if (_pasteTimer.IsEnabled && SrcPaste.IsChecked == true) { _pasteTimer.Stop(); _pendingWork = PasteWork(); }
        if (_pendingWork is { } work)
        {
            _loadGen++;
            _pendingWork = null;
            Apply(work());
        }
        var picked = _rows.Where(r => r.Selected && r.CanImport).ToList();
        if (picked.Count == 0) { ShowError(L.T("가져올 context 를 하나 이상 고르세요.")); return; }
        if (_loaded.Doc is not { } doc) return;
        var group = GroupBox.Text.Trim();
        var list = new List<K8sCluster>();
        foreach (var r in picked)
        {
            try { list.Add(KubeconfigImport.ToCluster(doc, r.Context, _loaded.BaseDir, group)); }
            catch (FormatException ex) { ShowError($"{r.Context.Name}: {ex.Message}"); return; }
        }
        _result = list;
        DialogResult = true;
    }

    private void ShowError(string msg)
    {
        ErrorText.Text = msg;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not TextBox) DragMove();
    }
}
