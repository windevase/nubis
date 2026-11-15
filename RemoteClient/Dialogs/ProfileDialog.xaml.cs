using System.Windows;
using System.Windows.Input;

namespace Nubis;

public partial class ProfileDialog : Window
{
    private readonly CloudProfile _p;

    private ProfileDialog(CloudProfile p)
    {
        InitializeComponent();
        _p = p;
        Badge.Background = p.ProviderBrush;
        BadgeText.Text = p.ProviderText;
        TitleText.Text = p.GroupName;
        SubText.Text = p.Provider switch
        {
            CloudProvider.Azure => L.F("구독 {0}", p.Name),
            CloudProvider.Gcp => L.F("프로젝트 {0}", p.Name),
            _ => L.F("프로필 {0}", p.Name),
        };
        NameBox.Text = p.GroupName;
        UserBox.Text = p.DefaultUsername;
        PassBox.Password = p.DefaultPassword;
        // 비워 두면 설정의 기본 포트를 따른다
        SshPortBox.Text = p.SshPort?.ToString() ?? "";
        RdpPortBox.Text = p.RdpPort?.ToString() ?? "";
        SshPortBox.Tag = L.F("기본 포트 ({0})", HostEntry.PreferredPort(Protocol.Ssh));
        RdpPortBox.Tag = L.F("기본 포트 ({0})", HostEntry.PreferredPort(Protocol.Rdp));
        (p.UsePublicIp ? PublicIp : PrivateIp).IsChecked = true;
        StoppedBox.IsChecked = p.IncludeStopped;
        if (p.HiddenInstances.Count > 0)
        {
            UnhideBox.Content = L.F("목록에서 지운 서버 {0}개 다시 표시 (다음 동기화부터)", p.HiddenInstances.Count);
            UnhideBox.Visibility = Visibility.Visible;
        }
        Loaded += (_, _) => UserBox.Focus();
    }

    /// <summary>저장하면 true. 포트/주소 방식이 바뀌면 다음 동기화부터 새 호스트에 적용된다.</summary>
    public static bool Edit(Window owner, CloudProfile p) => new ProfileDialog(p) { Owner = owner }.ShowDialog() == true;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        // 잘못된 포트는 이유를 보여 주고 그 칸으로 이동 (전에는 아무 안내 없이 저장만 안 됐다)
        if (!TryPort(SshPortBox.Text, out var ssh)) { ShowError(SshPortBox); return; }
        if (!TryPort(RdpPortBox.Text, out var rdp)) { ShowError(RdpPortBox); return; }
        ErrorText.Visibility = Visibility.Collapsed;

        _p.DisplayName = NameBox.Text.Trim();
        _p.DefaultUsername = UserBox.Text.Trim();
        _p.DefaultPassword = PassBox.Password;
        _p.SshPort = ssh;
        _p.RdpPort = rdp;
        _p.UsePublicIp = PublicIp.IsChecked == true;
        _p.IncludeStopped = StoppedBox.IsChecked == true;
        if (UnhideBox.IsChecked == true) _p.HiddenInstances.Clear();
        DialogResult = true;
    }

    private void ShowError(System.Windows.Controls.TextBox box)
    {
        ErrorText.Text = L.T("포트는 1~65535 사이 숫자로 입력하세요.");
        ErrorText.Visibility = Visibility.Visible;
        box.Focus();
        box.SelectAll();
    }

    /// <summary>빈 칸 = null(기본 포트 따름), 1~65535 = 지정 포트</summary>
    internal static bool TryPort(string text, out int? port)
    {
        port = null;
        text = text.Trim();
        if (text.Length == 0) return true;
        if (!int.TryParse(text, out var v) || v is < 1 or > 65535) return false;
        port = v;
        return true;
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
