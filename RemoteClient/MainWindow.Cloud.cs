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
    // =================== 클라우드 ===================

    private void SaveProfiles()
    {
        try { CloudStore.Save(_profiles); }
        catch (Exception ex) { Dialog.Show(this, L.F("클라우드 프로필 저장 실패: {0}", ex.Message), "Nubis"); }
    }

    private void UpdateProfileCounts()
    {
        foreach (var p in _profiles)
        {
            var hosts = _hosts.Where(h => h.CloudProfileId == p.Id).ToList();
            p.HostCount = hosts.Count;
            p.RunningCount = hosts.Count(h => !h.IsStopped);
            p.NotifySummary();
        }
    }

    private CloudSection SectionOf(CloudProfile p) => _sections.First(s => s.Provider == p.Provider);

    private void OnGroupHeaderClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not HostGroup g) return;
        g.IsCollapsed = !g.IsCollapsed;
        var set = g.IsCloud ? _expandedCloud : _collapsedLocal;
        if (g.IsCloud ^ g.IsCollapsed) set.Add(g.Key); else set.Remove(g.Key);
        RebuildRows();
    }

    private async void OnGroupSync(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is HostGroup { Profile: { } p }) await SyncProfileAsync(p);
    }

    private async void OnProfileSync(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CloudProfile p) await SyncProfileAsync(p);
    }

    private async void OnSyncAll(object sender, RoutedEventArgs e)
    {
        if (_profiles.Count == 0) return;
        SyncAllButton.IsEnabled = false;
        SyncAllButton.Content = L.T("동기화 중...");
        try
        {
            using var gate = new System.Threading.SemaphoreSlim(4);
            await Task.WhenAll(_profiles.ToList().Select(async p =>
            {
                await gate.WaitAsync();
                try { await SyncProfileAsync(p); } finally { gate.Release(); }
            }));
        }
        finally
        {
            SyncAllButton.IsEnabled = true;
            SyncAllButton.Content = L.T("전체 동기화");
        }
    }

    /// <summary>프로필의 인스턴스를 조회해 호스트 목록에 반영 (조회는 백그라운드, 반영은 UI 스레드)</summary>
    private async Task SyncProfileAsync(CloudProfile p)
    {
        if (p.IsSyncing) return;
        p.IsSyncing = true;
        p.StatusText = "";
        try
        {
            var provider = CloudProviders.Get(p.Provider);
            var fetched = await Task.Run(() => provider.FetchAsync(p, System.Threading.CancellationToken.None));
            if (!_profiles.Contains(p)) return;   // 조회 중 제거됨
            var (added, updated, removed) = CloudSync.Apply(p, fetched.Items, _hosts, removeMissing: !fetched.IsPartial);
            p.LastResult = added + removed > 0 ? L.F("추가 {0} · 제거 {1}", added, removed) : L.T("변경 없음");
            if (fetched.IsPartial)
                p.StatusText = L.F("일부 조회 실패 {0}건 — 이번엔 삭제하지 않음: {1}", fetched.Errors.Count, CloudProviders.FirstLine(fetched.Errors[0]));
            SaveHosts();
            SaveProfiles();
        }
        catch (Exception ex)
        {
            p.StatusText = L.F("실패: {0}", CloudProviders.FirstLine(ex.Message));
        }
        finally
        {
            p.IsSyncing = false;
            UpdateProfileCounts();
            RefreshHosts();
        }
    }

    private async void OnAddProfiles(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CloudSection section || section.IsBusy) return;
        var button = (Button)sender;
        section.IsBusy = true;
        section.Message = "";
        List<CloudCandidate> candidates;
        try
        {
            candidates = await Task.Run(() => CloudProviders.Get(section.Provider).DiscoverAsync(System.Threading.CancellationToken.None));
        }
        catch (Exception ex)
        {
            section.Message = L.F("목록 조회 실패: {0}  (\"로그인\" 버튼으로 CLI 로그인 후 다시 시도)", CloudProviders.FirstLine(ex.Message));
            return;
        }
        finally { section.IsBusy = false; }

        var existing = section.Profiles.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fresh = candidates.Where(c => !existing.Contains(c.Name)).ToList();
        if (fresh.Count == 0)
        {
            section.Message = candidates.Count == 0 ? L.T("이 PC에서 찾은 프로필이 없습니다.") : L.T("모든 프로필이 이미 추가되어 있습니다.");
            return;
        }

        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var all = new MenuItem { Header = L.F("모두 추가 ({0})", fresh.Count), FontWeight = FontWeights.SemiBold };
        all.Click += async (_, _) => await AddProfilesAsync(section, fresh);
        menu.Items.Add(all);
        foreach (var c in fresh)
        {
            var label = c.DisplayName.Equals(c.Name, StringComparison.OrdinalIgnoreCase) ? c.DisplayName : $"{c.DisplayName}   ({c.Name})";
            var item = new MenuItem { Header = label };
            item.Click += async (_, _) => await AddProfilesAsync(section, new List<CloudCandidate> { c });
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private async Task AddProfilesAsync(CloudSection section, List<CloudCandidate> candidates)
    {
        var added = new List<CloudProfile>();
        foreach (var c in candidates)
        {
            var p = new CloudProfile { Provider = section.Provider, Name = c.Name, DisplayName = c.DisplayName, TenantId = c.TenantId };
            _profiles.Add(p);
            section.Profiles.Add(p);
            added.Add(p);
        }
        SaveProfiles();
        RefreshHosts();

        // 추가한 프로필은 바로 동기화
        using var gate = new System.Threading.SemaphoreSlim(4);
        await Task.WhenAll(added.Select(async p =>
        {
            await gate.WaitAsync();
            try { await SyncProfileAsync(p); } finally { gate.Release(); }
        }));
    }

    private void OnProfileSettings(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CloudProfile p) return;
        var (oldSsh, oldRdp, oldPublic) = (p.EffectiveSshPort, p.EffectiveRdpPort, p.UsePublicIp);
        if (!ProfileDialog.Edit(this, p)) return;
        // 기본 접속 주소(사설/공인)를 바꾸면 공인 IP 가 있는 서버 전체에 적용
        if (oldPublic != p.UsePublicIp)
            foreach (var h in _hosts.Where(h => h.CloudProfileId == p.Id && h.HasPublicIp)) h.UsePublicIp = p.UsePublicIp;

        // 기본 포트를 바꾸면 기본값을 쓰던 호스트도 함께 변경
        foreach (var h in _hosts.Where(h => h.CloudProfileId == p.Id))
        {
            if (h.Protocol == Protocol.Ssh && h.Port == oldSsh) h.Port = p.EffectiveSshPort;
            if (h.Protocol == Protocol.Rdp && h.Port == oldRdp) h.Port = p.EffectiveRdpPort;
        }
        SaveProfiles();
        SaveHosts();
        RefreshHosts();
        CloudSections.Items.Refresh();
    }

    private void OnProfileRemove(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CloudProfile p) return;
        var count = _hosts.Count(h => h.CloudProfileId == p.Id);
        if (Dialog.Show(this, L.F("[{0}] {1} 프로필과 동기화된 호스트 {2}개를 제거할까요?\n(클라우드의 서버에는 아무 영향이 없습니다)", p.ProviderText, p.GroupName, count),
                L.T("프로필 제거"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        _hosts.RemoveAll(h => h.CloudProfileId == p.Id);
        _profiles.Remove(p);
        SectionOf(p).Profiles.Remove(p);
        SaveHosts();
        SaveProfiles();
        RefreshHosts();
    }

    /// <summary>클라우드 인증 파일(폴더) 위치를 직접 선택</summary>
    private void OnChangeCloudPath(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CloudSection section) return;
        var current = CloudPaths.Effective(section.Provider);
        string? chosen;
        if (CloudPaths.IsFolder(section.Provider))
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = L.F("{0} 인증 폴더 선택 ({1})", section.Title, section.Provider == CloudProvider.Tencent ? L.T("*.credential 파일이 있는 폴더") : L.T("az CLI 설정 폴더")),
                InitialDirectory = Directory.Exists(current) ? current : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            chosen = dlg.ShowDialog(this) == true ? dlg.FolderName : null;
        }
        else
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = L.F("{0} 인증 파일 선택 ({1})", section.Title, section.Provider == CloudProvider.Aws ? "AWS config" : "ADC JSON"),
                Filter = section.Provider == CloudProvider.Gcp ? L.T("JSON (*.json)|*.json|모든 파일|*.*") : L.T("모든 파일|*.*"),
                InitialDirectory = Path.GetDirectoryName(current) is { } d && Directory.Exists(d) ? d : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                FileName = Path.GetFileName(current),
            };
            chosen = dlg.ShowDialog(this) == true ? dlg.FileName : null;
        }
        if (chosen == null) return;
        SetCloudPath(section, chosen);
    }

    private void OnResetCloudPath(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CloudSection section) SetCloudPath(section, null);
    }

    private void SetCloudPath(CloudSection section, string? path)
    {
        SettingsStore.Update(s =>
        {
            if (path == null) s.CloudPaths.Remove(section.Provider.ToString());
            else s.CloudPaths[section.Provider.ToString()] = path;
        });
        CloudPaths.Apply();
        section.NotifyPath();
        section.Message = path == null
            ? L.T("기본 경로로 되돌렸습니다. 프로필 추가·동기화부터 적용됩니다.")
            : L.T("경로를 바꿨습니다. 프로필 추가·동기화와 로그인 버튼에 바로 적용됩니다.");
    }

    /// <summary>CLI 로그인 명령을 새 콘솔 창에서 실행 (브라우저 인증은 사용자가 진행)</summary>
    private void OnProviderLogin(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CloudSection section) return;
        var p = section.Profiles.FirstOrDefault();
        if (section.Provider == CloudProvider.Aws && p == null)
        {
            section.Message = L.T("먼저 프로필을 추가하세요. (같은 SSO 포털을 쓰는 프로필은 한 번 로그인으로 모두 사용 가능)");
            return;
        }
        var cmd = CloudProviders.Get(section.Provider).LoginCommand(p);
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/k {cmd}") { UseShellExecute = true });
            section.Message = L.F("새 창에서 '{0}' 를 실행했습니다. 로그인을 마친 뒤 동기화하세요.", cmd);
        }
        catch (Exception ex) { section.Message = L.F("실행 실패: {0}", ex.Message); }
    }
}
