using System;
using System.Threading;
using System.Windows;

namespace Nubis;

/// <summary>가져오기 창: Active Directory 조회 (입력 확인 · 조회 · 취소)</summary>
public partial class ImportHostsDialog
{
    // ===================== Active Directory =====================

    private void OnAdCurrentUserChanged(object sender, RoutedEventArgs e)
    {
        if (AdCredRow != null) AdCredRow.IsEnabled = AdCurrentUser.IsChecked != true;
    }

    /// <summary>Vault 계정으로 사용자명·비밀번호 칸을 채운다 (이번 조회에만 쓰고 저장하지 않음)</summary>
    private void OnAdPickVault(object sender, RoutedEventArgs e)
    {
        if (VaultPickerDialog.Pick(this, null, Protocol.Rdp, L.T("Active Directory 조회에 쓸 계정을 고르세요. 이번 조회에만 쓰고 저장하지 않습니다.")) is not { } id) return;
        if (VaultStore.Get(id) is not { } v) return;
        AdCurrentUser.IsChecked = false;
        AdUserBox.Text = v.Username;
        AdPasswordBox.Password = Secret.Unprotect(v.ProtectedPassword);
    }

    private async void OnFetchAd(object sender, RoutedEventArgs e)
    {
        // 조회 중에 다시 누르면 취소
        if (_adCts != null) { CancelAd(L.T("조회를 취소했습니다.")); return; }

        var domain = AdDomainBox.Text.Trim();
        if (domain.Length == 0) { ShowStatus(L.T("도메인을 입력하세요."), true); AdDomainBox.Focus(); return; }
        var current = AdCurrentUser.IsChecked == true;
        var user = AdUserBox.Text.Trim();
        if (!current && user.Length == 0) { ShowStatus(L.T("사용자를 입력하거나 '현재 Windows 로그인 사용' 을 켜세요."), true); AdUserBox.Focus(); return; }
        int? days = null;
        if (AdRecentCheck.IsChecked == true)
        {
            if (!int.TryParse(AdDaysBox.Text.Trim(), out var d) || d is < 1 or > 3650) { ShowStatus(L.T("최근 로그인 일수는 1~3650 사이 숫자로 입력하세요."), true); AdDaysBox.Focus(); return; }
            days = d;
        }
        var options = new AdImportOptions(domain, NullIfEmpty(AdServerBox.Text), current ? null : user, current ? null : AdPasswordBox.Password,
            current, NullIfEmpty(AdBaseBox.Text), AdServersOnly.IsChecked == true, AdSkipDisabled.IsChecked == true, days, AdResolveIp.IsChecked == true);

        var cts = new CancellationTokenSource();
        _adCts = cts;
        // 읽는 중이던 파일 결과가 나중에 와도 AD 결과를 덮지 않게 — 반대로 조회 중에 파일을 불러오면 늦게 온 AD 결과를 버린다
        var seq = ++_loadSeq;
        ClearValue(CursorProperty);   // 버려진 파일 읽기의 대기 커서
        AdFetchButton.Content = L.T("조회 취소");
        ShowStatus(L.F("{0} 에서 컴퓨터 목록을 가져오는 중...", domain), false);
        try
        {
            var result = await ActiveDirectoryImport.FetchAsync(options, cts.Token);
            if (!cts.IsCancellationRequested && seq == _loadSeq && !_closed) SetSource(result, "Active Directory");
        }
        catch (OperationCanceledException) { if (seq == _loadSeq && !_closed) ShowStatus(L.T("조회를 취소했습니다."), false); }
        catch (Exception ex) { if (seq == _loadSeq && !_closed) ShowStatus(L.F("Active Directory 조회 실패: {0}", ex.Message), true); }
        finally
        {
            // 취소한 뒤 새 조회를 시작했으면 그쪽 상태(_adCts · 버튼)는 건드리지 않는다
            if (ReferenceEquals(_adCts, cts))
            {
                _adCts = null;
                AdFetchButton.Content = L.T("가져오기");
            }
            cts.Dispose();
        }
    }

    /// <summary>
    /// AD 조회 취소. LDAP 바인드 · 페이지 요청은 취소 토큰을 보지 않아 최대 30초씩 걸릴 수 있으므로 끝나기를 기다리지 않고
    /// 화면(버튼 · 상태)을 바로 되돌린다 — 뒤늦게 끝난 조회의 결과는 순번(_loadSeq)으로 버리고, 연결은 그 조회가 끝날 때 닫힌다
    /// </summary>
    private void CancelAd(string? status)
    {
        if (_adCts is not { } cts) return;
        _adCts = null;
        cts.Cancel();
        ++_loadSeq;
        AdFetchButton.Content = L.T("가져오기");
        if (status != null) ShowStatus(status, false);
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
