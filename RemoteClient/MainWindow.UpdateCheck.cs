using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Nubis;

/// <summary>
/// 새 버전 알림: 창이 뜬 뒤 백그라운드에서 하루 한 번 GitHub 최신 릴리스를 확인한다 (Settings 에서 끌 수 있음, PC 단위 공통 설정).
/// - 새로 확인해서 새 버전이 있으면 토스트 (누르면 릴리스 노트)
/// - 사이드바 버전 글자 아래 "새 버전" 표시는 지난 확인 결과로도 계속 보인다 (누르면 릴리스 노트 열기 · 이 버전 건너뛰기)
/// 실패(오프라인 · 레이트리밋)는 조용히 넘어간다.
/// </summary>
public partial class MainWindow
{
    // 4자리 전부 비교한다 — ToString(3) 이면 0.6.1.1 빌드가 v0.6.1.1 릴리스를 계속 "새 버전" 으로 본다
    private static string CurrentVersion => typeof(MainWindow).Assembly.GetName().Version?.ToString() ?? "0";

    private ReleaseInfo? _availableUpdate;
    private bool _updateChecking;

    /// <summary>Loaded 에서 한 번. 시작을 늦추지 않게 네트워크 확인은 백그라운드로</summary>
    private void StartUpdateCheck()
    {
        ShowCachedUpdate();
        var g = SettingsStore.Global;
        if (!g.CheckForUpdates || !UpdateCheck.IsDue(g.UpdateLastCheckUtc, DateTime.UtcNow)) return;
        _ = CheckForUpdateAsync();
    }

    private async Task CheckForUpdateAsync()
    {
        if (_updateChecking) return;   // 설정을 껐다 켰다 해도 한 번만
        _updateChecking = true;
        ReleaseInfo? latest;
        try
        {
            // 창이 막 뜬 순간의 작업(자동 접속 · 클라우드 동기화)과 겹치지 않게 조금 뒤에
            await Task.Delay(TimeSpan.FromSeconds(5));
            if (!SettingsStore.Global.CheckForUpdates) return;
            latest = await Task.Run(() => UpdateCheck.FetchLatestAsync());
        }
        finally { _updateChecking = false; }
        // 실패해도 시각은 기록한다 — 오프라인 PC 에서 실행할 때마다 접속을 다시 시도하지 않게 (하루 한 번)
        SettingsStore.UpdateGlobalState(s =>
        {
            s.UpdateLastCheckUtc = DateTime.UtcNow;
            if (latest != null) { s.UpdateLatestTag = latest.Tag; s.UpdateLatestUrl = latest.Url; }
        });
        // 확인하는 동안 사용자가 설정을 껐으면 알리지 않는다
        if (latest == null || !IsLoaded || !SettingsStore.Global.CheckForUpdates) return;
        if (!UpdateCheck.ShouldNotify(CurrentVersion, latest, SettingsStore.Global.UpdateSkipVersion)) { SetAvailableUpdate(null); return; }
        SetAvailableUpdate(latest);
        Toast.Show(L.F("Nubis {0} 가 나왔습니다. 눌러서 릴리스 노트 열기", latest.Version), ToastKind.Info, 12000,
            () => OpenReleaseNotes(latest));
    }

    /// <summary>지난 확인 결과로 사이드바 표시 (접속 없이)</summary>
    private void ShowCachedUpdate()
    {
        var g = SettingsStore.Global;
        ReleaseInfo? cached = g.CheckForUpdates && !string.IsNullOrWhiteSpace(g.UpdateLatestTag) && UpdateCheck.IsWebUrl(g.UpdateLatestUrl)
            ? new ReleaseInfo(g.UpdateLatestTag!, g.UpdateLatestUrl!)
            : null;
        SetAvailableUpdate(UpdateCheck.ShouldNotify(CurrentVersion, cached, g.UpdateSkipVersion) ? cached : null);
    }

    private void SetAvailableUpdate(ReleaseInfo? info)
    {
        _availableUpdate = info;
        if (info == null) { UpdateLink.Visibility = Visibility.Collapsed; return; }
        UpdateLink.Content = L.F("새 버전 {0}", info.Version);
        System.Windows.Automation.AutomationProperties.SetName(UpdateLink, L.F("새 버전 {0} 이 나왔습니다", info.Version));
        UpdateLink.Visibility = Visibility.Visible;
    }

    private void OnUpdateLinkClick(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is not { } info) return;
        var menu = new ContextMenu { PlacementTarget = UpdateLink, Placement = PlacementMode.Top };
        var open = new MenuItem { Header = L.T("릴리스 노트 열기"), FontWeight = FontWeights.SemiBold };
        open.Click += (_, _) => OpenReleaseNotes(info);
        var skip = new MenuItem { Header = L.T("이 버전 건너뛰기") };
        skip.Click += (_, _) =>
        {
            SettingsStore.UpdateGlobalState(s => s.UpdateSkipVersion = info.Version);
            SetAvailableUpdate(null);
        };
        menu.Items.Add(open);
        menu.Items.Add(skip);
        menu.IsOpen = true;
    }

    /// <summary>기본 브라우저로 릴리스 노트 (http/https 주소만)</summary>
    private void OpenReleaseNotes(ReleaseInfo info)
    {
        if (!UpdateCheck.IsWebUrl(info.Url)) return;
        try { Process.Start(new ProcessStartInfo(new Uri(info.Url).AbsoluteUri) { UseShellExecute = true }); }
        catch { Toast.Show(L.F("브라우저를 열지 못했습니다. 직접 여세요: {0}", info.Url), ToastKind.Error, 10000); }
    }

    // ----- Settings -----

    private void OnUpdateCheckClick(object sender, RoutedEventArgs e)
    {
        var on = UpdateCheckBox.IsChecked == true;
        // 상태 저장으로 충분 — UpdateGlobal 은 Changed 로 열린 터미널 전부에 설정을 다시 보낸다 (이 값은 터미널과 무관)
        SettingsStore.UpdateGlobalState(s => s.CheckForUpdates = on);
        if (!on) SetAvailableUpdate(null);
        else
        {
            ShowCachedUpdate();
            if (UpdateCheck.IsDue(SettingsStore.Global.UpdateLastCheckUtc, DateTime.UtcNow)) _ = CheckForUpdateAsync();
        }
    }
}
