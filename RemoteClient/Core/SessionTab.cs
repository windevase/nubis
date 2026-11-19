using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace Nubis;

// 세션 탭 모델 (탭 줄 항목 · 세션 뷰 계약). Models.cs 에서 분리 — 500줄 규칙

public enum SessionStatus { Connecting, Connected, Disconnected }

public interface ISessionView
{
    event Action<SessionStatus>? StatusChanged;
    void FocusSession();
    void CloseSession();
    /// <summary>터미널 화면의 마지막 N 줄. 터미널이 아니면(RDP) null. UI 스레드에서 호출</summary>
    System.Threading.Tasks.Task<string?> ReadTerminalAsync(int lines);
    /// <summary>터미널에서 선택한 텍스트. 터미널이 아니면 null. UI 스레드에서 호출</summary>
    System.Threading.Tasks.Task<string?> GetSelectionAsync();
    /// <summary>대화형 세션에 텍스트 입력. 불가능하면 false</summary>
    bool SendText(string text);
    /// <summary>끊긴 세션 다시 연결 (연결 중이면 무시)</summary>
    void Reconnect();
    /// <summary>
    /// 앱이 이미 알고 있는 터미널 상태 (현재 폴더·사용자 등, Claude 가 읽는 글). 명령을 보내지 않는다.
    /// 터미널이 아니면(RDP) null. UI 스레드에서 호출
    /// </summary>
    System.Threading.Tasks.Task<string?> GetTerminalInfoAsync() => System.Threading.Tasks.Task.FromResult<string?>(null);
    /// <summary>화면에 셸 프롬프트가 보이는가 (명령 입력 대기 중). 모르면 false. UI 스레드에서 호출</summary>
    System.Threading.Tasks.Task<bool> IsAtPromptAsync() => System.Threading.Tasks.Task.FromResult(false);
}

public sealed class SessionTab : INotifyPropertyChanged
{
    private bool _isSelected;
    private SessionStatus _status = SessionStatus.Connecting;

    /// <summary>MCP 에서 세션을 가리킬 때 쓰는 번호</summary>
    public int Id { get; init; }
    private string _title = "";
    public required string Title { get => _title; set { if (_title == value) return; _title = value; OnChanged(); } }
    public required Protocol Protocol { get; init; }
    public required System.Windows.FrameworkElement View { get; init; }
    /// <summary>원본 호스트 (로컬 터미널이면 null)</summary>
    public HostEntry? Host { get; init; }
    public ISessionView Session => (ISessionView)View;
    /// <summary>쿠버네티스 탭이면 클러스터 Id (클러스터 터미널 · 파드 셸 · 로그). Protocol 은 Local (이 PC 의 프로세스)</summary>
    public Guid? K8sClusterId { get; init; }
    /// <summary>파드 셸·로그 탭 (닫은 탭 다시 열기 대상이 아님)</summary>
    public bool K8sPod { get; init; }

    public string Glyph => K8sClusterId != null ? (K8sPod ? "" : "") : Protocol switch
    {
        Protocol.Ssh => "",
        Protocol.Rdp => "",
        _ => "",
    };

    public bool IsSelected
    {
        get => _isSelected;
        // 같은 값이면 알리지 않는다 — 탭을 고를 때마다 모든 탭 항목의 바인딩·트리거가 다시 도는 것을 막는다
        set { if (_isSelected == value) return; _isSelected = value; OnChanged(); }
    }

    /// <summary>고른 탭과 나란히 보이는 탭 (나란히 보기의 다른 쪽 — 탭 줄에서 옅게 표시)</summary>
    public bool IsBeside { get; set { if (field == value) return; field = value; OnChanged(); } }

    private int _forwardCount;
    /// <summary>SSH 탭에 열린 포트 포워딩 수 (탭 줄 배지)</summary>
    public int ForwardCount
    {
        get => _forwardCount;
        set { if (_forwardCount == value) return; _forwardCount = value; OnChanged(); OnChanged(nameof(HasForwards)); }
    }
    public bool HasForwards => _forwardCount > 0;

    public SessionStatus Status
    {
        get => _status;
        set { if (_status == value) return; _status = value; OnChanged(); OnChanged(nameof(StatusBrush)); }
    }

    // 상태 점 색: 바뀔 때마다 새 브러시를 만들지 않고 고정(Freeze)된 것을 함께 쓴다 (처음 쓸 때 만든다)
    private static class StatusBrushes
    {
        public static readonly Brush Connected = Frozen(0x20, 0xC5, 0xA0);
        public static readonly Brush Connecting = Frozen(0xF5, 0xB7, 0x40);
        public static readonly Brush Other = Frozen(0x6B, 0x70, 0x88);
        private static Brush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }

    public Brush StatusBrush => Status switch
    {
        SessionStatus.Connected => StatusBrushes.Connected,
        SessionStatus.Connecting => StatusBrushes.Connecting,
        _ => StatusBrushes.Other,
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
