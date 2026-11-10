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
        var viaPublic = saved.HasPublicIp && host.Address == saved.PublicIp && saved.PublicIp != saved.PrimaryAddress;
        // 접속 때 입력한 계정 저장 (목록에 있는 호스트만. 빠른 접속은 저장 안 함)
        Action<string, string>? save = _hosts.Contains(saved) ? (u, p) => SaveEnteredCredentials(saved, u, p) : null;
        FrameworkElement view = host.Protocol == Protocol.Ssh ? new SshSessionView(host, save) : new RdpSessionView(host, save);
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
    /// 접속 때 입력해 로그인에 성공한 계정을 저장. 호스트가 Vault 계정의 비밀번호를 쓰고 있었으면(호스트에 직접 적은
    /// 비밀번호가 없고 Vault 계정에 비밀번호가 있음) 그 Vault 계정의 비밀번호를 바꾼다 — 서버 비밀번호가 바뀐 경우로,
    /// 같은 계정을 쓰는 다른 호스트에도 적용된다. 사용자명이 Vault 와 같으면 호스트에는 비워 둔다(계속 Vault 를 따름).
    /// </summary>
    private void SaveEnteredCredentials(HostEntry saved, string user, string password)
    {
        if (VaultStore.Get(saved.VaultId) is { HasPassword: true } entry && string.IsNullOrEmpty(saved.ProtectedPassword))
        {
            entry.Password = password;
            SaveVault(entry);
            saved.Username = user == entry.Username && string.IsNullOrWhiteSpace(saved.Username) ? "" : user;
        }
        else
        {
            saved.Username = user;
            saved.Password = password;
        }
        SaveHosts();
        RefreshHosts();
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
        Dispatcher.BeginInvoke(() => TabsScroll.ScrollToRightEnd(), DispatcherPriority.Loaded);
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
            CloseTab(t);
            e.Handled = true;
        }
    }

    private void OnTabClose(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SessionTab t) CloseTab(t);
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
        else if (e.Key == Key.Escape && EditorLayer.Visibility == Visibility.Visible)
        {
            CloseEditor();
            e.Handled = true;
        }
    }
}
