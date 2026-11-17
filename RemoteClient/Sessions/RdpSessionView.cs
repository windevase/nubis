using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms.Integration;
using System.Windows.Media;
using System.Windows.Threading;

namespace Nubis;

/// <summary>RDP 세션 탭: WindowsFormsHost 안에 mstscax ActiveX 를 띄운다.</summary>
public sealed partial class RdpSessionView : UserControl, ISessionView
{
    private readonly HostEntry _host;
    private RdpAxHost _rdp = RdpAxHost.Acquire();   // 풀에서 재사용 (핸들·스레드 누수 방지). 치명적 오류 뒤에는 새 컨트롤로 바꾼다
    private readonly WindowsFormsHost _wfh;
    private readonly Border _overlay;
    private readonly TextBlock _overlayTitle = new();
    private readonly TextBlock _overlayText = new();
    private readonly DispatcherTimer _resizeTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    // 로그인 직후에는 세션이 해상도 변경을 아직 받지 못해 실패할 때가 있어 잠시 뒤 한 번 더 시도한다
    private readonly DispatcherTimer _displayRetryTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private Button? _reconnectButton;
    // 마지막으로 원격에 보낸 해상도·배율 (같은 값이면 UpdateSessionDisplaySettings 를 다시 부르지 않는다). default = 아직 안 보냄
    private (int w, int h, int scale) _appliedDisplay;

    private bool _initialized, _closed, _loggedIn;
    // 인증 실패(비밀번호 오류·계정 잠김 등)로 끊겼으면 저장된 비밀번호가 있어도 다음 연결 때 반드시 다시 묻는다 (같은 비밀번호 재시도로 계정이 잠기지 않게)
    private bool _forcePrompt;
    private string? _user, _password;
    private SessionStatus _status = SessionStatus.Connecting;

    public event Action<SessionStatus>? StatusChanged;

    private readonly Action<string, string>? _saveCredentials;
    private bool _pendingSave;

    private readonly SessionSource? _source;

    /// <param name="saveCredentials">접속 때 입력한 계정을 저장하는 콜백 (저장 가능한 호스트일 때만)</param>
    /// <param name="source">저장된 호스트의 최신 계정 (다시 연결할 때 반영, 목록에 있는 호스트만)</param>
    public RdpSessionView(HostEntry host, Action<string, string>? saveCredentials = null, SessionSource? source = null)
    {
        _host = host;
        _saveCredentials = saveCredentials;
        _source = source;
        Background = (Brush)Application.Current.Resources["Bg"];

        _wfh = new WindowsFormsHost { Child = _rdp, Background = Brushes.Black };
        _overlay = BuildOverlay();

        var grid = new Grid();
        grid.Children.Add(_wfh);
        grid.Children.Add(_overlay);
        Content = grid;

        WireEvents();

        _resizeTimer.Tick += (_, _) => { _resizeTimer.Stop(); UpdateDisplay(); };
        _displayRetryTimer.Tick += (_, _) => { _displayRetryTimer.Stop(); UpdateDisplay(); };   // 재시도도 실패하면 SmartSizing 으로 대체
        SizeChanged += (_, _) => RestartResizeTimer();
        // 숨은 탭은 해상도를 바꾸지 않고(크기 0 → 640x480 로 줄였다 되돌리는 낭비 방지), 다시 보일 때 한 번 맞춘다
        IsVisibleChanged += (_, _) => { if (IsVisible) RestartResizeTimer(); };
        Loaded += (_, _) => { if (!_initialized) { _initialized = true; Dispatcher.BeginInvoke(Connect, DispatcherPriority.Loaded); } };
    }

    /// <summary>RDP 컨트롤 이벤트 연결 (생성 때, 그리고 치명적 오류 뒤 컨트롤을 바꿀 때)</summary>
    private void WireEvents()
    {
        _rdp.Connected += () => { if (_closed) return; HideOverlay(); SetStatus(SessionStatus.Connected); if (IsVisible) _rdp.Focus(); };
        _rdp.LoginComplete += () =>
        {
            if (_closed) return;
            _loggedIn = true;
            _forcePrompt = false;
            if (!TryUpdateDisplay()) { _displayRetryTimer.Stop(); _displayRetryTimer.Start(); }
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
        // 전체 화면을 나오면 탭 크기로 되돌린다. 나온 직후에는 세션이 해상도 변경을 거부할 때가 있어 실패하면 한 번 더 시도
        _rdp.LeftFullScreen += () =>
        {
            if (_closed) return;
            _appliedDisplay = default;
            Dispatcher.BeginInvoke(() => { if (!TryUpdateDisplay()) { _displayRetryTimer.Stop(); _displayRetryTimer.Start(); } }, DispatcherPriority.Background);
        };
        _rdp.FatalError += code => OnDisconnected(-code);
        // 네트워크 순단 때 컨트롤이 스스로 다시 연결하는 동안(컨트롤이 자체 안내 화면을 그린다) 탭 상태만 "연결 중" 으로 보여 준다
        _rdp.AutoReconnecting += (_, _) => { if (!_closed && _status == SessionStatus.Connected) SetStatus(SessionStatus.Connecting); };
        _rdp.AutoReconnected += () =>
        {
            if (_closed) return;
            HideOverlay();
            SetStatus(SessionStatus.Connected);
            // 다시 붙은 세션이 이전 해상도를 잃었을 수 있어 탭 크기로 다시 맞춘다
            if (_loggedIn) { _appliedDisplay = default; _displayRetryTimer.Stop(); _displayRetryTimer.Start(); }
        };
    }

    /// <summary>
    /// 치명적 오류(OnFatalError)를 낸 컨트롤은 다시 연결해도 실패하므로 반납(해제)하고 새 컨트롤로 바꾼다.
    /// </summary>
    private void ReplaceFaultedControl()
    {
        var old = _rdp;
        try { _wfh.Child = null; } catch { }
        RdpAxHost.Release(old);   // Faulted 라 풀에 넣지 않고 해제
        _rdp = RdpAxHost.Acquire();
        WireEvents();
        _wfh.Child = _rdp;
    }

    private void RestartResizeTimer()
    {
        // 숨은 탭에서는 타이머를 돌리지 않는다 (보일 때 IsVisibleChanged 가 다시 부른다) — 쉬는 동안 CPU 0
        if (_loggedIn && !_closed && IsVisible) { _resizeTimer.Stop(); _resizeTimer.Start(); }
    }

    /// <summary>DPI 가 다른 모니터로 창을 옮기면 DIP 크기는 그대로라 SizeChanged 가 오지 않는다 → 원격 해상도·배율을 다시 맞춘다</summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        RestartResizeTimer();
    }

    /// <summary>안내 화면 종류 — 상태 아이콘의 모양·색과 "다시 연결" 버튼 표시를 정한다</summary>
    private enum OverlayKind { Connecting, Neutral, Error }

    // 안내 카드의 상태 아이콘 (원형 배지 안의 글리프). 계속 도는 애니메이션은 쓰지 않는다 — 연결 중에도 정지 아이콘
    private readonly Border _overlayBadge = new();
    private readonly TextBlock _overlayIcon = new();

    private static T Res<T>(string key) => (T)Application.Current.Resources[key];

    private Border BuildOverlay()
    {
        _overlayIcon.FontFamily = Res<FontFamily>("IconFont");
        _overlayIcon.FontSize = 24;
        _overlayIcon.HorizontalAlignment = HorizontalAlignment.Center;
        _overlayIcon.VerticalAlignment = VerticalAlignment.Center;

        _overlayBadge.Width = 56;
        _overlayBadge.Height = 56;
        _overlayBadge.CornerRadius = new CornerRadius(28);
        _overlayBadge.HorizontalAlignment = HorizontalAlignment.Center;
        _overlayBadge.Margin = new Thickness(0, 0, 0, 16);
        _overlayBadge.Child = _overlayIcon;

        _overlayTitle.Style = Res<Style>("TextSubtitle");
        _overlayTitle.HorizontalAlignment = HorizontalAlignment.Center;
        _overlayTitle.TextAlignment = TextAlignment.Center;
        _overlayTitle.TextWrapping = TextWrapping.Wrap;

        _overlayText.FontSize = 13;
        _overlayText.Foreground = Res<Brush>("TextSecondary");
        _overlayText.Margin = new Thickness(0, 6, 0, 0);
        _overlayText.TextAlignment = TextAlignment.Center;
        _overlayText.TextWrapping = TextWrapping.Wrap;
        _overlayText.LineHeight = 19;

        var reconnect = _reconnectButton = new Button
        {
            Content = L.T("다시 연결"),
            Style = Res<Style>("PrimaryButton"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(20, 8, 20, 8),
            Margin = new Thickness(0, 20, 0, 0),
            MinWidth = 120,
        };
        reconnect.Click += (_, _) => Connect();

        var panel = new StackPanel();
        panel.Children.Add(_overlayBadge);
        panel.Children.Add(_overlayTitle);
        panel.Children.Add(_overlayText);
        panel.Children.Add(reconnect);

        // 가운데 카드 1장 (Layer3 + 머리카락 선 + 큰 반경). 그림자는 이 카드 하나에만 — 안내 화면이 보일 때만 그려진다
        var card = new Border
        {
            Background = Res<Brush>("Layer3Bg"),
            BorderBrush = Res<Brush>("Stroke"),
            BorderThickness = new Thickness(1),
            CornerRadius = Res<CornerRadius>("RadiusXl"),
            Padding = new Thickness(32, 28, 32, 28),
            MinWidth = 360,
            MaxWidth = 520,
            Margin = new Thickness(24),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = Res<System.Windows.Media.Effects.Effect>("Shadow8"),
            Child = panel,
        };

        return new Border { Background = Res<Brush>("Layer2Bg"), Child = card, Visibility = Visibility.Collapsed };
    }

    private void ShowOverlay(string title, string text, OverlayKind kind)
    {
        // 연결 중: 정보(파랑) 모니터 · 정상 종료/취소: 중립 모니터 · 오류: 경고(노랑) 삼각형
        var (glyph, fg, bg) = kind switch
        {
            OverlayKind.Connecting => ("\uE7F4", "Info", "InfoSubtle"),
            OverlayKind.Error => ("\uE7BA", "Warning", "WarningSubtle"),
            _ => ("\uE7F4", "TextSecondary", "ControlFillHover"),
        };
        _overlayIcon.Text = glyph;
        _overlayIcon.Foreground = Res<Brush>(fg);
        _overlayBadge.Background = Res<Brush>(bg);
        _overlayTitle.Text = title;
        _overlayText.Text = text;
        _overlayText.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
        _reconnectButton!.Visibility = kind == OverlayKind.Connecting ? Visibility.Collapsed : Visibility.Visible;
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

    /// <summary>마지막 연결 시도에서 사용자가 계정 입력을 취소했는가 ("끊긴 탭 모두 다시 연결" 이 나머지 탭을 묻지 않고 멈추는 데 씀)</summary>
    public bool LastConnectCancelled { get; private set; }

    // 계정 입력 창(중첩 메시지 루프)이 떠 있는 동안 MCP reconnect 등으로 Connect 가 다시 불리면 창이 두 개 겹쳐 뜬다 — 한 번만
    private bool _prompting;

    private void Connect()
    {
        if (_closed || _prompting) return;
        LastConnectCancelled = false;

        // 저장된 호스트·Vault 의 계정이 바뀌었으면(이 탭을 연 뒤 Vault 비밀번호를 고친 경우 등) 그 값으로 다시 읽는다
        // 계정이 새로 바뀌었으면 인증 실패 때의 비밀번호가 아니므로 저장된 값으로 바로 시도한다
        if (_source?.Reload() is { } fresh && _host.RefreshCredentialsFrom(fresh)) { _user = null; _forcePrompt = false; }

        if (_user == null || _forcePrompt)
        {
            if (_user == null)
            {
                _user = _host.Username;
                _password = _host.Password;
            }
            // 인증 실패 뒤에는 저장된 비밀번호가 있어도 묻는다 (사용자 이름만 채워서)
            if (_forcePrompt || string.IsNullOrWhiteSpace(_user) || string.IsNullOrEmpty(_password))
            {
                (string User, string Password, bool Save)? cred;
                _prompting = true;
                try { cred = CredentialDialog.Ask(Window.GetWindow(this), $"{_host.DisplayName} ({_host.Address})", _user, _saveCredentials != null); }
                finally { _prompting = false; }
                // 대화상자(중첩 메시지 루프)가 떠 있는 동안 탭이 닫혔을 수 있다 (MCP close_session 등) — 컨트롤은 이미 풀에 반납됨
                if (_closed) return;
                if (cred == null)
                {
                    // 인증 실패 뒤 취소했으면 사용자 이름은 남겨 다음에도 다시 묻고, 틀린 비밀번호는 버린다
                    if (_forcePrompt) _password = null;
                    else _user = null;
                    LastConnectCancelled = true;
                    SetStatus(SessionStatus.Disconnected);
                    ShowOverlay(L.T("연결 취소됨"), L.T("인증 정보 입력이 취소되었습니다."), OverlayKind.Neutral);
                    return;
                }
                _user = cred.Value.User;
                _password = cred.Value.Password;
                _pendingSave = cred.Value.Save;
            }
        }

        // 연결 중에는 ActiveX 의 흰 화면 대신 어두운 안내 화면을 보여주고, OnConnected 때 컨트롤을 드러낸다
        ShowOverlay(L.F("{0} 에 연결 중...", _host.DisplayName), $"{_host.Address}:{_host.Port}", OverlayKind.Connecting);
        SetStatus(SessionStatus.Connecting);
        _loggedIn = false;
        _appliedDisplay = default;   // 새 세션 — 로그인 뒤 한 번은 반드시 맞춘다
        _sessionGeneration++;        // Claude 화면 조작 허용은 이 연결에만 (다시 연결하면 새로 승인)

        try
        {
            if (_rdp.Faulted) ReplaceFaultedControl();
            _rdp.CreateControl();
            var ocx = _rdp.Ocx;
            var (w, h, scale) = DesktopSize();

            ocx.Server = _host.Address;
            var user = _user ?? "";
            var slash = user.IndexOf('\\');
            if (slash > 0) { ocx.Domain = user[..slash]; ocx.UserName = user[(slash + 1)..]; }
            else { ocx.Domain = ""; ocx.UserName = user; }

            ocx.DesktopWidth = w;
            ocx.DesktopHeight = h;
            // 첫 연결부터 화면 배율을 맞춘다 (고 DPI 모니터에서 원격 글자가 작게 보이지 않게). 지원하지 않는 버전이면 무시
            _rdp.TrySetExtendedProperty("DesktopScaleFactor", (uint)scale);
            _rdp.TrySetExtendedProperty("DeviceScaleFactor", 100u);
            // mstsc 기본 설정(문서\Default.rdp)을 그대로 따른다
            var d = RdpDefaults.Load();
            ocx.ColorDepth = d.ColorDepth;
            ocx.ConnectingText = L.F("{0} 에 연결 중...", _host.DisplayName);

            var adv = ocx.AdvancedSettings9;
            adv.RDPPort = _host.Port;
            // NLA(CredSSP): 기본은 켬. Entra ID 가입 PC 처럼 끄라고 한 호스트(또는 Default.rdp 의 enablecredsspsupport:i:0)만 끈다 —
            // 끄면 접속 전 인증 없이 원격 로그인 화면까지 연결되고, 계정은 그 화면에서 확인된다
            adv.EnableCredSspSupport = d.CredSspFor(_host);
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

            ApplyGateway(ocx, d);
            try { ocx.SecuredSettings3.KeyboardHookMode = d.KeyboardHook; } catch { }   // Win/Alt+Tab 키 전달 방식
            try { ocx.SecuredSettings2.AudioRedirectionMode = d.AudioMode; } catch { }   // 소리 재생 위치

            _rdp.BeginSession();
            ocx.Connect();
        }
        catch (Exception ex)
        {
            SetStatus(SessionStatus.Disconnected);
            ShowOverlay(L.T("연결 실패"), ex.Message, OverlayKind.Error);
        }
    }

    /// <summary>
    /// RD 게이트웨이 (IMsRdpClientTransportSettings2). 호스트에 적은 게이트웨이 → Default.rdp 의 게이트웨이 순.
    /// 풀에서 재사용한 컨트롤일 수 있으므로 게이트웨이를 쓰지 않을 때도 "안 씀" 으로 덮어쓴다 (이전 호스트 설정이 남지 않게).
    /// </summary>
    private void ApplyGateway(dynamic ocx, RdpDefaults d)
    {
        try
        {
            var ts = ocx.TransportSettings2;
            if (d.GatewayFor(_host.RdpGateway) is { } gw)
            {
                ts.GatewayHostname = gw.Host;
                ts.GatewayUsageMethod = (uint)gw.Usage;
                ts.GatewayProfileUsageMethod = 1u;                      // 이 설정을 쓴다 (explicit)
                ts.GatewayCredsSource = (uint)d.GatewayCredsSource;
                ts.GatewayCredSharing = d.GatewayCredSharing ? 1u : 0u; // 게이트웨이에도 원격 PC 와 같은 계정
            }
            else
            {
                ts.GatewayUsageMethod = 0u;
                ts.GatewayProfileUsageMethod = 1u;
                ts.GatewayHostname = "";
            }
        }
        catch (Exception ex) when (!string.IsNullOrWhiteSpace(_host.RdpGateway))
        {
            // 호스트에 게이트웨이를 적었는데 적용하지 못함 — 게이트웨이 없이 몰래 직접 연결하지 않는다
            throw new InvalidOperationException(L.F("RD 게이트웨이 설정을 적용하지 못했습니다: {0}", ex.Message), ex);
        }
        catch { /* Default.rdp 게이트웨이를 적용하지 못한 경우는 mstsc 처럼 직접 연결 */ }
    }

    /// <summary>로그인 후·창 크기 변경 시 원격 해상도를 창에 맞춘다 (Windows 8.1/2012R2 이상 서버).</summary>
    private void UpdateDisplay() => TryUpdateDisplay();

    /// <returns>실패(예외)면 false. 할 일이 없을 때(연결 전·닫힘)는 true</returns>
    private bool TryUpdateDisplay()
    {
        if (_closed || !_loggedIn || _status != SessionStatus.Connected) return true;
        // 숨은 탭(크기 0)은 건너뛴다 — 640x480 으로 줄였다가 되돌리며 원격 창 배치를 흔들지 않게. 보일 때 다시 불린다
        if (!IsVisible || ActualWidth < 1 || ActualHeight < 1) return true;
        // 전체 화면 중에는 모니터 해상도를 유지한다 — 전환 직전에 걸어 둔 크기 조절 타이머·DPI 변경이 탭 크기로 되돌리지 않게 (나오면 LeftFullScreen 이 맞춘다)
        try { if ((bool)_rdp.Ocx.FullScreen) return true; } catch { }
        var size = DesktopSize();
        if (size == _appliedDisplay) return true;   // 같은 값이면 원격에 다시 보내지 않는다
        var (w, h, scale) = size;
        try
        {
            _rdp.Ocx.UpdateSessionDisplaySettings((uint)w, (uint)h, (uint)w, (uint)h, 0u, (uint)scale, 100u);
            _appliedDisplay = size;
            return true;
        }
        catch { return false; /* 지원하지 않는 서버는 SmartSizing 으로 대체 */ }
    }

    private void OnDisconnected(int reason)
    {
        if (_closed) return;
        _loggedIn = false;
        _displayRetryTimer.Stop();
        // 원격 화면에 키보드 포커스가 있었으면(안내 화면으로 바뀌며 포커스가 사라짐) "다시 연결" 버튼으로 옮겨 Enter 로 바로 다시 연결할 수 있게.
        // 다른 곳(Claude 패널 등)에서 입력 중이면 포커스를 빼앗지 않는다
        bool hadFocus;
        try { hadFocus = _rdp.ContainsFocus || _wfh.IsKeyboardFocusWithin; } catch { hadFocus = false; }
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
        // 인증 실패로 끊긴 경우 다음 연결 때 저장된 비밀번호가 있어도 다시 묻는다 (같은 비밀번호 재시도 → 계정 잠김 방지)
        // 2055 비밀번호 오류 · 2567 사용자 없음 · 2823 계정 사용 안 함 · 3335 계정 잠김 · 3591 계정 만료 · 3847 비밀번호 만료 · 4615 비밀번호 변경 필요
        if (IsAuthFailureReason(reason))
        {
            _forcePrompt = true;
            _pendingSave = false;   // 실패한 계정은 저장하지 않는다
        }

        // 로그오프·원격에서 연결 끊기·관리자가 끊음(사유 1·2·3)은 오류가 아니므로 제목을 구분한다
        ShowOverlay(IsNormalDisconnectReason(reason)
            ? L.F("{0} 세션 정상 종료", _host.DisplayName)
            : L.F("{0} 연결 종료", _host.DisplayName), text,
            IsNormalDisconnectReason(reason) ? OverlayKind.Neutral : OverlayKind.Error);
        if (hadFocus && IsVisible) Dispatcher.BeginInvoke(FocusSession, DispatcherPriority.Input);
    }

    /// <summary>
    /// 오류가 아닌 끊김인가: 1 = 이 쪽에서 끊음, 2 = 원격에서 사용자가 끊음·로그오프, 3 = 서버가 끊음(관리자·유휴 시간 정책).
    /// 음수(RDP 컨트롤 치명적 오류)·0·그 밖의 코드는 오류로 본다.
    /// </summary>
    internal static bool IsNormalDisconnectReason(int reason) => reason is 1 or 2 or 3;

    /// <summary>인증 실패로 끊긴 사유 코드인가 (다음 연결 때 저장된 비밀번호를 쓰지 않고 다시 묻는다)</summary>
    internal static bool IsAuthFailureReason(int reason) =>
        reason is 2055 or 2567 or 2823 or 3335 or 3591 or 3847 or 4615;

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
            _appliedDisplay = default;   // 전체 화면 해상도로 바뀜 — 나오면 탭 크기로 다시 보낸다
            _resizeTimer.Stop();         // 전환 직전에 걸린 탭 크기 조절이 전체 화면 해상도를 덮어쓰지 않게
            _displayRetryTimer.Stop();
            _rdp.Ocx.FullScreenTitle = _host.DisplayName;
            _rdp.Ocx.FullScreen = true;
        }
        catch (Exception ex)
        {
            // 위에서 원격 해상도를 모니터 크기로 바꾸고 크기 조절 타이머를 멈췄다 — 전환에 실패했으면 탭 크기로 되돌린다
            RestartResizeTimer();
            Dialog.Show(Window.GetWindow(this), L.F("전체 화면으로 바꾸지 못했습니다: {0}", ex.Message), "RDP");
        }
    }

    public void FocusSession()
    {
        // 닫은 탭(SelectTab 이 예약한 늦은 포커스 등)은 풀로 돌아가 다른 탭이 쓰는 컨트롤에 포커스를 주지 않는다
        if (_closed) return;
        if (_wfh.Visibility == Visibility.Visible) _rdp.Focus();
        // 끊김·실패 안내 화면이면 "다시 연결" 버튼에 포커스 — 탭을 고른 뒤 마우스 없이 Enter/Space 로 다시 연결할 수 있게
        else if (_overlay.Visibility == Visibility.Visible && _reconnectButton is { Visibility: Visibility.Visible } btn) btn.Focus();
    }

    public void CloseSession()
    {
        // 두 번 불려도(탭 닫기 + 앱 종료 등) 같은 컨트롤을 풀에 두 번 넣지 않게 한 번만 정리한다
        // — 두 번 넣으면 두 탭이 한 컨트롤을 나눠 갖고, 풀 자리를 차지해 다른 컨트롤이 해제되며 핸들·스레드가 샌다
        if (_closed) return;
        _closed = true;
        _resizeTimer.Stop();
        _displayRetryTimer.Stop();
        StatusChanged = null;   // 닫은 뒤에는 MainWindow 쪽 탭 상태를 건드리지 않는다
        _password = null;   // 닫은 탭에 비밀번호를 남겨 두지 않는다
        try { _wfh.Child = null; } catch { }
        RdpAxHost.Release(_rdp);   // 연결 끊고 풀에 반납 (풀이 차면 해제)
        HwndHostRelease.Detach(_wfh);   // 키보드 입력 사이트 등록을 풀고 트리에서 뺀 뒤 Dispose (WebView2 와 같은 HwndHost 누수 방지)
        _wfh.Dispose();
    }
}
