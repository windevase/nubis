using System;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace Nubis;

/// <summary>
/// Vault 계정 추가·수정 (사용자명 + 비밀번호 및/또는 SSH 키).
/// 수정할 때 저장된 비밀 값은 화면에 보여주지 않으며, 칸을 비워 두면 기존 값을 유지한다.
/// </summary>
public partial class VaultEntryDialog : Window
{
    private readonly VaultEntry _entry;
    private readonly bool _isNew;

    private VaultEntryDialog(VaultEntry entry, bool isNew)
    {
        InitializeComponent();
        _entry = entry;
        _isNew = isNew;
        TitleText.Text = isNew ? L.T("새 Vault 계정") : L.T("Vault 계정 수정");
        NameBox.Text = entry.Name;
        UserBox.Text = entry.Username;
        NoteBox.Text = entry.Note;
        if (entry.HasPassword)
        {
            PasswordLabel.Text = L.T("비밀번호 (저장됨 — 비워 두면 그대로)");
            ClearPasswordBox.Visibility = Visibility.Visible;
        }
        if (entry.HasKey)
        {
            KeyLabel.Text = L.T("SSH 개인키 (저장됨 — 비워 두면 그대로)");
            ClearKeyBox.Visibility = Visibility.Visible;
            if (entry.PublicKey is { Length: > 0 } pub) KeyBox.Tag = L.F("저장된 키: {0}...", pub[..Math.Min(pub.Length, 52)]);
        }
        Loaded += (_, _) => NameBox.Focus();
    }

    /// <summary>새 계정을 만들어 저장하면 그 항목, 취소하면 null</summary>
    public static VaultEntry? Create(Window? owner)
    {
        var entry = new VaultEntry();
        return Show(owner, new VaultEntryDialog(entry, true)) ? entry : null;
    }

    /// <summary>기존 계정 수정 (저장하면 true)</summary>
    public static bool Edit(Window? owner, VaultEntry entry) => Show(owner, new VaultEntryDialog(entry, false));

    private static bool Show(Window? owner, VaultEntryDialog dlg)
    {
        if (owner != null && owner.IsLoaded) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dlg.ShowDialog() == true;
    }

    private void OnImportKey(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = L.T("SSH 개인키 선택"),
            Filter = L.T("모든 파일|*.*|PuTTY 키 (*.ppk)|*.ppk|PEM (*.pem)|*.pem"),
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh"),
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(dlg.FileName).Length > 64 * 1024) { ShowError(L.T("개인키 파일로 보기에는 너무 큽니다 (64KB 초과).")); return; }
            KeyBox.Text = File.ReadAllText(dlg.FileName);
            ClearKeyBox.IsChecked = false;
            if (NameBox.Text.Trim().Length == 0) NameBox.Text = Path.GetFileNameWithoutExtension(dlg.FileName);
            ErrorText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0) { NameBox.Focus(); return; }

        // 저장 후 상태 계산 (입력 > 지우기 > 기존 값)
        var newPassword = PasswordBox.Password;
        var keyText = KeyBox.Text.Trim();
        var newPass = PassphraseBox.Password;
        var clearPassword = ClearPasswordBox.IsChecked == true && newPassword.Length == 0;
        var clearKey = ClearKeyBox.IsChecked == true && keyText.Length == 0;
        var willHavePassword = newPassword.Length > 0 || (_entry.HasPassword && !clearPassword);
        var willHaveKey = keyText.Length > 0 || (_entry.HasKey && !clearKey);
        if (!willHavePassword && !willHaveKey) { PasswordBox.Focus(); ShowError(L.T("비밀번호나 SSH 키 중 하나는 있어야 합니다.")); return; }

        string? publicKey = _entry.PublicKey;
        if (willHaveKey && (keyText.Length > 0 || newPass.Length > 0))
        {
            // 키를 바꾸지 않고 키 암호만 바꾸는 경우도 기존 키로 검사
            var material = keyText.Length > 0 ? keyText + "\n" : _entry.Key;
            var pass = newPass.Length > 0 ? newPass : _entry.Passphrase;
            try { publicKey = VaultStore.ReadPublicKey(material, pass); }
            catch (Exception ex)
            {
                ShowError(ex is Renci.SshNet.Common.SshPassPhraseNullOrEmptyException
                    ? L.T("암호가 걸린 키입니다. 키 암호를 입력하세요.")
                    : L.F("개인키를 읽을 수 없습니다: {0}", ex.Message));
                return;
            }
        }

        if (newPassword.Length > 0) _entry.Password = newPassword;
        else if (clearPassword) _entry.ProtectedPassword = null;
        if (keyText.Length > 0) _entry.Key = keyText + "\n";
        if (newPass.Length > 0) _entry.Passphrase = newPass;
        if (clearKey) { _entry.ProtectedKey = null; _entry.ProtectedPassphrase = null; publicKey = null; }
        _entry.PublicKey = willHaveKey ? publicKey : null;

        _entry.Name = name;
        _entry.Username = UserBox.Text.Trim();
        _entry.Note = NoteBox.Text.Trim();
        PasswordBox.Clear();
        PassphraseBox.Clear();
        DialogResult = true;
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
