using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Nubis;

public partial class MainWindow
{
    // =================== Vault ===================

    private void RefreshVault()
    {
        var entries = VaultStore.All();
        foreach (var v in entries)
            v.UsedBy = _hosts.Count(h => h.VaultId == v.Id);
        VaultList.ItemsSource = entries;
        VaultEmpty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnVaultAdd(object sender, RoutedEventArgs e)
    {
        if (VaultEntryDialog.Create(this) is not { } entry) return;
        SaveVault(entry);
        RefreshVault();
    }

    private void SaveVault(VaultEntry entry)
    {
        try { VaultStore.Upsert(entry); }
        catch (Exception ex) { Dialog.Show(this, L.F("Vault 저장 실패: {0}", ex.Message), "Vault"); }
    }

    private void OnVaultEdit(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not VaultEntry entry) return;
        // 대화상자에서 취소해도 원본이 바뀌지 않게 사본을 고친 뒤 반영
        var copy = VaultStore.Get(entry.Id);
        if (copy == null) { RefreshVault(); return; }
        var backup = (copy.Name, copy.Username, copy.Note, copy.ProtectedPassword, copy.ProtectedKey, copy.ProtectedPassphrase, copy.PublicKey);
        if (VaultEntryDialog.Edit(this, copy)) SaveVault(copy);
        else (copy.Name, copy.Username, copy.Note, copy.ProtectedPassword, copy.ProtectedKey, copy.ProtectedPassphrase, copy.PublicKey) = backup;
        RefreshVault();
    }

    private void OnVaultDelete(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not VaultEntry entry) return;
        var users = _hosts.Where(h => h.VaultId == entry.Id).ToList();
        var msg = L.F("Vault 계정 '{0}' ({1}) 를 삭제할까요?", entry.Name, entry.MethodsText) +
                  (users.Count > 0
                      ? "\n\n" + L.F("이 계정을 쓰는 호스트 {0}개는 연결이 해제되어, 호스트에 직접 적은 값이 없으면 다음 접속 때 계정을 묻습니다:", users.Count) + "\n" +
                        string.Join("\n", users.Take(10).Select(h => "  " + h.DisplayName)) + (users.Count > 10 ? "\n  ..." : "")
                      : "");
        if (Dialog.Show(this, msg, L.T("Vault 삭제"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        foreach (var h in users) h.VaultId = null;
        try { VaultStore.Remove(entry.Id); }
        catch (Exception ex) { Dialog.Show(this, L.F("Vault 저장 실패: {0}", ex.Message), "Vault"); }
        if (users.Count > 0) { SaveHosts(); RefreshHosts(); }
        RefreshVault();
    }

    private void OnVaultCopyPublic(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not VaultEntry { PublicKey: { Length: > 0 } pub } entry) return;
        try
        {
            Clipboard.SetText($"{pub} {entry.Name.Replace(' ', '_')}");
            Dialog.Show(this, L.T("공개키를 복사했습니다.\n서버의 ~/.ssh/authorized_keys 에 한 줄로 붙여넣으세요."), "Vault");
        }
        catch { }
    }
}
