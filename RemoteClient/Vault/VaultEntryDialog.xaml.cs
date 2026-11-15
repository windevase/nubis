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
            var text = File.ReadAllText(dlg.FileName);
            // 파일에서 읽은 개인키는 화면에 펼쳐 보이지 않는다 (화면 공유·녹화·어깨 너머로 새지 않게). 칸에 직접 붙여 넣으면 그 값이 우선
            KeyBox.Text = "";
            _importedKey = text;
            _keyTagBeforeImport ??= KeyBox.Tag;
            KeyBox.Tag = L.F("파일에서 가져옴: {0} (내용은 표시하지 않음)", Path.GetFileName(dlg.FileName));
            ClearKeyBox.IsChecked = false;
            if (NameBox.Text.Trim().Length == 0) NameBox.Text = Path.GetFileNameWithoutExtension(dlg.FileName);
            ErrorText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    /// <summary>"파일에서 가져오기" 로 읽은 개인키 (KeyBox 에는 보이지 않음)</summary>
    private string? _importedKey;
    /// <summary>가져오기 전 키 칸 안내 문구 (가져온 키를 버리면 되돌린다)</summary>
    private object? _keyTagBeforeImport;

    /// <summary>키 칸에 직접 입력·붙여 넣으면 파일에서 가져온 키는 버린다</summary>
    private void OnKeyTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (KeyBox.Text.Length == 0 || _importedKey == null) return;
        _importedKey = null;
        // 안내 문구도 되돌린다 — 그대로 두면 칸을 다시 비웠을 때 "파일에서 가져옴" 이 보여 키가 들어간 것으로 착각한다
        if (_keyTagBeforeImport != null) KeyBox.Tag = _keyTagBeforeImport;
    }

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        // 이름이 비었으면 이유를 보여 준다 (포커스만 옮기면 저장이 왜 안 되는지 알 수 없다)
        if (name.Length == 0) { NameBox.Focus(); ShowError(L.T("이름을 입력하세요.")); return; }
        // 같은 이름의 계정이 둘이면 고르는 창·호스트 카드에서 구분할 수 없다 (예전부터 같은 이름이던 계정은 이름을 바꿀 때만 검사)
        if ((_isNew || !string.Equals(name, _entry.Name.Trim(), StringComparison.CurrentCultureIgnoreCase)) && VaultStore.NameTaken(name, _entry.Id))
        {
            NameBox.Focus();
            ShowError(L.F("같은 이름의 Vault 계정 '{0}' 이(가) 이미 있습니다. 다른 이름을 쓰세요.", name));
            return;
        }

        // 저장 후 상태 계산 (입력 > 지우기 > 기존 값)
        var newPassword = PasswordBox.Password;
        var keyText = KeyBox.Text.Trim();
        if (keyText.Length == 0 && _importedKey != null) keyText = _importedKey.Trim();
        var newPass = PassphraseBox.Password;
        var clearPassword = ClearPasswordBox.IsChecked == true && newPassword.Length == 0;
        var clearKey = ClearKeyBox.IsChecked == true && keyText.Length == 0;
        var willHavePassword = newPassword.Length > 0 || (_entry.HasPassword && !clearPassword);
        var willHaveKey = keyText.Length > 0 || (_entry.HasKey && !clearKey);
        if (!willHavePassword && !willHaveKey) { PasswordBox.Focus(); ShowError(L.T("비밀번호나 SSH 키 중 하나는 있어야 합니다.")); return; }

        string? publicKey = _entry.PublicKey;
        // 암호가 걸리지 않은 키에 키 암호를 넣었으면 저장하지 않는다 (쓰이지 않는 값이 남아 목록에 "키 암호 저장" 으로 보이지 않게)
        var dropPass = false;
        if (willHaveKey && (keyText.Length > 0 || newPass.Length > 0))
        {
            // 키를 바꾸지 않고 키 암호만 바꾸는 경우도 기존 키로 검사.
            // 새 키를 넣었으면 예전 키의 암호를 이어 쓰지 않는다 (새 키에는 맞지 않거나, 암호 없는 키에 남아 "키 암호 저장" 으로 보임)
            var material = keyText.Length > 0 ? keyText + "\n" : _entry.Key;
            var pass = newPass.Length > 0 || keyText.Length > 0 ? newPass : _entry.Passphrase;
            if (pass.Length > 0 && VaultStore.TryReadUnencryptedPublicKey(material) is { } plainPub)
            {
                publicKey = plainPub;
                dropPass = true;
            }
            else try { publicKey = VaultStore.ReadPublicKey(material, pass); }
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
        if (keyText.Length > 0) { _entry.Key = keyText + "\n"; _entry.Passphrase = dropPass ? "" : newPass; }   // 새 키: 키 암호도 새로 (비우면 지움)
        else if (newPass.Length > 0) _entry.Passphrase = dropPass ? "" : newPass;
        if (clearKey) { _entry.ProtectedKey = null; _entry.ProtectedPassphrase = null; publicKey = null; }
        _entry.PublicKey = willHaveKey ? publicKey : null;

        _entry.Name = name;
        _entry.Username = UserBox.Text.Trim();
        _entry.Note = NoteBox.Text.Trim();
        PasswordBox.Clear();
        PassphraseBox.Clear();
        KeyBox.Clear();
        _importedKey = null;
        DialogResult = true;
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
