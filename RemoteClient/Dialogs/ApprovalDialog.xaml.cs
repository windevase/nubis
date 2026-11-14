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
        // 창이 뜨자마자 반사적으로/겹친 클릭으로 승인되지 않도록 잠시 실행 버튼을 잠근다
        OkButton.IsEnabled = false;
        var remain = 3;
        OkButton.Content = L.F("실행 ({0})", remain);
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            if (--remain > 0) { OkButton.Content = L.F("실행 ({0})", remain); return; }
            timer.Stop();
            OkButton.Content = L.T("실행");
            OkButton.IsEnabled = true;
        };
        Loaded += (_, _) => timer.Start();
    }

    public static bool Ask(Window owner, string title, string description, IReadOnlyList<string> targets, string command)
    {
        var dlg = new ApprovalDialog { Owner = owner };
        dlg.TitleText.Text = title;
        dlg.DescText.Text = description;
        dlg.TargetsLabel.Text = L.F("대상 {0}개", targets.Count);
        dlg.TargetsText.Text = string.Join("\n", targets);
        // 보이지 않는 제어 문자(Ctrl-U·ESC 시퀀스 등)는 \xNN 으로 드러낸다 — 화면에 보이는 명령과 실제로 실행되는 명령이 달라지는 것을 사용자가 알아보게
        var visible = McpHelpers.VisibleControlChars(command);
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
        owner.Activate();
        return dlg.ShowDialog() == true;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
