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
            CloudProvider.Azure => $"구독 {p.Name}",
            CloudProvider.Gcp => $"프로젝트 {p.Name}",
            _ => $"프로필 {p.Name}",
        };
        NameBox.Text = p.GroupName;
        UserBox.Text = p.DefaultUsername;
        PassBox.Password = p.DefaultPassword;
        SshPortBox.Text = p.SshPort.ToString();
        RdpPortBox.Text = p.RdpPort.ToString();
        (p.UsePublicIp ? PublicIp : PrivateIp).IsChecked = true;
        StoppedBox.IsChecked = p.IncludeStopped;
        if (p.HiddenInstances.Count > 0)
        {
            UnhideBox.Content = $"목록에서 지운 서버 {p.HiddenInstances.Count}개 다시 표시 (다음 동기화부터)";
            UnhideBox.Visibility = Visibility.Visible;
        }
        Loaded += (_, _) => UserBox.Focus();
    }

    /// <summary>저장하면 true. 포트/주소 방식이 바뀌면 다음 동기화부터 새 호스트에 적용된다.</summary>
    public static bool Edit(Window owner, CloudProfile p) => new ProfileDialog(p) { Owner = owner }.ShowDialog() == true;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(SshPortBox.Text.Trim(), out var ssh) || ssh is < 1 or > 65535) { SshPortBox.Focus(); return; }
        if (!int.TryParse(RdpPortBox.Text.Trim(), out var rdp) || rdp is < 1 or > 65535) { RdpPortBox.Focus(); return; }

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

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
