using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Nubis;

/// <summary>SSH 탭: SFTP 패널 열기·닫기와 터미널 현재 폴더·사용자(sudo) 따라가기</summary>
public sealed partial class SshSessionView
{
    /// <summary>패널을 연 뒤, 패널이 열리기 전에 알아낸 터미널 현재 폴더가 있으면 그리로</summary>
    private async System.Threading.Tasks.Task OpenSftpAsync(SftpPanel panel)
    {
        await panel.OpenAsync();
        if (!SettingsStore.Current.SftpFollowTerminal) return;
        // 터미널이 이미 다른 사용자(sudo su -)로 전환된 뒤에 패널을 연 경우
        if (FollowPromptUser(await _term.ReadBufferAsync(8))) return;
        if ((_lastCwd ?? _promptCwd ?? await TerminalCwdAsync()) is { } cwd) await panel.FollowAsync(cwd);
    }

    /// <summary>
    /// 다시 연결된 뒤 sudo 사용자였던 패널: 새 연결로 다시 읽어(이때 로그인 계정으로 연결되며 Home 이 로그인 계정 홈으로 바뀐다)
    /// 그 홈으로 옮긴다. 앞 폴더(/root 등)를 못 읽어 오류가 잠깐 보여도 홈으로 옮기면 사라진다.
    /// </summary>
    private static async System.Threading.Tasks.Task RefreshSftpToHomeAsync(SftpPanel panel)
    {
        await panel.RefreshAsync();
        await panel.FollowAsync(panel.Home);
    }

    /// <summary>터미널 현재 폴더: OSC 7 로 받은 값, 없으면 화면 프롬프트에서 읽기</summary>
    private async System.Threading.Tasks.Task<string?> TerminalCwdAsync()
    {
        if (_lastCwd != null) return _lastCwd;
        if (_sftp is not { IsOpen: true }) return _promptCwd;   // 홈 폴더를 아직 모름
        var screen = await _term.ReadBufferAsync(8);
        return TerminalCwd.Resolve(screen, _sftp.Home, _promptCwd ?? _sftp.Home) ?? _promptCwd;
    }

    /// <summary>Enter 친 뒤: OSC 7 을 보내지 않는 셸(대부분의 서버 기본 bash)이면 새 프롬프트가 그려지길 기다린다</summary>
    private void OnCommandEntered()
    {
        // OSC 7 로 폴더를 받는 셸이어도 프롬프트는 본다 — sudo su - 같은 사용자 전환은 프롬프트로만 알 수 있다
        if (!SettingsStore.Current.SftpFollowTerminal || _sftp is not { IsVisible: true, IsOpen: true }) return;
        _watchingPrompt = true;
        _term.WatchPrompt();
    }

    /// <summary>
    /// 프롬프트의 사용자가 바뀌었으면(sudo su - → root, exit → 로그인 계정) SFTP 도 그 사용자 권한으로 바꾼다.
    /// 바꾸는 중이면 true — 폴더 따라가기는 전환이 끝난 뒤 새 홈 기준으로 한다.
    /// </summary>
    private bool FollowPromptUser(string screen)
    {
        if (_sftp == null || _authUser == null || TerminalCwd.PromptUser(screen) is not { } user) return false;
        // 로그인한 서버의 프롬프트일 때만 — 터미널에서 ssh 로 다른 서버에, docker exec 로 컨테이너에 들어간 프롬프트(root@abc123)를
        // 이 서버의 사용자 전환으로 보고 sudo -u 를 시도하지 않게. 로그인 직후 프롬프트의 호스트를 아직 모르면 따라가지 않는다
        var host = TerminalCwd.PromptHost(screen);
        if (_loginPromptHost == null && user == _authUser && host != null) _loginPromptHost = host;
        if (_loginPromptHost == null || !string.Equals(host, _loginPromptHost, StringComparison.OrdinalIgnoreCase)) return false;
        var want = user == _authUser ? null : user;
        if (want == _sftp.RunAs || _sftp.RunAsFailed(want) || _switchingUser) return false;
        _ = SwitchSftpUserAsync(want, screen);
        return true;
    }

    private bool _switchingUser;

    /// <summary>로그인 직후 첫 프롬프트의 호스트 이름 (사용자 전환을 이 서버 안에서만 따라가기 위해). 다시 연결하면 새로 읽는다</summary>
    private string? _loginPromptHost;

    /// <summary>연결 직후 첫 프롬프트가 그려지길 기다려 그 호스트 이름을 기억한다 (최대 약 15초)</summary>
    private async Task CaptureLoginPromptHostAsync(int gen)
    {
        for (var i = 0; i < 30 && _loginPromptHost == null; i++)
        {
            await Task.Delay(500);
            if (_closed || gen != _gen || _status != SessionStatus.Connected) return;
            string screen;
            try { screen = await _term.ReadBufferAsync(4); }
            catch { return; }
            if (_loginPromptHost == null && TerminalCwd.PromptHost(screen) is { } host) _loginPromptHost = host;
        }
    }

    private async System.Threading.Tasks.Task SwitchSftpUserAsync(string? user, string screen)
    {
        _switchingUser = true;
        try
        {
            // 새 셸 — 앞 셸이 OSC 7 로 알려준 폴더는 더 이상 맞지 않는다
            _lastCwd = null;
            _promptCwd = null;
            _lastPromptScreen = null;
            if (_sftp == null || !await _sftp.RunAsAsync(user)) return;
            // 홈이 바뀌었으니(~ = /root 등) 새 홈 기준으로 프롬프트의 폴더를 다시 계산
            if (TerminalCwd.Resolve(screen, _sftp.Home, _sftp.Home) is { } cwd)
            {
                _promptCwd = cwd;
                await _sftp.FollowAsync(cwd);
            }
            else await _sftp.FollowAsync(_sftp.Home);
        }
        finally { _switchingUser = false; }
    }

    /// <summary>
    /// 출력이 그려질 때마다 (첫 Enter 줄 ~ 커서 줄). 새 프롬프트가 보이면 그 폴더로 따라간다.
    /// 감시는 마지막 Enter 뒤 일정 시간 이어진다 — 앞 명령의 프롬프트가 오기 전에 다음 cd 를 친 경우도 마지막 프롬프트까지 따라간다.
    /// </summary>
    private void OnPromptScreen(string screen)
    {
        if (!_watchingPrompt || _sftp == null) return;
        // 화면이 그대로인데 다시 오는 경우(벨 등) — 같은 cd 를 이미 옮긴 폴더 기준으로 한 번 더 계산하지 않게
        if (screen == _lastPromptScreen) return;
        _lastPromptScreen = screen;
        // 커서가 빈 줄(명령 출력 뒤)이면 아직 새 프롬프트가 아니다 — 출력에 섞인 "root@db:~#" 를 사용자 전환·폴더 이동으로 오해하지 않게
        if (!TerminalCwd.CursorLineHasText(screen)) return;
        if (FollowPromptUser(screen)) return;
        if (_lastCwd != null) return;   // 셸이 OSC 7 로 폴더를 알려줌 — 프롬프트로 계산하지 않는다
        if (TerminalCwd.Resolve(screen, _sftp.Home, _promptCwd ?? _sftp.Home) is not { } cwd) return;   // 아직 명령 실행 중
        if (cwd == _promptCwd) return;   // 폴더 그대로 (패널에서 직접 옮겨 둔 위치는 건드리지 않음)
        _promptCwd = cwd;
        if (_lastCwd == null && SettingsStore.Current.SftpFollowTerminal && _sftp.IsVisible) _ = _sftp.FollowAsync(cwd);
    }

    private FrameworkElement BuildToolStrip()
    {
        var res = Application.Current.Resources;
        var sftpButton = new Button
        {
            Style = (Style)res["IconButton"],
            Content = "",
            FontSize = 15,
            Width = 30,
            Height = 30,
            Margin = new Thickness(0, 6, 0, 0),
            ToolTip = L.T("SFTP 파일 (업로드·다운로드)"),
        };
        sftpButton.Click += (_, _) => ToggleSftp();
        var panel = new StackPanel();
        panel.Children.Add(sftpButton);
        return new Border
        {
            Width = 36,
            Background = (System.Windows.Media.Brush)res["SidebarBg"],
            BorderBrush = (System.Windows.Media.Brush)res["Line"],
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = panel,
        };
    }

    /// <param name="focusTerminal">닫을 때 터미널에 포커스를 줄지 (Claude 가 고르지 않은 탭의 패널을 닫을 때는 false)</param>
    public void ToggleSftp(bool? open = null, bool focusTerminal = true)
    {
        var isOpen = _sftpColumn.Width.Value > 0;
        var want = open ?? !isOpen;
        if (want == isOpen) return;
        if (want)
        {
            if (_sftp == null)
            {
                _sftp = new SftpPanel(() => _authUser != null ? (_host, _authUser, _authPassword ?? "") : null, TerminalCwdAsync, () => _client);
                _sftp.CloseRequested += () => ToggleSftp(false);
                Grid.SetColumn(_sftp, 1);
                ((Grid)Content).Children.Add(_sftp);
            }
            _sftp.Visibility = Visibility.Visible;
            _sftpColumn.MinWidth = 260;
            _sftpColumn.Width = new GridLength(_sftpWidth);
            _splitter.Visibility = Visibility.Visible;
            _ = OpenSftpAsync(_sftp);
        }
        else
        {
            _sftpWidth = _sftpColumn.ActualWidth > 0 ? _sftpColumn.ActualWidth : _sftpWidth;
            _sftpColumn.MinWidth = 0;
            _sftpColumn.Width = new GridLength(0);
            _splitter.Visibility = Visibility.Collapsed;
            if (_sftp != null) _sftp.Visibility = Visibility.Collapsed;
            if (focusTerminal) _term.FocusTerminal();
        }
    }
}
