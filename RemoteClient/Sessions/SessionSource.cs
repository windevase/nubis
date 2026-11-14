using System;

namespace Nubis;

/// <summary>
/// 탭이 저장된 호스트를 다시 볼 수 있게 하는 연결점 (목록에 있는 호스트만).
/// Reload: 지금 저장된 호스트·Vault 기준 접속용 사본 — 다시 연결할 때 바뀐 계정을 반영한다.
/// CredentialSource: 그 계정이 어디서 왔는지 ("Vault 계정 'x'" 등) — 터미널 첫 줄에 보인다.
/// </summary>
public sealed record SessionSource(Func<HostEntry> Reload, Func<string?> CredentialSource);
