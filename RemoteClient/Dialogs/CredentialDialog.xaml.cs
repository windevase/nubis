using System.Windows;
using System.Windows.Input;

namespace Nubis;

public partial class CredentialDialog : Window
{
    private CredentialDialog(string target, string? user, bool canSave)
    {
        InitializeComponent();
        TargetText.Text = target;
        UserBox.Text = user ?? "";
        SaveBox.Visibility = canSave ? Visibility.Visible : Visibility.Collapsed;
        // 연결 스레드의 요청으로 뜨는 창 — 다른 창이 앞에 있어도 입력이 바로 이 창으로 가게 활성화한 뒤 포커스
        ContentRendered += (_, _) =>
        {
            Activate();
            if (string.IsNullOrWhiteSpace(UserBox.Text)) Keyboard.Focus(UserBox);
            else Keyboard.Focus(PassBox);
        };
    }

    /// <param name="canSave">"이 호스트에 저장" 체크박스 표시 (저장된 호스트일 때)</param>
    public static (string User, string Password, bool Save)? Ask(Window? owner, string target, string? user, bool canSave = false)
    {
        var dlg = new CredentialDialog(target, user, canSave);
        if (owner != null && owner.IsLoaded) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dlg.ShowDialog() == true ? (dlg.UserBox.Text.Trim(), dlg.PassBox.Password, dlg.SaveBox.IsChecked == true) : null;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(UserBox.Text)) { UserBox.Focus(); return; }
        DialogResult = true;
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
