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
        var profiles = _profiles.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());   // 호스트마다 프로필 목록을 훑지 않게
        var used = _hosts.Select(h => h.EffectiveVaultId(h.CloudProfileId is { } pid ? profiles.GetValueOrDefault(pid) : null))
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

    /// <summary>저장하면 true. 실패하면 알리고 false (VaultStore 가 메모리 목록도 되돌린다)</summary>
    private bool SaveVault(VaultEntry entry)
    {
        try { VaultStore.Upsert(entry); return true; }
        catch (Exception ex) { Dialog.Show(this, L.F("Vault 저장 실패: {0}", ex.Message), "Vault", MessageBoxButton.OK, MessageBoxImage.Error); return false; }
    }

    private void OnVaultEdit(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not VaultEntry entry) return;
        // 저장된 개체가 아니라 사본을 고친 뒤 Upsert 로 바꿔 넣는다 — 창이 열린 동안 접속(다른 스레드)이 반쯤 바뀐 값을 읽지 않고,
        // 취소·저장 실패 때 원본이 그대로 남는다
        var copy = VaultStore.Get(entry.Id)?.Clone();
        if (copy == null) { RefreshVault(); return; }
        if (VaultEntryDialog.Edit(this, copy) && SaveVault(copy)) RefreshHosts();   // 카드의 사용자명·툴팁이 Vault 값을 보여 준다
        RefreshVault();
    }

    private void OnVaultDelete(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not VaultEntry entry) return;
        var users = _hosts.Where(h => h.VaultId == entry.Id).ToList();
        // 기본 계정(클라우드 프로필·설정)으로 이 계정을 쓰는 호스트도 센다 — 지우면 다른 계정으로 넘어가거나 접속 때 묻게 된다
        // (호스트마다 프로필 목록을 훑지 않게 사전으로)
        var profiles = _profiles.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
        var viaDefault = _hosts.Where(h => h.VaultId != entry.Id &&
                                           h.EffectiveVaultId(h.CloudProfileId is { } pid ? profiles.GetValueOrDefault(pid) : null) == entry.Id).ToList();
        var all = users.Concat(viaDefault).ToList();
        var msg = L.F("Vault 계정 '{0}' ({1}) 를 삭제할까요?", entry.Name, entry.MethodsText) +
                  (all.Count > 0
                      ? "\n\n" + L.F("이 계정을 쓰는 호스트 {0}개는 연결이 해제되어, 호스트에 직접 적은 값이 없으면 다음 접속 때 계정을 묻습니다:", all.Count) + "\n" +
                        string.Join("\n", all.Take(10).Select(h => "  " + h.DisplayName)) + (all.Count > 10 ? "\n  ..." : "")
                      : "");
        if (Dialog.Show(this, msg, L.T("Vault 삭제"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        // 먼저 지우고, 성공했을 때만 참조를 정리한다 (지우기에 실패했는데 호스트·기본 계정 연결만 끊기지 않게)
        try { VaultStore.Remove(entry.Id); }
        catch (Exception ex)
        {
            Dialog.Show(this, L.F("Vault 저장 실패: {0}", ex.Message), "Vault", MessageBoxButton.OK, MessageBoxImage.Error);
            RefreshVault();
            return;
        }
        foreach (var h in users) h.VaultId = null;
        // 지워진 계정을 가리키는 기본 계정 참조도 정리 (남아 있으면 지운 ID 를 계속 들고 있다)
        var profilesChanged = false;
        foreach (var p in _profiles)
        {
            if (p.SshVaultId == entry.Id) { p.SshVaultId = null; profilesChanged = true; }
            if (p.RdpVaultId == entry.Id) { p.RdpVaultId = null; profilesChanged = true; }
        }
        if (profilesChanged) SaveProfiles();
        // 기본 계정이었을 때만 설정을 쓴다 — 매번 쓰면 settings.json 을 다시 쓰고 열린 모든 터미널에 설정을 다시 보낸다
        var cur = SettingsStore.Current;
        if (cur.DefaultSshVaultId == entry.Id || cur.DefaultRdpVaultId == entry.Id)
            SettingsStore.Update(s =>
            {
                if (s.DefaultSshVaultId == entry.Id) s.DefaultSshVaultId = null;
                if (s.DefaultRdpVaultId == entry.Id) s.DefaultRdpVaultId = null;
            });
        if (users.Count > 0) SaveHosts();
        RefreshHosts();
        RefreshVault();
    }

    private void OnVaultCopyPublic(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not VaultEntry { PublicKey: { Length: > 0 } pub } entry) return;
        // 복사 결과는 토스트로 (확인 창을 매번 닫지 않아도 되게). 클립보드를 다른 프로그램이 잡고 있으면 실패를 알린다
        try { Clipboard.SetText($"{pub} {entry.Name.Replace(' ', '_')}"); }
        catch
        {
            Toast.Show(L.T("공개키를 클립보드에 복사하지 못했습니다. 잠시 뒤 다시 시도하세요."), ToastKind.Error);
            return;
        }
        Toast.Show(L.T("공개키를 복사했습니다.\n서버의 ~/.ssh/authorized_keys 에 한 줄로 붙여넣으세요."), ToastKind.Success);
    }
}

/// <summary>
/// Known Hosts 화면 표시용: 저장값 "[REJECTED ]형식 SHA256:..." 을 나눠 보여 준다 (UI 전용 — 저장값은 그대로).
/// ConverterParameter: "type" = 키 형식, "fp" = 지문(형식이 없는 값은 전체), "rejected" = 거부 배지 Visibility
/// </summary>
internal sealed class KnownHostPartConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        var v = value as string ?? "";
        var rejected = KnownHostStore.IsRejected(v);
        if (parameter as string == "rejected") return rejected ? Visibility.Visible : Visibility.Collapsed;
        var body = rejected ? v["REJECTED ".Length..] : v;
        var sp = body.IndexOf(' ');
        return parameter as string == "type"
            ? (sp > 0 ? body[..sp] : "")
            : (sp > 0 ? body[(sp + 1)..] : body);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}
