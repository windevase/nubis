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

    private async void OnGroupSync(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is HostGroup { Profile: { } p }) await SyncOrCancelAsync(p);
    }

    private async void OnProfileSync(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CloudProfile p) await SyncOrCancelAsync(p);
    }

    /// <summary>동기화 버튼: 동기화 중이면 취소, 아니면 동기화</summary>
    private async Task SyncOrCancelAsync(CloudProfile p)
    {
        if (p.IsSyncing && _syncCancels.TryGetValue(p, out var cts))
        {
            cts.Cancel();
            return;
        }
        await SyncProfileAsync(p);
    }

    private async void OnSyncAll(object sender, RoutedEventArgs e) => await SyncAllAsync();

    private System.Windows.Threading.DispatcherTimer? _cloudSyncTimer;

    /// <summary>설정의 자동 동기화: 앱 시작 때 한 번 · N분마다 (Settings 에서 바꾸면 다시 부른다)</summary>
    private void StartCloudAutoSync(bool startup = false)
    {
        var s = SettingsStore.Current;
        _cloudSyncTimer?.Stop();
        // hosts.json 을 읽지 못해 빈 목록으로 시작했으면 자동 동기화가 클라우드 호스트만으로 원본을 덮어쓰지 않게 건너뛴다
        // (설정을 켜 둔 사용자가 왜 동기화가 안 되는지 알 수 있게 시작할 때 Cloud 화면에 알린다)
        if (HostStore.LoadFailed)
        {
            if (startup && (s.CloudSyncOnStart || s.CloudSyncIntervalMinutes > 0))
                foreach (var sec in _sections)
                    sec.Message = L.T("hosts.json 을 읽지 못해 이번 실행에서는 자동 동기화를 하지 않습니다. (동기화 버튼으로 직접 동기화할 수 있습니다)");
            return;
        }
        if (s.CloudSyncIntervalMinutes > 0)
        {
            _cloudSyncTimer ??= new System.Windows.Threading.DispatcherTimer();
            _cloudSyncTimer.Interval = TimeSpan.FromMinutes(Math.Max(5, s.CloudSyncIntervalMinutes));
            _cloudSyncTimer.Tick -= OnCloudSyncTick;
            _cloudSyncTimer.Tick += OnCloudSyncTick;
            _cloudSyncTimer.Start();
        }
        if (startup && s.CloudSyncOnStart) _ = SyncAllAsync();
    }

    private async void OnCloudSyncTick(object? sender, EventArgs e) => await SyncAllAsync();

    private bool _syncingAll;

    private async Task SyncAllAsync()
    {
        if (_profiles.Count == 0 || _syncingAll) return;
        _syncingAll = true;
        SyncAllButton.IsEnabled = false;
        SyncAllText.Text = L.T("동기화 중...");
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
            _syncingAll = false;
            SyncAllButton.IsEnabled = true;
            SyncAllText.Text = L.T("전체 동기화");
        }
    }

    // 진행 중인 프로필 동기화 (UI 스레드에서만 접근). 이미 동기화 중인 프로필을 다시 부르면 같은 작업을 기다린다
    private readonly Dictionary<CloudProfile, Task> _syncTasks = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<CloudProfile, System.Threading.CancellationTokenSource> _syncCancels = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// 프로필의 인스턴스를 조회해 호스트 목록에 반영 (조회는 백그라운드, 반영은 UI 스레드).
    /// 이미 동기화 중이면 바로 돌아가지 않고 진행 중인 동기화가 끝날 때까지 기다린다 (MCP cloud_sync 등이 이전 결과를 보고하지 않도록).
    /// cancel 은 이 호출이 새로 시작하는 동기화에만 연결된다 — 다른 쪽이 시작한 진행 중 동기화는 취소하지 않는다
    /// (기다리기만 그만두려면 호출하는 쪽에서 WaitAsync(cancel)).
    /// </summary>
    private Task SyncProfileAsync(CloudProfile p, System.Threading.CancellationToken cancel = default)
    {
        if (_syncTasks.TryGetValue(p, out var running)) return running;
        if (!_profiles.Contains(p)) return Task.CompletedTask;   // 이미 제거된 프로필 (MCP 가 목록을 잡은 뒤 제거 등)
        var task = SyncProfileCoreAsync(p, cancel);
        // 첫 await 전에 끝난 경우(예외 등)는 등록하지 않는다 — 끝난 작업이 남아 다음 동기화를 막지 않도록
        if (!task.IsCompleted) _syncTasks[p] = task;
        return task;
    }

    private async Task SyncProfileCoreAsync(CloudProfile p, System.Threading.CancellationToken cancel)
    {
        // 동기화 버튼(취소) · 프로필 제거 · 이 동기화를 시작한 MCP 요청의 취소 중 하나라도 오면 취소
        using var cts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(cancel);
        _syncCancels[p] = cts;
        p.IsSyncing = true;
        p.StatusText = "";
        try
        {
            var provider = CloudProviders.Get(p.Provider);
            var fetch = Task.Run(() => provider.FetchAsync(p, cts.Token));
            CloudFetchResult fetched;
            // 취소 토큰을 무시하는 SDK 호출도 있어, 취소하면 결과를 기다리지 않고 바로 끝낸다 (늦게 온 결과는 버림)
            try { fetched = await fetch.WaitAsync(cts.Token); }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                _ = fetch.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                if (_profiles.Contains(p))
                {
                    p.StatusText = L.T("동기화를 취소했습니다 (변경 없음)");
                    p.LastResult = "";
                }
                return;
            }
            if (!_profiles.Contains(p)) return;   // 조회 중 제거됨

            // 반영 전 주소: 주소가 바뀐 서버 수를 알려 주기 위해 (Id 가 겹치는 이상한 데이터에도 예외 없이)
            var beforeAddress = new Dictionary<Guid, string>();
            foreach (var h in _hosts.Where(h => h.CloudProfileId == p.Id)) beforeAddress.TryAdd(h.Id, h.Address);
            var removedHosts = new List<HostEntry>();
            // 실제 조회 실패가 있으면 지우지 않고, 권한 거부 리전만 있으면 그 리전 서버만 남긴다 (CloudSync.ApplyResult)
            var (added, _, removed) = CloudSync.ApplyResult(p, fetched, _hosts, removedHosts);
            var addressChanged = _hosts.Count(h => h.CloudProfileId == p.Id && beforeAddress.TryGetValue(h.Id, out var a) && a != h.Address);

            var parts = new List<string>();
            if (added + removed > 0) parts.Add(L.F("추가 {0} · 제거 {1}", added, removed));
            if (addressChanged > 0) parts.Add(L.F("주소 변경 {0}", addressChanged));
            if (fetched.IsPartial) parts.Add(L.T("일부 조회 실패"));
            p.LastResult = parts.Count > 0 ? string.Join(" · ", parts) : L.T("변경 없음");

            var status = new List<string>();
            // 실패한 리전·영역을 모두 보여 준다 (그룹 헤더에서는 잘리므로 툴팁으로 전체 확인)
            var errors = string.Join(" · ", fetched.Errors.Select(e => CloudProviders.FirstLine(e)));
            if (fetched.HasUnexpectedErrors)
                status.Add(L.F("일부 조회 실패 {0}건 — 이번엔 삭제하지 않음: {1}", fetched.Errors.Count, errors));
            else if (fetched.IsPartial)
                status.Add(L.F("권한이 없어 조회하지 못한 리전 {0}개 — 그 리전 서버는 그대로 둠: {1}", fetched.DeniedRegions.Count, errors));
            // 사라진 서버는 두 번 연속 안 보여야 지운다 (CloudSync 유예) — 첫 번째에는 남겨 두고 알린다
            if (p.PendingRemoval > 0)
                status.Add(L.F("클라우드 목록에 없는 서버 {0}개 — 다음 동기화에서도 없으면 제거", p.PendingRemoval));
            if (removedHosts.Count > 0)
            {
                status.Add(L.F("클라우드에서 사라져 제거된 호스트: {0}", NameList(removedHosts.Select(h => h.Label))));
                // 제거된 서버를 점프 호스트로 쓰던 호스트: 같은 인스턴스의 중복 호스트가 지워진 경우면 남은 호스트로 옮기고,
                // 서버 자체가 사라졌으면 점프 지정을 그대로 둔다 — 지우면 다음 접속이 점프 없이 직접 연결을 시도하므로,
                // 접속 때 "삭제된 점프 서버" 오류(AttachJump → JumpTunnel)가 나게 두고 여기서 다시 지정하라고 알린다
                var dangling = CloudJumpFix.Apply(_hosts, removedHosts);
                if (dangling.Count > 0)
                    status.Add(L.F("주의: 제거된 서버를 점프 호스트로 쓰던 호스트 {0}개 — 호스트 편집에서 점프 호스트를 다시 지정하세요: {1}",
                        dangling.Count, NameList(dangling.Select(h => h.DisplayName))));
            }
            p.StatusText = string.Join("  ·  ", status);
            SaveHosts();
            SaveProfiles();
        }
        catch (Exception ex)
        {
            p.StatusText = L.F("실패: {0}", CloudProviders.FirstLine(ex.Message));
            p.LastResult = "";   // 이전 동기화 결과가 이번 결과처럼 보이지 않도록
        }
        finally
        {
            _syncCancels.Remove(p);
            _syncTasks.Remove(p);
            p.IsSyncing = false;
            UpdateProfileCounts();
            RefreshHosts();
        }
    }

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
        return added;
    }

    private void OnProfileSettings(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CloudProfile p) EditProfile(p);
    }

    private void EditProfile(CloudProfile p)
    {
        var (oldSsh, oldRdp, oldPublic) = (p.EffectiveSshPort, p.EffectiveRdpPort, p.UsePublicIp);
        if (!ProfileDialog.Edit(this, p)) return;
        // 기본 접속 주소(사설/공인)를 바꾸면 기본값을 따르던 서버에 적용. 호스트별로 따로 고른 서버가 있으면 그것도 바꿀지 묻는다
        if (oldPublic != p.UsePublicIp)
        {
            var withPublic = _hosts.Where(h => h.CloudProfileId == p.Id && h.HasPublicIp).ToList();
            var custom = withPublic.Count(h => h.UsePublicIp != oldPublic);
            var all = custom == 0 || Dialog.Show(this,
                L.F("호스트별로 접속 주소를 따로 고른 서버가 {0}개 있습니다. 이 서버들도 새 기본값({1})으로 바꿀까요?\n(아니요: 기본값을 따르던 서버만 바꿉니다)",
                    custom, p.UsePublicIp ? L.T("공인 IP") : L.T("사설 IP")),
                L.T("프로필 설정"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
            foreach (var h in withPublic.Where(h => all || h.UsePublicIp == oldPublic)) h.UsePublicIp = p.UsePublicIp;
        }

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

        if (_syncCancels.TryGetValue(p, out var cts)) cts.Cancel();   // 진행 중인 동기화는 결과를 버린다
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
                Title = L.F("{0} 인증 파일 선택 ({1})", section.Title, section.Provider switch
                {
                    CloudProvider.Aws => "AWS config",
                    CloudProvider.Oci => "OCI config",
                    _ => "ADC JSON",
                }),
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
        section.Message = StartLogin(section.Provider, p);
    }

    /// <summary>그룹 우클릭 "CLI 로그인": 그 프로필로 로그인 (SSO 포털이 여러 개여도 프로필마다 로그인 가능)</summary>
    private void RunProviderLogin(CloudProfile p)
    {
        var msg = StartLogin(p.Provider, p);
        p.StatusText = msg;
        SectionOf(p).Message = msg;
    }

    /// <summary>
    /// UI 로그인 버튼 경로. 프로필 이름은 사용자 PC 의 설정 파일에서 온 값이라 cmd 메타 문자가 섞일 수 있으므로
    /// MCP cloud_login 과 같은 기준으로 검증한 뒤에만 실행한다
    /// </summary>
    private static string StartLogin(CloudProvider provider, CloudProfile? p)
    {
        var cmd = CloudProviders.Get(provider).LoginCommand(p);
        if (!McpCloudHelpers.IsSafeLogin(p?.Name, p?.TenantId, cmd))
            return L.F("프로필 이름 또는 로그인 명령에 명령줄에 넣을 수 없는 문자가 있어 실행하지 않습니다. 터미널에서 직접 로그인하세요: {0}", cmd);
        return LaunchLoginCommand(cmd, out _);
    }

    /// <summary>로그인 명령을 새 cmd 창에서 실행 (UI 로그인 버튼 · MCP cloud_login 공용). 화면에 보여 줄 안내 문구를 돌려준다</summary>
    private static string LaunchLoginCommand(string cmd, out bool started)
    {
        started = false;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/k {cmd}") { UseShellExecute = true });
            started = true;
            return L.F("새 창에서 '{0}' 를 실행했습니다. 로그인을 마친 뒤 동기화하세요.", cmd);
        }
        catch (Exception ex) { return L.F("실행 실패: {0}", ex.Message); }
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

/// <summary>동기화로 지워진 호스트를 점프 호스트로 쓰던 호스트 정리 (UI 와 무관, 테스트 대상)</summary>
internal static class CloudJumpFix
{
    /// <summary>
    /// removed 중 같은 프로필·인스턴스의 호스트가 목록(hosts)에 남아 있으면(중복 정리) 점프 지정을 그 호스트로 옮긴다.
    /// 옮길 곳이 없는(서버가 사라진) 점프 지정은 그대로 두고, 그런 호스트를 돌려준다 (화면에 다시 지정 안내).
    /// </summary>
    public static List<HostEntry> Apply(IReadOnlyList<HostEntry> hosts, IReadOnlyCollection<HostEntry> removed)
    {
        var dangling = new List<HostEntry>();
        if (removed.Count == 0) return dangling;
        var removedById = new Dictionary<Guid, HostEntry>();
        foreach (var r in removed) removedById.TryAdd(r.Id, r);
        foreach (var h in hosts)
        {
            if (h.JumpHostId is not { } j || !removedById.TryGetValue(j, out var gone)) continue;
            var keep = gone.CloudInstanceId == null ? null : hosts.FirstOrDefault(x =>
                x.CloudProfileId == gone.CloudProfileId && x.CloudInstanceId == gone.CloudInstanceId &&
                x.Protocol == Protocol.Ssh && !ReferenceEquals(x, h) && !removedById.ContainsKey(x.Id));
            if (keep != null) h.JumpHostId = keep.Id;
            else dangling.Add(h);
        }
        return dangling;
    }
}
