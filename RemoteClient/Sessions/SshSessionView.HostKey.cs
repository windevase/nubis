using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Nubis;

/// <summary>SSH 탭: 호스트키 확인 (대상 서버·점프 서버)</summary>
public sealed partial class SshSessionView
{
    private enum HostKeyDecision { Trusted, AcceptedAfterPrompt, Rejected }

    private void OnHostKey(object? sender, HostKeyEventArgs e)
    {
        var d = CheckHostKey(_host, e);
        if (d == HostKeyDecision.AcceptedAfterPrompt) _hostKeyAccepted = true;
        if (d == HostKeyDecision.Rejected) _hostKeyRejected = true;
        e.CanTrust = d != HostKeyDecision.Rejected;
    }

    /// <summary>
    /// 점프 서버 호스트키 확인 — 대상 서버와 같은 정책(설정 SshHostKeyPrompt, 바뀐 키 경고·거부 기록)을 점프 서버 자신의 주소:포트 기준으로.
    /// JumpTunnel.Open 의 verifyHostKey (거부 사유, 신뢰하면 null)
    /// </summary>
    private string? VerifyJumpHostKey(HostEntry jump, HostKeyEventArgs e) =>
        CheckHostKey(jump, e) == HostKeyDecision.Rejected ? L.T("호스트키를 신뢰하지 않았습니다.") : null;

    /// <summary>대화형 호스트키 확인 (h = 대상 서버 또는 점프 서버). SSH.NET 의 백그라운드 스레드에서 호출되므로 창은 Dispatcher 로</summary>
    private HostKeyDecision CheckHostKey(HostEntry h, HostKeyEventArgs e)
    {
        var key = SshAuth.HostKeyId(h);
        var fp = SshAuth.Fingerprint(e);
        var known = KnownHostStore.Get(key);

        // OpenSSH 의 known_hosts 를 참고 — 폐기(@revoked)된 키는 Nubis 에 저장된 키와 같아도 항상 거부한다
        var openSsh = OpenSshKnownHosts.Check(h.Address, h.Port, e.HostKey);
        if (openSsh == OpenSshKnownHosts.Verdict.Revoked)
        {
            _term.Info(L.F("OpenSSH known_hosts 에 폐기(@revoked)된 호스트키라 연결하지 않습니다: {0}", fp), "31");
            return HostKeyDecision.Rejected;
        }
        if (known == fp) return HostKeyDecision.Trusted;

        // 일치·불일치는 Nubis 가 처음 보는 서버일 때만 참고 — ssh 로 이미 신뢰한 키면 묻지 않고, 다른 키면 바뀐 키처럼 경고.
        // Nubis 가 아는 서버는 저장된 값(또는 거부 기록) 기준으로 판단한다
        if (known != null) openSsh = OpenSshKnownHosts.Verdict.Unknown;
        if (openSsh == OpenSshKnownHosts.Verdict.Match)
        {
            KnownHostStore.Set(key, fp);
            _term.Info(L.F("OpenSSH known_hosts 에서 신뢰한 호스트키: {0} {1}", key, fp));
            return HostKeyDecision.Trusted;
        }

        // 이 주소로는 처음이지만 같은 서버의 다른 주소(사설·공인 IP)에 저장된 키가 있으면 그 값과 비교한다
        // (사설 IP 로 신뢰한 서버에 공인 IP 로 재시도할 때 다른 키를 처음 보는 서버처럼 자동 신뢰하지 않게)
        var sibling = known == null && openSsh == OpenSshKnownHosts.Verdict.Unknown
            ? SshAuth.CheckSiblingKeys(h, fp) : (SshAuth.SiblingVerdict.Unknown, null, null);
        if (sibling.Verdict == SshAuth.SiblingVerdict.Match)
        {
            KnownHostStore.Set(key, fp);
            _term.Info(L.F("같은 서버의 다른 주소({0})에서 신뢰한 호스트키: {1}", sibling.Id, fp));
            return HostKeyDecision.Trusted;
        }
        // 비교한 저장값이 어느 주소의 것인지 확인 창에 함께 보인다 (이 주소에는 저장된 값이 없다)
        var knownFrom = key;
        if (sibling.Verdict == SshAuth.SiblingVerdict.Mismatch)
        {
            known = sibling.Known;          // 아래에서 "바뀐 키" 경고로 묻는다
            knownFrom = $"{key} ← {sibling.Id}";
        }
        if (sibling.Verdict == SshAuth.SiblingVerdict.Rejected) known = KnownHostStore.RejectedMark(fp);   // 전에 거부한 키 — 다시 묻는다

        // 처음 보는 서버: 설정이 꺼져 있으면(기본) 확인 없이 저장하고 접속 (ssh 의 StrictHostKeyChecking=accept-new)
        // 저장된 키와 다른 경우(중간자 공격 가능)와 사용자가 전에 거부한 키는 아래에서 항상 묻는다
        if (known == null && openSsh == OpenSshKnownHosts.Verdict.Unknown && !SettingsStore.Current.SshHostKeyPrompt)
        {
            KnownHostStore.Set(key, fp);
            _term.Info(L.F("새 호스트키 저장: {0} {1}", key, fp));
            return HostKeyDecision.Trusted;
        }
        // 사용자가 전에 거부한 키는 "아직 신뢰 안 함" 으로 보고 다시 묻는다
        if (known != null && KnownHostStore.IsRejected(known)) known = null;

        var (title, msg, detail, icon) = openSsh == OpenSshKnownHosts.Verdict.Mismatch
            ? (L.T("호스트키가 바뀌었습니다"),
               L.T("OpenSSH(known_hosts)에 저장된 이 서버의 키와 다릅니다. 서버를 다시 설치했거나 IP 가 다른 서버로 바뀐 경우가 아니라면 중간자 공격일 수 있습니다.\n그래도 이 키를 신뢰하고 접속할까요?"),
               $"{key}\n{fp}", MessageBoxImage.Warning)
            : known == null
            ? (L.T("처음 접속하는 서버입니다"),
               L.F("{0} 서버의 호스트키를 신뢰하고 저장할까요?\n다음 접속부터는 묻지 않습니다.", h.DisplayName),
               $"{key}\n{fp}", MessageBoxImage.Question)
            : (L.T("호스트키가 바뀌었습니다"),
               L.T("저장된 값과 다릅니다. 서버를 다시 설치했거나 IP 가 다른 서버로 바뀐 경우가 아니라면 중간자 공격일 수 있습니다.\n그래도 새 키로 교체하고 접속할까요?"),
               L.F("{0}\n저장된 값: {1}\n현재 값:   {2}", knownFrom, known, fp), MessageBoxImage.Warning);

        var ok = Dispatcher.Invoke(() =>
            Dialog.Show(Window.GetWindow(this), msg, title, MessageBoxButton.YesNo, icon, detail) == MessageBoxResult.Yes);
        if (ok)
        {
            KnownHostStore.Set(key, fp);
            return HostKeyDecision.AcceptedAfterPrompt;
        }
        // 거부한 키는 기록해 둔다 — Claude 일괄 실행·SFTP 의 "처음 보는 키 자동 신뢰" 가 이 키를 받아들이지 않도록
        if (KnownHostStore.Get(key) == null) KnownHostStore.Set(key, KnownHostStore.RejectedMark(fp));
        return HostKeyDecision.Rejected;
    }
}
