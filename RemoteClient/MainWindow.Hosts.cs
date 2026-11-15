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
    // =================== 호스트 목록 ===================

    private void RefreshHosts()
    {
        var q = SearchBox.Text.Trim();
        var searching = q.Length > 0;
        var profiles = _profiles.ToDictionary(p => p.Id);
        // "중지된 서버 제외" 프로필의 중지된 서버는 목록에서만 숨긴다 (호스트 설정은 유지)
        IEnumerable<HostEntry> list = _hosts.Where(h =>
            !(h.IsCloud && h.IsStopped && h.CloudProfileId is { } pid && profiles.TryGetValue(pid, out var pp) && !pp.IncludeStopped));
        if (searching)
        {
            var tokens = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // "vault:" 검색에 쓰는 Vault 이름 목록은 한 번만 (호스트마다 VaultStore.All() 을 부르지 않게)
            var vaultNames = tokens.Any(t => t.StartsWith("vault:", StringComparison.OrdinalIgnoreCase))
                ? VaultStore.All().Select(e => e.Name).ToList() : new List<string>();
            list = list.Where(h => tokens.All(t => MatchesSearch(h, t, profiles, vaultNames)));
        }

        var filtered = list.ToList();
        var groups = new List<HostGroup>();

        // 즐겨찾기 · 최근 접속 (검색 중이 아닐 때만, 원래 그룹에도 그대로 있음)
        if (!searching)
        {
            var favs = filtered.Where(h => h.Favorite).OrderBy(h => h.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (favs.Count > 0)
                groups.Add(new HostGroup { Key = FavoritesKey, Name = L.T("즐겨찾기"), Hosts = favs, IsCollapsed = _collapsedLocal.Contains(FavoritesKey) });
            if (SettingsStore.Current.ShowRecent)
            {
                var since = DateTime.Now.AddDays(-30);
                var recent = filtered.Where(h => h.LastConnected > since && !h.Favorite)
                    .OrderByDescending(h => h.LastConnected).Take(_cardsPerRow * 2).ToList();
                if (recent.Count > 0)
                    groups.Add(new HostGroup { Key = RecentKey, Name = L.T("최근 접속"), Hosts = recent, IsCollapsed = _collapsedLocal.Contains(RecentKey) });
            }
        }

        // 직접 등록한 호스트: Group 별 (지정된 그룹 먼저, 미지정은 "Hosts"). 대소문자만 다른 그룹 이름은 하나로.
        // 그룹 키(접힘 상태 저장)·표시 이름은 호스트 순서·검색 결과와 상관없이 늘 같은 표기로 (CanonicalGroupNames)
        var canon = CanonicalGroupNames(_hosts);
        // 예전 표기(대소문자만 다른 이름)로 저장된 접힘 상태는 지금 표기로 옮기고 바로 저장한다
        // (저장하지 않으면 그룹을 한 번 접고 펼 때까지 설정에는 예전 키가 남아 다음 실행에서 다시 옮겨야 했다)
        if (MigrateCollapsedKeys(_collapsedLocal, canon)) SaveGroupState();
        groups.AddRange(filtered.Where(h => !h.IsCloud)
            .GroupBy(h => string.IsNullOrWhiteSpace(h.Group) ? "" : h.Group.Trim(), StringComparer.CurrentCultureIgnoreCase)
            .Select(g => (Name: canon.TryGetValue(g.Key, out var n) ? n : g.Key, Hosts: g))
            .OrderBy(g => g.Name.Length == 0)
            .ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => new HostGroup
            {
                Key = "L:" + g.Name,
                Name = g.Name.Length == 0 ? "Hosts" : g.Name,
                GroupName = g.Name,
                Hosts = g.Hosts.OrderBy(h => h.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList(),
                IsCollapsed = !searching && _collapsedLocal.Contains("L:" + g.Name),
            }));

        // 클라우드 프로필 그룹: AWS → Tencent → GCP → Azure → OCI, 기본은 접힌 상태 (서버가 많으므로)
        // 프로필마다 전체 목록을 다시 훑지 않게 한 번에 나눠 둔다 (프로필 수 × 호스트 수 → 호스트 수)
        var byProfile = filtered.Where(h => h.CloudProfileId != null).ToLookup(h => h.CloudProfileId!.Value);
        foreach (var p in _profiles.OrderBy(p => p.Provider).ThenBy(p => p.GroupName, StringComparer.CurrentCultureIgnoreCase))
        {
            var hosts = byProfile[p.Id]
                .OrderBy(h => h.IsStopped)
                .ThenBy(h => h.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            // 검색에 안 걸렸거나, 동기화했는데 서버가 없는 프로필(VM 없는 GCP 프로젝트 등)은 숨김
            if (hosts.Count == 0 && (searching || p.LastSync != null)) continue;
            var key = "C:" + p.Id;
            groups.Add(new HostGroup
            {
                Key = key,
                Name = p.GroupName,
                Profile = p,
                Hosts = hosts,
                IsCollapsed = !searching && !_expandedCloud.Contains(key),
            });
        }

        _groups = groups;
        if (_selected.Count > 0)
        {
            var all = _hosts.ToHashSet();
            foreach (var gone in _selected.Where(h => !all.Contains(h)).ToList()) SetSelected(gone, false);
            UpdateSelectionBar();
        }
        RebuildRows();
        // 검색어가 바뀌었으면 첫 결과를 골라 둔다 (Enter = 그 호스트로 연결, ↑↓ 로 이동).
        // 검색어가 그대로인 새로 고침(자동 동기화·편집 저장·창 크기)은 키보드로 고른 카드를 유지한다
        var visible = VisibleHosts();
        var keep = q == _lastSearch && _highlighted != null && visible.Contains(_highlighted) ? _highlighted : null;
        _lastSearch = q;
        SetHighlight(keep ?? (searching ? visible.FirstOrDefault() : null), scroll: keep == null);
        // 보이는 그룹이 없으면 안내 (검색 중이면 검색 결과 없음)
        EmptyState.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = searching ? L.T("검색 결과가 없습니다") : _profiles.Count > 0 ? L.T("동기화된 서버가 없습니다") : L.T("등록된 호스트가 없습니다");
        EmptyHint.Text = searching
            ? L.T("Enter 를 누르면 입력한 user@host 로 바로 접속합니다.")
            : _profiles.Count > 0
                ? L.T("Cloud 페이지에서 프로필을 동기화하거나, NEW HOST 로 서버를 추가하세요.")
                : L.T("NEW HOST(Ctrl+N) 로 서버를 추가하거나, IMPORT 로 엑셀·~/.ssh/config 등에서 가져오거나, Cloud 에서 클라우드 계정의 서버를 가져오세요.");
        // 빈 화면의 바로가기 버튼은 검색 결과가 없을 때가 아니라 정말 비어 있을 때만
        EmptyActions.Visibility = searching ? Visibility.Collapsed : Visibility.Visible;
        var shown = groups.Where(g => !g.IsVirtual).Sum(g => g.Hosts.Count);
        _countBase = searching ? L.F("{0} / {1}개", shown, _hosts.Count) : L.F("{0}개 호스트", _hosts.Count);
        UpdateCountText();
    }

    private const string FavoritesKey = "F:";
    private const string RecentKey = "R:";
    // 마지막으로 목록을 만든 검색어 (검색어가 그대로면 키보드로 고른 카드를 유지)
    private string _lastSearch = "";

    /// <summary>
    /// 그룹 이름(대소문자 무시)마다 늘 같은 표기 하나 — 접힘 상태 키("L:이름")가 호스트 순서에 따라 바뀌어
    /// 다시 시작하거나 호스트를 추가하면 접어 둔 그룹이 펼쳐지던 문제. 서수 비교로 가장 앞선 표기를 쓴다
    /// </summary>
    internal static Dictionary<string, string> CanonicalGroupNames(IEnumerable<HostEntry> hosts) =>
        hosts.Where(h => !h.IsCloud)
            .Select(h => string.IsNullOrWhiteSpace(h.Group) ? "" : h.Group.Trim())
            .GroupBy(n => n, StringComparer.CurrentCultureIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(n => n, StringComparer.Ordinal).First(), StringComparer.CurrentCultureIgnoreCase);

    /// <summary>
    /// 직접 등록한 그룹의 접힘 키("L:이름")를 지금 표기(CanonicalGroupNames)로 옮긴다. 바뀌었으면 true (저장 필요)
    /// </summary>
    internal static bool MigrateCollapsedKeys(HashSet<string> collapsed, IReadOnlyDictionary<string, string> canon)
    {
        var changed = false;
        foreach (var k in collapsed.Where(k => k.StartsWith("L:", StringComparison.Ordinal)).ToList())
        {
            if (!canon.TryGetValue(k[2..], out var cn) || cn == k[2..]) continue;
            collapsed.Remove(k);
            collapsed.Add("L:" + cn);
            changed = true;
        }
        return changed;
    }

    // ----- 일괄 작업 결과 같은 짧은 안내 (호스트 수 · 선택 막대 옆에 몇 초 보였다가 사라짐) -----
    private string _countBase = "";
    private string? _flashText;
    private DispatcherTimer? _flashTimer;

    private void UpdateCountText()
    {
        CountText.Text = _flashText == null ? _countBase : _countBase + "  ·  " + _flashText;
    }

    /// <summary>작업 결과를 몇 초 동안 보여 준다 (대화 상자 없이)</summary>
    private void FlashHostStatus(string text)
    {
        _flashText = text;
        if (_flashTimer == null)
        {
            _flashTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _flashTimer.Tick += (_, _) => { _flashTimer.Stop(); _flashText = null; UpdateCountText(); UpdateSelectionBar(); };
        }
        _flashTimer.Stop();
        _flashTimer.Start();
        UpdateCountText();
        UpdateSelectionBar();
    }

    /// <summary>
    /// 검색어 한 단어가 호스트와 맞는지. 이름·주소(사설/공인)·사용자·그룹·프로토콜·인스턴스 ID·리전·OS·용도·프로필 이름.
    /// "vault:이름" 은 그 Vault 계정을 쓰는 호스트 (기본 계정으로 쓰는 경우 포함)
    /// </summary>
    /// <summary>검색어 한 단어로 쓰는 Vault 이름 (검색어는 공백으로 나뉘므로 공백을 _ 로)</summary>
    internal static string VaultSearchName(string name) => name.Trim().Replace(' ', '_');

    /// <summary>
    /// "vault:이름" 검색: 이름이 정확히 같은 Vault 계정이 있으면 그 계정만 (이름이 겹치는 다른 계정 제외),
    /// 없으면 부분 일치 (사용자가 일부만 입력한 경우)
    /// </summary>
    internal static bool VaultNameMatches(string vaultName, string query, IEnumerable<string> allNames)
    {
        var vn = VaultSearchName(vaultName);
        if (string.Equals(vn, query, StringComparison.CurrentCultureIgnoreCase)) return true;
        return !allNames.Any(n => string.Equals(VaultSearchName(n), query, StringComparison.CurrentCultureIgnoreCase))
               && vn.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private static bool MatchesSearch(HostEntry h, string q, Dictionary<Guid, CloudProfile> profiles, IReadOnlyList<string> vaultNames)
    {
        var profile = h.CloudProfileId is { } pid && profiles.TryGetValue(pid, out var p) ? p : null;
        if (q.StartsWith("vault:", StringComparison.OrdinalIgnoreCase))
            return VaultStore.Get(h.EffectiveVaultId(profile)) is { } v && VaultNameMatches(v.Name, q[6..], vaultNames);
        return Contains(h.DisplayName, q) || Contains(h.Address, q) || Contains(h.PrivateIp, q) || Contains(h.PublicIp, q) ||
               Contains(h.Username, q) || Contains(h.Group, q) || Contains(h.ProtocolText, q) ||
               Contains(h.CloudInstanceId, q) || Contains(h.CloudRegion, q) || Contains(h.Os, q) || Contains(h.Purpose, q) ||
               Contains(profile?.GroupName, q);
    }

    /// <summary>화면에 보이는 순서대로의 호스트 (접힌 그룹 제외, 같은 호스트는 한 번)</summary>
    private List<HostEntry> VisibleHosts() =>
        _groups.Where(g => !g.IsCollapsed).SelectMany(g => g.Hosts).Distinct().ToList();

    private List<HostGroup> _groups = new();
    private int _cardsPerRow = 3;
    private const double CardSlotWidth = 302;   // 카드 290 + 간격 12

    /// <summary>그룹 헤더 + 카드 줄(창 폭에 맞춘 개수)로 행 목록 구성. 스크롤 위치는 유지</summary>
    private void RebuildRows()
    {
        var rows = new List<object>();
        foreach (var g in _groups)
        {
            rows.Add(g);
            if (g.IsCollapsed) continue;
            for (var i = 0; i < g.Hosts.Count; i += _cardsPerRow)
                rows.Add(new CardRow { Hosts = g.Hosts.GetRange(i, Math.Min(_cardsPerRow, g.Hosts.Count - i)) });
        }
        var sv = FindScrollViewer(HostRows);
        var offset = sv?.VerticalOffset ?? 0;
        HostRows.ItemsSource = rows;
        if (sv != null && offset > 0) Dispatcher.BeginInvoke(() => sv.ScrollToVerticalOffset(offset), DispatcherPriority.Loaded);
    }

    private void OnHostRowsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 좌우 여백(28+16)과 스크롤바 폭을 빼고 한 줄에 들어가는 카드 수
        var per = Math.Max(1, (int)((e.NewSize.Width - 44 - 12) / CardSlotWidth));
        if (per == _cardsPerRow) return;
        _cardsPerRow = per;
        // "최근 접속" 은 두 줄 분량(카드 수 × 2)만 보이므로 한 줄 카드 수가 바뀌면 목록을 다시 만든다
        if (SettingsStore.Current.ShowRecent && SearchBox.Text.Trim().Length == 0) RefreshHosts();
        else RebuildRows();
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    private static bool Contains(string? s, string q) => s?.Contains(q, StringComparison.OrdinalIgnoreCase) == true;

    private void SaveHosts()
    {
        try { HostStore.Save(_hosts); }
        catch (Exception ex) { Dialog.Show(this, L.F("호스트 목록 저장 실패: {0}", ex.Message), "Nubis"); }
    }

    /// <summary>카드(또는 공유 우클릭 메뉴의 대상 카드)의 호스트</summary>
    private static HostEntry? HostOf(object sender)
    {
        if (sender is MenuItem { Parent: ContextMenu { PlacementTarget: FrameworkElement target } })
            return target.DataContext as HostEntry;
        return (sender as FrameworkElement)?.DataContext as HostEntry;
    }

    private void OnCardMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (HostOf(sender) is not { } h) return;
        if (e.ClickCount == 1 && HandleCardSelectClick(h, ScreenIndexOf(sender as DependencyObject, h))) { e.Handled = true; return; }
        if (e.ClickCount == 1 && Keyboard.Modifiers == ModifierKeys.None)
        {
            // 그냥 클릭한 카드를 키보드로 고른 카드로 — 이어서 방향키 이동 · Enter 연결 · F2 편집 · Delete 삭제 (HandleCardKey).
            // 검색창에 입력 중이면 포커스는 그대로 둔다 (검색창에서도 Enter · F2 · ↑↓ 가 이 카드에 적용됨)
            var clicked = CardRowOf(sender as DependencyObject);
            SetHighlight(h, clicked == null ? -1 : CardRows().IndexOf(clicked), scroll: false);
            if (Keyboard.FocusedElement is not (TextBox or PasswordBox)) HostRows.Focus();
        }
        if (e.ClickCount == 2 && Keyboard.Modifiers == ModifierKeys.None) { Connect(h); e.Handled = true; }
    }

    /// <summary>카드가 들어 있는 카드 줄 (모르면 null)</summary>
    private static CardRow? CardRowOf(DependencyObject? card)
    {
        for (var d = card; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is FrameworkElement { DataContext: CardRow cr }) return cr;
        return null;
    }

    /// <summary>
    /// 클릭한 카드의 화면 위치 (ScreenOrder 의 순번). 같은 호스트가 즐겨찾기·최근 접속과 원래 그룹에 함께 있으면
    /// 실제로 클릭한 쪽. 모르면 -1
    /// </summary>
    private int ScreenIndexOf(DependencyObject? card, HostEntry h)
    {
        var clicked = CardRowOf(card);
        if (clicked == null) return -1;
        var offset = 0;
        foreach (var row in CardRows())
        {
            if (ReferenceEquals(row, clicked))
            {
                var i = row.Hosts.IndexOf(h);
                return i >= 0 ? offset + i : -1;
            }
            offset += row.Hosts.Count;
        }
        return -1;
    }

    /// <summary>화면 순서 그대로의 호스트 (접힌 그룹 제외, 즐겨찾기·최근 접속과 원래 그룹에 함께 있는 호스트는 두 번)</summary>
    private List<HostEntry> ScreenOrder() => _groups.Where(g => !g.IsCollapsed).SelectMany(g => g.Hosts).ToList();

    private void OnEditHost(object sender, RoutedEventArgs e) { if (HostOf(sender) is { } h) OpenEditor(h); }
    private void OnMenuConnect(object sender, RoutedEventArgs e) { if (HostOf(sender) is { } h) Connect(h); }
    private void OnMenuConnectPublic(object sender, RoutedEventArgs e) { if (HostOf(sender) is { } h) Connect(h, usePublic: true); }
    private void OnMenuConnectPrivate(object sender, RoutedEventArgs e) { if (HostOf(sender) is { } h) Connect(h, usePublic: false); }

    /// <summary>공유 우클릭 메뉴: 공인 IP 가 있는 호스트에만 "공인/사설 IP 로 연결" 표시</summary>
    private void OnHostMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu { PlacementTarget: FrameworkElement { DataContext: HostEntry h } } menu) return;
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            item.Visibility = (item.Tag as string) switch
            {
                "public" => h.HasPublicIp && !h.UsePublicIp ? Visibility.Visible : Visibility.Collapsed,
                "private" => h.HasPublicIp && h.UsePublicIp ? Visibility.Visible : Visibility.Collapsed,
                _ => Visibility.Visible,
            };
            if ((item.Tag as string) == "fav") item.Header = h.Favorite ? L.T("즐겨찾기 해제") : L.T("즐겨찾기에 추가");
            if ((item.Tag as string) == "public") item.Header = L.F("공인 IP로 연결 ({0})", h.PublicIp);
            if ((item.Tag as string) == "private") item.Header = L.F("사설 IP로 연결 ({0})", h.PrimaryAddress);
        }
    }
    private void OnMenuEdit(object sender, RoutedEventArgs e) { if (HostOf(sender) is { } h) OpenEditor(h); }

    private void OnMenuDuplicate(object sender, RoutedEventArgs e)
    {
        if (HostOf(sender) is not { } h) return;
        var copy = h.Clone();
        copy.Id = Guid.NewGuid();
        copy.Label = L.F("{0} (복사본)", string.IsNullOrWhiteSpace(h.Label) ? h.Address : h.Label);
        copy.LastConnected = null;
        copy.Favorite = false;
        // 클라우드 호스트의 복사본은 직접 관리하는 호스트로 (같은 인스턴스 ID 를 가진 유령 사본 방지)
        var note = h.IsCloud ? DetachFromCloud(copy) : null;
        // 저장은 편집 패널에서 "저장" 을 눌렀을 때만 (취소하면 사본이 남지 않음)
        OpenEditor(null, copy);
        if (note != null) EditorError.Text = note;
    }

    /// <summary>
    /// 클라우드 호스트(의 사본)를 직접 관리하는 호스트로 바꾼다. 프로필이 없어지므로 원본이 실제로 쓰던 계정을 호스트에 옮긴다.
    /// 옮기지 못한 것이 있으면 편집 패널에 보여 줄 안내를 돌려준다.
    /// </summary>
    private string? DetachFromCloud(HostEntry copy)
    {
        var profile = _profiles.FirstOrDefault(p => p.Id == copy.CloudProfileId);
        var (vaultId, source) = copy.ResolveVault(profile);
        string? note = null;
        var v = VaultStore.Get(vaultId);
        if (vaultId != null && (source == "host" || !BulkEdit.HasOwnCredentials(copy)))
            copy.VaultId = vaultId;   // 원본도 그 Vault 계정으로 로그인 — 복사본에 직접 지정 (프로필이 없어지므로)
        else if (v != null && source == "default")
        {
            // 설정의 기본 Vault 계정: 프로필이 없어져도 복사본에 그대로 적용된다 — 비밀번호를 옮겨 적으면
            // 호스트 값이 생겨 기본 계정(SSH 키 포함)이 더는 쓰이지 않으므로 아무것도 바꾸지 않는다
        }
        else if (v != null)
        {
            // 원본은 프로필 기본 계정이 호스트에 적힌 사용자명 뒤에 쓰였다 (호스트에 비밀번호·키 파일이 있으면 Vault 를 안 쓰므로 여기는 사용자명만 있는 경우).
            // Vault 에 사용자명이 없거나 같으면 직접 지정해도 로그인 사용자·비밀번호·키가 그대로다
            if (string.IsNullOrWhiteSpace(v.Username) || string.Equals(v.Username, copy.Username, StringComparison.Ordinal))
                copy.VaultId = vaultId;
            else
            {
                // 직접 지정하면 Vault 의 사용자명이 우선이 되어 로그인 사용자가 바뀌므로 빈 값만 채운다 — 키는 옮길 수 없다
                if (string.IsNullOrEmpty(copy.ProtectedPassword)) copy.ProtectedPassword = v.ProtectedPassword;
                if (copy.Protocol == Protocol.Ssh && v.HasKey)
                    note = L.F("원본이 쓰던 Vault 계정 '{0}' 의 SSH 키는 사용자명이 달라 옮기지 못했습니다. 키로 로그인하려면 Vault 계정을 직접 고르세요.", v.Name);
            }
        }
        else
        {
            if (string.IsNullOrEmpty(copy.Username)) copy.Username = profile?.DefaultUsername ?? "";
            if (string.IsNullOrEmpty(copy.ProtectedPassword)) copy.ProtectedPassword = profile?.ProtectedDefaultPassword;
        }
        // 그룹은 비어 있을 때만 프로필 이름으로 — 편집 패널에서 적은 그룹(지워진 클라우드 호스트를 다시 추가할 때)을 덮어쓰지 않게
        if (string.IsNullOrWhiteSpace(copy.Group)) copy.Group = profile?.GroupName ?? "";
        copy.CloudProfileId = null;
        copy.CloudInstanceId = null;
        copy.CloudState = null;
        copy.CloudRegion = null;
        copy.CloudName = null;
        return note;
    }

    private void OnMenuFavorite(object sender, RoutedEventArgs e)
    {
        if (HostOf(sender) is not { } h) return;
        h.Favorite = !h.Favorite;
        SaveHosts();
        RefreshHosts();
    }

    private void OnMenuDelete(object sender, RoutedEventArgs e) { if (HostOf(sender) is { } h) DeleteHost(h); }

    private void DeleteHost(HostEntry h)
    {
        var profile = h.IsCloud ? _profiles.FirstOrDefault(p => p.Id == h.CloudProfileId) : null;
        var msg = profile != null
            ? L.F("'{0}' 을(를) 목록에서 지울까요?\n클라우드의 서버는 그대로이며, 다음 동기화에서도 다시 나타나지 않도록 숨깁니다.\n(프로필 설정에서 숨김을 해제할 수 있습니다)", h.DisplayName)
            : L.F("'{0}' 호스트를 삭제할까요?", h.DisplayName);
        msg += JumpDependentsNote(new[] { h });
        if (Dialog.Show(this, msg, L.T("삭제 확인"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        RemoveHosts(new[] { h });
    }

    /// <summary>지울 호스트를 점프 서버로 쓰는 (같이 지우지 않는) 호스트</summary>
    internal static List<HostEntry> JumpDependents(IEnumerable<HostEntry> all, IReadOnlyCollection<HostEntry> deleting)
    {
        var ids = deleting.Select(x => x.Id).ToHashSet();
        return all.Where(x => x.JumpHostId is { } j && ids.Contains(j) && !ids.Contains(x.Id)).ToList();
    }

    /// <summary>
    /// 삭제 확인 문구에 붙일 안내: 이 호스트(들)를 점프 서버로 쓰는 호스트는 점프 서버를 다시 고를 때까지 연결되지 않는다. 없으면 ""
    /// </summary>
    private string JumpDependentsNote(IReadOnlyCollection<HostEntry> deleting)
    {
        var dependents = JumpDependents(_hosts, deleting);
        if (dependents.Count == 0) return "";
        return "\n\n" + L.F("이 호스트를 점프 서버로 쓰는 호스트 {0}개는 점프 서버를 다시 고를 때까지 연결되지 않습니다:", dependents.Count) + "\n" +
               string.Join("\n", dependents.Take(10).Select(x => "  " + x.DisplayName)) + (dependents.Count > 10 ? "\n  ..." : "");
    }

    /// <summary>확인 없이 삭제 (확인은 호출하는 쪽에서). 클라우드 호스트는 다음 동기화에서도 다시 나타나지 않게 숨긴다</summary>
    private void RemoveHosts(IEnumerable<HostEntry> hosts)
    {
        var profilesChanged = false;
        foreach (var h in hosts.ToList())
        {
            var profile = h.IsCloud ? _profiles.FirstOrDefault(p => p.Id == h.CloudProfileId) : null;
            if (profile != null && h.CloudInstanceId != null && !profile.HiddenInstances.Contains(h.CloudInstanceId))
            {
                profile.HiddenInstances.Add(h.CloudInstanceId);
                profilesChanged = true;
            }
            _hosts.Remove(h);
        }
        if (profilesChanged) SaveProfiles();
        SaveHosts();
        UpdateProfileCounts();
        RefreshHosts();
    }

    private void OnNewHost(object sender, RoutedEventArgs e) => OpenEditor(null);

    /// <summary>호스트 가져오기 (엑셀·CSV · ~/.ssh/config · mRemoteNG · Ansible · Active Directory) — 미리보기에서 고른 것만 등록</summary>
    private void OnImportHosts(object sender, RoutedEventArgs e)
    {
        if (ImportHostsDialog.Ask(this, _hosts) is not { } r) return;
        var applied = r.ApplyTo(_hosts);
        SaveHosts();
        RefreshHosts();
        // "가져온 호스트 보기": 검색을 비우고 추가·갱신된 호스트를 선택 상태로 보여 준다 (선택 막대에서 바로 일괄 작업 가능)
        if (ImportHostsDialog.ShowSummary(this, applied))
        {
            if (SearchBox.Text.Length > 0) SearchBox.Clear();
            ClearSelection();
            foreach (var h in _hosts.Where(h => applied.AddedIds.Contains(h.Id) || applied.UpdatedIds.Contains(h.Id)))
                SetSelected(h, true);
            UpdateSelectionBar();
        }
    }
}
