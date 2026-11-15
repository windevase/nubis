using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace Nubis;

/// <summary>호스트 편집에서 Vault 계정을 고르는 창. 여기서 새 계정을 만들 수도 있다.</summary>
public partial class VaultPickerDialog : Window
{
    private VaultPickerDialog(Guid? current, Protocol protocol, string? subtitle)
    {
        InitializeComponent();
        TitleText.Text = L.T("Vault 계정 선택");
        SubText.Text = subtitle ?? (protocol == Protocol.Rdp
            ? L.T("이 호스트에 로그인할 계정을 고르세요. 계정의 사용자명·비밀번호·SSH 키로 접속하며, 호스트에 직접 적은 값보다 우선합니다 (해제하면 다시 그 값을 씁니다). RDP 는 비밀번호만 씁니다.")
            : L.T("이 호스트에 로그인할 계정을 고르세요. 계정의 사용자명·비밀번호·SSH 키로 접속하며, 호스트에 직접 적은 값보다 우선합니다 (해제하면 다시 그 값을 씁니다)."));
        Reload(current);
        Loaded += (_, _) => FilterBox.Focus();
    }

    private void Reload(Guid? select)
    {
        var q = FilterBox.Text.Trim();
        var items = VaultStore.All().Where(e => q.Length == 0 ||
            e.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || e.Username.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            e.Note.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        EntryList.ItemsSource = items;
        // 지금 쓰는 계정만 미리 고른다. 아무것도 고르지 않은 채 Enter 를 누르면 첫 계정이 지정되던 문제 —
        // 검색어를 입력한 경우에만 첫 결과를 골라 "검색 → Enter" 로 바로 고를 수 있게 한다
        EntryList.SelectedItem = items.FirstOrDefault(e => e.Id == select) ?? (q.Length > 0 ? items.FirstOrDefault() : null);
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>allowClear 로 연 창에서 "지정 해제" 를 누르면 Pick 이 돌려주는 값 (실제 계정 Id 는 Guid.Empty 가 아니다)</summary>
    public static readonly Guid Cleared = Guid.Empty;

    // "지정 해제" 로 닫았는지
    private bool _cleared;

    /// <summary>
    /// 고른 계정 Id, 취소하면 null.
    /// allowClear 이고 지금 지정된 계정(current)이 있으면 "지정 해제" 버튼을 보이며, 누르면 <see cref="Cleared"/> 를 돌려준다
    /// (allowClear 를 넘기지 않는 호출은 예전처럼 계정 Id 또는 null 만 받는다).
    /// </summary>
    public static Guid? Pick(Window owner, Guid? current, Protocol protocol, string? subtitle = null, bool allowClear = false)
    {
        var dlg = new VaultPickerDialog(current, protocol, subtitle) { Owner = owner };
        if (allowClear && current != null) dlg.ClearButton.Visibility = Visibility.Visible;
        if (dlg.ShowDialog() != true) return null;
        if (dlg._cleared) return Cleared;
        return dlg.EntryList.SelectedItem is VaultEntry e ? e.Id : null;
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _cleared = true;
        DialogResult = true;
    }

    private void OnFilterChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        Reload((EntryList.SelectedItem as VaultEntry)?.Id);

    /// <summary>검색 칸에서 ↑↓ 로 목록 이동, Enter 로 선택</summary>
    private void OnFilterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Down or Key.Up && EntryList.Items.Count > 0)
        {
            var i = Math.Clamp(EntryList.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, EntryList.Items.Count - 1);
            EntryList.SelectedIndex = i;
            EntryList.ScrollIntoView(EntryList.SelectedItem);
            e.Handled = true;
        }
    }

    private void OnCreate(object sender, RoutedEventArgs e)
    {
        if (VaultEntryDialog.Create(this) is not { } entry) return;
        try { VaultStore.Upsert(entry); }
        catch (Exception ex)
        {
            Dialog.Show(this, L.F("Vault 저장 실패: {0}", ex.Message), "Vault", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        FilterBox.Text = "";   // 새 계정이 검색어에 걸러져 안 보이는 일이 없게
        Reload(entry.Id);
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (EntryList.SelectedItem is VaultEntry) DialogResult = true;
    }

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is VaultEntry) DialogResult = true;
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
