using System;
using System.Collections.Generic;

namespace Nubis;

/// <summary>
/// 여러 호스트를 한 번에 바꿀 값. null(또는 false) 인 항목은 바꾸지 않는다.
/// </summary>
public sealed class BulkEdit
{
    /// <summary>그룹 이름 ("" = 그룹 없음). 클라우드 호스트는 프로필 그룹이라 건너뛴다</summary>
    public string? Group { get; set; }
    /// <summary>true 면 VaultId 로 바꾼다 (VaultId 가 null 이면 지정 해제 = 기본 계정 따르기)</summary>
    public bool SetVault { get; set; }
    public Guid? VaultId { get; set; }
    /// <summary>호스트에 직접 적은 사용자명·비밀번호·키 파일을 지운다 (Vault 계정이 쓰이게)</summary>
    public bool ClearOwnCredentials { get; set; }
    /// <summary>사용자명. 직접 고른 Vault 계정에 사용자명이 있으면 접속 때는 그 값이 우선. ClearOwnCredentials 뒤에 적용</summary>
    public string? Username { get; set; }
    public int? Port { get; set; }
    /// <summary>공인 IP 로 접속할지. 공인 IP 가 있는 호스트에만 적용</summary>
    public bool? UsePublicIp { get; set; }
    public bool? Favorite { get; set; }

    public bool IsEmpty => Group == null && !SetVault && !ClearOwnCredentials && Username == null &&
                           Port == null && UsePublicIp == null && Favorite == null;

    /// <summary>호스트들에 적용하고 실제로 값이 바뀐 호스트 수를 돌려준다</summary>
    public int Apply(IEnumerable<HostEntry> hosts)
    {
        var changed = 0;
        foreach (var h in hosts)
            if (ApplyOne(h)) changed++;
        return changed;
    }

    private bool ApplyOne(HostEntry h)
    {
        var before = (h.Group, h.VaultId, h.Username, h.ProtectedPassword, h.KeyPath, h.Port, h.UsePublicIp, h.Favorite);
        var remote = h.Protocol != Protocol.Local;

        if (Group != null && !h.IsCloud) h.Group = Group.Trim();
        if (remote)
        {
            if (SetVault) h.VaultId = VaultId;
            if (ClearOwnCredentials) ClearCredentials(h);
            if (Username != null) h.Username = Username.Trim();
            if (Port is >= 1 and <= 65535) h.Port = Port.Value;
            if (UsePublicIp != null && h.HasPublicIp) h.UsePublicIp = UsePublicIp.Value;
        }
        if (Favorite != null) h.Favorite = Favorite.Value;

        return before != (h.Group, h.VaultId, h.Username, h.ProtectedPassword, h.KeyPath, h.Port, h.UsePublicIp, h.Favorite);
    }

    /// <summary>호스트에 직접 적은 사용자명·비밀번호·키 파일이 있는지 (있으면 그 값이 Vault 보다 우선)</summary>
    public static bool HasOwnCredentials(HostEntry h) =>
        !string.IsNullOrWhiteSpace(h.Username) || !string.IsNullOrEmpty(h.ProtectedPassword) || !string.IsNullOrWhiteSpace(h.KeyPath);

    /// <summary>호스트에 직접 적은 사용자명·비밀번호·키 파일을 지운다 (Vault 계정이 쓰이게)</summary>
    public static void ClearCredentials(HostEntry h)
    {
        h.Username = "";
        h.ProtectedPassword = null;
        h.KeyPath = null;
    }
}
