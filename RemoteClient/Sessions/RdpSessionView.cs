using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Media;
using System.Windows.Threading;

namespace Nubis;

/// <summary>RDP 세션 탭: WindowsFormsHost 안에 mstscax ActiveX 를 띄운다.</summary>
public sealed class RdpSessionView : UserControl, ISessionView
{
    private readonly HostEntry _host;
    private readonly RdpAxHost _rdp = RdpAxHost.Acquire();   // 풀에서 재사용 (핸들·스레드 누수 방지)
    private readonly WindowsFormsHost _wfh;
    private readonly Border _overlay;
    private readonly TextBlock _overlayTitle = new();
    private readonly TextBlock _overlayText = new();
    private readonly DispatcherTimer _resizeTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private Button? _reconnectButton;

    private bool _initialized, _closed, _loggedIn;
    private string? _user, _password;
    private SessionStatus _status = SessionStatus.Connecting;

    public event Action<SessionStatus>? StatusChanged;

    private readonly Action<string, string>? _saveCredentials;
    private bool _pendingSave;

    /// <param name="saveCredentials">접속 때 입력한 계정을 저장하는 콜백 (저장 가능한 호스트일 때만)</param>
    public RdpSessionView(HostEntry host, Action<string, string>? saveCredentials = null)
    {
        _host = host;
        _saveCredentials = saveCredentials;
        Background = (Brush)Application.Current.Resources["Bg"];

        _wfh = new WindowsFormsHost { Child = _rdp, Background = Brushes.Black };
        _overlay = BuildOverlay();

        var grid = new Grid();
        grid.Children.Add(_wfh);
        grid.Children.Add(_overlay);
        Content = grid;

        _rdp.Connected += () => { HideOverlay(); SetStatus(SessionStatus.Connected); _rdp.Focus(); };
        _rdp.LoginComplete += () =>
        {
            _loggedIn = true;
            UpdateDisplay();
            // 로그인에 성공한 계정만 저장
            if (_pendingSave && _user != null && _password != null)
            {
                _pendingSave = false;
                _host.Username = _user;
                _host.Password = _password;
                _saveCredentials?.Invoke(_user, _password);
            }
        };
        _rdp.Disconnected += OnDisconnected;
        _rdp.LeftFullScreen += () => Dispatcher.BeginInvoke(UpdateDisplay, DispatcherPriority.Background);
        _rdp.FatalError += code => OnDisconnected(-code);

        _resizeTimer.Tick += (_, _) => { _resizeTimer.Stop(); UpdateDisplay(); };
        SizeChanged += (_, _) => { if (_loggedIn) { _resizeTimer.Stop(); _resizeTimer.Start(); } };
        Loaded += (_, _) => { if (!_initialized) { _initialized = true; Dispatcher.BeginInvoke(Connect, DispatcherPriority.Loaded); } };
    }

    private Border BuildOverlay()
    {
        _overlayTitle.FontSize = 18;
        _overlayTitle.FontWeight = FontWeights.SemiBold;
        _overlayTitle.Foreground = (Brush)Application.Current.Resources["Text"];
        _overlayTitle.HorizontalAlignment = HorizontalAlignment.Center;

        _overlayText.Foreground = (Brush)Application.Current.Resources["SubText"];
        _overlayText.Margin = new Thickness(0, 8, 0, 20);
        _overlayText.TextAlignment = TextAlignment.Center;
        _overlayText.TextWrapping = TextWrapping.Wrap;
        _overlayText.MaxWidth = 520;

        var reconnect = _reconnectButton = new Button
        {
            Content = L.T("다시 연결"),
            Style = (Style)Application.Current.Resources["PrimaryButton"],
            HorizontalAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(22, 9, 22, 9),
        };
        reconnect.Click += (_, _) => Connect();

        var icon = new TextBlock
        {
            Text = "",
            FontFamily = (FontFamily)Application.Current.Resources["IconFont"],
            FontSize = 40,
            Foreground = (Brush)Application.Current.Resources["RdpBrush"],
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 16),
        };

        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(icon);
        panel.Children.Add(_overlayTitle);
        panel.Children.Add(_overlayText);
        panel.Children.Add(reconnect);

        return new Border { Background = (Brush)Application.Current.Resources["Bg"], Child = panel, Visibility = Visibility.Collapsed };
    }

    private void ShowOverlay(string title, string text, bool showReconnect = true)
    {
        _overlayTitle.Text = title;
        _overlayText.Text = text;
        _reconnectButton!.Visibility = showReconnect ? Visibility.Visible : Visibility.Collapsed;
        _overlay.Visibility = Visibility.Visible;
        _wfh.Visibility = Visibility.Collapsed;   // ActiveX(HWND)는 WPF 위에 그려지므로 숨겨야 오버레이가 보인다
    }

    private void HideOverlay()
    {
        _overlay.Visibility = Visibility.Collapsed;
        _wfh.Visibility = Visibility.Visible;
    }

    private void SetStatus(SessionStatus s)
    {
        _status = s;
        StatusChanged?.Invoke(s);
    }

    private (int w, int h, int scale) DesktopSize()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var w = (int)(ActualWidth * dpi.DpiScaleX);
        var h = (int)(ActualHeight * dpi.DpiScaleY);
        w = Math.Clamp(w, 640, 8192) & ~1;   // 짝수 폭
        h = Math.Clamp(h, 480, 8192);
        var scale = (int)Math.Round(dpi.DpiScaleX * 100);
        return (w, h, Math.Clamp(scale, 100, 500));
    }

    private void Connect()
    {
        if (_closed) return;

        if (_user == null)
        {
            _user = _host.Username;
            _password = _host.Password;
            if (string.IsNullOrWhiteSpace(_user) || string.IsNullOrEmpty(_password))
            {
                var cred = CredentialDialog.Ask(Window.GetWindow(this), $"{_host.DisplayName} ({_host.Address})", _user, _saveCredentials != null);
                if (cred == null)
                {
                    _user = null;
                    SetStatus(SessionStatus.Disconnected);
                    ShowOverlay(L.T("연결 취소됨"), L.T("인증 정보 입력이 취소되었습니다."));
                    return;
                }
                _user = cred.Value.User;
                _password = cred.Value.Password;
                _pendingSave = cred.Value.Save;
            }
        }

        // 연결 중에는 ActiveX 의 흰 화면 대신 어두운 안내 화면을 보여주고, OnConnected 때 컨트롤을 드러낸다
        ShowOverlay(L.F("{0} 에 연결 중...", _host.DisplayName), $"{_host.Address}:{_host.Port}", showReconnect: false);
        SetStatus(SessionStatus.Connecting);
        _loggedIn = false;

        try
        {
            _rdp.CreateControl();
            var ocx = _rdp.Ocx;
            var (w, h, _) = DesktopSize();

            ocx.Server = _host.Address;
            var user = _user ?? "";
            var slash = user.IndexOf('\\');
            if (slash > 0) { ocx.Domain = user[..slash]; ocx.UserName = user[(slash + 1)..]; }
            else { ocx.Domain = ""; ocx.UserName = user; }

            ocx.DesktopWidth = w;
            ocx.DesktopHeight = h;
            // mstsc 기본 설정(문서\Default.rdp)을 그대로 따른다
            var d = RdpDefaults.Load();
            ocx.ColorDepth = d.ColorDepth;
            ocx.ConnectingText = L.F("{0} 에 연결 중...", _host.DisplayName);

            var adv = ocx.AdvancedSettings9;
            adv.RDPPort = _host.Port;
            adv.EnableCredSspSupport = true;
            // 서버 인증(인증서) 검증: 2 = 확인 실패 시 경고 후 선택(기본), 0 = 경고 없이 연결(자체 서명 인증서 환경용 설정)
            adv.AuthenticationLevel = SettingsStore.Current.RdpCertWarning ? 2 : 0;
            adv.SmartSizing = true;              // 해상도 갱신 전까지는 창 크기에 맞춰 축소/확대 (Nubis 탭 동작)
            adv.RedirectClipboard = d.RedirectClipboard;
            adv.EnableAutoReconnect = d.AutoReconnect;
            adv.BitmapPersistence = d.BitmapPersistence ? 1 : 0;
            try { adv.RedirectPrinters = d.RedirectPrinters; } catch { }
            try { adv.RedirectDrives = d.RedirectDrives; } catch { }
            try { adv.RedirectSmartCards = d.RedirectSmartCards; } catch { }
            // 연결 품질: 자동 감지(7)면 대역폭을 재서 화면 효과를 알아서 정하고, 직접 고른 경우에만 화면 효과 플래그를 적용
            try { adv.BandwidthDetection = d.BandwidthDetection; adv.NetworkConnectionType = d.ConnectionType; } catch { }
            if (d.ConnectionType != 7) { try { adv.PerformanceFlags = d.PerformanceFlags; } catch { } }
            // 풀에서 재사용한 컨트롤일 수 있으므로 비밀번호는 항상 덮어쓴다 (이전 호스트 비밀번호가 남지 않게)
            adv.ClearTextPassword = _password ?? "";

            try { ocx.SecuredSettings3.KeyboardHookMode = d.KeyboardHook; } catch { }   // Win/Alt+Tab 키 전달 방식
            try { ocx.SecuredSettings2.AudioRedirectionMode = d.AudioMode; } catch { }   // 소리 재생 위치

            ocx.Connect();
        }
        catch (Exception ex)
        {
            SetStatus(SessionStatus.Disconnected);
            ShowOverlay(L.T("연결 실패"), ex.Message);
        }
    }

    /// <summary>로그인 후·창 크기 변경 시 원격 해상도를 창에 맞춘다 (Windows 8.1/2012R2 이상 서버).</summary>
    private void UpdateDisplay()
    {
        if (_closed || !_loggedIn || _status != SessionStatus.Connected) return;
        var (w, h, scale) = DesktopSize();
        try
        {
            _rdp.Ocx.UpdateSessionDisplaySettings((uint)w, (uint)h, (uint)w, (uint)h, 0u, (uint)scale, 100u);
        }
        catch { /* 지원하지 않는 서버는 SmartSizing 으로 대체 */ }
    }

    private void OnDisconnected(int reason)
    {
        if (_closed) return;
        _loggedIn = false;
        SetStatus(SessionStatus.Disconnected);

        string text;
        if (reason < 0) text = L.F("RDP 컨트롤 오류 (코드 {0})", -reason);
        else
        {
            try
            {
                var ocx = _rdp.Ocx;
                text = (string)ocx.GetErrorDescription((uint)reason, (uint)ocx.ExtendedDisconnectReason);
            }
            catch { text = ""; }
            if (string.IsNullOrWhiteSpace(text)) text = L.F("연결이 끊어졌습니다. (사유 코드 {0})", reason);
            else text += L.F("\n(사유 코드 {0})", reason);
        }
        // 비밀번호 오류 등으로 끊긴 경우 다음 연결 때 다시 묻도록
        if (reason is 2055 or 2567 or 2823 or 3335 or 3591) _user = null;

        ShowOverlay(L.F("{0} 연결 종료", _host.DisplayName), text);
    }

    public System.Threading.Tasks.Task<string?> ReadTerminalAsync(int lines) => System.Threading.Tasks.Task.FromResult<string?>(null);
    public System.Threading.Tasks.Task<string?> GetSelectionAsync() => System.Threading.Tasks.Task.FromResult<string?>(null);
    public bool SendText(string text) => false;

    public void Reconnect() { if (_status == SessionStatus.Disconnected) Connect(); }

    public bool IsConnected => _status == SessionStatus.Connected;

    /// <summary>
    /// 전체 화면 (mstsc 와 같은 연결 표시줄로 돌아옴). 전체 화면에서는 Win 키·Alt+Tab 이 원격으로 간다 (KeyboardHook 기본값).
    /// 원격 해상도를 모니터 크기로 바꾼 뒤 전환하고, 전체 화면을 나오면 탭 크기로 되돌린다.
    /// </summary>
    public void EnterFullScreen()
    {
        if (_closed || _status != SessionStatus.Connected) return;
        try
        {
            var screen = System.Windows.Forms.Screen.FromHandle(_rdp.Handle).Bounds;
            var scale = (int)Math.Round(VisualTreeHelper.GetDpi(this).DpiScaleX * 100);
            if (_loggedIn)
            {
                try { _rdp.Ocx.UpdateSessionDisplaySettings((uint)(screen.Width & ~1), (uint)screen.Height, (uint)screen.Width, (uint)screen.Height, 0u, (uint)Math.Clamp(scale, 100, 500), 100u); }
                catch { }
            }
            _rdp.Ocx.FullScreenTitle = _host.DisplayName;
            _rdp.Ocx.FullScreen = true;
        }
        catch (Exception ex) { Dialog.Show(Window.GetWindow(this), L.F("전체 화면으로 바꾸지 못했습니다: {0}", ex.Message), "RDP"); }
    }

    public void FocusSession()
    {
        if (_wfh.Visibility == Visibility.Visible) _rdp.Focus();
    }

    public void CloseSession()
    {
        _closed = true;
        _resizeTimer.Stop();
        try { _wfh.Child = null; } catch { }
        RdpAxHost.Release(_rdp);   // 연결 끊고 풀에 반납 (풀이 차면 해제)
        _wfh.Dispose();
    }
}
