using System;
using System.Linq;
using System.Windows;

namespace Nubis;

// 호스트 복제 (우클릭 "복제" · MCP duplicate_host 공통) · 클라우드 호스트 사본을 직접 관리하는 호스트로 바꾸기
public partial class MainWindow
{
    private void OnMenuDuplicate(object sender, RoutedEventArgs e)
    {
        if (HostOf(sender) is not { } h) return;
        var (copy, note) = MakeDuplicate(h);
        // 저장은 편집 패널에서 "저장" 을 눌렀을 때만 (취소하면 사본이 남지 않음)
        OpenEditor(null, copy);
        // 열려 있던 편집 패널의 변경을 버리지 않기로 해 복제 패널이 열리지 않았으면 그 패널에 안내를 쓰지 않는다
        if (note != null && ReferenceEquals(_template, copy)) EditorError.Text = note;
    }

    /// <summary>
    /// 호스트 복제본 (우클릭 "복제" · MCP duplicate_host 공통). 새 id, 이름 "(복사본)", 최근 접속·즐겨찾기 없음.
    /// group 을 주면 그 그룹으로 (클라우드 원본은 DetachFromCloud 가 빈 그룹만 프로필 이름으로 채우므로 먼저 넣는다).
    /// 클라우드 호스트의 복사본은 직접 관리하는 호스트로 (같은 인스턴스 ID 를 가진 유령 사본 방지). note = 옮기지 못한 계정 안내
    /// </summary>
    private (HostEntry Copy, string? Note) MakeDuplicate(HostEntry h, string? label = null, string? group = null)
    {
        var copy = h.Clone();
        copy.Id = Guid.NewGuid();
        copy.Label = label ?? L.F("{0} (복사본)", string.IsNullOrWhiteSpace(h.Label) ? h.Address : h.Label);
        copy.LastConnected = null;
        copy.Favorite = false;
        if (group != null) copy.Group = group;
        var note = h.IsCloud ? DetachFromCloud(copy) : null;
        return (copy, note);
    }

    /// <summary>
    /// 클라우드 호스트(의 사본)를 직접 관리하는 호스트로 바꾼다. 프로필이 없어지므로 원본이 실제로 쓰던 계정을 호스트에 옮긴다.
    /// 옮기지 못한 것이 있으면 편집 패널에 보여 줄 안내를 돌려준다.
    /// </summary>
    private string? DetachFromCloud(HostEntry copy)
    {
        var profile = _profiles.FirstOrDefault(p => p.Id == copy.CloudProfileId);
        var (vaultId, source) = copy.ResolveVault(profile);
        string? note = null;
        var v = VaultStore.Get(vaultId);
        if (vaultId != null && (source == "host" || !BulkEdit.HasOwnCredentials(copy)))
            copy.VaultId = vaultId;   // 원본도 그 Vault 계정으로 로그인 — 복사본에 직접 지정 (프로필이 없어지므로)
        else if (v != null && source == "default")
        {
            // 설정의 기본 Vault 계정: 프로필이 없어져도 복사본에 그대로 적용된다 — 비밀번호를 옮겨 적으면
            // 호스트 값이 생겨 기본 계정(SSH 키 포함)이 더는 쓰이지 않으므로 아무것도 바꾸지 않는다
        }
        else if (v != null)
        {
            // 원본은 프로필 기본 계정이 호스트에 적힌 사용자명 뒤에 쓰였다 (호스트에 비밀번호·키 파일이 있으면 Vault 를 안 쓰므로 여기는 사용자명만 있는 경우).
            // Vault 에 사용자명이 없거나 같으면 직접 지정해도 로그인 사용자·비밀번호·키가 그대로다
            if (string.IsNullOrWhiteSpace(v.Username) || string.Equals(v.Username, copy.Username, StringComparison.Ordinal))
                copy.VaultId = vaultId;
            else
            {
                // 직접 지정하면 Vault 의 사용자명이 우선이 되어 로그인 사용자가 바뀌므로 빈 값만 채운다 — 키는 옮길 수 없다
                if (string.IsNullOrEmpty(copy.ProtectedPassword)) copy.ProtectedPassword = v.ProtectedPassword;
                if (copy.Protocol == Protocol.Ssh && v.HasKey)
                    note = L.F("원본이 쓰던 Vault 계정 '{0}' 의 SSH 키는 사용자명이 달라 옮기지 못했습니다. 키로 로그인하려면 Vault 계정을 직접 고르세요.", v.Name);
            }
        }
        else
        {
            if (string.IsNullOrEmpty(copy.Username)) copy.Username = profile?.DefaultUsername ?? "";
            if (string.IsNullOrEmpty(copy.ProtectedPassword)) copy.ProtectedPassword = profile?.ProtectedDefaultPassword;
        }
        // 그룹은 비어 있을 때만 프로필 이름으로 — 편집 패널에서 적은 그룹(지워진 클라우드 호스트를 다시 추가할 때)을 덮어쓰지 않게
        if (string.IsNullOrWhiteSpace(copy.Group)) copy.Group = profile?.GroupName ?? "";
        copy.CloudProfileId = null;
        copy.CloudInstanceId = null;
        copy.CloudState = null;
        copy.CloudRegion = null;
        copy.CloudName = null;
        return note;
    }
}
