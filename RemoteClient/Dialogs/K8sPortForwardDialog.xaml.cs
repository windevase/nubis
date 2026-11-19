using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Nubis;

/// <summary>
/// 포트 포워딩 창: 리소스의 알려진 포트(TCP)를 목록으로 보이고, 원격 포트를 고르면 로컬 포트를 같은 번호 → 쓰이면 빈 포트로 제안한다.
/// 둘 다 직접 입력할 수 있다 (로컬 포트를 손으로 바꾸면 그 뒤로는 제안하지 않는다)
/// </summary>
public partial class K8sPortForwardDialog : Window
{
    public sealed record Choice(int Local, int Remote, K8sPort? Port);

    private K8sItem _item = null!;
    private bool _localEdited, _settingLocal;
    private Choice? _result;

    private K8sPortForwardDialog() => InitializeComponent();

    public static Choice? Ask(Window? owner, K8sItem item)
    {
        var dlg = new K8sPortForwardDialog { _item = item };
        if (owner != null && owner.IsLoaded) dlg.Owner = owner;
        else
        {
            dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dlg.ShowInTaskbar = true;
        }
        dlg.TargetText.Text = $"{K8sPortForward.TargetRef(item.Kind, item.Name)}  ·  {item.Namespace}";
        dlg.TargetText.ToolTip = dlg.TargetText.Text;
        var ports = K8sPortForward.TcpPorts(item.Ports);
        foreach (var p in ports)
            dlg.PortList.Items.Add(new ListBoxItem { Content = K8sPortForward.PortText(item.Kind, p), Tag = p, Foreground = (System.Windows.Media.Brush)dlg.FindResource("Text") });
        if (ports.Count == 0) dlg.PortsSection.Visibility = Visibility.Collapsed;
        dlg.Loaded += (_, _) =>
        {
            if (dlg.PortList.Items.Count > 0)
            {
                dlg.PortList.SelectedIndex = 0;
                (dlg.PortList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
            }
            else dlg.RemoteBox.Focus();
        };
        Dialog.RestoreIfMinimized(dlg.Owner);
        return dlg.ShowDialog() == true ? dlg._result : null;
    }

    private K8sPort? SelectedPort => (PortList.SelectedItem as ListBoxItem)?.Tag as K8sPort;

    private void OnPortSelected(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedPort is { } p) RemoteBox.Text = p.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private void OnRemoteChanged(object sender, TextChangedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        if (_localEdited || K8sPortForward.ParsePort(RemoteBox.Text) is not { } remote) return;
        var local = K8sPortForward.SuggestLocalPort(remote, K8sPortForward.PortProbe());
        _settingLocal = true;
        LocalBox.Text = local?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
        _settingLocal = false;
    }

    private void OnLocalChanged(object sender, TextChangedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        if (!_settingLocal) _localEdited = LocalBox.Text.Trim().Length > 0;
    }

    private void ShowError(string text, TextBox focus)
    {
        ErrorText.Text = text;
        ErrorText.Visibility = Visibility.Visible;
        focus.Focus();
        focus.SelectAll();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (K8sPortForward.ParsePort(RemoteBox.Text) is not { } remote) { ShowError(L.T("원격 포트는 1~65535 사이의 숫자여야 합니다."), RemoteBox); return; }
        if (K8sPortForward.ParsePort(LocalBox.Text) is not { } local) { ShowError(L.T("로컬 포트는 1~65535 사이의 숫자여야 합니다."), LocalBox); return; }
        if (!K8sPortForward.IsPortAvailable(local))
        {
            var alt = K8sPortForward.SuggestLocalPort(local, K8sPortForward.PortProbe());
            ShowError(alt is { } a
                ? L.F("로컬 포트 {0} 은(는) 이미 쓰이고 있습니다. 빈 포트 {1} 을(를) 넣었습니다.", local, a)
                : L.F("로컬 포트 {0} 은(는) 이미 쓰이고 있습니다.", local), LocalBox);
            if (alt is { } b) { _settingLocal = true; LocalBox.Text = b.ToString(System.Globalization.CultureInfo.InvariantCulture); _settingLocal = false; _localEdited = true; LocalBox.SelectAll(); }
            return;
        }
        // 목록에서 고른 포트와 번호가 같으면 그 포트 정보(이름·appProtocol)를 함께 — 브라우저 버튼 판단용
        var port = SelectedPort is { } sp && sp.Port == remote ? sp : null;
        port ??= Array.Find(K8sPortForward.TcpPorts(_item.Ports).ToArray(), p => p.Port == remote);
        _result = new Choice(local, remote, port);
        DialogResult = true;
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
