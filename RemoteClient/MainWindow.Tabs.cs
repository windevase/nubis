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
    // =================== 세션 / 탭 ===================

    private int _nextSessionId = 1;

    /// <param name="usePublic">null = 호스트 설정대로, true/false = 이번만 공인/사설 IP 로</param>
    private SessionTab Connect(HostEntry saved, bool? usePublic = null)
    {
        // 클라우드 호스트는 비어 있는 계정을 프로필 기본 계정으로 채우고, 사설/공인 중 실제 접속 주소로
        var host = saved.ForConnection(_profiles.FirstOrDefault(p => p.Id == saved.CloudProfileId), usePublic);
        // 세션은 주소(공인 IP 재시도)·계정을 바꿔 쓰므로 저장된 원본이 아니라 항상 사본을 넘긴다
        // (ForConnection 은 바꿀 것이 없으면 원본을 그대로 돌려준다)
        if (ReferenceEquals(host, saved)) host = saved.Clone();
        var viaPublic = saved.HasPublicIp && host.Address == saved.PublicIp && saved.PublicIp != saved.PrimaryAddress;
        // 접속 때 입력한 계정 저장 (목록에 있는 호스트만. 빠른 접속은 저장 안 함)
        Action<string, string>? save = _hosts.Contains(saved) ? (u, p) => SaveEnteredCredentials(saved, u, p) : null;
        // 다시 연결할 때 저장된 호스트·Vault 의 최신 계정을 반영 (탭을 연 뒤 Vault 비밀번호를 고친 경우)
        CloudProfile? Profile() => _profiles.FirstOrDefault(p => p.Id == saved.CloudProfileId);
        var source = _hosts.Contains(saved)
            ? new SessionSource(() => saved.ForConnection(Profile(), usePublic), () => saved.CredentialSourceText(Profile()),
                () => saved.PromptAnswers, (question, answer) =>
                {
                    // "Choose [1] Push, or [2] TOTP" 같은 고정 선택을 이 호스트에 기억 (호스트 편집에서 지울 수 있음)
                    (saved.PromptAnswers ??= [])[question] = answer;
                    SaveHosts();
                })
            : null;
        FrameworkElement view = host.Protocol == Protocol.Ssh ? new SshSessionView(host, save, source) : new RdpSessionView(host, save, source);
        var title = host.Protocol == Protocol.Ssh && !string.IsNullOrWhiteSpace(host.Username) && string.IsNullOrWhiteSpace(host.Label)
            ? $"{host.Username}@{host.Address}"
            : host.DisplayName;
        if (viaPublic) title = L.F("{0} (공인)", title);
        var tab = AddTab(new SessionTab { Id = _nextSessionId++, Title = title, Protocol = host.Protocol, View = view, Host = saved });

        if (_hosts.Contains(saved))
        {
            saved.LastConnected = DateTime.Now;
            SaveHosts();
        }
        return tab;
    }

    /// <summary>
    /// 접속 때 입력해 로그인에 성공한 계정을 저장. <see cref="CredentialSaveTarget"/> 이 Vault 계정을 고르면 그 계정의
    /// 비밀번호를 바꾸고(서버 비밀번호가 바뀐 경우 — 같은 계정을 쓰는 다른 호스트에도 적용), 아니면 호스트에만 저장한다.
    /// </summary>
    private void SaveEnteredCredentials(HostEntry saved, string user, string password)
    {
        var profile = _profiles.FirstOrDefault(p => p.Id == saved.CloudProfileId);
        if (CredentialSaveTarget(saved, profile, user) is { } entry)
        {
            entry.Password = password;
            SaveVault(entry);
            // 사용자명은 Vault 와 같으므로 호스트에는 비워 둔다 (계속 Vault 를 따름)
            if (string.IsNullOrWhiteSpace(saved.Username)) saved.Username = "";
        }
        else if (saved.ResolveVault(profile).source == "host")
        {
            // 직접 고른 Vault 계정이 우선인 호스트에 다른 사용자명으로 들어옴 — 호스트에 적어도 쓰이지 않으므로 저장하지 않는다
            return;
        }
        else
        {
            saved.Username = user;
            saved.Password = password;
        }
        SaveHosts();
        RefreshHosts();
    }

    /// <summary>
    /// 접속 때 입력한 계정으로 바꿀 Vault 계정. 호스트가 <b>직접 고른</b> Vault 계정(호스트 값보다 우선)이고
    /// 입력한 사용자명이 그 계정과 같을 때만. 다른 사용자명을 입력했거나
    /// 프로필·설정의 기본 계정(여러 호스트가 함께 씀)이면 null — 다른 호스트의 로그인을 깨뜨리지 않는다.
    /// </summary>
    internal static VaultEntry? CredentialSaveTarget(HostEntry saved, CloudProfile? profile, string user)
    {
        var (vaultId, source) = saved.ResolveVault(profile);
        if (source != "host") return null;
        return VaultStore.Get(vaultId) is { HasPassword: true } entry && string.Equals(user, entry.Username, StringComparison.Ordinal)
            ? entry : null;
    }

    private HostEntry WithProfileDefaults(HostEntry h) =>
        h.WithDefaults(_profiles.FirstOrDefault(p => p.Id == h.CloudProfileId));

    private SessionTab AddTab(SessionTab tab)
    {
        tab.Session.StatusChanged += s =>
        {
            tab.Status = s;
            if (tab.IsSelected) UpdateActiveContext();
        };
        _tabs.Add(tab);
        SessionsHost.Children.Add(tab.View);
        SelectTab(tab);
        return tab;
    }

    /// <summary>로컬 PowerShell 탭 (Ctrl+Shift+T)</summary>
    private void OpenLocalTerminal()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var view = new LocalTerminalView(() => "powershell.exe -NoLogo", home, L.T("PowerShell 이 종료되었습니다."));
        AddTab(new SessionTab { Id = _nextSessionId++, Title = "PowerShell", Protocol = Protocol.Local, View = view });
    }

    private void OnNewLocalTerminal(object sender, RoutedEventArgs e) => OpenLocalTerminal();

    private void CycleTab(int dir)
    {
        if (_tabs.Count == 0) return;
        var cur = _tabs.FirstOrDefault(t => t.IsSelected);
        var i = (cur == null ? -1 : _tabs.IndexOf(cur)) + dir;
        if (cur == null && dir < 0) i = _tabs.Count - 1;
        if (i < 0 || i >= _tabs.Count) SelectHome();
        else SelectTab(_tabs[i]);
    }

    /// <summary>터미널(WebView2)에 포커스가 있을 때 들어오는 앱 단축키</summary>
    private void OnTerminalHotkey(string key) => Dispatcher.BeginInvoke(() =>
    {
        switch (key)
        {
            case "nexttab": CycleTab(+1); break;
            case "prevtab": CycleTab(-1); break;
            case "claude": ToggleClaude(); break;
            case "localterm": OpenLocalTerminal(); break;
            case "closetab": if (_tabs.FirstOrDefault(t => t.IsSelected) is { } cur) CloseTabs(new[] { cur }); break;
            case "search": FocusSearch(); break;
            default:
                if (key.StartsWith("tab") && int.TryParse(key[3..], out var n)) SelectTabNumber(n);
                break;
        }
    });

    private void SelectTab(SessionTab tab)
    {
        foreach (var t in _tabs)
        {
            t.IsSelected = t == tab;
            t.View.Visibility = t == tab ? Visibility.Visible : Visibility.Collapsed;
        }
        HomeView.Visibility = Visibility.Collapsed;
        SessionsHost.Visibility = Visibility.Visible;
        HomeTab.Background = Brushes.Transparent;
        Dispatcher.BeginInvoke(() => tab.Session.FocusSession(), DispatcherPriority.Input);
        // 넘친 탭도 고르면 화면 안으로
        Dispatcher.BeginInvoke(() => (TabsList.ItemContainerGenerator.ContainerFromItem(tab) as FrameworkElement)?.BringIntoView(),
            DispatcherPriority.Loaded);
        UpdateActiveContext();
    }

    private void SelectHome()
    {
        foreach (var t in _tabs) t.IsSelected = false;
        SessionsHost.Visibility = Visibility.Collapsed;
        HomeView.Visibility = Visibility.Visible;
        HomeTab.Background = (Brush)FindResource("CardBg");
        UpdateActiveContext();
    }

    /// <summary>연결 중인 탭은 닫기 전에 확인 (Settings 에서 끌 수 있음). 닫아도 되면 true</summary>
    private bool ConfirmCloseTabs(IReadOnlyCollection<SessionTab> tabs)
    {
        var live = tabs.Count(t => t.Status == SessionStatus.Connected && t.Protocol != Protocol.Local);
        if (live == 0 || !SettingsStore.Current.ConfirmCloseTab) return true;
        var msg = tabs.Count == 1
            ? L.F("'{0}' 연결을 끊고 탭을 닫을까요?", tabs.First().Title)
            : L.F("연결 중인 세션 {0}개를 끊고 탭 {1}개를 닫을까요?", live, tabs.Count);
        return Dialog.Show(this, msg, L.T("탭 닫기"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }

    private void CloseTabs(IReadOnlyCollection<SessionTab> tabs)
    {
        if (tabs.Count == 0 || !ConfirmCloseTabs(tabs)) return;
        foreach (var t in tabs.ToList()) CloseTab(t);
    }

    private void CloseTab(SessionTab tab)
    {
        var idx = _tabs.IndexOf(tab);
        var wasSelected = tab.IsSelected;
        try { tab.Session.CloseSession(); } catch { }
        SessionsHost.Children.Remove(tab.View);
        _tabs.Remove(tab);

        if (!wasSelected) { UpdateActiveContext(); return; }
        if (_tabs.Count == 0) SelectHome();
        else SelectTab(_tabs[Math.Min(idx, _tabs.Count - 1)]);
    }

    private void OnHomeTab(object sender, MouseButtonEventArgs e) => SelectHome();

    private void OnTabClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SessionTab t) SelectTab(t);
    }

    private void OnTabMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 휠 클릭으로 탭 닫기
        if (e.ChangedButton == MouseButton.Middle && (sender as FrameworkElement)?.DataContext is SessionTab t)
        {
            CloseTabs(new[] { t });
            e.Handled = true;
        }
    }

    private void OnTabClose(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SessionTab t) CloseTabs(new[] { t });
        e.Handled = true;
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        // Ctrl+Tab / Ctrl+Shift+Tab : 탭 전환 (WPF 포커스일 때. 터미널 포커스일 때는 OnTerminalHotkey)
        if (e.Key == Key.Tab && ctrl)
        {
            CycleTab(shift ? -1 : +1);
            e.Handled = true;
        }
        else if (ctrl && shift && e.Key == Key.A) { ToggleClaude(); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.T) { OpenLocalTerminal(); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.W)
        {
            if (_tabs.FirstOrDefault(t => t.IsSelected) is { } cur) CloseTabs(new[] { cur });
            e.Handled = true;
        }
        else if (ctrl && !shift && e.Key is >= Key.D1 and <= Key.D9 && Keyboard.FocusedElement is not TextBox and not PasswordBox)
        {
            SelectTabNumber(e.Key - Key.D0);
            e.Handled = true;
        }
        else if (ctrl && (e.Key == Key.K || (e.Key == Key.F && !shift && EditorLayer.Visibility != Visibility.Visible && Keyboard.FocusedElement is not TextBox)))
        {
            FocusSearch();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.S && EditorLayer.Visibility == Visibility.Visible)
        {
            SaveEditor();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && EditorLayer.Visibility == Visibility.Visible)
        {
            TryCloseEditor();
            e.Handled = true;
        }
        else if (!shift && HandleSelectionKey(e)) e.Handled = true;
    }

    /// <summary>Ctrl+1~8 = 그 번호 탭, Ctrl+9 = 마지막 탭 (브라우저와 같음)</summary>
    private void SelectTabNumber(int n)
    {
        if (_tabs.Count == 0) { SelectHome(); return; }
        if (n >= 9) SelectTab(_tabs[^1]);
        else if (n - 1 < _tabs.Count) SelectTab(_tabs[n - 1]);
    }

    /// <summary>Ctrl+K / Ctrl+F (터미널에서는 Ctrl+Shift+K): 홈으로 가서 검색창에 포커스</summary>
    private void FocusSearch()
    {
        SelectHome();
        NavHosts.IsChecked = true;
        Dispatcher.BeginInvoke(() => { SearchBox.Focus(); SearchBox.SelectAll(); }, DispatcherPriority.Input);
    }

    // ---------- 탭 줄: 휠 스크롤 · 넘친 탭 목록 ----------

    private void OnTabsWheel(object sender, MouseWheelEventArgs e)
    {
        TabsScroll.ScrollToHorizontalOffset(TabsScroll.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private void OnTabsScrollChanged(object sender, ScrollChangedEventArgs e) =>
        TabListButton.Visibility = TabsScroll.ScrollableWidth > 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnTabListClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = TabListButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        for (var i = 0; i < _tabs.Count; i++)
        {
            var t = _tabs[i];
            var item = new MenuItem
            {
                Header = (i < 8 ? $"{i + 1}  " : "    ") + t.Title + (t.Status == SessionStatus.Disconnected ? L.T("  (끊김)") : ""),
                FontWeight = t.IsSelected ? FontWeights.SemiBold : FontWeights.Normal,
            };
            item.Click += (_, _) => SelectTab(t);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    // ---------- 탭 우클릭 메뉴 ----------

    private void OnTabMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu { PlacementTarget: FrameworkElement { DataContext: SessionTab tab } } menu) return;
        menu.Items.Clear();
        void Add(string header, Action act, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => act();
            menu.Items.Add(item);
        }
        var idx = _tabs.IndexOf(tab);
        var disconnected = _tabs.Where(t => t.Status == SessionStatus.Disconnected).ToList();
        Add(L.T("다시 연결"), () => { SelectTab(tab); tab.Session.Reconnect(); }, tab.Status == SessionStatus.Disconnected);
        if (tab.Host != null) Add(L.T("같은 호스트로 새 탭"), () => Connect(tab.Host));
        if (tab.Protocol == Protocol.Local) Add(L.T("새 PowerShell 탭"), OpenLocalTerminal);
        if (tab.View is RdpSessionView rdp) Add(L.T("전체 화면"), () => { SelectTab(tab); rdp.EnterFullScreen(); }, rdp.IsConnected);
        Add(L.T("탭 이름 바꾸기..."), () =>
        {
            if (PromptDialog.Ask(this, L.T("탭 이름 바꾸기"), L.T("새 이름"), tab.Title) is { Length: > 0 } name) tab.Title = name;
        });
        if (tab.Host != null && _hosts.Contains(tab.Host)) Add(L.T("호스트 편집"), () => { SelectHome(); OpenEditor(tab.Host); });
        menu.Items.Add(new Separator());
        Add(L.T("닫기"), () => CloseTabs(new[] { tab }));
        Add(L.T("다른 탭 모두 닫기"), () => CloseTabs(_tabs.Where(t => t != tab).ToList()), _tabs.Count > 1);
        Add(L.T("오른쪽 탭 모두 닫기"), () => CloseTabs(_tabs.Skip(idx + 1).ToList()), idx < _tabs.Count - 1);
        Add(L.T("끊긴 탭 모두 다시 연결"), () => { foreach (var t in disconnected) t.Session.Reconnect(); }, disconnected.Count > 0);
        Add(L.T("끊긴 탭 모두 닫기"), () => CloseTabs(disconnected), disconnected.Count > 0);
    }

    // ---------- 탭 끌어서 순서 바꾸기 ----------

    private Point _tabDragStart;
    private SessionTab? _tabDragging;

    private void OnTabDragStart(object sender, MouseButtonEventArgs e)
    {
        _tabDragStart = e.GetPosition(this);
        _tabDragging = (sender as FrameworkElement)?.DataContext as SessionTab;
    }

    private void OnTabDragMove(object sender, MouseEventArgs e)
    {
        if (_tabDragging == null || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(this) - _tabDragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance * 2) return;
        var tab = _tabDragging;
        _tabDragging = null;
        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(typeof(SessionTab), tab), DragDropEffects.Move);
    }

    private void OnTabDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(SessionTab)) ? DragDropEffects.Move : DragDropEffects.None;
        if (e.Data.GetData(typeof(SessionTab)) is SessionTab dragged && (sender as FrameworkElement)?.DataContext is SessionTab over && dragged != over)
        {
            var from = _tabs.IndexOf(dragged);
            var to = _tabs.IndexOf(over);
            if (from >= 0 && to >= 0) _tabs.Move(from, to);
        }
        e.Handled = true;
    }

    private void OnTabDrop(object sender, DragEventArgs e) => e.Handled = true;
}
