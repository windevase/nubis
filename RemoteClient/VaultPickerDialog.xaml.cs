using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace Nubis;

/// <summary>호스트 편집에서 Vault 계정을 고르는 창. 여기서 새 계정을 만들 수도 있다.</summary>
public partial class VaultPickerDialog : Window
{
    private VaultPickerDialog(Guid? current, Protocol protocol)
    {
        InitializeComponent();
        TitleText.Text = "Vault 계정 선택";
        SubText.Text = "이 호스트에 로그인할 계정을 고르세요. 계정의 사용자명·비밀번호·SSH 키로 접속하며, 호스트에 직접 적은 값이 있으면 그 값이 우선합니다." +
                       (protocol == Protocol.Rdp ? " RDP 는 비밀번호만 씁니다." : "");
        Reload(current);
    }

    private void Reload(Guid? select)
    {
        var items = VaultStore.All();
        EntryList.ItemsSource = items;
        EntryList.SelectedItem = items.FirstOrDefault(e => e.Id == select) ?? items.FirstOrDefault();
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>고른 계정 Id, 취소하면 null</summary>
    public static Guid? Pick(Window owner, Guid? current, Protocol protocol)
    {
        var dlg = new VaultPickerDialog(current, protocol) { Owner = owner };
        return dlg.ShowDialog() == true && dlg.EntryList.SelectedItem is VaultEntry e ? e.Id : null;
    }

    private void OnCreate(object sender, RoutedEventArgs e)
    {
        if (VaultEntryDialog.Create(this) is not { } entry) return;
        VaultStore.Upsert(entry);
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
