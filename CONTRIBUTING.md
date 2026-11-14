# Contributing to Nubis

**English** | [한국어](#한국어)

Thanks for your interest! Bug reports, ideas and pull requests are all welcome. Please read the [Code of Conduct](CODE_OF_CONDUCT.md) first.

## Reporting bugs and asking for features

- Use the issue templates (**Bug report** / **Feature request**).
- **Security problems:** do not open a public issue — follow [SECURITY.md](SECURITY.md).
- Remove passwords, keys, tokens and real server addresses from logs and screenshots.

## Development setup

Requirements: Windows 10/11 x64 and the .NET 10 SDK. A per-user SDK install (`%LOCALAPPDATA%\Microsoft\dotnet`) is fine — `build.ps1` finds it.

```powershell
.\build.ps1            # Debug build, then run
.\build.ps1 test       # xUnit tests (tests\Nubis.Tests)
.\build.ps1 publish    # publish\ folder + dist\ portable zip
```

When you call `dotnet` directly, set `$env:DOTNET_ROOT="$env:LOCALAPPDATA\Microsoft\dotnet"` first.

**Never test with your real data.** Point the app at a throwaway folder:

```powershell
$env:NUBIS_DATA_DIR = "$env:TEMP\nubis-test"; .\RemoteClient\bin\Debug\net10.0-windows\Nubis.exe
```

The public demo server `test.rebex.net` (`demo` / `password`) is handy for SSH/SFTP checks.

## Pull requests

1. Branch from `main`; keep each PR focused on one topic.
2. Before you open the PR:
   - `.\build.ps1 test` passes and the build has **0 warnings**.
   - New logic has tests (UI-independent code lives in testable classes such as `McpHelpers`, `KbdAuth`, `BulkEdit`).
3. Follow the project rules in [CLAUDE.md](CLAUDE.md). The most important ones:
   - **UI text** is wrapped with the Korean original as the key — `L.T("한국어")`, `L.F("{0} …", x)`, XAML `{l:Tr '…'}` — and the English translation goes into `RemoteClient\Lang\en\*.json` in the same change. `LocalizationTests` checks this.
   - Anything that runs on a remote server for the Claude panel needs the approval dialog **and** an audit-log entry. Never put passwords or keys in tool results or logs.
   - Never retry authentication failures, and never weaken host-key mismatch warnings.
   - Files that store accounts use whole-file DPAPI (`SafeFile.WriteProtected`) and are added to `ClaudeGuard.SecretFiles`.
   - Do not switch the release to single-file publishing (antivirus false positives).
   - Code comments and UI text are in Korean; `.cs`/`.xaml` are UTF-8 without BOM, `.ps1` UTF-8 with BOM.
4. When a feature changes, update **both** `README.md` and `README.ko.md` (and `docs/guide*.md` where relevant). New open-source dependencies go into `THIRD-PARTY-NOTICES.md`.
5. Fill in the pull request template, including what you could not verify (for example, WinRM against a real server).

SSH.NET is a patched fork (`third_party\SSH.NET`); rebuild it with `tools\build-sshnet.ps1` if you need to change it.

## License

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE).

---

## 한국어

관심 가져 주셔서 고맙습니다. 버그 신고, 아이디어, PR 모두 환영합니다. 먼저 [행동 강령](CODE_OF_CONDUCT.md)을 읽어 주세요.

**이슈:** 이슈 템플릿(버그 신고 / 기능 제안)을 써 주세요. 보안 문제는 공개 이슈 대신 [SECURITY.md](SECURITY.md) 를 따라 주세요. 로그·화면에서 비밀번호·키·토큰·실제 서버 주소는 지워 주세요.

**개발 환경:** Windows 10/11 x64, .NET 10 SDK (사용자 폴더 설치도 됨). `.\build.ps1` (실행) · `.\build.ps1 test` (테스트) · `.\build.ps1 publish` (배포 파일). **실제 데이터로 테스트하지 마세요** — `NUBIS_DATA_DIR` 로 임시 폴더를 지정합니다. SSH/SFTP 확인에는 공개 데모 서버 `test.rebex.net` (`demo` / `password`) 를 쓸 수 있습니다.

**PR 규칙**
1. `main` 에서 브랜치를 만들고, PR 하나에는 한 가지 주제만 담아 주세요.
2. `.\build.ps1 test` 통과, 빌드 경고 0개. 새 로직에는 테스트를 붙여 주세요.
3. [CLAUDE.md](CLAUDE.md) 의 규칙을 따라 주세요. 특히 화면 문구는 한국어 원문을 키로 감싸고(`L.T`/`L.F`/`{l:Tr}`) 영어 번역을 `Lang\en\*.json` 에 함께 넣습니다. Claude 패널이 원격에서 실행하는 동작은 승인 창과 감사 로그를 거쳐야 하고, 인증 실패는 다시 시도하지 않으며, 호스트키 불일치 경고는 약하게 만들지 않습니다.
4. 기능이 바뀌면 `README.md` 와 `README.ko.md` 를 함께 고쳐 주세요.
5. PR 템플릿을 채우고, 확인하지 못한 부분도 적어 주세요.

기여한 코드는 [MIT 라이선스](LICENSE)로 배포됩니다.
