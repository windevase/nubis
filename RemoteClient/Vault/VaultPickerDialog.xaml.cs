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
        // 가상화 목록이라 지금 쓰는 계정이 아래쪽에 있으면 선택이 화면 밖에 숨는다 — 보이게 스크롤 (아직 그려지기 전이면 ListBox 가 미뤄서 처리)
        if (EntryList.SelectedItem != null) EntryList.ScrollIntoView(EntryList.SelectedItem);
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 계정 고르기 버튼 내용: "이름  (사용자명)" (사용자명이 없으면 이름만) 한 줄. 좁은 칸·긴 이름이면 … 로 줄이고 전체는 툴팁으로.
    /// 계정이 없으면 empty 문구
    /// </summary>
    public static System.Windows.Controls.TextBlock ButtonContent(Guid? id, string empty)
    {
        var text = VaultStore.Get(id) is { } v
            ? (string.IsNullOrWhiteSpace(v.Username) ? v.Name : $"{v.Name}  ({v.Username})")
            : empty;
        var tb = new System.Windows.Controls.TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = text };
        tb.ToolTipOpening += OnButtonContentToolTipOpening;
        return tb;
    }

    /// <summary>다 보이는(잘리지 않은) 글자면 같은 문구 툴팁을 띄우지 않는다</summary>
    private static void OnButtonContentToolTipOpening(object sender, System.Windows.Controls.ToolTipEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBlock t) return;
        var full = new System.Windows.Media.FormattedText(t.Text, System.Globalization.CultureInfo.CurrentUICulture, t.FlowDirection,
            new System.Windows.Media.Typeface(t.FontFamily, t.FontStyle, t.FontWeight, t.FontStretch), t.FontSize,
            System.Windows.Media.Brushes.Black, System.Windows.Media.VisualTreeHelper.GetDpi(t).PixelsPerDip);
        if (full.WidthIncludingTrailingWhitespace <= t.ActualWidth + 0.5) e.Handled = true;
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
