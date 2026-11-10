using System;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace Nubis;

/// <summary>로컬 프로세스 터미널 (PowerShell 탭, Claude Code 패널)</summary>
public sealed class LocalTerminalView : UserControl, ISessionView
{
    private readonly XtermView _term = new();
    private readonly Func<string> _commandLine;
    private readonly string _workingDir;
    private readonly string _exitHint;
    private PseudoConsoleProcess? _pty;
    private bool _closed;
    private SessionStatus _status = SessionStatus.Connecting;

    public event Action<SessionStatus>? StatusChanged;
    public XtermView Terminal => _term;
    public SessionStatus Status => _status;

    /// <param name="commandLine">실행할 명령줄 (재시작할 때마다 다시 계산)</param>
    public LocalTerminalView(Func<string> commandLine, string workingDir, string? exitHint = null)
    {
        _commandLine = commandLine;
        _workingDir = workingDir;
        _exitHint = exitHint ?? L.T("프로세스가 종료되었습니다.");
        Content = _term;
        _term.Ready += Start;
        _term.Resized += (c, r) => _pty?.Resize((short)c, (short)r);
        _term.Input += d =>
        {
            if (_status == SessionStatus.Connected) _pty?.Write(d);
            else if (_status == SessionStatus.Disconnected && d.Contains('\r')) Start();
        };
    }

    private void Start()
    {
        if (_closed) return;
        _pty?.Dispose();
        var pty = new PseudoConsoleProcess();
        pty.Output += _term.Write;
        pty.Exited += code => Dispatcher.BeginInvoke(() =>
        {
            if (_closed || _pty != pty) return;
            SetStatus(SessionStatus.Disconnected);
            _term.Write("\r\n");
            _term.Info(L.F("{0} (종료 코드 {1})  Enter 키를 누르면 다시 시작합니다.", _exitHint, code), "33");
        });
        try
        {
            pty.Start(_commandLine(), _workingDir, (short)_term.Cols, (short)_term.Rows);
            _pty = pty;
            SetStatus(SessionStatus.Connected);
            _term.FocusTerminal();
        }
        catch (Exception ex)
        {
            pty.Dispose();
            _term.Info(L.F("실행 실패: {0}", ex.Message), "31");
            SetStatus(SessionStatus.Disconnected);
        }
    }

    private void SetStatus(SessionStatus s)
    {
        _status = s;
        StatusChanged?.Invoke(s);
    }

    public void FocusSession() => _term.FocusTerminal();
    public async Task<string?> ReadTerminalAsync(int lines) => await _term.ReadBufferAsync(lines);
    public async Task<string?> GetSelectionAsync() => await _term.GetSelectionAsync();

    public bool SendText(string text)
    {
        if (_pty == null || _status != SessionStatus.Connected) return false;
        _pty.Write(text);
        return true;
    }

    public void CloseSession()
    {
        _closed = true;
        _pty?.Dispose();
        _term.Close();
    }
}
