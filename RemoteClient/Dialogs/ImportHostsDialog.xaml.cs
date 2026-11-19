using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace Nubis;

/// <summary>
/// 호스트 가져오기 창: 출처(엑셀·CSV / ~/.ssh/config / mRemoteNG / Ansible / Active Directory / .rdp 파일)에서 서버를 읽어 미리 보고,
/// 체크한 줄과 옵션을 돌려준다. 호스트 목록에 반영·저장은 호출한 쪽이 한다:
/// <code>
/// if (ImportHostsDialog.Ask(this, _hosts) is { } r)
/// {
///     var applied = r.ApplyTo(_hosts);   // 목록을 바꾼다
///     SaveHosts();
///     RefreshHosts();
///     if (ImportHostsDialog.ShowSummary(this, applied))   // "가져온 호스트 보기"
///         { /* applied.AddedIds · applied.UpdatedIds 의 호스트를 목록에서 선택해 보여 준다 */ }
/// }
/// </code>
/// </summary>
public partial class ImportHostsDialog : Window
{
    private readonly IReadOnlyList<HostEntry> _existing;
    private ImportResult? _source;
    private ImportPlan? _plan;
    private ICollectionView? _view;
    /// <summary>옵션을 바꿔 미리보기를 다시 만들 때 사용자가 바꾼 체크를 유지 (원본 개체 기준)</summary>
    private readonly Dictionary<ImportedHost, bool> _choices = new(ReferenceEqualityComparer.Instance);
    private Guid? _sshVault;
    private Guid? _rdpVault;
    private CancellationTokenSource? _adCts;
    private ImportPlanResult? _final;
    private readonly DispatcherTimer _replanTimer;
    /// <summary>검색어 입력이 잠깐 멈추면 필터를 다시 적용 (수천 줄에서 글자마다 전체를 다시 거르지 않게)</summary>
    private readonly DispatcherTimer _searchTimer;
    /// <summary>지금 적용한 검색어 (필터가 줄마다 TextBox 를 읽지 않게)</summary>
    private string _query = "";
    /// <summary>모두 선택 · 해제처럼 여러 줄을 한꺼번에 바꾸는 중 — 줄마다 요약을 다시 세지 않고 끝나고 한 번만</summary>
    private bool _bulk;
    private bool _ready;
    // 파일 출처별로 마지막에 적은 경로
    private string _sshPath = SshConfigImport.DefaultPath;
    private string _mrngPath = MRemoteNgImport.DefaultPath;
    private bool _fileIsSsh = true;
    /// <summary>불러오기 순번 — 늦게 끝난 앞 요청의 결과를 버린다</summary>
    private int _loadSeq;
    private bool _closed;

    private ImportHostsDialog(IReadOnlyList<HostEntry> existing)
    {
        InitializeComponent();
        _existing = existing;
        _replanTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _replanTimer.Tick += (_, _) => { _replanTimer.Stop(); Replan(); };
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _searchTimer.Tick += (_, _) => RefreshView();

        try { AdDomainBox.Text = ActiveDirectoryImport.CurrentDomain() ?? ""; }
        catch { /* 도메인에 가입하지 않은 PC — 직접 입력 */ }
        AdPasswordBox.ToolTip = L.T("비밀번호");
        System.Windows.Automation.AutomationProperties.SetName(AdPasswordBox, L.T("비밀번호"));   // 비밀번호 칸은 placeholder 가 없어 화면 낭독기 이름을 따로
        DataObject.AddPastingHandler(PasteBox, OnPasting);
        UpdateVaultButtons();
        UpdateGroupHint();
        UpdateSourcePanel();
        UpdateSummary();

        Loaded += (_, _) => PasteBox.Focus();
        Closed += (_, _) =>
        {
            _closed = true;
            _adCts?.Cancel();
            _replanTimer.Stop();
            _searchTimer.Stop();
            // 돌려준 계획(ImportPlanResult)의 줄들은 호출한 쪽이 계속 쓰므로, 줄 → 이 창으로 가는 참조(PropertyChanged 구독 ·
            // 기본 컬렉션 뷰의 Filter 대리자)를 끊어 닫힌 창 전체(미리보기 컨트롤 · 수천 줄 결과)가 계획과 함께 남지 않게 한다
            if (_plan != null)
                foreach (var row in _plan.Rows) row.PropertyChanged -= OnRowChanged;
            RowList.ItemsSource = null;
            WarningsList.ItemsSource = null;
            if (_view != null) _view.Filter = null;
        };
        _ready = true;
    }

    /// <summary>
    /// 등록할 계획 (체크한 줄 + 옵션), 취소하면 null. 반영·저장은 호출한 쪽에서 (클래스 설명 참고).
    /// rdpFiles 를 주면 .rdp 파일 출처를 고른 상태로 열고 바로 읽는다 (호스트 목록에 .rdp 파일을 끌어다 놓았을 때)
    /// </summary>
    public static ImportPlanResult? Ask(Window owner, IReadOnlyList<HostEntry> existing, IReadOnlyList<string>? rdpFiles = null)
    {
        var dlg = new ImportHostsDialog(existing) { Owner = owner };
        if (rdpFiles is { Count: > 0 }) dlg.StartWithRdp(rdpFiles);
        return dlg.ShowDialog() == true ? dlg._final : null;
    }

    /// <summary>
    /// 등록 결과 알림: "N개 추가 · M개 갱신 · K개 건너뜀" (+ 점프 서버 경고).
    /// 추가·갱신한 호스트가 있으면 "가져온 호스트 보기" 버튼도 보여 주고, 누르면 true —
    /// 호출한 쪽이 r.AddedIds · r.UpdatedIds 의 호스트를 목록에서 골라(선택) 보여 준다
    /// </summary>
    public static bool ShowSummary(Window owner, ImportApplyResult r)
    {
        var detail = r.Warnings.Count > 0 ? string.Join("\n", r.Warnings) : null;
        if (r.AddedIds.Count + r.UpdatedIds.Count == 0)
        {
            Dialog.Show(owner, r.Summary, L.T("호스트 가져오기"), MessageBoxButton.OK, MessageBoxImage.Information, detail);
            return false;
        }
        // 기본(Enter) 버튼은 확인 — 결과 창을 닫으려던 사용자의 선택 상태를 바꾸지 않게
        return Dialog.Choose(owner, r.Summary, L.T("호스트 가져오기"), L.T("확인"), L.T("가져온 호스트 보기"),
            icon: MessageBoxImage.Information, detail: detail) == 1;
    }

    // ===================== 출처 선택 =====================

    private void OnSourceChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        UpdateSourcePanel();
    }

    private void UpdateSourcePanel()
    {
        // 파일 출처를 오가면 각자 경로를 기억
        if (FilePanel.Visibility == Visibility.Visible)
        {
            if (_fileIsSsh) _sshPath = FilePathBox.Text;
            else _mrngPath = FilePathBox.Text;
        }
        TablePanel.Visibility = SrcTable.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        FilePanel.Visibility = SrcSsh.IsChecked == true || SrcMrng.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AnsiblePanel.Visibility = SrcAnsible.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AdPanel.Visibility = SrcAd.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        RdpPanel.Visibility = SrcRdp.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        if (SrcSsh.IsChecked == true)
        {
            _fileIsSsh = true;
            FilePathBox.Text = _sshPath;
            FileHint.Text = L.T("Host 마다 HostName · Port · User · ProxyJump 를 읽습니다. Include 도 따라가고, 와일드카드(*) Host 는 다른 Host 의 기본값으로만 씁니다.");
        }
        else if (SrcMrng.IsChecked == true)
        {
            _fileIsSsh = false;
            FilePathBox.Text = _mrngPath;
            FileHint.Text = L.T("mRemoteNG 연결 파일(confCons.xml)의 SSH · RDP 연결을 폴더 구조 그대로 그룹으로 가져옵니다. 저장된 비밀번호는 가져오지 않습니다.");
        }
    }

    // ===================== 읽기 =====================

    /// <summary>빈 칸에 붙여 넣으면 바로 미리보기</summary>
    private void OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (PasteBox.Text.Length > 0) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (PasteBox.Text.Trim().Length > 0) PreviewTable();
        }));
    }

    private void OnPreviewTable(object sender, RoutedEventArgs e) => PreviewTable();

    private void PreviewTable()
    {
        var text = PasteBox.Text;
        if (text.Trim().Length == 0) { ShowStatus(L.T("붙여 넣은 내용이 없습니다."), true); PasteBox.Focus(); return; }
        Load(() => TableImport.Parse(text), L.T("엑셀·CSV"));
    }

    private void OnClearPaste(object sender, RoutedEventArgs e)
    {
        PasteBox.Clear();
        PasteBox.Focus();
    }

    private void OnBrowseFile(object sender, RoutedEventArgs e)
    {
        var filter = _fileIsSsh
            ? L.T("모든 파일") + "|*.*"
            : L.T("mRemoteNG 연결 파일") + " (*.xml)|*.xml|" + L.T("모든 파일") + "|*.*";
        if (Browse(FilePathBox.Text, filter) is { } path) FilePathBox.Text = path;
    }

    private void OnLoadFile(object sender, RoutedEventArgs e)
    {
        var path = FilePathBox.Text.Trim().Trim('"');
        if (!CheckFile(path)) return;
        if (_fileIsSsh) Load(() => SshConfigImport.ParseFile(path), "~/.ssh/config");
        else Load(() => MRemoteNgImport.ParseFile(path), "mRemoteNG");
    }

    private void OnBrowseAnsible(object sender, RoutedEventArgs e)
    {
        var filter = L.T("인벤토리") + " (*.ini;*.yml;*.yaml;hosts)|*.ini;*.yml;*.yaml;hosts|" + L.T("모든 파일") + "|*.*";
        if (Browse(AnsiblePathBox.Text, filter) is { } path) AnsiblePathBox.Text = path;
    }

    private void OnLoadAnsible(object sender, RoutedEventArgs e)
    {
        var text = AnsibleTextBox.Text;
        if (text.Trim().Length > 0) { Load(() => AnsibleInventoryImport.Parse(text), L.T("Ansible 인벤토리")); return; }
        var path = AnsiblePathBox.Text.Trim().Trim('"');
        if (path.Length == 0) { ShowStatus(L.T("인벤토리 파일을 고르거나 내용을 붙여 넣으세요."), true); return; }
        if (!CheckFile(path)) return;
        Load(() => AnsibleInventoryImport.ParseFile(path), L.T("Ansible 인벤토리"));
    }

    private bool CheckFile(string path)
    {
        if (path.Length > 0 && File.Exists(path)) return true;
        ShowStatus(path.Length == 0 ? L.T("파일 경로를 입력하세요.") : L.F("파일을 찾을 수 없습니다: {0}", path), true);
        return false;
    }

    private string? Browse(string current, string filter)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = filter, CheckFileExists = true };
        try
        {
            var dir = Path.GetDirectoryName(current.Trim().Trim('"'));
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) dlg.InitialDirectory = dir;
        }
        catch (ArgumentException) { /* 경로에 쓸 수 없는 글자 — 기본 위치에서 연다 */ }
        return dlg.ShowDialog(this) == true ? dlg.FileName : null;
    }

    /// <summary>
    /// 파서 실행 — 큰 파일(수천 대 · Include · group_vars)에서 창이 멈추지 않게 백그라운드에서 읽는다.
    /// 읽는 중에 다른 출처를 다시 불러오면 앞의 결과는 버린다. 파일 읽기 오류 등은 상태 줄에 표시
    /// </summary>
    private async void Load(Func<ImportResult> parse, string sourceName)
    {
        CancelAd(null);   // AD 조회 중이었으면 멈춘다 (순번을 올리므로 아래 seq 보다 먼저)
        var seq = ++_loadSeq;
        ShowStatus(L.T("읽는 중..."), false);
        Cursor = Cursors.AppStarting;
        try
        {
            var result = await Task.Run(parse);
            if (seq != _loadSeq || _closed) return;
            SetSource(result, sourceName);
        }
        catch (Exception ex)
        {
            if (seq == _loadSeq && !_closed) ShowStatus(L.F("읽지 못했습니다: {0}", ex.Message), true);
        }
        finally
        {
            // 뒤에 시작한 불러오기가 아직 읽는 중이면 그쪽이 끝날 때 되돌린다
            if (seq == _loadSeq) ClearValue(CursorProperty);
        }
    }

    // ===================== 미리보기 =====================

    private void SetSource(ImportResult result, string sourceName)
    {
        _source = result;
        _choices.Clear();
        _plan = null;
        Replan();

        var msg = L.F("{0}: 서버 {1}대를 읽었습니다.", sourceName, result.Hosts.Count);
        if (result.Hosts.Count == 0) msg += " " + L.T("가져올 서버가 없습니다.");
        ShowStatus(msg, result.Hosts.Count == 0);

        WarningsList.ItemsSource = result.Warnings.ToList();
        WarningsTitle.Text = L.F("경고 {0}개", result.Warnings.Count);
        WarningsBox.Visibility = result.Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private ImportDefaults CurrentDefaults()
    {
        var group = GroupBox.Text.Trim();
        var d = ImportDefaults.FromSettings();
        return GroupOverrideMode.IsChecked == true
            ? d with { GroupOverride = group, SshVaultId = _sshVault, RdpVaultId = _rdpVault }
            : d with { GroupPrefix = group.Length > 0 ? group : null, SshVaultId = _sshVault, RdpVaultId = _rdpVault };
    }

    /// <summary>옵션(그룹 · Vault)이 바뀌면 다시 만든다. 사용자가 바꾼 체크는 유지</summary>
    private void Replan()
    {
        if (_source == null) { UpdateSummary(); return; }
        if (_plan != null)
            foreach (var row in _plan.Rows)
            {
                row.PropertyChanged -= OnRowChanged;
                if (row.IsSelectable) _choices[row.Source] = row.IsSelected;
            }

        _plan = ImportMerge.Plan(_source.Hosts, _existing, CurrentDefaults());
        foreach (var row in _plan.Rows)
        {
            if (_choices.TryGetValue(row.Source, out var chosen)) row.IsSelected = chosen;
            row.PropertyChanged += OnRowChanged;
        }
        _searchTimer.Stop();
        _query = SearchBox.Text.Trim();
        RowList.ItemsSource = _plan.Rows;
        _view = CollectionViewSource.GetDefaultView(_plan.Rows);
        _view.Filter = FilterRow;
        UpdateEmptyText();
        UpdateSummary();
    }

    /// <summary>검색어 · 필터를 지금 값으로 다시 적용</summary>
    private void RefreshView()
    {
        _searchTimer.Stop();
        _query = SearchBox.Text.Trim();
        if (_view == null) return;
        _view.Refresh();
        UpdateEmptyText();
    }

    /// <summary>빈 목록 안내: 읽은 서버가 없음 / 검색 · 필터에 맞는 줄이 없음 (그냥 빈 칸이면 목록이 고장 난 것처럼 보인다)</summary>
    private void UpdateEmptyText()
    {
        if (_plan == null || _view == null) return;
        var text = _plan.Rows.Count == 0 ? L.T("가져올 서버가 없습니다.")
            : _view.IsEmpty ? L.T("검색어나 필터에 맞는 줄이 없습니다.")
            : null;
        EmptyText.Text = text ?? "";
        EmptyText.Visibility = text != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_bulk && e.PropertyName == nameof(ImportRow.IsSelected)) UpdateSummary();
    }

    private bool FilterRow(object o)
    {
        if (o is not ImportRow r) return false;
        if (FilterNew.IsChecked == true && r.Status != ImportStatus.New) return false;
        if (FilterExisting.IsChecked == true && r.Status != ImportStatus.Existing) return false;
        if (FilterProblem.IsChecked == true && r.Status is not (ImportStatus.Duplicate or ImportStatus.Invalid)) return false;
        return _query.Length == 0 || r.SearchText.Contains(_query, StringComparison.CurrentCultureIgnoreCase);
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        RefreshView();
    }

    private void OnSelectAll(object sender, RoutedEventArgs e) => SetVisibleSelection(true);
    private void OnSelectNone(object sender, RoutedEventArgs e) => SetVisibleSelection(false);

    /// <summary>지금 필터로 보이는 줄만. 아직 반영되지 않은 검색어가 있으면 먼저 반영 (보이는 것과 다른 줄이 바뀌지 않게)</summary>
    private void SetVisibleSelection(bool value)
    {
        if (_searchTimer.IsEnabled) RefreshView();
        if (_view == null) return;
        _bulk = true;
        try
        {
            foreach (var row in _view.Cast<ImportRow>().ToList()) row.IsSelected = value;
        }
        finally { _bulk = false; }
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        if (_plan == null)
        {
            SummaryText.Text = "";
            ImportButton.IsEnabled = false;
            return;
        }
        // 한 번만 훑는다 (체크 하나 바꿀 때마다 불림)
        int newSel = 0, existSel = 0, problems = 0;
        foreach (var r in _plan.Rows)
        {
            if (r.Status is ImportStatus.Duplicate or ImportStatus.Invalid) problems++;
            else if (r.IsSelected) { if (r.Status == ImportStatus.New) newSel++; else existSel++; }
        }
        var update = ExistingUpdate.IsChecked == true;
        var text = L.F("새 호스트 {0}개 추가", newSel);
        if (existSel > 0) text += " · " + (update ? L.F("이미 있는 호스트 {0}개 갱신", existSel) : L.F("이미 있는 호스트 {0}개 건너뜀", existSel));
        if (problems > 0) text += " · " + L.F("중복·오류 {0}개 제외", problems);
        SummaryText.Text = text;
        ImportButton.IsEnabled = newSel > 0 || (update && existSel > 0);
    }

    // ===================== 옵션 =====================

    private void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        UpdateGroupHint();
        Replan();
    }

    private void OnGroupTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        UpdateGroupHint();
        _replanTimer.Stop();
        _replanTimer.Start();
    }

    private void UpdateGroupHint()
    {
        var override_ = GroupOverrideMode.IsChecked == true;
        GroupBox.Tag = override_ ? L.T("비우면 그룹 없음") : L.T("예: 가져옴 (비우면 붙이지 않음)");
        var g = GroupBox.Text.Trim();
        GroupHint.Text = override_
            ? L.T("가져온 그룹은 무시하고 모든 호스트를 이 그룹에 넣습니다.")
            : g.Length > 0 ? L.F("가져온 그룹 앞에 붙습니다 (예: {0} / web).", g) : L.T("가져온 그룹(폴더 · 인벤토리 그룹 · OU)을 그대로 씁니다.");
    }

    private void OnExistingModeChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        UpdateSummary();
    }

    private void OnPickSshVault(object sender, RoutedEventArgs e)
    {
        if (VaultPickerDialog.Pick(this, _sshVault, Protocol.Ssh, L.T("가져온 SSH 호스트가 이 계정으로 로그인합니다.")) is not { } id) return;
        _sshVault = id;
        UpdateVaultButtons();
        Replan();
    }

    private void OnPickRdpVault(object sender, RoutedEventArgs e)
    {
        if (VaultPickerDialog.Pick(this, _rdpVault, Protocol.Rdp, L.T("가져온 RDP 호스트가 이 계정으로 로그인합니다.")) is not { } id) return;
        _rdpVault = id;
        UpdateVaultButtons();
        Replan();
    }

    private void OnClearSshVault(object sender, RoutedEventArgs e)
    {
        _sshVault = null;
        UpdateVaultButtons();
        Replan();
    }

    private void OnClearRdpVault(object sender, RoutedEventArgs e)
    {
        _rdpVault = null;
        UpdateVaultButtons();
        Replan();
    }

    private void UpdateVaultButtons()
    {
        // 사용자명이 없는 계정은 "이름  ()" 가 아니라 이름만, 좁은 옵션 칸(250)에서 넘치면 … + 툴팁
        SshVaultButton.Content = VaultPickerDialog.ButtonContent(_sshVault, L.T("지정 안 함 (기본 계정)"));
        RdpVaultButton.Content = VaultPickerDialog.ButtonContent(_rdpVault, L.T("지정 안 함 (기본 계정)"));
        SshVaultClear.Visibility = _sshVault != null ? Visibility.Visible : Visibility.Collapsed;
        RdpVaultClear.Visibility = _rdpVault != null ? Visibility.Visible : Visibility.Collapsed;
    }

    // ===================== 등록 · 공통 =====================

    private void OnImport(object sender, RoutedEventArgs e)
    {
        if (_plan == null) return;
        // 입력 중이던 그룹 이름이 아직 반영되지 않았으면 지금 반영
        if (_replanTimer.IsEnabled) { _replanTimer.Stop(); Replan(); }
        var selected = _plan.Rows.Where(r => r.IsSelected).ToList();
        if (selected.Count == 0) return;
        _final = new ImportPlanResult(_plan, selected, ExistingUpdate.IsChecked == true);
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void ShowStatus(string message, bool error)
    {
        SourceStatus.Text = message;
        SourceStatus.Foreground = (System.Windows.Media.Brush)FindResource(error ? "Danger" : "SubText");
        SourceStatus.Visibility = Visibility.Visible;
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
