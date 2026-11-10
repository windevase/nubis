# Nubis — 작업 가이드

Windows 용 SSH / RDP / SFTP 원격 접속 클라이언트 (WPF, .NET 10). 클라우드(AWS·Tencent·GCP·Azure) 서버를 프로필별로 가져오고, 앱 안의 Claude Code 패널이 내장 MCP 서버로 앱을 다룬다. 기능 설명은 `README.md`.

## 빌드 · 실행 · 테스트

.NET SDK 는 **사용자 폴더 설치본** (`%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe`, PATH 에 없음, 관리자 권한 없음). `build.ps1` 이 알아서 찾는다.

```powershell
.\build.ps1            # Debug 빌드 후 실행 (RemoteClient\bin\Debug\net10.0-windows\Nubis.exe)
.\build.ps1 test       # xUnit (tests\Nubis.Tests)
.\build.ps1 publish    # publish\Nubis.exe 자체 포함 단일 exe (+ wwwroot\) + dist\ 포터블 zip·SHA256SUMS.txt
```

- 직접 `dotnet` 을 부를 때는 `$env:DOTNET_ROOT="$env:LOCALAPPDATA\Microsoft\dotnet"` 를 설정해야 빌드된 exe/테스트 exe 가 실행된다.
- 단일 exe **압축(EnableCompressionInSingleFile)은 쓰지 않는다** — 실행할 때마다 DLL 을 메모리에 풀어 약 100MB 를 더 쓴다. ReadyToRun 도 효과 대비 크기만 커져 뺐다 (exe 약 180MB 가 정상).
- **사용자가 `publish\Nubis.exe` 를 실행 중이면 publish 가 파일 잠금으로 실패한다.** 사용자의 Nubis 를 종료하지 말고, 닫아 달라고 요청한다. 확인:
  `Get-CimInstance Win32_Process -Filter "Name='Nubis.exe'" | ? ExecutablePath -like '*\publish\Nubis.exe'`
- 변경 후에는 빌드 경고 0 · 테스트 전체 통과를 확인한다.

## 구조

소스 폴더 이름은 `RemoteClient\` 이지만 프로젝트/어셈블리/네임스페이스는 `Nubis` 다 (폴더 이름은 VS Code 가 잡고 있어 못 바꿈. 바꾸면 `build.ps1` 경로 두 곳도 수정).

| 위치 | 역할 |
|---|---|
| `MainWindow.xaml(.cs)` | 창 초기화·사이드바 전환·창 제어. 화면별 코드는 partial 로 나눔 ↓ |
| `MainWindow.Hosts.cs` / `.HostEditor.cs` / `.Vault.cs` / `.Settings.cs` / `.Cloud.cs` / `.Tabs.cs` | 호스트 목록(가상화 카드) · 호스트 편집 패널 · Vault 페이지 · Settings 페이지 · 클라우드 페이지·동기화 · 세션 탭 |
| `Claude\MainWindow.ClaudePanel.cs` | Claude 패널(ConPTY 로 `claude` CLI), 현재 탭 알려주기 hook(`active-session.txt`) |
| `Claude\MainWindow.McpTools.cs` | **MCP 도구 등록 목록 전체** (`BuildTools`, `ExtraTools`) + 기본 도구·설정 변경 구현. 새 도구는 여기에 등록 |
| `Claude\MainWindow.Mcp*.cs` | 도구 구현: `McpHosts`(호스트 추가·수정·삭제, Vault 목록, 클라우드 동기화) · `McpDiagnostics`(test_connection, wait_for_output) · `McpSftp` · `McpCapture` · `McpRunCommand` |
| `Claude\McpServer.cs` | 최소 MCP 서버 (Streamable HTTP, JSON 응답, 127.0.0.1 + Bearer 토큰). 도구 결과는 string 또는 `McpImage` |
| `Claude\ClaudeGuard.cs` | Claude 패널 권한 거부 규칙(`<데이터>\claude\nubis-permissions.json`, 실행마다 다시 씀) — 계정 파일 읽기·셸 접근·DPAPI 복호화·settings.json 수정 차단 |
| `Claude\ReadOnlyCommands.cs` | run_command 읽기 전용 판별 (설정 `AutoApproveReadOnly` 일 때 승인 생략). 허용 목록 방식, 애매하면 false |
| `Claude\SettingsCatalog.cs` / `McpHelpers.cs` | update_settings 가 다룰 수 있는 설정·검증(보안 설정은 승인, 편집기 경로는 제외) / MCP 도구의 화면 무관 판단 로직(테스트 대상) |
| `Sessions\XtermView.cs` + `wwwroot\terminal.html` | WebView2 위 xterm.js 공통 화면 (테마·구문 강조·버퍼 읽기·단축키·OSC 7 현재 폴더) |
| `Sessions\SshSessionView.cs` | SSH 탭 (SSH.NET ShellStream) + 왼쪽 SFTP 패널 자동 열기·터미널 폴더 따라가기 |
| `Sessions\RdpSessionView.cs` / `RdpAxHost.cs` / `RdpDefaults.cs` | RDP 탭 (Windows 내장 mstscax ActiveX, `Default.rdp` 값 적용). 컨트롤은 `RdpAxHost.Acquire/Release` 풀로 재사용 — 인스턴스를 해제할 때마다 핸들·스레드가 남는 mstscax 누수 회피 |
| `Sessions\LocalTerminalView.cs` / `ConPty.cs` / `TerminalThemes.cs` | 로컬 PowerShell 탭·Claude 패널 (ConPTY, 자식 환경변수 정리 `ChildEnvironment`) / 터미널 테마 |
| `Sftp\SftpPanel.xaml(.cs)` + `.Transfer.cs` / `.DragOut.cs` / `.Edit.cs` | SFTP 패널: 탐색 · 업로드·다운로드 · 탐색기로 끌어내기(지연 렌더링) · 편집 후 저장하면 자동 업로드(충돌 확인) |
| `Remote\SshExec.cs` / `WinRm.cs` | 여러 서버 비대화형 명령 실행·SFTP 연결 (SSH / WinRM 직접 구현: 5986 HTTPS → 5985 HTTP+NTLM 암호화) |
| `Cloud\Cloud.cs` / `CloudSync.cs` / `*Provider.cs` | 클라우드 프로필·`CloudPaths`·제공자 목록 / 저장·`CloudSync.Apply` 병합 / 제공자 4종(AWS SDK, Tencent TC3 직접 서명, GCP·Azure REST) |
| `Vault\Vault.cs` + 대화상자 | Vault 계정(사용자명 + 비밀번호/SSH 키, `vault.json`). 호스트는 `VaultId` 하나로 참조하고, `WithDefaults` 가 접속용 사본에만 값을 채운다 — 호스트에 직접 적은 값 우선(`KeyMaterial`·`KeyPassphrase` 는 `[JsonIgnore]`). 예전 분리형(`Kind`/`ProtectedSecret`, `VaultPasswordId`/`VaultKeyId`)은 읽을 때 변환 |
| `Core\Models.cs` / `Storage.cs` / `Logging.cs` / `BrandIcons.cs` | 호스트 모델 · JSON 저장(`SafeFile`)·DPAPI(`Secret`)·known_hosts·설정 · `AuditLog`·`LogFiles` · 벡터 아이콘 |
| `Dialogs\*Dialog.xaml` | 앱 테마 대화상자. **`MessageBox.Show` 대신 `Dialog.Show`** (같은 인자 순서) |
| `tools\IconGen` | 앱 아이콘을 WPF 로 그려 `Assets\nubis.ico/png` 생성 |

- 네임스페이스는 폴더와 상관없이 모두 `Nubis` (폴더는 정리용). `MainWindow.xaml` 은 `Assets/...` 상대 경로를 쓰므로 루트에 둔다.
- 한 파일이 500줄을 넘기 시작하면 역할별 partial / 파일로 나눈다.

## 데이터 위치

- 사용자 데이터: `%APPDATA%\Nubis\` (`hosts.json`, `cloud_profiles.json`, `known_hosts.json`, `vault.json`, `login.json`, `settings.json`, `claude\`, `claude_audit.log`). 저장소에는 데이터가 없다.
- **계정별 프로필**: 시작 화면 로그인을 쓰면 `AppPaths.DataDir` 가 `users\<제공자>_<계정 ID>\`(`Profiles`) 로 바뀐다 — 앱 시작 때 한 번, 메인 창 전에. 공통 폴더(`AppPaths.RootDir`)에는 언어·로그인 설정(`SettingsStore.Global`)과 `login.json` 만. 화면 설정은 `SettingsStore.Current`(계정별). 언어·로그인은 반드시 `Global`/`UpdateGlobal` 로. 소유자(`Login.OwnerKey`)만 로그인 설정을 바꾼다.
- `hosts.json` · `cloud_profiles.json` · `known_hosts.json` · `vault.json` · `login.json` 은 **파일 전체 DPAPI 암호화**(`SafeFile.LoadProtected/WriteProtected`, 표식 `NUBIS-DPAPI1`, entropy `Nubis.DataFile.v1` — 바꾸지 않는다). 표식이 없는 평문은 읽을 때 자동 암호화. `settings.json` 은 평문 유지(비밀 값 없음, 로그인 설정을 잘못했을 때 사용자가 직접 고쳐 풀 수 있게). 새 데이터 파일을 만들면 `WriteProtected` 를 쓰고 `ClaudeGuard.SecretFiles` 에 추가한다.
- **암호화된 데이터는 이전 버전(0.1.2 이하)이 읽지 못한다** — 읽기 실패로 빈 목록이 되므로 옛 exe 를 실행하지 않게 안내한다.
- 캐시: `%LOCALAPPDATA%\Nubis\WebView2\`
- `NUBIS_DATA_DIR` 환경변수로 데이터 폴더를 바꿀 수 있다 → **테스트는 반드시 이걸로 별도 폴더에서** 한다. xUnit 은 `VaultTests.cs` 의 `[ModuleInitializer]` 가 임시 폴더로 지정한다.
- 구 폴더 `%APPDATA%\RemoteClient` 는 시작 시 자동 이전.

## 반드시 지킬 것

- **사용자 데이터를 건드리지 않는다.** `%APPDATA%\Nubis\hosts.json` 등을 테스트용으로 덮어쓰거나 지우지 않는다 (예전에 그렇게 해서 사용자가 동기화한 호스트가 지워진 적이 있다). 테스트 데이터는 `NUBIS_DATA_DIR` 의 임시 폴더에.
- **사용자의 Nubis 프로세스를 이름으로 종료하지 않는다.** 직접 띄운 프로세스만 PID 로 종료.
- `Secret` 의 DPAPI entropy 문자열 `"RemoteClient.v1"` 은 바꾸지 않는다 (바꾸면 저장된 비밀번호를 못 푼다).
- 원격 서버에 영향을 주는 동작(Claude 의 `run_command`, `send_to_terminal`)은 **`ApprovalDialog` 승인 + `AuditLog` 기록**을 거쳐야 한다. 새 실행 경로를 추가할 때도 같다. 예외는 사용자가 켠 `AutoApproveReadOnly` 의 읽기 전용 명령뿐이며, 이때도 감사 로그는 남긴다. `ReadOnlyCommands` 허용 목록을 넓힐 때는 쓰기 가능한 하위 명령·옵션을 막고 `ReadOnlyCommandTests` 에 반례를 추가한다. 비밀번호는 MCP 결과·로그에 절대 넣지 않는다. 계정이 저장되는 파일을 새로 만들면 `ClaudeGuard.SecretFiles` 에도 추가한다.
- 호스트키: 처음 보는 서버는 accept-new (대화형 접속은 Settings `SshHostKeyPrompt` 를 켜면 확인 창, 비대화형 run_command·SFTP 는 `SshAuth.VerifyAcceptNew`). **저장된 키와 다르면 항상 경고/거부** — 이 경고는 없애지 않는다. 사용자가 거부한 키는 `REJECTED` 표시로 남아 자동 신뢰되지 않는다.
- 클라우드 동기화: 일부 리전/영역 조회가 실패하면(`CloudFetchResult.IsPartial`) **삭제하지 않는다**. 사용자가 바꾼 프로토콜·포트·계정·`UsePublicIp` 는 유지, 이름·주소·상태만 갱신.
- 명령 실행 재시도: 연결 실패만 다음 포트·주소(사설→공인)로. **인증 실패·셸 생성 이후 오류는 재시도하지 않는다** (계정 잠금·중복 실행 방지).
- WPF 와 WinForms 를 같이 쓰므로 `ImplicitUsings` 는 꺼져 있다. `System.Drawing` 등은 완전한 이름으로 쓴다.
- 다크 테마: `ListBox` 등은 Windows 기본 글자색(검정)을 다시 지정하므로 `Foreground="{StaticResource Text}"` 를 명시한다.

## 화면 문구 번역 (한국어 기본 · 영어)

- 사용자에게 보이는 문구는 **한국어 원문을 키로** 감싼다 (`Core\Localization.cs`). 영어는 `RemoteClient\Lang\en\*.json` (`"한국어 원문": "English"`). 번역이 없으면 한국어가 그대로 보인다.
  - C#: `L.T("호스트 추가")`. 값이 들어가면 `L.F("{0} 에 업로드", path)` — 보간 문자열 `$"..."` 을 `L.T` 로 감싸지 않는다 (키가 매번 달라짐). 서식은 `{0:HH:mm}` 처럼 자리표시자에.
  - XAML: 루트에 `xmlns:l="clr-namespace:Nubis"` 를 두고 `Text="{l:Tr '주소'}"`. 작은따옴표는 `\'`, 쉼표·중괄호가 있어도 작은따옴표 안이면 된다. `<Run Text="{l:Tr '...'}"/>` 도 같다.
  - `L.T` 에는 문자열 리터럴 **하나**만 (이어 붙이기 `"a" + "b"` 금지 — 테스트가 키를 못 찾음).
- 번역하지 않는 것: MCP 도구 설명·도구 결과·`McpToolException` 메시지(Claude 만 읽음), 감사 로그 내용, 로그 파일, 정규식·구문 강조 키워드, 파일 이름·설정 키, UI 자동화가 찾는 이름이 아닌 내부 식별자. 단 MCP 경로라도 **사용자 화면에 뜨는** `ApprovalDialog.Ask` 제목·설명, `Dialog.Show`, 상태 문구는 번역한다.
- `LocalizationTests` 가 감싼 모든 키의 영어 번역 존재 · 자리표시자 일치 · 영어 값에 한국어가 남지 않았는지를 검사한다. 새 문구를 추가하면 같은 변경에서 영어 사전에도 넣는다.
- 언어는 공통 설정 `Language` (`ko`/`en`, 처음 실행 때 `LanguageWindow` 로 고름). 바꾸면 다시 시작해야 적용. Claude 패널 답변 언어도 따라간다.

## 인코딩 주의 (Windows PowerShell 5.1)

- PowerShell 5.1 의 `Get-Content`/`Set-Content` 는 BOM 없는 UTF-8 을 ANSI 로 읽고 써서 **한글이 깨진다** (csproj 주석이 그렇게 깨진 적 있음). 파일 수정은 Edit/Write 도구나 `[IO.File]::ReadAllText/WriteAllText(..., UTF8Encoding)` 를 쓴다.
- `build.ps1` 처럼 PowerShell 이 실행하는 `.ps1` 은 **UTF-8 BOM** 으로 저장해야 한글이 깨지지 않는다.
- `.cs`/`.xaml` 은 BOM 없는 UTF-8.

## 테스트 요령

- 공개 SSH/SFTP 데모 서버: `test.rebex.net:22`, `demo` / `password` (읽기 전용, 한글을 깨뜨려 돌려주므로 UTF-8 검증용으로는 부적합). 호스트키 `ssh-ed25519 SHA256:d7Te2DHmvBNSWJNBWik2KbDTjmWtYHe2bvXTMM9lVg4`.
- 테스트 호스트의 `ProtectedPassword` 는 DPAPI(entropy `RemoteClient.v1`, CurrentUser)로 만들어 `hosts.json` 에 넣는다. 테스트용 `hosts.json` 은 평문으로 써 두면 앱이 처음 읽을 때 암호화한다.
- 시작 화면 로그인은 `settings.json` 의 `Login` 으로 켜고 끈다 (테스트 데이터 폴더에서). 실제 로그인 검증에는 사용자의 OAuth 앱 Client ID 와 브라우저 승인이 필요하다.
- `Nubis.exe --connect "호스트이름"` 으로 시작하면서 바로 접속 (여러 번 지정 가능). `--claude` 는 시작하면서 Claude 패널을 연다.
- 터미널 내부 검증: `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS="--remote-debugging-port=9222"` 로 실행하면 CDP 로 `readBuffer(n)`, `term.write()` 등을 호출할 수 있다.
- MCP 서버는 Claude 패널을 열어야 시작된다. 주소·토큰은 `<데이터폴더>\claude\mcp-config.json`. 토큰은 출력하지 않는다.
- 화면 확인은 PrintWindow(`PW_RENDERFULLCONTENT`)로 창을 캡처 — WebView2·RDP 화면도 담긴다. 합성 마우스 클릭은 창이 가려지면 빗나가므로 결과를 캡처로 확인한다.
- 클라우드 조회 확인은 별도 콘솔 프로젝트에서 `Nubis.csproj` 를 참조해 `CloudProviders.Get(...).FetchAsync` 를 부른다 (조회 API 만, 리소스 변경 금지).
- WinRM 은 아직 실서버(5985/5986 이 열린 Windows 서버)에서 검증하지 못했다. 실서버 검증 전까지 WinRM 변경은 "미검증" 으로 보고한다.

## 문서

- 기능이 바뀌면 `README.md`(영어, GitHub 기본) 와 `README.ko.md`(한국어) 를 **둘 다** 같은 내용으로 갱신한다. 두 파일 맨 위의 언어 전환 링크는 유지한다. 새 오픈소스 의존성은 `THIRD-PARTY-NOTICES.md` 에 추가한다.
- 코드 주석과 사용자 문구는 한국어.
