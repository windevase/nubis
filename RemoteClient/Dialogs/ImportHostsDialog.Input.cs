using System.Windows.Controls;
using System.Windows.Input;

namespace Nubis;

/// <summary>가져오기 창: 키보드(Enter 로 불러오기 · Esc) · 미리보기 검색 · 경고 펼치기</summary>
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
    }
}
