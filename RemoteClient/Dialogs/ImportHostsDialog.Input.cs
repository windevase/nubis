using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Nubis;

/// <summary>가져오기 창: 키보드(Enter 로 불러오기 · Esc) · 미리보기 검색 · 경고 펼치기 · 열 머리글 맞춤</summary>
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

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        _view?.Refresh();
    }

    /// <summary>검색 칸에서 Esc = 검색어 지우기 (비어 있으면 창 닫기로 넘김)</summary>
    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || SearchBox.Text.Length == 0) return;
        SearchBox.Clear();
        e.Handled = true;
    }

    /// <summary>경고 목록 펼치기 · 접기</summary>
    private void OnToggleWarnings(object sender, MouseButtonEventArgs e)
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
