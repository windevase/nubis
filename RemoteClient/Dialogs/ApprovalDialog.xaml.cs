using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;

namespace Nubis;

/// <summary>Claude 가 서버에 명령을 보내기 전 사용자 승인. 거부가 기본(Esc).</summary>
public partial class ApprovalDialog : Window
{
    // 실수로 승인하기 쉬운 위험 명령은 경고 표시
    private static readonly Regex Dangerous = new(
        @"(?<![\w-])(rm\s+(-\w+\s+)*-\w*[rRf]|rm\s+.*--(recursive|force)|rd\s+/s|rmdir\s+/s|find\s.*-delete|shred|wipefs|format\s+[a-z]:|Clear-Content|Clear-Disk|Remove-Partition|mkfs|dd\s+if=|shutdown|reboot|halt|poweroff|init\s+[06]|systemctl\s+(stop|restart|disable|kill)|" +
        @"kill(all)?\s|pkill|userdel|passwd|chmod\s+-R|chown\s+-R|iptables|firewall-cmd|truncate|>\s*/dev/sd|Remove-Item|rmdir|del\s+/[sq]|" +
        @"Stop-Computer|Restart-Computer|Format-Volume|Stop-Service|Restart-Service|Stop-Process|DROP\s+(TABLE|DATABASE)|DELETE\s+FROM|TRUNCATE\s+TABLE)",
        RegexOptions.IgnoreCase);

    /// <summary>서비스 중단·데이터 삭제 가능성이 있는 명령인지 (경고 표시용)</summary>
    internal static bool IsDangerous(string command) => Dangerous.IsMatch(command);

    private ApprovalDialog()
    {
        InitializeComponent();
        // 창이 뜨자마자 반사적으로/겹친 클릭으로 승인되지 않도록 잠시 실행 버튼을 잠근다.
        // 잠금은 창이 활성화될 때마다 다시 건다 — 다른 프로그램에서 입력 중일 때 활성화 없이(ShowActivated=false) 맨 위에 뜬 창을
        // 무심코 클릭하면, 그 클릭이 창을 활성화하는 순간 잠금이 시작돼 같은 클릭으로 승인되지 않는다
        var remain = 3;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        void Lock()
        {
            timer.Stop();
            remain = 3;
            OkButton.IsEnabled = false;
            OkButton.Content = L.F("실행 ({0})", remain);
        }
        Lock();
        timer.Tick += (_, _) =>
        {
            if (--remain > 0) { OkButton.Content = L.F("실행 ({0})", remain); return; }
            timer.Stop();
            OkButton.Content = L.T("실행");
            OkButton.IsEnabled = true;
        };
        Activated += (_, _) =>
        {
            Lock();
            timer.Start();
            // 사용자가 돌아왔으므로 작업 표시줄 깜박임을 멈춘다 (모달이라 소유 창 대신 이 창이 활성화돼 TIMERNOFG 로는 안 멈출 수 있음)
            if (Owner != null) StopFlash(Owner);
        };
        Deactivated += (_, _) => Lock();
        Closed += (_, _) => timer.Stop();
    }

    /// <summary>
    /// 승인 창의 대상 표시: 항목마다 제어 문자·폭 없는 문자를 드러내고(VisibleControlChars), 항목 안의 줄바꿈·줄/문단 구분자도
    /// \n · \u{2028} 로 바꿔 한 항목이 여러 대상처럼 보이지 않게 한다 (호스트 이름은 클라우드 태그 등 외부 값)
    /// </summary>
    internal static string TargetsDisplay(IReadOnlyList<string> targets) =>
        string.Join("\n", targets.Select(t => ShowSeparators(McpHelpers.VisibleControlChars(t)).Replace("\n", "\\n")));

    /// <summary>VisibleControlChars 가 그대로 두는 줄 구분자(U+2028)·문단 구분자(U+2029)도 드러낸다 — WPF 는 이것을 줄바꿈으로 그린다</summary>
    internal static string ShowSeparators(string text) =>
        text.Replace("\u2028", "\\u{2028}").Replace("\u2029", "\\u{2029}");

    /// <summary>뒤에 기다리는 다른 승인 요청 수 표시 (0 이면 숨김). 창이 열려 있는 동안 바뀌면 다시 부른다</summary>
    public void SetWaiting(int count)
    {
        QueueText.Text = count > 0 ? L.F("승인 대기 {0}건 더 있음", count) : "";
        QueueText.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO info);

    /// <summary>작업 표시줄 버튼을 사용자가 창으로 돌아올 때까지 깜박인다 (FLASHW_ALL | FLASHW_TIMERNOFG)</summary>
    private static void Flash(Window w)
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            var info = new FLASHWINFO { hwnd = hwnd, dwFlags = 3 | 12, uCount = uint.MaxValue, dwTimeout = 0 };
            info.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(info);
            FlashWindowEx(ref info);
        }
        catch { }
    }

    /// <summary>깜박임 멈춤 (FLASHW_STOP)</summary>
    private static void StopFlash(Window w)
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;
            var info = new FLASHWINFO { hwnd = hwnd, dwFlags = 0 };
            info.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(info);
            FlashWindowEx(ref info);
        }
        catch { }
    }

    /// <param name="tool">요청한 MCP 도구 이름 (배지로 표시, 생략 가능)</param>
    /// <param name="waiting">이 창 뒤에 기다리는 승인 요청 수</param>
    /// <param name="opened">창을 만든 직후(표시 전) 호출 — 호출한 쪽이 이 창을 기억해 대기 수를 갱신하거나 취소 때 닫는다</param>
    public static bool Ask(Window owner, string title, string description, IReadOnlyList<string> targets, string command,
        string? tool = null, int waiting = 0, Action<ApprovalDialog>? opened = null)
    {
        var dlg = new ApprovalDialog { Owner = owner };
        dlg.TitleText.Text = title;
        if (!string.IsNullOrEmpty(tool))
        {
            dlg.ToolText.Text = tool;
            dlg.ToolBadge.Visibility = Visibility.Visible;
        }
        dlg.SetWaiting(waiting);
        dlg.DescText.Text = description;
        dlg.TargetsLabel.Text = L.F("대상 {0}개", targets.Count);
        dlg.TargetsText.Text = TargetsDisplay(targets);
        // 보이지 않는 제어 문자(Ctrl-U·ESC 시퀀스 등)는 \xNN 으로 드러낸다 — 화면에 보이는 명령과 실제로 실행되는 명령이 달라지는 것을 사용자가 알아보게
        var visible = ShowSeparators(McpHelpers.VisibleControlChars(command));
        dlg.CommandBox.Text = visible;
        var warnings = new List<string>();
        // 제어 문자를 뺀 형태로도 검사한다 ("r\x15m -rf" 처럼 끼워 넣어 경고를 피하지 못하게)
        var stripped = new string(command.Where(ch => !char.IsControl(ch) || ch is '\n' or '\t').ToArray());
        if (IsDangerous(command) || IsDangerous(stripped))
            warnings.Add(L.T("⚠ 서비스 중단·데이터 삭제가 될 수 있는 명령이 포함되어 있습니다. 대상과 명령을 다시 확인하세요."));
        if (visible != command.Replace("\r\n", "\n"))
            warnings.Add(L.F("⚠ 보이지 않는 제어 문자가 포함되어 있습니다 ({0} 처럼 표시). 터미널에서 입력한 줄을 지우거나 다른 명령으로 바꿀 수 있습니다.", @"\x15"));
        if (warnings.Count > 0)
        {
            dlg.WarnText.Text = string.Join("\n", warnings);
            dlg.WarnText.Visibility = Visibility.Visible;
        }
        if (owner.WindowState == WindowState.Minimized) owner.WindowState = WindowState.Normal;
        if (owner.IsActive || owner.OwnedWindows.Cast<Window>().Any(w => w.IsActive))
            owner.Activate();
        else
        {
            // 사용자가 다른 프로그램에서 입력 중이면 키 입력을 빼앗지 않는다 — 창은 위에 띄우되 활성화하지 않고 작업 표시줄을 깜박인다
            dlg.ShowActivated = false;
            Flash(owner);
        }
        opened?.Invoke(dlg);
        return dlg.ShowDialog() == true;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
