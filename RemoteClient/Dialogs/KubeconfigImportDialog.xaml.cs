using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

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

    private string _yaml = "";
    private string? _baseDir;
    private List<Row> _rows = new();
    private List<K8sCluster>? _result;

    private static string DefaultPath =>
        Environment.GetEnvironmentVariable("KUBECONFIG") is { Length: > 0 } env
            ? env.Split(';', StringSplitOptions.RemoveEmptyEntries)[0]
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kube", "config");

    private KubeconfigImportDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadFile(DefaultPath);
    }

    /// <summary>가져올 클러스터 (취소하면 null)</summary>
    public static List<K8sCluster>? Ask(Window owner)
    {
        var dlg = new KubeconfigImportDialog { Owner = owner };
        return dlg.ShowDialog() == true ? dlg._result : null;
    }

    private void OnSourceChanged(object sender, RoutedEventArgs e)
    {
        if (PasteBox == null) return;   // InitializeComponent 중
        PasteBox.Visibility = SrcPaste.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (SrcDefault.IsChecked == true) LoadFile(DefaultPath);
        else if (SrcFile.IsChecked == true)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = L.T("kubeconfig 파일 선택"),
                Filter = L.T("모든 파일|*.*|YAML (*.yaml;*.yml)|*.yaml;*.yml"),
                InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kube"),
            };
            if (dlg.ShowDialog(this) == true) LoadFile(dlg.FileName);
            else { SrcDefault.IsChecked = true; }
        }
        else
        {
            SourceText.Text = L.T("kubeconfig 내용을 아래에 붙여 넣으세요. 인증서를 파일 경로로 가리키면 파일로 가져오세요.");
            Parse(PasteBox.Text, null);
            PasteBox.Focus();
        }
    }

    private void OnPasteChanged(object sender, TextChangedEventArgs e)
    {
        if (SrcPaste.IsChecked == true) Parse(PasteBox.Text, null);
    }

    private void LoadFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) { SourceText.Text = path; ShowList(new List<Row>(), L.F("파일이 없습니다: {0}", path)); return; }
            if (info.Length > Kubeconfig.MaxChars) { ShowList(new List<Row>(), L.T("kubeconfig 가 너무 큽니다 (1MB 이하).")); return; }
            SourceText.Text = path;
            Parse(File.ReadAllText(path), Path.GetDirectoryName(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowList(new List<Row>(), ex.Message);
        }
    }

    private void Parse(string yaml, string? baseDir)
    {
        _yaml = yaml;
        _baseDir = baseDir;
        if (string.IsNullOrWhiteSpace(yaml)) { ShowList(new List<Row>(), L.T("가져올 context 가 없습니다.")); return; }
        try
        {
            var existing = K8sStore.All();
            var current = Kubeconfig.CurrentContext(yaml);
            var rows = Kubeconfig.ReadContexts(yaml).Select(c =>
            {
                // 같은 API 서버 + context 이름이 이미 있으면 다시 가져오지 않는다
                var dup = existing.Any(x => x.ContextName == c.Name && (x.Endpoint == (c.Server ?? "") || x.Endpoint.Length == 0));
                var auth = AuthOf(c.Name);
                return new Row
                {
                    Context = c,
                    Badge = auth,
                    Detail = string.Join("  ·  ", new[] { c.Server, c.Namespace is { Length: > 0 } ns ? "ns " + ns : null, c.User }.Where(s => !string.IsNullOrEmpty(s))),
                    Note = dup ? L.T("이미 있음") : "",
                    CanImport = !dup,
                    Selected = !dup && (c.Name == current || existing.Count == 0),
                };
            }).ToList();
            ShowList(rows, rows.Count == 0 ? L.T("가져올 context 가 없습니다.") : null);
        }
        catch (FormatException ex) { ShowList(new List<Row>(), ex.Message); }
    }

    /// <summary>context 의 인증 방식 배지 (잘라 보지 않고 원본 사용자 항목으로)</summary>
    private string AuthOf(string context)
    {
        try
        {
            var one = Kubeconfig.Extract(_yaml, context, _baseDir);
            var kind = Kubeconfig.DetectAuth(one);
            var badge = new K8sCluster { AuthKind = kind }.AuthBadge;
            if (Kubeconfig.ExecCommand(one) is { } cmd && !Kubectl.CommandExists(cmd)) badge += " · " + L.F("{0} 없음", Path.GetFileName(cmd));
            return badge;
        }
        catch (FormatException) { return "?"; }
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
        var picked = _rows.Where(r => r.Selected && r.CanImport).ToList();
        if (picked.Count == 0) { ShowError(L.T("가져올 context 를 하나 이상 고르세요.")); return; }
        var group = GroupBox.Text.Trim();
        var list = new List<K8sCluster>();
        foreach (var r in picked)
        {
            try
            {
                var one = Kubeconfig.Extract(_yaml, r.Context.Name, _baseDir);
                list.Add(new K8sCluster
                {
                    Name = r.Context.Name,
                    Group = group,
                    Source = K8sSource.Imported,
                    ContextName = r.Context.Name,
                    Endpoint = r.Context.Server ?? "",
                    AuthKind = Kubeconfig.DetectAuth(one),
                    Kubeconfig = one,
                });
            }
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
