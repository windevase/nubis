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
        // 기본 계정(설정·프로필)으로 쓰는 호스트도 센다
        var used = _hosts.Select(h => h.EffectiveVaultId(_profiles.FirstOrDefault(p => p.Id == h.CloudProfileId)))
            .Where(id => id != null).GroupBy(id => id!.Value).ToDictionary(g => g.Key, g => g.Count());
        var s = SettingsStore.Current;
        foreach (var v in entries)
        {
            v.UsedBy = used.GetValueOrDefault(v.Id);
            v.DefaultBadge = (s.DefaultSshVaultId == v.Id, s.DefaultRdpVaultId == v.Id) switch
            {
                (true, true) => L.T("기본 (SSH · RDP)"),
                (true, false) => L.T("SSH 기본"),
                (false, true) => L.T("RDP 기본"),
                _ => "",
            };
        }
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
        if (VaultEntryDialog.Edit(this, copy)) { SaveVault(copy); RefreshHosts(); }   // 카드의 사용자명·툴팁이 Vault 값을 보여 준다
        else (copy.Name, copy.Username, copy.Note, copy.ProtectedPassword, copy.ProtectedKey, copy.ProtectedPassphrase, copy.PublicKey) = backup;
        RefreshVault();
    }

    private void OnVaultDelete(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not VaultEntry entry) return;
        var users = _hosts.Where(h => h.VaultId == entry.Id).ToList();
        // 기본 계정(클라우드 프로필·설정)으로 이 계정을 쓰는 호스트도 센다 — 지우면 다른 계정으로 넘어가거나 접속 때 묻게 된다
        var viaDefault = _hosts.Where(h => h.VaultId != entry.Id &&
                                           h.EffectiveVaultId(_profiles.FirstOrDefault(p => p.Id == h.CloudProfileId)) == entry.Id).ToList();
        var all = users.Concat(viaDefault).ToList();
        var msg = L.F("Vault 계정 '{0}' ({1}) 를 삭제할까요?", entry.Name, entry.MethodsText) +
                  (all.Count > 0
                      ? "\n\n" + L.F("이 계정을 쓰는 호스트 {0}개는 연결이 해제되어, 호스트에 직접 적은 값이 없으면 다음 접속 때 계정을 묻습니다:", all.Count) + "\n" +
                        string.Join("\n", all.Take(10).Select(h => "  " + h.DisplayName)) + (all.Count > 10 ? "\n  ..." : "")
                      : "");
        if (Dialog.Show(this, msg, L.T("Vault 삭제"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        foreach (var h in users) h.VaultId = null;
        // 지워진 계정을 가리키는 기본 계정 참조도 정리 (남아 있으면 지운 ID 를 계속 들고 있다)
        var profilesChanged = false;
        foreach (var p in _profiles)
        {
            if (p.SshVaultId == entry.Id) { p.SshVaultId = null; profilesChanged = true; }
            if (p.RdpVaultId == entry.Id) { p.RdpVaultId = null; profilesChanged = true; }
        }
        if (profilesChanged) SaveProfiles();
        SettingsStore.Update(s =>
        {
            if (s.DefaultSshVaultId == entry.Id) s.DefaultSshVaultId = null;
            if (s.DefaultRdpVaultId == entry.Id) s.DefaultRdpVaultId = null;
        });
        try { VaultStore.Remove(entry.Id); }
        catch (Exception ex) { Dialog.Show(this, L.F("Vault 저장 실패: {0}", ex.Message), "Vault"); }
        if (users.Count > 0) SaveHosts();
        RefreshHosts();
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
