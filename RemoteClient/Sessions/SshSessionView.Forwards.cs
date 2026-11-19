using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Nubis;

/// <summary>
/// SSH 탭: 로컬 포트 포워딩 (ssh -L). 탭의 SSH 연결을 그대로 쓴다 — 새로 로그인하지 않는다 (OTP 서버도).
/// 연결되면 호스트에 저장된 켜진 규칙을 열고, 끊기거나 탭을 닫으면 닫고, 다시 연결되면 다시 연다.
/// </summary>
public sealed partial class SshSessionView
{
    private SshPortForwardSet _forwards = null!;
    private Button? _fwdButton;
    private Border? _fwdBadge;
    private TextBlock? _fwdBadgeText;
    private int _fwdRefreshQueued;

    /// <summary>이 탭의 포워딩 (MCP list_port_forwards · 탭 창)</summary>
    public SshPortForwardSet Forwards => _forwards;

    /// <summary>열린 포워딩 수가 바뀜 (UI 스레드) — 탭 줄 배지</summary>
    public event Action<int>? ForwardsChanged;

    /// <summary>탭 제목에 쓰는 호스트 이름 (포워딩 창 제목·감사 로그)</summary>
    public string HostDisplayName => _host.DisplayName;

    private void InitForwards()
    {
        _forwards = new SshPortForwardSet(_host.PortForwards);
        // 연결마다(브라우저는 한 번에 여러 개) 오므로 화면 갱신은 한 번에 모은다
        _forwards.Changed += () =>
        {
            if (System.Threading.Interlocked.Exchange(ref _fwdRefreshQueued, 1) == 0)
                Dispatcher.BeginInvoke(() => { _fwdRefreshQueued = 0; OnForwardsChanged(); });
        };
    }

    /// <summary>로그인 성공 뒤 켜진 규칙 열기. 열지 못한 규칙은 터미널에 알린다 (나머지는 연다)</summary>
    private void AttachForwards(int gen)
    {
        var client = _client;
        // 켜진 규칙이 없어도 붙여 둔다 — 탭 창에서 켜거나 임시로 추가하면 바로 열리게
        if (client == null) return;
        _ = Task.Run(() =>
        {
            // 확인은 Attach 의 잠금 안에서 — 확인 뒤 탭 닫기·끊김의 Detach 가 먼저 지나가 포트가 열린 채 남지 않게
            if (!_forwards.Attach(client, () => !_closed && gen == _gen && _status == SessionStatus.Connected && client.IsConnected)) return;
            foreach (var f in _forwards.Items.Where(f => f.State == ForwardState.Failed))
                _term.Info(L.F("포트 포워딩 {0} 을(를) 열지 못했습니다: {1}", f.Describe, f.Error ?? ""), "33");
        });
    }

    private void OnForwardsChanged()
    {
        var items = _forwards.Items;
        var open = items.Count(f => f.State == ForwardState.Open);
        var failed = items.Count(f => f.State == ForwardState.Failed);
        if (_fwdBadge != null && _fwdBadgeText != null)
        {
            _fwdBadge.Visibility = open > 0 || failed > 0 ? Visibility.Visible : Visibility.Collapsed;
            _fwdBadgeText.Text = failed > 0 && open == 0 ? "!" : open.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _fwdBadge.Background = (Brush)Application.Current.Resources[failed > 0 ? "Danger" : "Accent"];
        }
        if (_fwdButton != null)
            _fwdButton.ToolTip = open + failed == 0 ? L.T("포트 포워딩")
                : failed > 0 ? L.F("포트 포워딩 — {0}개 열림, {1}개 실패", open, failed)
                : L.F("포트 포워딩 — {0}개 열림", open);
        ForwardsChanged?.Invoke(open);
        PortForwardWindow.RefreshFor(this);
    }

    /// <summary>왼쪽 도구 막대의 포워딩 버튼 (열린 수 배지)</summary>
    private FrameworkElement BuildForwardButton()
    {
        var res = Application.Current.Resources;
        _fwdButton = new Button
        {
            Style = (Style)res["IconButton"],
            Content = "",
            FontSize = 15,
            Width = 30,
            Height = 30,
            ToolTip = L.T("포트 포워딩"),
        };
        _fwdButton.Click += (_, _) => ShowForwards();
        _fwdBadgeText = new TextBlock
        {
            FontSize = 9,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)res["OnAccent"],
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _fwdBadge = new Border
        {
            MinWidth = 14,
            Height = 14,
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(3, 0, 3, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -2, -3, 0),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Child = _fwdBadgeText,
        };
        var g = new Grid { Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        g.Children.Add(_fwdButton);
        g.Children.Add(_fwdBadge);
        return g;
    }

    /// <summary>포워딩 창 열기 (도구 막대 버튼)</summary>
    public void ShowForwards()
    {
        if (Window.GetWindow(this) is { } owner && _fwdButton != null) PortForwardWindow.Toggle(owner, _fwdButton, this);
    }
}
