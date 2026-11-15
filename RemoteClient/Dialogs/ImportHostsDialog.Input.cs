using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Nubis;

/// <summary>가져오기 창: 키보드(Enter 로 불러오기 · Esc · 목록 Space 체크) · 미리보기 검색 · 경고 펼치기 · 열 머리글 맞춤</summary>
public partial class ImportHostsDialog
{
    // ===================== 키보드 =====================

    /// <summary>경로 칸에서 Enter = 불러오기</summary>
    private void OnFilePathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        OnLoadFile(sender, e);
    }

    private void OnAnsiblePathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        OnLoadAnsible(sender, e);
    }

    /// <summary>AD 입력 칸(도메인 · 서버 · OU · 사용자 · 비밀번호)에서 Enter = 가져오기 (조회 중이면 무시 — 취소는 버튼으로만)</summary>
    private void OnAdKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.OriginalSource is not (TextBox or PasswordBox)) return;
        e.Handled = true;
        if (_adCts == null) OnFetchAd(sender, e);
    }

    // ===================== 검색 · 경고 =====================

    /// <summary>검색어 입력: 지우면 바로, 치는 중이면 잠깐 멈췄을 때 거른다 (수천 줄에서 글자마다 전체를 다시 거르지 않게)</summary>
    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        _searchTimer.Stop();
        if (SearchBox.Text.Trim().Length == 0) RefreshView();
        else _searchTimer.Start();
    }

    /// <summary>창 단축키: Ctrl+F = 미리보기 검색 칸으로</summary>
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F || Keyboard.Modifiers != ModifierKeys.Control) return;
        SearchBox.Focus();
        SearchBox.SelectAll();
        e.Handled = true;
    }

    /// <summary>검색 칸에서 Esc = 검색어 지우기 (비어 있으면 창 닫기로 넘김)</summary>
    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || SearchBox.Text.Length == 0) return;
        SearchBox.Clear();
        e.Handled = true;
    }

    /// <summary>
    /// 미리보기 목록에서 Space = 포커스가 있는 줄 체크 켜기 · 끄기. 줄(ListBoxItem) 자체에 포커스가 있을 때만 —
    /// 체크 상자에 포커스가 있으면 체크 상자가 직접 처리한다. 선택 줄이 아니라 포커스 줄 기준
    /// (Ctrl+↑↓ 로 선택 없이 포커스만 옮긴 뒤 Space 를 누르면 포커스 표시와 다른 줄이 바뀌던 문제)
    /// </summary>
    private void OnRowListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || Keyboard.Modifiers != ModifierKeys.None) return;
        if (e.OriginalSource is not ListBoxItem { DataContext: ImportRow row } || !row.IsSelectable) return;
        row.IsSelected = !row.IsSelected;
        e.Handled = true;
    }

    /// <summary>경고 목록 펼치기 · 접기 (머리글 버튼 — 마우스 · Enter · Space)</summary>
    private void OnToggleWarnings(object sender, RoutedEventArgs e)
    {
        WarningsScroll.MaxHeight = WarningsScroll.MaxHeight > 100 ? 74 : 260;
        // 펼친 상태면 위 화살표(접기), 접힌 상태면 아래 화살표(펼치기)
        WarningsChevron.Text = WarningsScroll.MaxHeight > 100 ? "" : "";
    }

    // ===================== 열 머리글 =====================

    /// <summary>
    /// 목록에 세로 스크롤바가 보이면 그 너비만큼 머리글 오른쪽 여백을 늘려 열을 맞춘다 (줄이 적어 스크롤바가 없으면 여백 없이).
    /// 스크롤바 표시 여부는 줄 수 · 필터 · 창 크기로 바뀌므로 ScrollChanged 마다 다시 계산한다
    /// </summary>
    private void OnRowListScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not ScrollViewer sv || HeaderGrid == null) return;
        double bar = 0;
        if (sv.ComputedVerticalScrollBarVisibility == Visibility.Visible)
        {
            bar = sv.Template?.FindName("PART_VerticalScrollBar", sv) is ScrollBar sb && sb.ActualWidth > 0
                ? sb.ActualWidth
                : SystemParameters.VerticalScrollBarWidth;
        }
        var m = HeaderGrid.Margin;
        if (m.Right != 10 + bar) HeaderGrid.Margin = new Thickness(m.Left, m.Top, 10 + bar, m.Bottom);
    }
}
