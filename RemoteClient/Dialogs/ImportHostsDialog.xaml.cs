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
/// 호스트 가져오기 창: 출처(엑셀·CSV / ~/.ssh/config / mRemoteNG / Ansible / Active Directory)에서 서버를 읽어 미리 보고,
/// 체크한 줄과 옵션을 돌려준다. 호스트 목록에 반영·저장은 호출한 쪽이 한다:
/// <code>
/// if (ImportHostsDialog.Ask(this, _hosts) is { } r)
/// {
///     var applied = r.ApplyTo(_hosts);   // 목록을 바꾼다
///     SaveHosts();
///     RefreshHosts();
///     ImportHostsDialog.ShowSummary(this, applied);
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

        try { AdDomainBox.Text = ActiveDirectoryImport.CurrentDomain() ?? ""; }
        catch { /* 도메인에 가입하지 않은 PC — 직접 입력 */ }
        AdPasswordBox.ToolTip = L.T("비밀번호");
        DataObject.AddPastingHandler(PasteBox, OnPasting);
        UpdateVaultButtons();
        UpdateGroupHint();
        UpdateSourcePanel();
        UpdateSummary();

        Loaded += (_, _) => PasteBox.Focus();
        Closed += (_, _) => { _closed = true; _adCts?.Cancel(); _replanTimer.Stop(); };
        _ready = true;
    }

    /// <summary>등록할 계획 (체크한 줄 + 옵션), 취소하면 null. 반영·저장은 호출한 쪽에서 (클래스 설명 참고)</summary>
    public static ImportPlanResult? Ask(Window owner, IReadOnlyList<HostEntry> existing)
    {
        var dlg = new ImportHostsDialog(existing) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg._final : null;
    }

    /// <summary>등록 결과 알림: "N개 추가 · M개 갱신 · K개 건너뜀" (+ 점프 서버 경고)</summary>
    public static void ShowSummary(Window owner, ImportApplyResult r) =>
        Dialog.Show(owner, r.Summary, L.T("호스트 가져오기"), MessageBoxButton.OK, MessageBoxImage.Information,
            r.Warnings.Count > 0 ? string.Join("\n", r.Warnings) : null);

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
            ClearValue(CursorProperty);
        }
    }

    // ===================== Active Directory =====================

    private void OnAdCurrentUserChanged(object sender, RoutedEventArgs e)
    {
        if (AdCredRow != null) AdCredRow.IsEnabled = AdCurrentUser.IsChecked != true;
    }

    /// <summary>Vault 계정으로 사용자명·비밀번호 칸을 채운다 (이번 조회에만 쓰고 저장하지 않음)</summary>
    private void OnAdPickVault(object sender, RoutedEventArgs e)
    {
        if (VaultPickerDialog.Pick(this, null, Protocol.Rdp, L.T("Active Directory 조회에 쓸 계정을 고르세요. 이번 조회에만 쓰고 저장하지 않습니다.")) is not { } id) return;
        if (VaultStore.Get(id) is not { } v) return;
        AdCurrentUser.IsChecked = false;
        AdUserBox.Text = v.Username;
        AdPasswordBox.Password = Secret.Unprotect(v.ProtectedPassword);
    }

    private async void OnFetchAd(object sender, RoutedEventArgs e)
    {
        // 조회 중에 다시 누르면 취소
        if (_adCts != null) { _adCts.Cancel(); return; }

        var domain = AdDomainBox.Text.Trim();
        if (domain.Length == 0) { ShowStatus(L.T("도메인을 입력하세요."), true); AdDomainBox.Focus(); return; }
        var current = AdCurrentUser.IsChecked == true;
        var user = AdUserBox.Text.Trim();
        if (!current && user.Length == 0) { ShowStatus(L.T("사용자를 입력하거나 '현재 Windows 로그인 사용' 을 켜세요."), true); AdUserBox.Focus(); return; }
        int? days = null;
        if (AdRecentCheck.IsChecked == true)
        {
            if (!int.TryParse(AdDaysBox.Text.Trim(), out var d) || d is < 1 or > 3650) { ShowStatus(L.T("최근 로그인 일수는 1~3650 사이 숫자로 입력하세요."), true); AdDaysBox.Focus(); return; }
            days = d;
        }
        var options = new AdImportOptions(domain, NullIfEmpty(AdServerBox.Text), current ? null : user, current ? null : AdPasswordBox.Password,
            current, NullIfEmpty(AdBaseBox.Text), AdServersOnly.IsChecked == true, AdSkipDisabled.IsChecked == true, days, AdResolveIp.IsChecked == true);

        using var cts = new CancellationTokenSource();
        _adCts = cts;
        _loadSeq++;   // 읽는 중이던 파일 결과가 나중에 와도 AD 결과를 덮지 않게
        AdFetchButton.Content = L.T("조회 취소");
        ShowStatus(L.F("{0} 에서 컴퓨터 목록을 가져오는 중...", domain), false);
        try
        {
            var result = await ActiveDirectoryImport.FetchAsync(options, cts.Token);
            if (!cts.IsCancellationRequested) SetSource(result, "Active Directory");
        }
        catch (OperationCanceledException) { ShowStatus(L.T("조회를 취소했습니다."), false); }
        catch (Exception ex) { ShowStatus(L.F("Active Directory 조회 실패: {0}", ex.Message), true); }
        finally
        {
            _adCts = null;
            AdFetchButton.Content = L.T("가져오기");
        }
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

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
        RowList.ItemsSource = _plan.Rows;
        _view = CollectionViewSource.GetDefaultView(_plan.Rows);
        _view.Filter = FilterRow;
        EmptyText.Text = _plan.Rows.Count == 0 ? L.T("가져올 서버가 없습니다.") : "";
        EmptyText.Visibility = _plan.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateSummary();
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ImportRow.IsSelected)) UpdateSummary();
    }

    private bool FilterRow(object o)
    {
        if (o is not ImportRow r) return false;
        if (FilterNew.IsChecked == true && r.Status != ImportStatus.New) return false;
        if (FilterExisting.IsChecked == true && r.Status != ImportStatus.Existing) return false;
        if (FilterProblem.IsChecked == true && r.Status is not (ImportStatus.Duplicate or ImportStatus.Invalid)) return false;
        var q = SearchBox.Text.Trim();
        if (q.Length == 0) return true;
        return Has(r.NameText) || Has(r.AddressText) || Has(r.GroupText) || Has(r.UserText) || Has(r.DetailText);
        bool Has(string s) => s.Contains(q, StringComparison.CurrentCultureIgnoreCase);
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        _view?.Refresh();
    }

    private void OnSelectAll(object sender, RoutedEventArgs e) => SetVisibleSelection(true);
    private void OnSelectNone(object sender, RoutedEventArgs e) => SetVisibleSelection(false);

    /// <summary>지금 필터로 보이는 줄만</summary>
    private void SetVisibleSelection(bool value)
    {
        if (_view == null) return;
        foreach (var row in _view.Cast<ImportRow>().ToList()) row.IsSelected = value;
    }

    private void UpdateSummary()
    {
        if (_plan == null)
        {
            SummaryText.Text = "";
            ImportButton.IsEnabled = false;
            return;
        }
        var rows = _plan.Rows;
        var newSel = rows.Count(r => r.IsSelected && r.Status == ImportStatus.New);
        var existSel = rows.Count(r => r.IsSelected && r.Status == ImportStatus.Existing);
        var update = ExistingUpdate.IsChecked == true;
        var problems = rows.Count(r => r.Status is ImportStatus.Duplicate or ImportStatus.Invalid);
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
        SshVaultButton.Content = VaultLabel(_sshVault);
        RdpVaultButton.Content = VaultLabel(_rdpVault);
        SshVaultClear.Visibility = _sshVault != null ? Visibility.Visible : Visibility.Collapsed;
        RdpVaultClear.Visibility = _rdpVault != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string VaultLabel(Guid? id) =>
        VaultStore.Get(id) is { } v ? $"{v.Name}  ({v.Username})" : L.T("지정 안 함 (기본 계정)");

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
