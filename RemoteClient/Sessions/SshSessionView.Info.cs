using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace Nubis;

/// <summary>
/// Claude 의 get_terminal_info: 앱이 이미 알고 있는 터미널 상태(현재 폴더·사용자)를 명령 없이 알려준다.
/// 예전에는 "지금 폴더가 어디야" 에 pwd 를 보내고(승인 창) 출력을 기다렸다(몇 초~수십 초).
/// </summary>
public sealed partial class SshSessionView
{
    public async Task<string?> GetTerminalInfoAsync()
    {
        var lines = new List<string>
        {
            $"서버: {_host.DisplayName} ({_host.Address}:{_host.Port})" + (_host.Jump is { } j ? $" · 점프 서버 {j.DisplayName}" : ""),
            $"연결: {MainWindow.StatusText(_status)}",
        };
        if (_status != SessionStatus.Connected) return string.Join("\n", lines);

        var screen = await _term.ReadBufferAsync(8);
        var promptUser = TerminalCwd.PromptUser(screen);
        lines.Add($"로그인 계정: {_authUser ?? _host.Username}");
        if (promptUser != null && promptUser != _authUser) lines.Add($"현재 셸 사용자: {promptUser} (sudo su - 등으로 전환됨)");

        // 현재 폴더: 셸이 알려준 값(OSC 7) → 프롬프트에서 읽은 값. 홈 폴더를 모르면 ~ 그대로
        var home = _sftp is { IsOpen: true } ? _sftp.Home : null;
        string? cwd;
        string how;
        if (_lastCwd != null) { cwd = _lastCwd; how = "셸이 알려준 값 (OSC 7)"; }
        else
        {
            cwd = TerminalCwd.Resolve(screen, home ?? "~", _promptCwd ?? home) ?? _promptCwd;
            how = cwd == null ? "" : "프롬프트에서 읽은 값";
        }
        lines.Add(cwd != null ? $"현재 폴더: {cwd} ({how})" : "현재 폴더: 알 수 없음 (프롬프트에 경로가 없고 셸이 알려주지 않음 — read_terminal 로 화면을 보거나 사용자 승인 후 pwd)");

        var atPrompt = promptUser != null || TerminalCwd.PowerShellCwd(screen) != null;
        lines.Add(atPrompt ? "상태: 셸 프롬프트 (명령 입력 대기)" : "상태: 프롬프트가 보이지 않음 — 명령 실행 중이거나 프로그램(vim·mysql 등)·질문(비밀번호 등) 화면일 수 있음");
        if (_sftp is { IsOpen: true } panel)
            lines.Add($"SFTP 패널: {(panel.RunAs != null ? $"{panel.RunAs} 권한 (sudo)" : "로그인 계정")} · 홈 {panel.Home}");
        return string.Join("\n", lines);
    }

    /// <summary>화면에 셸 프롬프트가 보이는가 (send_to_terminal 의 읽기 전용 자동 승인 조건)</summary>
    public async Task<bool> IsAtPromptAsync()
    {
        if (_status != SessionStatus.Connected) return false;
        var screen = await _term.ReadBufferAsync(4);
        return TerminalCwd.PromptUser(screen) != null || XtermView.PromptPath(screen, "~") != null;
    }
}
