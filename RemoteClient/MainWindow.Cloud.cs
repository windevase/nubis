using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Nubis;

// Cloud 화면: 프로필 저장 · 서버 수 · 그룹 접힘 · 프로필 추가 · 설정 · 제거 (동기화는 MainWindow.CloudSync.cs, 인증 경로 · 로그인은 MainWindow.CloudAuth.cs)
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
        // 프로필마다 전체 호스트를 다시 훑지 않게 한 번에 센다 (동기화가 끝날 때마다 불린다)
        var counts = new Dictionary<Guid, (int total, int running)>();
        foreach (var h in _hosts)
        {
            if (h.CloudProfileId is not { } pid) continue;
            var c = counts.GetValueOrDefault(pid);
            counts[pid] = (c.total + 1, c.running + (h.IsStopped ? 0 : 1));
        }
        foreach (var p in _profiles)
        {
            var c = counts.GetValueOrDefault(p.Id);
            p.HostCount = c.total;
            p.RunningCount = c.running;
            p.NotifySummary();
        }
    }

    private CloudSection SectionOf(CloudProfile p) => _sections.First(s => s.Provider == p.Provider);

    private void OnGroupHeaderClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not HostGroup g) return;
        if (e.ChangedButton != MouseButton.Left) return;
        SetGroupCollapsed(g, !g.IsCollapsed);
        RebuildRows();
        SaveGroupState();
    }

    private void SetGroupCollapsed(HostGroup g, bool collapsed)
    {
        g.IsCollapsed = collapsed;
        var set = g.IsCloud ? _expandedCloud : _collapsedLocal;
        if (g.IsCloud ^ g.IsCollapsed) set.Add(g.Key); else set.Remove(g.Key);
    }

    /// <summary>그룹 접힘 상태를 설정에 저장 (다시 시작해도 유지)</summary>
    private void SaveGroupState() => SettingsStore.UpdateState(s =>
    {
        s.CollapsedGroups = _collapsedLocal.ToList();
        s.ExpandedCloudGroups = _expandedCloud.ToList();
    });

    /// <summary>이름 목록을 한 줄로 (많으면 앞 5개 + "외 N개")</summary>
    private static string NameList(IEnumerable<string> names)
    {
        var list = names.ToList();
        return list.Count <= 5 ? string.Join(", ", list) : string.Join(", ", list.Take(5)) + " " + L.F("외 {0}개", list.Count - 5);
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

    /// <summary>후보를 프로필로 추가하고 바로 동기화한다 (UI 의 프로필 추가 메뉴 · MCP cloud_add_profile 공용). 추가한 프로필을 돌려준다</summary>
    private async Task<List<CloudProfile>> AddProfilesAsync(CloudSection section, List<CloudCandidate> candidates)
    {
        var added = new List<CloudProfile>();
        // 메뉴를 띄워 둔 사이 같은 프로필이 다른 경로(MCP cloud_add_profile · 다시 연 메뉴)로 이미 추가됐을 수 있다 —
        // 같은 이름을 또 추가하면 같은 서버가 두 그룹에 겹쳐 동기화되므로 건너뛴다
        var names = section.Profiles.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var c in candidates)
        {
            if (!names.Add(c.Name)) continue;
            var p = new CloudProfile { Provider = section.Provider, Name = c.Name, DisplayName = c.DisplayName, TenantId = c.TenantId };
            _profiles.Add(p);
            section.Profiles.Add(p);
            added.Add(p);
        }
        if (added.Count == 0) return added;
        SaveProfiles();
        RefreshHosts();

        // 추가한 프로필은 바로 동기화
        using var gate = new System.Threading.SemaphoreSlim(4);
        await Task.WhenAll(added.Select(async p =>
        {
            await gate.WaitAsync();
            try { await SyncProfileAsync(p); } finally { gate.Release(); }
        }));
        return added;
    }

    private void OnProfileSettings(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CloudProfile p) EditProfile(p);
    }

    /// <summary>프로필 설정 창이 열려 있는 프로필 (UI 스레드 전용)</summary>
    private CloudProfile? _editingProfile;

    private void EditProfile(CloudProfile p)
    {
        var before = CloudProfileEdit.Snapshot(p);
        // 설정 창이 열려 있는 동안 MCP cloud_update_profile 이 같은 프로필을 바꾸면 창의 OK 가 그 변경을 덮어쓰고
        // 호스트 포트·주소 반영 기준(before)도 어긋나므로, 창이 열려 있는 프로필은 MCP 가 바꾸지 않는다 (_editingProfile 확인)
        _editingProfile = p;
        bool ok;
        try { ok = ProfileDialog.Edit(this, p); }
        finally { _editingProfile = null; }
        if (!ok) return;
        // 기본 접속 주소(사설/공인)를 바꾸면 기본값을 따르던 서버에 적용. 호스트별로 따로 고른 서버가 있으면 그것도 바꿀지 묻는다
        var overrideCustom = true;
        if (before.UsePublicIp != p.UsePublicIp)
        {
            var custom = CloudProfileEdit.CustomAddressCount(_hosts, p, before.UsePublicIp);
            overrideCustom = custom == 0 || Dialog.Show(this,
                L.F("호스트별로 접속 주소를 따로 고른 서버가 {0}개 있습니다. 이 서버들도 새 기본값({1})으로 바꿀까요?\n(아니요: 기본값을 따르던 서버만 바꿉니다)",
                    custom, p.UsePublicIp ? L.T("공인 IP") : L.T("사설 IP")),
                L.T("프로필 설정"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        }
        // 기본 주소·포트 변경을 호스트에 반영 (MCP cloud_update_profile 과 공용)
        CloudProfileEdit.ApplyCascade(_hosts, p, before, overrideCustom);
        FinishProfileEdit(p, before);
    }

    /// <summary>
    /// 프로필 설정 변경 마무리 (UI 프로필 설정 · MCP cloud_update_profile 공용): 저장 · 화면 갱신.
    /// "중지된 서버 포함" 을 켜면 지금까지 빠져 있던 중지 서버는 다음 동기화 때에야 생기므로 바로 동기화한다
    /// (끄는 쪽은 기존 호스트를 지우지 않으므로 동기화할 필요 없음). 동기화를 시작했으면 true
    /// </summary>
    private bool FinishProfileEdit(CloudProfile p, CloudProfileEdit.Before before)
    {
        SaveProfiles();
        SaveHosts();
        RefreshHosts();
        CloudSections.Items.Refresh();
        if (!CloudProfileEdit.NeedsSyncAfter(before, p) || HostStore.LoadFailed) return false;
        _ = SyncProfileAsync(p);
        return true;
    }

    private void OnProfileRemove(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CloudProfile p) return;
        var count = _hosts.Count(h => h.CloudProfileId == p.Id);
        var message = L.F("[{0}] {1} 프로필과 동기화된 호스트 {2}개를 제거할까요?\n(클라우드의 서버에는 아무 영향이 없습니다)", p.ProviderText, p.GroupName, count);
        // 다른 그룹 호스트가 이 프로필의 서버를 점프 호스트로 쓰고 있으면, 제거 후 그 호스트들이 "삭제된 점프 서버" 로 접속하지 못하므로 미리 알린다
        var dependents = CloudProfileEdit.JumpDependents(_hosts, p);
        if (dependents.Count > 0)
            message += "\n\n" + L.F("주의: 이 프로필의 서버를 점프 호스트로 쓰는 다른 호스트 {0}개가 있습니다 — 제거하면 점프 호스트를 다시 지정해야 접속할 수 있습니다: {1}",
                dependents.Count, NameList(dependents.Select(h => h.DisplayName)));
        var k8sCount = K8sStore.All().Count(c => c.CloudProfileId == p.Id);
        if (k8sCount > 0) message += "\n" + L.F("이 프로필에서 가져온 쿠버네티스 클러스터 {0}개도 목록에서 제거합니다.", k8sCount);
        if (Dialog.Show(this, message, L.T("프로필 제거"), MessageBoxButton.YesNo,
                dependents.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        RemoveProfile(p);
    }

    /// <summary>
    /// 프로필과 동기화된 호스트·쿠버네티스 클러스터를 제거 (UI 프로필 제거 · MCP cloud_remove_profile 공용). 지운 호스트 수를 돌려준다.
    /// 클러스터를 남기면 프로필 없는 고아 카드가 되고, 같은 프로필을 다시 추가하면 같은 클러스터 카드가 둘 생긴다
    /// </summary>
    private int RemoveProfile(CloudProfile p)
    {
        if (_syncCancels.TryGetValue(p, out var cts)) cts.Cancel();   // 진행 중인 동기화는 결과를 버린다
        var removed = _hosts.RemoveAll(h => h.CloudProfileId == p.Id);
        _profiles.Remove(p);
        SectionOf(p).Profiles.Remove(p);
        SaveHosts();
        SaveProfiles();
        RefreshHosts();
        RemoveProfileClusters(p.Id);
        return removed;
    }

    /// <summary>프로필의 클러스터를 저장소에서 지운다. 저장에 실패하면 알린다 (클러스터는 남으므로 Kubernetes 페이지에서 직접 지울 수 있다)</summary>
    private void RemoveProfileClusters(Guid profileId)
    {
        var all = K8sStore.All();
        if (!all.Any(c => c.CloudProfileId == profileId)) return;
        try { K8sStore.ReplaceAll(all.Where(c => c.CloudProfileId != profileId).ToList()); }
        catch (Exception ex)
        {
            Dialog.Show(this, L.F("쿠버네티스 클러스터 목록을 저장하지 못했습니다: {0}", ex.Message), L.T("프로필 제거"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        // 지운 클러스터의 연결 상태·인증 캐시는 RefreshK8s 에서 정리된다 (페이지를 열 때도 다시 그린다)
        if (K8sPage.Visibility == Visibility.Visible) RefreshK8s();
    }
}

/// <summary>
/// Cloud 화면 프로필 행 표시용 (UI 전용). values[0] = CloudProfile, 그 뒤 값은 변경 알림만 받는다 (SummaryText 가
/// 서버 수 · 동기화 시각 · 결과가 바뀔 때 알려지므로 함께 묶어 둔다).
/// ConverterParameter: "total" = 서버 수 알약, "running" = 실행 중 알약, "synced" = 한 번이라도 동기화했으면 Visible, 그 밖 = 동기화 시각 · 결과 줄
/// </summary>
internal sealed class CloudProfileViewConverter : System.Windows.Data.IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        var mode = parameter as string;
        if (values.Length == 0 || values[0] is not CloudProfile p)
            return mode == "synced" ? Visibility.Collapsed : "";
        return mode switch
        {
            "synced" => p.LastSync == null ? Visibility.Collapsed : Visibility.Visible,
            "total" => L.F("{0}대", p.HostCount),
            "running" => L.F("실행 {0}", p.RunningCount),
            _ => p.LastSync is not { } at
                ? L.T("아직 동기화하지 않음")
                : L.F("{0:MM-dd HH:mm} 동기화", at) + (p.LastResult.Length > 0 ? "  ·  " + p.LastResult : ""),
        };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}
