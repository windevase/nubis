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

    // 탭을 열 때 고른 공인/사설 IP ("같은 호스트로 새 탭" 이 같은 주소로 열리게). 탭이 사라지면 함께 사라진다
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<SessionTab, object> _tabUsePublic = new();

    private bool? TabUsePublic(SessionTab tab) => _tabUsePublic.TryGetValue(tab, out var v) ? (bool)v : null;

    /// <param name="usePublic">null = 호스트 설정대로, true/false = 이번만 공인/사설 IP 로</param>
    private SessionTab Connect(HostEntry saved, bool? usePublic = null)
    {
        // 클라우드 호스트는 비어 있는 계정을 프로필 기본 계정으로 채우고, 사설/공인 중 실제 접속 주소로
        var host = saved.ForConnection(_profiles.FirstOrDefault(p => p.Id == saved.CloudProfileId), usePublic);
        // 세션은 주소(공인 IP 재시도)·계정을 바꿔 쓰므로 저장된 원본이 아니라 항상 사본을 넘긴다
        // (ForConnection 은 바꿀 것이 없으면 원본을 그대로 돌려준다)
        if (ReferenceEquals(host, saved)) host = saved.Clone();
        host = AttachJump(saved, host);
        var viaPublic = saved.HasPublicIp && host.Address == saved.PublicIp && saved.PublicIp != saved.PrimaryAddress;
        // 접속 때 입력한 계정 저장 (목록에 있는 호스트만. 빠른 접속은 저장 안 함)
        Action<string, string>? save = _hosts.Contains(saved) ? (u, p) => SaveEnteredCredentials(saved, u, p) : null;
        // 다시 연결할 때 저장된 호스트·Vault 의 최신 계정을 반영 (탭을 연 뒤 Vault 비밀번호를 고친 경우)
        CloudProfile? Profile() => _profiles.FirstOrDefault(p => p.Id == saved.CloudProfileId);
        var source = _hosts.Contains(saved)
            ? new SessionSource(() => AttachJump(saved, saved.ForConnection(Profile(), usePublic)), () => saved.CredentialSourceText(Profile()),
                () => saved.PromptAnswers, (question, answer) =>
                {
                    // "Choose [1] Push, or [2] TOTP" 같은 고정 선택을 이 호스트에 기억 (호스트 편집에서 지울 수 있음)
                    (saved.PromptAnswers ??= [])[question] = answer;
                    SaveHosts();
                },
                SaveJumpCredentials: (u, p) =>
                {
                    // 점프 서버가 요구해 바꾼 비밀번호 — 점프 서버 호스트(또는 그 Vault 계정)에 대상 서버와 같은 방식으로 저장
                    if (saved.JumpHostId is { } jid &&
                        _hosts.FirstOrDefault(x => x.Id == jid && x.Protocol == Protocol.Ssh && !ReferenceEquals(x, saved)) is { } jumpSaved)
                        SaveEnteredCredentials(jumpSaved, u, p);
                })
            : null;
        FrameworkElement view = host.Protocol == Protocol.Ssh ? new SshSessionView(host, save, source) : new RdpSessionView(host, save, source);
        var title = host.Protocol == Protocol.Ssh && !string.IsNullOrWhiteSpace(host.Username) && string.IsNullOrWhiteSpace(host.Label)
            ? $"{host.Username}@{host.Address}"
            : host.DisplayName;
        if (viaPublic) title = L.F("{0} (공인)", title);
        var tab = AddTab(new SessionTab { Id = _nextSessionId++, Title = title, Protocol = host.Protocol, View = view, Host = saved });
        if (usePublic is { } pub) _tabUsePublic.AddOrUpdate(tab, pub);

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
    /// 그 Vault 계정을 다른 호스트도 쓰면 Vault 를 바꿀지 · 이 호스트에만 저장할지 · 저장하지 않을지 묻는다.
    /// </summary>
    private void SaveEnteredCredentials(HostEntry saved, string user, string password)
    {
        var profile = _profiles.FirstOrDefault(p => p.Id == saved.CloudProfileId);
        if (CredentialSaveTarget(saved, profile, user) is { } entry)
        {
            if (entry.Password == password) return;   // 이미 같은 비밀번호 — 묻거나 다시 암호화해 저장하지 않는다
            // 같은 Vault 계정을 쓰는 다른 호스트가 있으면 묻는다 — 조용히 바꾸면 그 호스트들의 로그인이 깨질 수 있다
            var others = VaultUsers(entry.Id).Where(h => !ReferenceEquals(h, saved)).ToList();
            if (others.Count == 0) { ApplyEnteredToVault(saved, entry, user, password, 0); return; }
            // 묻는 창은 세션 콜백(RDP ActiveX 로그인 이벤트 등) 밖에서 띄운다
            Dispatcher.BeginInvoke(() =>
            {
                if (!_hosts.Contains(saved)) return;
                ApplyEnteredToVault(saved, entry, user, password, AskSharedVaultSave(entry, others, saved.Protocol));
            });
            return;
        }
        else if (saved.ResolveVault(profile).source == "host")
        {
            // 직접 고른 Vault 계정이 우선인 호스트에 다른 사용자명으로 들어옴 — 호스트에 적어도 쓰이지 않으므로 저장하지 않는다
            return;
        }
        else
        {
            saved.Username = user;
            if (saved.Password != password) saved.Password = password;
        }
        SaveHosts();
        RefreshHosts();
    }

    /// <param name="choice">0 = Vault 비밀번호 변경, 1 = 이 호스트에만 저장(Vault 지정 해제), 그 외 = 저장 안 함</param>
    private void ApplyEnteredToVault(HostEntry saved, VaultEntry entry, string user, string password, int choice)
    {
        if (choice == 0)
        {
            // 저장된 개체를 직접 고치지 않고 사본으로 바꿔 넣는다 — 저장이 실패하면 메모리 값도 그대로 남는다
            var copy = entry.Clone();
            copy.Password = password;
            if (!SaveVault(copy)) return;
            // 사용자명은 Vault 와 같으므로 호스트에는 비워 둔다 (계속 Vault 를 따름)
            if (string.IsNullOrWhiteSpace(saved.Username)) saved.Username = "";
        }
        else if (choice == 1)
        {
            // 이 호스트에만: 직접 고른 Vault 계정은 호스트 값보다 우선이라, 지정을 풀어야 호스트에 저장한 값이 쓰인다
            saved.VaultId = null;
            saved.Username = user;
            saved.Password = password;
        }
        else return;   // 저장 안 함
        SaveHosts();
        RefreshHosts();
    }

    /// <summary>이 Vault 계정으로 로그인하는 호스트 (직접 지정 + 프로필·설정의 기본 계정으로 쓰는 호스트)</summary>
    private List<HostEntry> VaultUsers(Guid vaultId) =>
        _hosts.Where(h => h.Protocol != Protocol.Local &&
                          h.EffectiveVaultId(_profiles.FirstOrDefault(p => p.Id == h.CloudProfileId)) == vaultId).ToList();

    /// <summary>
    /// 접속 때 입력한 비밀번호를 여러 호스트가 함께 쓰는 Vault 계정에 저장할지.
    /// 0 = Vault 비밀번호 변경(모든 호스트에 적용), 1 = 이 호스트에만 저장(Vault 지정 해제), -1 = 저장 안 함
    /// </summary>
    private int AskSharedVaultSave(VaultEntry entry, IReadOnlyList<HostEntry> others, Protocol proto)
    {
        var msg = L.F("Vault 계정 '{0}' 을(를) 이 호스트 말고도 호스트 {1}개가 함께 씁니다.\n입력한 비밀번호를 어디에 저장할까요?", entry.Name, others.Count) + "\n\n" +
                  L.F("· Vault 비밀번호 변경: 이 계정을 쓰는 호스트 {0}개 모두 새 비밀번호로 로그인합니다.", others.Count + 1) + "\n" +
                  L.T("· 이 호스트에만 저장: 이 호스트의 Vault 계정 지정을 해제하고, 사용자명·비밀번호를 이 호스트에 직접 저장합니다.");
        if (proto == Protocol.Ssh && entry.HasKey) msg += "\n" + L.T("  (이 호스트는 Vault 계정의 SSH 키도 더 이상 쓰지 않습니다)");
        var detail = string.Join("\n", others.Take(15).Select(h => h.DisplayName)) + (others.Count > 15 ? "\n..." : "");
        var choice = Dialog.Choose(this, msg, L.T("비밀번호 저장"), L.T("Vault 비밀번호 변경"), L.T("이 호스트에만 저장"), L.T("저장 안 함"),
            MessageBoxImage.Question, detail);
        return choice is 0 or 1 ? choice : -1;
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
        // 비밀번호가 없는 계정(키 전용 등)이어도 사용자명이 같으면 그 계정에 저장 — 저장을 골랐는데 조용히 버려 매번 다시 묻던 문제.
        // 사용자명은 대소문자를 구분한다 (리눅스 계정 이름)
        return VaultStore.Get(vaultId) is { } entry && !string.IsNullOrWhiteSpace(entry.Username) &&
               string.Equals(user, entry.Username, StringComparison.Ordinal)
            ? entry : null;
    }

    private HostEntry WithProfileDefaults(HostEntry h) =>
        AttachJump(h, h.WithDefaults(_profiles.FirstOrDefault(p => p.Id == h.CloudProfileId)));

    /// <summary>
    /// 점프 서버(ProxyJump): 목록의 다른 SSH 호스트를 접속용 사본(계정·주소 채운 것)으로 붙인다.
    /// 점프 서버의 점프는 쓰지 않는다 (한 단계만). 점프 서버가 지워졌으면 붙이지 않는다.
    /// </summary>
    private HostEntry AttachJump(HostEntry saved, HostEntry conn)
    {
        if (saved.Protocol != Protocol.Ssh || saved.JumpHostId is not { } id) return conn;
        var j = _hosts.FirstOrDefault(x => x.Id == id && x.Protocol == Protocol.Ssh && !ReferenceEquals(x, saved));
        if (ReferenceEquals(conn, saved)) conn = saved.Clone();
        if (j == null)
        {
            // 점프 서버가 지워졌다 — 조용히 직접 연결하지 않고 연결할 때 오류를 내게 표시만 붙인다 (JumpTunnel.Open)
            conn.Jump = new HostEntry { Label = L.T("삭제된 점프 서버"), Protocol = Protocol.Ssh, IsMissingJump = true };
            return conn;
        }
        var jc = j.ForConnection(_profiles.FirstOrDefault(p => p.Id == j.CloudProfileId));
        if (ReferenceEquals(jc, j)) jc = j.Clone();
        jc.Jump = null;
        conn.Jump = jc;
        return conn;
    }

    private SessionTab AddTab(SessionTab tab)
    {
        tab.Session.StatusChanged += s =>
        {
            // 같은 상태가 다시 오면(재연결 시도 등) 탭 줄·현재 탭 정보(파일 쓰기)를 다시 갱신하지 않는다
            if (tab.Status == s) return;
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
            case "reopentab": ReopenClosedTab(); break;
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
        // SFTP 편집 중인 파일이 있으면 그 확인 하나로 대신한다 (설정으로 끌 수 없음)
        if (ConfirmCloseWithSftpEdits(tabs, closingApp: false) is { } ok) return ok;
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
        if (_tabs.Contains(tab)) RememberClosedTab(tab);   // "닫은 탭 다시 열기" (Ctrl+Shift+R)
        try { tab.Session.CloseSession(); } catch { }
        SessionsHost.Children.Remove(tab.View);
        _tabs.Remove(tab);
        // 끌기 준비 상태가 닫은 탭(과 그 뷰)을 다음 끌기까지 붙잡지 않게
        if (ReferenceEquals(_tabDragging, tab)) _tabDragging = null;

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
        // Ctrl+Shift+R : 마지막으로 닫은 탭 다시 열기 (입력란에서는 무시)
        else if (ctrl && shift && e.Key == Key.R && Keyboard.FocusedElement is not TextBox and not PasswordBox) { ReopenClosedTab(); e.Handled = true; }
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
        // 편집 패널은 홈 화면에 있을 때만 (다른 탭의 WPF 포커스에서 숨은 편집 패널이 저장·닫히지 않게), 닫는 중이면 무시
        else if (ctrl && e.Key == Key.S && _editorOpen && HomeView.Visibility == Visibility.Visible)
        {
            SaveEditor();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _editorOpen && HomeView.Visibility == Visibility.Visible)
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
        menu.Closed -= OnTabMenuClosed;
        menu.Closed += OnTabMenuClosed;
        void Add(string header, Action act, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => act();
            menu.Items.Add(item);
        }
        var idx = _tabs.IndexOf(tab);
        var disconnected = _tabs.Where(t => t.Status == SessionStatus.Disconnected).ToList();
        Add(L.T("다시 연결"), () => { SelectTab(tab); tab.Session.Reconnect(); }, tab.Status == SessionStatus.Disconnected);
        if (tab.Host != null) Add(L.T("같은 호스트로 새 탭"), () => Connect(tab.Host, TabUsePublic(tab)));
        if (tab.Protocol == Protocol.Local) Add(L.T("새 PowerShell 탭"), OpenLocalTerminal);
        if (tab.View is RdpSessionView rdp) Add(L.T("전체 화면"), () => { SelectTab(tab); rdp.EnterFullScreen(); }, rdp.IsConnected);
        Add(L.T("탭 이름 바꾸기..."), () =>
        {
            if (PromptDialog.Ask(this, L.T("탭 이름 바꾸기"), L.T("새 이름"), tab.Title) is { Length: > 0 } name)
            {
                tab.Title = name;
                if (tab.IsSelected) UpdateActiveContext();   // Claude 에게 알려 주는 현재 탭 정보(active-session.txt)도 새 이름으로
            }
        });
        if (tab.Host != null && _hosts.Contains(tab.Host)) Add(L.T("호스트 편집"), () => { SelectHome(); OpenEditor(tab.Host); });
        menu.Items.Add(new Separator());
        Add(L.T("닫기"), () => CloseTabs(new[] { tab }));
        Add(L.T("다른 탭 모두 닫기"), () => CloseTabs(_tabs.Where(t => t != tab).ToList()), _tabs.Count > 1);
        Add(L.T("오른쪽 탭 모두 닫기"), () => CloseTabs(_tabs.Skip(idx + 1).ToList()), idx < _tabs.Count - 1);
        Add(L.T("끊긴 탭 모두 다시 연결"), () => ReconnectAll(disconnected), disconnected.Count > 0);
        Add(L.T("끊긴 탭 모두 닫기"), () => CloseTabs(disconnected), disconnected.Count > 0);
        menu.Items.Add(new Separator());
        Add(L.T("닫은 탭 다시 열기 (Ctrl+Shift+R)"), ReopenClosedTab, CanReopenClosedTab);
    }

    /// <summary>
    /// 탭 메뉴(TabMenu)는 창 리소스 하나를 모든 탭이 함께 쓴다 → 닫힌 뒤에도 항목의 람다(마지막 탭·끊긴 탭 목록)와
    /// PlacementTarget(탭 항목 → 탭 → 세션 뷰)이 남아, 메뉴로 닫은 탭이 다음에 메뉴를 열 때까지 해제되지 않는다.
    /// 항목 클릭은 메뉴가 닫히고 그려진 뒤(Render 우선순위) 실행되므로, 그보다 낮은 ApplicationIdle 에서 비운다.
    /// </summary>
    private void OnTabMenuClosed(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (menu.IsOpen) return;   // 그 사이 다시 열렸으면 새 항목을 지우지 않는다
            menu.Items.Clear();
            menu.ClearValue(ContextMenu.PlacementTargetProperty);
        }, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// 끊긴 탭 여러 개 다시 연결. RDP 는 계정 입력 창을 탭마다 차례로 띄우므로, 사용자가 한 번 취소하면
    /// 나머지 탭은 묻지 않고 멈춘다 (취소해도 다음 창이 계속 뜨던 문제).
    /// </summary>
    private void ReconnectAll(IReadOnlyList<SessionTab> tabs)
    {
        foreach (var t in tabs.ToList())
        {
            // 앞 탭의 계정 입력 창(중첩 메시지 루프)이 떠 있는 동안 닫혔거나 이미 다시 연결된 탭은 건너뛴다
            if (!_tabs.Contains(t) || t.Status != SessionStatus.Disconnected) continue;
            t.Session.Reconnect();
            if (t.View is RdpSessionView { LastConnectCancelled: true }) break;
        }
    }

    // ---------- 탭 끌어서 순서 바꾸기 ----------

    private Point _tabDragStart;
    private SessionTab? _tabDragging;

    private void OnTabDragStart(object sender, MouseButtonEventArgs e)
    {
        _tabDragStart = e.GetPosition(this);
        _tabDragging = (sender as FrameworkElement)?.DataContext as SessionTab;
    }

    /// <summary>버튼을 떼면 끌기 준비를 푼다 (그냥 클릭한 탭이 나중에 다른 곳에서 누른 채 지나갈 때 끌려가지 않게)</summary>
    private void OnTabDragEnd(object sender, MouseButtonEventArgs e) => _tabDragging = null;

    private void OnTabDragMove(object sender, MouseEventArgs e)
    {
        if (_tabDragging == null || e.LeftButton != MouseButtonState.Pressed) return;
        // 누른 탭 위에서 움직일 때만 끌기 시작
        if (!ReferenceEquals((sender as FrameworkElement)?.DataContext, _tabDragging)) { _tabDragging = null; return; }
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
            // 폭이 다른 탭끼리 바꾸면 자리를 바꾼 직후 커서가 다시 상대 탭 위에 놓여 왔다 갔다 하던 문제 —
            // 바꾼 뒤에도 커서가 끄는 탭 위에 있게 될 만큼 들어왔을 때만 옮긴다
            var overBox = TabsList.ItemContainerGenerator.ContainerFromItem(over) as FrameworkElement;
            var dragBox = TabsList.ItemContainerGenerator.ContainerFromItem(dragged) as FrameworkElement;
            if (from >= 0 && to >= 0 &&
                (overBox == null || dragBox == null ||
                 ShouldReorderTab(from, to, e.GetPosition(overBox).X, overBox.ActualWidth, dragBox.ActualWidth)))
                _tabs.Move(from, to);
        }
        e.Handled = true;
    }

    /// <summary>
    /// 끄는 탭(폭 draggedWidth)을 over 탭(폭 overWidth, 커서 x 는 over 기준) 자리로 옮길지.
    /// 오른쪽으로 옮기면 over 탭이 왼쪽으로 draggedWidth 만큼 밀리므로 x ≥ overWidth − draggedWidth 여야
    /// 옮긴 뒤 커서가 끄는 탭 위에 남는다 (왼쪽도 대칭). 탭 절반은 넘어야 옮겨 살짝 걸친 것만으로 바뀌지 않게 한다.
    /// </summary>
    internal static bool ShouldReorderTab(int from, int to, double x, double overWidth, double draggedWidth)
    {
        if (from == to || overWidth <= 0) return false;
        return from < to
            ? x >= Math.Max(overWidth / 2, overWidth - draggedWidth)
            : x <= Math.Min(overWidth / 2, draggedWidth);
    }

    /// <summary>창을 닫을 때 확인할 세션 수: 로컬 PowerShell 을 뺀, 끊기지 않은(연결됨·연결 중) 원격 세션</summary>
    internal static int LiveRemoteSessionCount(IEnumerable<(Protocol protocol, SessionStatus status)> tabs) =>
        tabs.Count(t => t.protocol != Protocol.Local && t.status != SessionStatus.Disconnected);

    private void OnTabDrop(object sender, DragEventArgs e) => e.Handled = true;
}
