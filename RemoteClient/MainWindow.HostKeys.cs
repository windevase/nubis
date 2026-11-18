using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Nubis;

public partial class MainWindow
{
    // =================== 호스트 목록 키보드: 검색창 ↑↓·Tab 카드 이동 · Enter 빠른 접속 ===================

    private HostEntry? _highlighted;
    // 키보드로 고른 카드가 있는 카드 줄 순번 (같은 호스트가 즐겨찾기·최근 접속과 원래 그룹에 함께 있어도 지금 고른 쪽을 기억)
    private int _highlightRow = -1;
    // ↑↓ 로 이동할 때 맞출 열 (카드가 적은 줄을 지나도 원래 열로 돌아오게)
    private int _highlightWantCol;
    // 사용자가 방향키·Tab·클릭으로 직접 고른 카드인지 (검색어가 바뀌어 자동으로 첫 결과를 고른 것과 구분).
    // 직접 골랐으면 검색어가 주소처럼 보여도 Enter 는 그 카드로 연결한다 (QuickConnect)
    private bool _highlightPicked;

    /// <summary>화면의 카드 줄 (접힌 그룹 제외, RebuildRows 순서)</summary>
    private List<CardRow> CardRows() =>
        HostRows.ItemsSource is List<object> rows ? rows.OfType<CardRow>().ToList() : new List<CardRow>();

    private void SetHighlight(HostEntry? h, int row = -1, bool scroll = true)
    {
        if (_highlighted != null) _highlighted.IsHighlighted = false;
        if (!ReferenceEquals(_highlighted, h)) _highlightPicked = false;
        _highlighted = h;
        _highlightRow = -1;
        if (h == null) return;
        h.IsHighlighted = true;
        var rows = CardRows();
        if (row < 0 || row >= rows.Count || !rows[row].Hosts.Contains(h)) row = rows.FindIndex(r => r.Hosts.Contains(h));
        _highlightRow = row;
        if (row >= 0) _highlightWantCol = rows[row].Hosts.IndexOf(h);
        // 그 카드가 있는 줄이 보이게
        if (scroll && row >= 0) HostRows.ScrollIntoView(rows[row]);
    }

    /// <summary>키보드로 고른 카드 이동: dRow = 줄 단위(↑↓), dCol = 한 장씩(Tab, 줄 끝에서 다음 줄로)</summary>
    private void MoveHighlight(int dRow, int dCol)
    {
        var rows = CardRows();
        if (rows.Count == 0) return;
        (int Row, int Col)? cur = null;
        if (_highlighted != null)
        {
            var r = _highlightRow >= 0 && _highlightRow < rows.Count && rows[_highlightRow].Hosts.Contains(_highlighted)
                ? _highlightRow : rows.FindIndex(x => x.Hosts.Contains(_highlighted));
            if (r >= 0) cur = (r, dRow != 0 ? _highlightWantCol : rows[r].Hosts.IndexOf(_highlighted));
        }
        var (nr, nc) = GridMove(rows.Select(x => x.Hosts.Count).ToList(), cur, dRow, dCol);
        var keepCol = dRow != 0 ? _highlightWantCol : -1;
        SetHighlight(rows[nr].Hosts[nc], nr);
        _highlightPicked = true;
        if (keepCol >= 0 && cur != null) _highlightWantCol = keepCol;
    }

    /// <summary>
    /// 카드 격자에서 다음 위치. rowLengths = 줄마다 카드 수 (그룹마다 마지막 줄은 짧을 수 있음).
    /// 위아래는 같은 열(짧은 줄이면 그 줄의 마지막 카드), 좌우는 줄 끝에서 다음/이전 줄로 넘어간다. cur 가 없으면 처음(또는 끝)
    /// </summary>
    internal static (int Row, int Col) GridMove(IReadOnlyList<int> rowLengths, (int Row, int Col)? cur, int dRow, int dCol)
    {
        if (cur is not { } p) return dRow < 0 || dCol < 0 ? (rowLengths.Count - 1, rowLengths[^1] - 1) : (0, 0);
        var (r, c) = p;
        r = Math.Clamp(r, 0, rowLengths.Count - 1);
        if (dRow != 0) r = Math.Clamp(r + dRow, 0, rowLengths.Count - 1);
        c = Math.Clamp(c, 0, rowLengths[r] - 1);
        for (var n = Math.Abs(dCol); n > 0; n--)
        {
            if (dCol > 0)
            {
                if (c + 1 < rowLengths[r]) c++;
                else if (r + 1 < rowLengths.Count) { r++; c = 0; }
            }
            else
            {
                if (c > 0) c--;
                else if (r > 0) { r--; c = rowLengths[r] - 1; }
            }
        }
        return (r, c);
    }

    /// <summary>
    /// 카드 목록에 키보드 포커스가 있을 때(카드를 클릭한 뒤 등, 검색창·다른 입력 칸·버튼이 아닐 때) 키보드로 고른 카드 조작:
    /// 방향키 = 이동, Enter = 연결, F2 = 편집, Delete = 삭제, Esc = 고르기 해제. 처리했으면 true.
    /// 카드 자체는 포커스를 받지 않는다 (가상화 목록이라 화면 밖 카드는 컨트롤이 없음) — 고른 카드는 _highlighted 로만 기억
    /// </summary>
    private bool HandleCardKey(KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None || !CardListHasFocus()) return false;
        switch (e.Key)
        {
            case Key.Up or Key.Down:
                MoveHighlight(e.Key == Key.Down ? 1 : -1, 0);
                return true;
            case Key.Left or Key.Right:
                MoveHighlight(0, e.Key == Key.Right ? 1 : -1);
                return true;
        }
        if (_highlighted is not { } h || !_hosts.Contains(h)) return false;
        switch (e.Key)
        {
            case Key.Enter: Connect(h); return true;
            case Key.F2: OpenEditor(h); return true;
            case Key.Delete: DeleteHost(h); return true;
            case Key.Escape: SetHighlight(null); return true;
        }
        return false;
    }

    /// <summary>키보드 포커스가 카드 목록에 있거나 아무 데도 없는지 (버튼·체크박스 등의 Enter·방향키는 가로채지 않게)</summary>
    private bool CardListHasFocus() => Keyboard.FocusedElement switch
    {
        null => true,
        Window w => ReferenceEquals(w, this),
        System.Windows.Media.Visual v => ReferenceEquals(v, HostRows) || HostRows.IsAncestorOf(v),
        _ => false,
    };

    /// <summary>
    /// Win32 키보드 포커스가 이 창의 WPF 영역이 아닌 곳(내장된 WebView2 · RDP 창, 다른 스레드의 창)에 있는지.
    /// 그 경우 WPF 의 Keyboard.FocusedElement 는 null 이나 창 자신으로 보여 "카드 목록에 포커스" 와 구별되지 않는다
    /// </summary>
    private bool FocusInEmbeddedWindow()
    {
        var self = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        return GetFocus() != self;   // GetFocus 선언은 MainWindow.Tabs.cs
    }

    private DispatcherTimer? _searchTimer;

    /// <summary>입력이 잠깐 멈췄을 때 한 번만 목록 갱신</summary>
    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_searchTimer == null)
        {
            _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); RefreshHosts(); };
        }
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { QuickConnect(); e.Handled = true; }
        else if (e.Key == Key.Escape)
        {
            // 검색어가 비어 있으면 Esc 는 여러 개 고른 카드 선택 해제 (검색창에 포커스가 있으면 HandleSelectionKey 가 받지 않는다)
            if (SearchBox.Text.Length == 0 && _selected.Count > 0) ClearSelection();
            else SearchBox.Clear();
            SetHighlight(null);
            e.Handled = true;
        }
        else if (e.Key is Key.Down or Key.Up)
        {
            if (_searchTimer?.IsEnabled == true) { _searchTimer.Stop(); RefreshHosts(); }
            // ↑↓ = 카드 한 줄씩 (같은 열), Tab / Shift+Tab = 한 장씩
            MoveHighlight(e.Key == Key.Down ? 1 : -1, 0);
            e.Handled = true;
        }
        else if (e.Key == Key.Tab && _highlighted != null && (Keyboard.Modifiers & ~ModifierKeys.Shift) == ModifierKeys.None)
        {
            MoveHighlight(0, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
            e.Handled = true;
        }
        else if (e.Key == Key.F2 && _highlighted != null && Keyboard.Modifiers == ModifierKeys.None)
        {
            // F2 = 키보드로 고른 호스트 편집
            OpenEditor(_highlighted);
            e.Handled = true;
        }
    }

    private void OnQuickConnect(object sender, RoutedEventArgs e) => QuickConnect();

    /// <summary>검색 결과가 1개면 그 호스트로, 아니면 "user@host:port" / "rdp host" 형식으로 바로 접속 (MakeQuickHost)</summary>
    private void QuickConnect()
    {
        var text = SearchBox.Text.Trim();
        if (text.Length == 0)
        {
            // 검색어 없이 ↓ 로 카드를 골랐으면 그 호스트로
            if (_highlighted != null && _hosts.Contains(_highlighted)) Connect(_highlighted);
            else { FlashHostStatus(L.T("검색어나 user@host 를 입력하세요"), ToastKind.Info); SearchBox.Focus(); }
            return;
        }

        if (_searchTimer?.IsEnabled == true) { _searchTimer.Stop(); RefreshHosts(); }   // 입력 직후 Enter
        var matches = _groups.SelectMany(g => g.Hosts).Distinct().ToList();
        // 0) 결과에서 ↑↓·Tab·클릭으로 직접 고른 카드 → 그 호스트로. 예전에는 "10.1.2" 처럼 주소로 보이는 검색어면
        //    고른 카드를 무시하고 입력한 글자(없는 주소)로 접속했다
        if (_highlightPicked && _highlighted != null && matches.Contains(_highlighted)) { Connect(_highlighted); return; }
        var m = QuickRegex.Match(text);

        // 1) 이름·주소가 정확히 일치하는 호스트 → 그 호스트로 (10.0.0.1 을 쳤는데 10.0.0.12 로 붙는 일 방지)
        var exact = matches.Where(h => h.DisplayName.Equals(text, StringComparison.OrdinalIgnoreCase) ||
                                       h.Address.Equals(text, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) { Connect(exact[0]); return; }
        // 2) 주소·user@host 형태면 입력한 그대로 접속, 그 외 검색 결과가 하나뿐이면 그 호스트로
        var looksLikeAddress = m.Success && (text.Contains('@') || text.Contains('.') || m.Groups["proto"].Success || m.Groups["port"].Success);
        if (!looksLikeAddress && matches.Count == 1) { Connect(matches[0]); return; }
        // 3) 주소처럼 보이지 않는데 결과가 여러 개면 키보드로 고른(처음엔 첫 번째) 호스트로 — 입력한 글자를 주소로 접속하지 않음
        if (!looksLikeAddress && matches.Count > 1)
        {
            Connect(_highlighted != null && matches.Contains(_highlighted) ? _highlighted : matches[0]);
            return;
        }
        if (!m.Success)
        {
            Dialog.Show(this, L.T("user@host[:port] 또는 rdp host[:port] 형식으로 입력하세요."), L.T("빠른 접속"));
            return;
        }

        var proto = m.Groups["proto"].Value.Equals("rdp", StringComparison.OrdinalIgnoreCase) ? Protocol.Rdp : Protocol.Ssh;
        Connect(MakeQuickHost(proto, m.Groups["host"].Value, m.Groups["user"].Value,
            m.Groups["port"].Success ? int.Parse(m.Groups["port"].Value) : null));
    }

    /// <summary>
    /// 빠른 접속 대상 (목록에 등록하지 않는 호스트). 화면 빠른 접속 · MCP open_session(target_address) 공통.
    /// 포트가 없거나 범위 밖이면 설정의 기본 포트
    /// </summary>
    internal static HostEntry MakeQuickHost(Protocol proto, string address, string? user, int? port) => new()
    {
        Protocol = proto,
        Address = address,
        Username = user ?? "",
        Port = port is >= 1 and <= 65535 ? port.Value : HostEntry.PreferredPort(proto),
    };

    /// <summary>
    /// MCP 빠른 연결 인자 검사 — 화면 빠른 접속 칸이 받는 형식(QuickRegex: user@host:port)과 같은 주소·사용자명만.
    /// 잘못됐으면 오류 문구(MCP 결과용, 번역 안 함), 괜찮으면 null
    /// </summary>
    internal static string? QuickTargetError(string address, string user, int? port)
    {
        if (address.Length == 0) return "target_address 가 필요합니다.";
        if (McpHelpers.HasInvisibleChars(user)) return $"username 에 보이지 않는 제어 문자·양방향 문자가 있습니다: {McpHelpers.VisibleControlChars(user)}";
        var m = QuickRegex.Match((user.Length > 0 ? user + "@" : "") + address);
        if (!m.Success || m.Groups["proto"].Success || m.Groups["port"].Success || m.Groups["host"].Value != address)
            return "target_address 는 IP 또는 호스트명만 씁니다 (영문·숫자·. - _, 포트·사용자명은 port · username 인자로).";
        if (m.Groups["user"].Value != user) return "username 이 올바르지 않습니다 (공백·@ 불가).";
        if (port is not null and (< 1 or > 65535)) return "port 는 1~65535 입니다.";
        return null;
    }
}
