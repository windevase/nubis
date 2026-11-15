using System.Collections.Generic;

namespace Nubis;

/// <summary>
/// 가져오기(엑셀/CSV · ~/.ssh/config · mRemoteNG · Ansible 인벤토리 · Active Directory)가 읽어 낸 서버 한 대.
/// 파서는 UI·저장소와 무관하게 이 형태만 만든다. 미리보기 화면이 이것을 HostEntry 로 바꿔 등록한다 (ImportMerge).
/// 비밀번호 등 비밀값은 어떤 출처에서도 가져오지 않는다 — 계정은 등록할 때 Vault 로 지정한다.
/// </summary>
public sealed record ImportedHost
{
    /// <summary>표시 이름 (없으면 주소)</summary>
    public string Name { get; init; } = "";
    /// <summary>IP 또는 호스트명 (DNS 이름 포함)</summary>
    public string Address { get; init; } = "";
    /// <summary>포트 (없으면 프로토콜 기본 포트)</summary>
    public int? Port { get; init; }
    /// <summary>SSH / RDP. null = 출처에 정보가 없음 (미리보기에서 포트·OS 로 추정, 기본 SSH)</summary>
    public Protocol? Protocol { get; init; }
    /// <summary>그룹 (폴더 · 인벤토리 그룹 · OU 등). 여러 단계면 " / " 로 잇는다</summary>
    public string? Group { get; init; }
    /// <summary>로그인 사용자명 (있을 때만)</summary>
    public string? Username { get; init; }
    /// <summary>점프 서버 (ProxyJump) — 같은 가져오기 결과 안의 다른 항목 Name 또는 "user@host:port" 형태 그대로</summary>
    public string? JumpHost { get; init; }
    /// <summary>OS 설명 (AD operatingSystem 등). Windows 가 들어 있으면 RDP 로 추정한다</summary>
    public string? Os { get; init; }
    /// <summary>설명 · 출처 메모 (미리보기에 표시)</summary>
    public string? Note { get; init; }
}

/// <summary>파서 결과: 가져온 서버 목록 + 사용자에게 보여 줄 경고 (건너뛴 줄, 무시한 비밀번호 등 — L.T/L.F 로 번역된 문구)</summary>
public sealed record ImportResult(List<ImportedHost> Hosts, List<string> Warnings)
{
    public static ImportResult Empty() => new(new List<ImportedHost>(), new List<string>());
}
