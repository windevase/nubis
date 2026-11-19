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
        // SSH 탭의 열린 포트 포워딩 수 → 탭 줄 배지
        if (tab.View is SshSessionView ssh) ssh.ForwardsChanged += n => tab.ForwardCount = n;
        _tabs.Add(tab);
        Grid.SetRow(tab.View, 1);   // 0행은 나란히 보기 머리줄
        // 나란히 보는 쪽을 클릭하면 그 탭이 고른 탭이 된다 (MainWindow.SplitView.cs)
        tab.View.IsKeyboardFocusWithinChanged += OnSessionViewFocusWithin;
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
            case "search": ShowCommandPalette(); break;   // 터미널 포커스에서 Ctrl+Shift+K → 명령 팔레트
            default:
                if (key.StartsWith("tab") && int.TryParse(key[3..], out var n)) SelectTabNumber(n);
                break;
        }
    });

    private void SelectTab(SessionTab tab)
    {
        // 나란히 보는 짝이면 두 탭을 함께 보인다 (MainWindow.SplitView.cs). 현재 화면 정보도 여기서 쓴다
        ShowSessionLayout(tab);
        Dispatcher.BeginInvoke(() => tab.Session.FocusSession(), DispatcherPriority.Input);
        // 넘친 탭도 고르면 화면 안으로
        Dispatcher.BeginInvoke(() => (TabsList.ItemContainerGenerator.ContainerFromItem(tab) as FrameworkElement)?.BringIntoView(),
            DispatcherPriority.Loaded);
    }

    private void SelectHome()
    {
        foreach (var t in _tabs) t.IsSelected = false;
        HideSplitLayout();   // 나란히 보기 훅·머리줄 (MainWindow.SplitView.cs)
        SessionsHost.Visibility = Visibility.Collapsed;
        HomeView.Visibility = Visibility.Visible;
        // 선택된 홈 탭은 본문 바탕(Layer2)과 이어진다. 선택 해제는 Brushes.Transparent — XAML 트리거가 이 인스턴스로 판단한다
        HomeTab.Background = (Brush)FindResource("Layer2Bg");
        UpdateActiveContext();
        Dispatcher.BeginInvoke(ReleaseHiddenSessionFocus, DispatcherPriority.Input);
    }

    /// <summary>
    /// 홈으로 왔는데 키보드 포커스가 숨은 세션(터미널 WebView2 · RDP ActiveX 의 자식 HWND)에 남았거나, 닫힌 탭과 함께 사라졌으면
    /// 메인 창으로 되돌린다. Win32 포커스는 창을 숨겨도 옮겨지지 않아, 홈에서 친 글자가 보이지 않는 터미널로 가거나
    /// (닫힌 경우) 클릭하기 전까지 Ctrl+K 등 단축키가 먹지 않는다. 보이는 Claude 패널 · 홈 화면의 포커스는 건드리지 않는다
    /// </summary>
    private void ReleaseHiddenSessionFocus()
    {
        if (!IsActive || HomeView.Visibility != Visibility.Visible) return;
        var focus = GetFocus();
        var lost = focus == IntPtr.Zero || !IsWindowVisible(focus);
        // WPF 쪽 포커스가 숨은 세션에 남은 경우는 Win32 포커스가 메인 창 자체일 때만 본다 — 보이는 Claude 패널(WebView2 자식 HWND)에
        // 실제 포커스가 있는데 WPF 포커스 기록만 세션 뷰에 남아 있으면 Claude 패널에서 포커스를 빼앗았다
        var mainHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (lost || (focus == mainHwnd && SessionsHost.IsKeyboardFocusWithin)) Focus();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

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
        CloseTabsNow(tabs);
    }

    /// <summary>확인 없이 여러 탭을 닫는다 (CloseTabs 의 확인 뒤 부분 — MCP close_sessions 는 승인 창이 확인을 대신한다)</summary>
    private void CloseTabsNow(IReadOnlyCollection<SessionTab> tabs)
    {
        // 확인 창(중첩 메시지 루프)이 떠 있는 동안 이미 닫힌 탭은 뺀다
        var closing = tabs.Where(_tabs.Contains).Distinct().ToList();
        if (closing.Count == 0) return;
        if (closing.Count == 1) { CloseTab(closing[0]); return; }
        // 여러 개: 지금 탭이 닫히면 남는 탭 중 오른쪽(없으면 왼쪽) 이웃으로 한 번만 옮긴다 —
        // 하나씩 닫으며 곧 닫힐 탭을 차례로 고르면 그 뷰(RDP 등)를 보였다 숨기고 현재 탭 정보도 매번 다시 쓴다
        var set = new HashSet<SessionTab>(closing);
        var selected = _tabs.FirstOrDefault(t => t.IsSelected);
        // 나란히 보던 짝의 한쪽(고른 탭)이 닫히면 남은 쪽이 화면을 채운다 — 고른 탭이 짝 밖이면 평소처럼 이웃으로
        var next = selected != null && set.Contains(selected)
            ? SplitLayout.NextAfterClose(_split, selected, set) ?? NeighborAfterClose(_tabs, _tabs.IndexOf(selected), set) : null;
        foreach (var t in closing) CloseTab(t, reselect: false);
        if (selected == null) UpdateActiveContext();
        else if (!set.Contains(selected)) ShowSessionLayout(selected);   // 나란히 보던 다른 쪽이 닫혔을 수 있다 — 배치를 다시
        else if (next != null && _tabs.Contains(next)) SelectTab(next);
        else if (_tabs.Count > 0) SelectTab(_tabs[^1]);
        else SelectHome();
    }

    /// <summary>index 의 탭을 포함해 closing 을 닫을 때 남는 탭 중 고를 이웃: 오른쪽에서 가장 가까운 것, 없으면 왼쪽. 다 닫히면 null</summary>
    internal static T? NeighborAfterClose<T>(IReadOnlyList<T> all, int index, ISet<T> closing) where T : class
    {
        for (var i = index + 1; i < all.Count; i++) if (!closing.Contains(all[i])) return all[i];
        for (var i = Math.Min(index, all.Count) - 1; i >= 0; i--) if (!closing.Contains(all[i])) return all[i];
        return null;
    }

    /// <param name="reselect">false =닫은 탭이 지금 탭이어도 다른 탭을 고르지 않는다 (여러 개를 닫는 쪽이 마지막에 한 번 고른다)</param>
    private void CloseTab(SessionTab tab, bool reselect = true)
    {
        var idx = _tabs.IndexOf(tab);
        var wasSelected = tab.IsSelected;
        if (idx >= 0) RememberClosedTab(tab);   // "닫은 탭 다시 열기" (Ctrl+Shift+R)
        // 나란히 보던 탭이면 나란히 보기를 푼다 — 남은 쪽이 화면을 채운다
        var splitBefore = _split;
        var (pair, survivor) = SplitLayout.AfterClose(_split, new[] { tab });
        _split = pair;
        try { tab.Session.CloseSession(); } catch { }
        tab.View.IsKeyboardFocusWithinChanged -= OnSessionViewFocusWithin;
        SessionsHost.Children.Remove(tab.View);
        _tabs.Remove(tab);
        tab.IsSelected = false;
        tab.IsBeside = false;
        // 끌기 준비 상태가 닫은 탭(과 그 뷰)을 다음 끌기까지 붙잡지 않게
        if (ReferenceEquals(_tabDragging, tab)) _tabDragging = null;
        if (!reselect) return;

        if (!wasSelected)
        {
            // 나란히 보이던 쪽이 닫혔으면 고른 탭 하나로 배치를 다시 잡는다
            if (!ReferenceEquals(splitBefore, _split) && _tabs.FirstOrDefault(t => t.IsSelected) is { } cur) ShowSessionLayout(cur);
            else UpdateActiveContext();
            return;
        }
        if (survivor != null && _tabs.Contains(survivor)) SelectTab(survivor);
        else if (_tabs.Count == 0) SelectHome();
        else SelectTab(_tabs[Math.Min(Math.Max(idx, 0), _tabs.Count - 1)]);
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
        else if (ctrl && e.Key == Key.K)
        {
            // 호스트 · 탭 · 동작 통합 검색 (Ctrl+K). Ctrl+Shift+K 도 같게 — 터미널 포커스에서 쓰는 키와 맞춘다 (OnTerminalHotkey "search")
            ShowCommandPalette();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.F && !shift && EditorLayer.Visibility != Visibility.Visible && Keyboard.FocusedElement is not TextBox)
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

    /// <summary>Ctrl+F: 홈으로 가서 검색창에 포커스 (Ctrl+K 는 명령 팔레트)</summary>
    private void FocusSearch()
    {
        SelectHome();
        NavHosts.IsChecked = true;
        Dispatcher.BeginInvoke(() => { SearchBox.Focus(); SearchBox.SelectAll(); }, DispatcherPriority.Input);
    }

    /// <summary>창을 닫을 때 확인할 세션 수: 로컬 PowerShell 을 뺀, 끊기지 않은(연결됨·연결 중) 원격 세션</summary>
    internal static int LiveRemoteSessionCount(IEnumerable<(Protocol protocol, SessionStatus status)> tabs) =>
        tabs.Count(t => t.protocol != Protocol.Local && t.status != SessionStatus.Disconnected);

    /// <summary>
    /// 앱 종료 확인 문구: 원격 세션 수 + 실행 중인 쿠버네티스 포트 포워딩 수 (클러스터 탭은 로컬 터미널이라 세션 수에 들지 않는다).
    /// 둘 다 0 이면 null (묻지 않음)
    /// </summary>
    internal static string? CloseAppPrompt(int liveSessions, int k8sForwards) => (liveSessions, k8sForwards) switch
    {
        ( <= 0, <= 0) => null,
        (_, <= 0) => L.F("연결 중인 세션 {0}개가 종료됩니다. 닫을까요?", liveSessions),
        ( <= 0, _) => L.F("실행 중인 쿠버네티스 포트 포워딩 {0}개가 멈춥니다. 닫을까요?", k8sForwards),
        _ => L.F("연결 중인 세션 {0}개가 종료되고 쿠버네티스 포트 포워딩 {1}개가 멈춥니다. 닫을까요?", liveSessions, k8sForwards),
    };
}
