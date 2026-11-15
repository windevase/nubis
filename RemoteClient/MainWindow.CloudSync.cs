using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace Nubis;

// Cloud 화면: 클라우드 동기화 (버튼 · 자동 동기화 · 전체 동기화 · 프로필 동기화와 취소)
public partial class MainWindow
{
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
        // 리전·영역을 모두 도는 조회는 수 초~수십 초 걸리므로 진행 중임을 상태 줄(프로필 행 · 그룹 헤더)에 보여 준다
        p.StatusText = L.T("서버 목록을 조회하는 중...");
        try
        {
            var provider = CloudProviders.Get(p.Provider);
            var fetch = Task.Run(() => provider.FetchAsync(p, cts.Token));
            CloudFetchResult fetched;
            // 취소 토큰을 무시하는 SDK 호출도 있어, 취소하면 결과를 기다리지 않고 바로 끝낸다 (늦게 온 결과는 버림)
            // 조회가 끝나 반영을 기다리는 사이(디스패처 대기열)에 취소가 와도 결과를 버린다 — 취소 후 반영되지 않게.
            // 아래 반영 부분은 await 없이 UI 스레드에서 한 번에 끝나므로 취소로 반쯤 반영되는 일은 없다
            try
            {
                fetched = await fetch.WaitAsync(cts.Token);
                cts.Token.ThrowIfCancellationRequested();
            }
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
