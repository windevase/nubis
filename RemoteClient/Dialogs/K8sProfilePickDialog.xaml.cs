using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace Nubis;

/// <summary>Kubernetes "클라우드에서 찾기" — 클러스터를 찾을 클라우드 프로필을 고르는 창</summary>
public partial class K8sProfilePickDialog : Window
{
    /// <summary>목록의 프로필 한 줄</summary>
    public sealed class Row : INotifyPropertyChanged
    {
        public required CloudProfile Profile { get; init; }
        public string Provider => Profile.ProviderText;
        public string Name => Profile.GroupName;
        public required string Detail { get; init; }
        /// <summary>화면 읽기·UI 자동화용 이름</summary>
        public string Label => $"[{Provider}] {Name}";
        public override string ToString() => Label;
        private bool _selected;
        public bool Selected
        {
            get => _selected;
            set { if (_selected == value) return; _selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>창의 결과: 고른 프로필 id · 고르지 않은 프로필의 클러스터를 지울지</summary>
    public sealed record Choice(HashSet<Guid> Selected, bool RemoveUnselected);

    private readonly List<Row> _rows;
    private Choice? _result;

    private K8sProfilePickDialog(IReadOnlyList<CloudProfile> profiles, IReadOnlyList<K8sCluster> clusters)
    {
        InitializeComponent();
        var counts = K8sProfilePick.ClusterCounts(clusters);
        var initial = K8sProfilePick.DefaultChecked(profiles);
        _rows = profiles
            .OrderBy(p => p.Provider).ThenBy(p => p.GroupName, StringComparer.CurrentCultureIgnoreCase)
            .Select(p =>
            {
                var n = counts.GetValueOrDefault(p.Id);
                var parts = new List<string>();
                if (!string.Equals(p.Name, p.GroupName, StringComparison.Ordinal)) parts.Add(p.Name);
                parts.Add(p.SyncClusters ? L.T("클러스터 동기화 켜짐") : L.T("클러스터 동기화 꺼짐"));
                parts.Add(L.F("클러스터 {0}개", n));
                return new Row { Profile = p, Detail = string.Join("  ·  ", parts), Selected = initial.Contains(p.Id) };
            }).ToList();
        foreach (var r in _rows) r.PropertyChanged += OnRowChanged;
        ProfileList.ItemsSource = _rows;
        UpdateCount();
        // 줄 이벤트는 창이 닫히면 끊는다 (프로필 객체는 오래 살지만 줄은 이 창의 것 — 서로 붙잡지 않게)
        Closed += OnClosedCleanup;
    }

    /// <summary>프로필을 고른다 (취소하면 null)</summary>
    public static Choice? Ask(Window owner, IReadOnlyList<CloudProfile> profiles, IReadOnlyList<K8sCluster> clusters)
    {
        var dlg = new K8sProfilePickDialog(profiles, clusters) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg._result : null;
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e) => UpdateCount();

    private void UpdateCount()
    {
        CountText.Text = L.F("프로필 {0}개 중 {1}개 선택", _rows.Count, _rows.Count(r => r.Selected));
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void OnSelectAll(object sender, RoutedEventArgs e) { foreach (var r in _rows) r.Selected = true; }
    private void OnSelectNone(object sender, RoutedEventArgs e) { foreach (var r in _rows) r.Selected = false; }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var selected = _rows.Where(r => r.Selected).Select(r => r.Profile.Id).ToHashSet();
        var remove = RemoveBox.IsChecked == true;
        // 아무것도 고르지 않고 끌 동기화·지울 클러스터도 없으면 할 일이 없다
        if (selected.Count == 0 && !remove && !_rows.Any(r => r.Profile.SyncClusters))
        {
            ErrorText.Text = L.T("클러스터를 찾을 프로필을 하나 이상 고르세요.");
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        _result = new Choice(selected, remove);
        DialogResult = true;
    }

    private void OnClosedCleanup(object? sender, EventArgs e)
    {
        Closed -= OnClosedCleanup;
        foreach (var r in _rows) r.PropertyChanged -= OnRowChanged;
        ProfileList.ItemsSource = null;
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase) DragMove();
    }
}
