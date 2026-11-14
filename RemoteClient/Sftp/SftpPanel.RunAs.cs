using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using Renci.SshNet;

namespace Nubis;

/// <summary>
/// SFTP 패널의 사용자 전환 — 터미널이 sudo su - 등으로 다른 사용자가 되면 SFTP 도 그 사용자 권한으로 (SudoSftp).
/// 실패한 사용자는 이 패널에서 다시 시도하지 않는다 (sudo 실패 누적·비밀번호 창 반복 방지).
/// </summary>
public partial class SftpPanel
{
    /// <summary>SFTP 를 여는 사용자 (null 이면 로그인 계정)</summary>
    private string? _runAs;
    /// <summary>확인된 sudo 비밀번호 — 같은 패널에서 다시 열 때 묻지 않게 (메모리에만)</summary>
    private string? _sudoPassword;
    private readonly HashSet<string> _runAsFailed = [];

    /// <summary>지금 SFTP 를 여는 사용자 (null 이면 로그인 계정)</summary>
    public string? RunAs => _runAs;

    /// <summary>이 사용자로 전환을 시도했다가 실패한 적이 있음</summary>
    public bool RunAsFailed(string? user) => user != null && _runAsFailed.Contains(user);

    /// <summary>
    /// SFTP 를 user 권한으로 다시 연다 (null 이면 로그인 계정). 전송 중이면 바꾸지 않는다.
    /// 성공하면 Home 이 그 사용자의 홈이 된다. 실패하면 사유를 상태줄에 보이고 false.
    /// </summary>
    public async Task<bool> RunAsAsync(string? user)
    {
        if (user == _runAs) return true;
        if (RunAsFailed(user) || _busy) return false;
        var previous = _runAs;
        _runAs = user;
        var old = _client;
        _client = null;
        try { old?.Dispose(); } catch { }
        try
        {
            await ClientAsync();
            if (_runAs != user) return false;   // 전환 실패 → ClientAsync 가 로그인 계정으로 되돌림
            UpdateRunAsBadge();
            if (user != null) ShowStatus(L.F("터미널을 따라 SFTP 를 {0} 권한으로 엽니다.", user));
            return true;
        }
        catch (Exception ex)
        {
            _runAs = previous;
            UpdateRunAsBadge();
            ShowMessage(ex.Message);
            return false;
        }
    }

    /// <summary>ClientAsync 에서: 다른 사용자 권한이면 sudo 로 연다. 실패하면 로그인 계정으로 되돌리고 null</summary>
    private async Task<SftpClient?> OpenRunAsAsync((HostEntry Host, string User, string Password) auth)
    {
        if (_runAs is not { } user) return null;
        if (user == auth.User) { _runAs = null; return null; }
        var shell = _getShell?.Invoke();
        try
        {
            var (client, home, password) = await Task.Run(() =>
                SudoSftp.Open(shell, user, auth.Password, _sudoPassword, reason => Dispatcher.Invoke(() => AskSudoPassword(auth, user, reason))));
            if (password != null) _sudoPassword = password;
            _home = home;
            return client;
        }
        catch (SudoSftp.SudoException ex)
        {
            _runAsFailed.Add(user);
            _runAs = null;
            UpdateRunAsBadge();
            ShowStatus(L.F("SFTP 를 {0} 권한으로 열지 못해 {1} 로 엽니다 — {2}", user, auth.User, ex.Message));
            return null;
        }
    }

    private string? AskSudoPassword((HostEntry Host, string User, string Password) auth, string user, string reason)
    {
        var instruction = L.F("터미널이 {0} 로 전환되어 SFTP 도 {0} 권한으로 엽니다.", user);
        if (reason.Length > 0) instruction += "\n" + reason;
        var answers = AuthPromptDialog.Ask(Window.GetWindow(this), $"{auth.User}@{auth.Host.DisplayName} ({auth.Host.Address})",
            new AuthPromptRequest(L.T("sudo 비밀번호"), instruction, [new AuthPromptField(L.F("[sudo] {0} 의 비밀번호", auth.User), true)]));
        return answers is { Length: > 0 } ? answers[0] : null;
    }

    /// <summary>터미널이 다시 연결됨 — 새 셸은 로그인 계정이므로 다음 연결부터 로그인 계정으로, 실패 기록도 지운다</summary>
    public void ResetRunAs() => Dispatcher.Invoke(() =>
    {
        _runAs = null;
        _runAsFailed.Clear();
        UpdateRunAsBadge();
    });

    private void UpdateRunAsBadge()
    {
        RunAsBadge.Visibility = _runAs != null ? Visibility.Visible : Visibility.Collapsed;
        RunAsText.Text = _runAs ?? "";
    }
}
