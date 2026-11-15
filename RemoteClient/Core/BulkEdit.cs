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
    /// <summary>태그 변경 (키, 값). 넘긴 키만 바꾸고 값이 null 이면 그 태그를 지운다 — 다른 태그는 호스트마다 그대로. 클라우드 호스트에도 적용</summary>
    public List<KeyValuePair<string, string?>>? Tags { get; set; }

    public bool IsEmpty => Group == null && !SetVault && !ClearOwnCredentials && Username == null &&
                           Port == null && UsePublicIp == null && Favorite == null && Tags is not { Count: > 0 };

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
        var tagChanged = false;
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
        if (Tags is { Count: > 0 })
        {
            // 키·값 검사는 대화상자·MCP 가 미리 한다 (여기서 던지면 일부 호스트만 바뀐 채 멈춤)
            var changes = new List<HostTags.Change>();
            var next = HostTags.Apply(h.Tags, Tags, replace: false, changes);
            if (changes.Count > 0) { h.Tags = next; tagChanged = true; }
        }

        return tagChanged || before != (h.Group, h.VaultId, h.Username, h.ProtectedPassword, h.KeyPath, h.Port, h.UsePublicIp, h.Favorite);
    }

    /// <summary>
    /// 태그 변경 목록을 미리 검사한다 (잘못된 키·값·중복 키면 ArgumentException). 개수 제한은 호스트마다 달라 Apply 때 본다 —
    /// 넘으면 ArgumentException 이므로 호출하는 쪽이 사본으로 먼저 적용해 확인한다
    /// </summary>
    public static void ValidateTags(IEnumerable<KeyValuePair<string, string?>> tags) =>
        HostTags.Apply(null, tags, replace: false, new List<HostTags.Change>());

    /// <summary>태그를 적용하면 개수 제한을 넘는 호스트가 있으면 오류 문구 (사본으로 확인, 원본은 그대로). 없으면 null</summary>
    public string? TagLimitError(IEnumerable<HostEntry> hosts)
    {
        if (Tags is not { Count: > 0 }) return null;
        foreach (var h in hosts)
        {
            try { HostTags.Apply(h.Tags, Tags, replace: false, new List<HostTags.Change>()); }
            catch (ArgumentException ex) { return $"{h.DisplayName}: {ex.Message}"; }
        }
        return null;
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
