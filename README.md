<img src="RemoteClient/Assets/nubis-256.png" width="96" align="right"/>

# Nubis

**Cloud & AI Remote Client** — 클라우드 서버를 프로필별로 모아 SSH/RDP/SFTP 로 접속하고, Claude Code 로 여러 서버를 함께 다루는 Windows 원격 접속 클라이언트.

> 이름: 라틴어 *nubis* = \"구름의\" + 우리말 \"누비다\" (여러 클라우드의 서버를 누비며 접속한다)

## 구성

| 영역 | 구현 |
|---|---|
| UI | WPF (.NET 10), 다크 테마, 커스텀 타이틀바 탭 |
| SSH | [SSH.NET](https://github.com/sshnet/SSH.NET) + WebView2 위의 [xterm.js](https://xtermjs.org/) |
| RDP | Windows 내장 RDP ActiveX(`mstscax.dll`, mstsc 와 같은 엔진)를 `WindowsFormsHost` 로 임베드 |
| 저장 | `%APPDATA%\Nubis\hosts.json`. 비밀번호는 Windows DPAPI(현재 사용자)로 암호화 |
| 호스트키 | `%APPDATA%\Nubis\known_hosts.json` (SHA256 지문). 처음 접속은 확인 없이 저장(Settings 에서 확인 창 켜기 가능), 키가 바뀌면 항상 경고 |
| SFTP | SSH.NET SftpClient (터미널과 같은 계정으로 별도 연결) |
| 이전 데이터 | 처음 실행 시 `%APPDATA%\RemoteClient` 를 `%APPDATA%\Nubis` 로 자동 이전 (옮기지 못하면 빠진 파일만 복사) |
| 저장 안전성 | 모든 설정 파일은 임시 파일에 쓴 뒤 교체. 읽지 못한 파일은 `*.corrupt-날짜` 로 보관하고 시작 시 경고 |
| 데이터 폴더 지정 | 환경변수 `NUBIS_DATA_DIR` 로 다른 폴더 사용 (포터블 실행·테스트) |

## 파일

```
RemoteClient/            (프로젝트: Nubis.csproj)
  App.xaml               테마(색상, 버튼·입력창·스크롤바 스타일)
  MainWindow.xaml(.cs)   탭바, 사이드바, 호스트 카드, 편집 패널, 빠른 접속
  SshSessionView.cs      SSH 세션 (터미널 + SFTP 패널)
  SftpPanel.xaml         SFTP 파일 패널 (탐색·업로드·다운로드·이름변경·삭제)
  XtermView.cs           xterm.js 터미널 화면 공통 (테마·강조·버퍼 읽기)
  LocalTerminalView.cs   로컬 터미널 (ConPTY: PowerShell 탭, Claude 패널)
  MainWindow.Claude.cs   Claude 패널 + MCP 도구
  McpServer.cs           내장 MCP 서버 (Streamable HTTP)
  SshExec.cs / WinRm.cs  여러 서버 명령 실행 (SSH / WinRM)
  RdpSessionView.cs      RDP 세션 (연결/해상도 자동 맞춤/끊김 처리)
  RdpAxHost.cs           mstscax ActiveX 래퍼 + 이벤트 싱크
  CredentialDialog.xaml  접속 시 사용자명/비밀번호 입력창
  TerminalThemes.cs      터미널 색 테마 11종 (ANSI 16색 팔레트)
  Cloud.cs               클라우드 프로필 모델, AWS/Tencent/GCP/Azure 조회, 동기화 병합
  ProfileDialog.xaml     클라우드 프로필 설정 (기본 계정·포트·주소 방식)
  Models.cs / Storage.cs 호스트 모델, JSON 저장, DPAPI, known_hosts, 설정(settings.json)
  wwwroot/               terminal.html + xterm.js 5.5 (오프라인 번들)
  Assets/                nubis.ico, nubis-256.png
tests/Nubis.Tests/       자동 테스트 (xUnit)
tools/IconGen/           아이콘 생성기 (WPF 로 그려 ico/png 생성)
build.ps1                빌드/실행, 테스트, 배포(publish)
```

## 빌드

```powershell
.\build.ps1            # Debug 빌드 후 실행
.\build.ps1 test       # 자동 테스트
.\build.ps1 publish    # publish\Nubis.exe (자체 포함 단일 exe, .NET 설치 불필요)
```

배포 시 `publish\` 폴더 전체(`Nubis.exe` + `wwwroot\`)를 복사한다.
단일 exe 압축은 실행할 때마다 DLL 을 메모리에 풀어 약 100MB 를 더 쓰므로 끄고 배포한다 (exe 약 180MB).
WebView2 런타임은 Windows 10/11 에 기본 포함되어 있다.

## 사용법

- **NEW HOST**: SSH/RDP 호스트 등록 (그룹별로 카드 표시)
- **카드 더블클릭**: 연결 / 우클릭: 연결·편집·복제·삭제
- **빠른 접속**: 검색창에 `user@host[:port]` (SSH) 또는 `rdp host[:port]` 입력 후 Enter
- **탭**: 휠 클릭으로 닫기, `Ctrl+Tab` / `Ctrl+Shift+Tab` 전환 (WPF 포커스일 때)
- **터미널**: 드래그 선택 후 우클릭 = 복사, 선택 없이 우클릭 = 붙여넣기, `Ctrl+Shift+C/V`
- **끊긴 SSH 세션**: Enter 키로 재연결 / RDP: "다시 연결" 버튼
- **RDP 사용자**: `DOMAIN\user` 형식 지원. 창 크기를 바꾸면 원격 해상도가 자동으로 맞춰진다 (Windows 8.1/2012R2 이상)
- **RDP 설정**: mstsc 기본 설정 파일(`문서\Default.rdp`)을 그대로 따른다 — 연결 품질(자동 감지), 색 깊이, 화면 효과, 클립보드·프린터·드라이브·스마트카드 리디렉션, 소리, Windows 키 전달, 자동 재연결. 파일이 없으면 mstsc 기본값. 서버 인증서 경고만 Nubis 설정(기본: 확인할 수 없으면 경고. 자체 서명 인증서 환경이면 Settings 에서 꺼서 바로 접속)을 따른다
- **바로가기**: `Nubis.exe --connect "호스트이름"` 으로 시작하면서 바로 접속
- **Cloud**: 클라우드 계정의 서버를 프로필 그룹으로 가져온다 (아래 참고)
- **Settings**: 터미널 테마(다크 9종 + 라이트 2종), 글꼴, 글꼴 크기. 열려 있는 터미널에 즉시 반영
  - *구문 강조*: IP 주소(CIDR·포트 포함), 숫자(단위 포함), 상태 키워드(error/warn/ok 및 한글 오류·경고·성공)에 색을 덧입힘. 출력 데이터는 변경하지 않고, 서버가 색을 지정한 글자와 vim·htop 같은 전체 화면 프로그램은 제외
  - *터미널 GPU 가속*: 기본 꺼짐 (WebView2 메모리 약 70MB 절약). 대량 출력이 잦으면 켠다 — 앱 재시작 후 적용
  - *글자색 자동 대비 보정*: 배경과 대비가 낮은 글자색(밝은 테마의 흰 글자 등)을 읽을 수 있게 자동 조정 (xterm.js `minimumContrastRatio` 4.5)

## 클라우드 프로필 동기화

이 PC의 CLI 로그인 정보를 그대로 사용한다. 별도 키 입력 없음.

| 클라우드 | 그룹 단위 | 인증 | 탐색 위치 |
|---|---|---|---|
| AWS | 프로필 | `~/.aws/config` (SSO 는 `aws sso login`) | config 의 `[profile …]` |
| Tencent | 프로필 | `~/.tccli/<프로필>.credential` | `.credential` 파일 |
| GCP | 프로젝트 | ADC (`gcloud auth application-default login`) | ACTIVE 프로젝트 (`sys-`, `gen-lang-client-` 제외) |
| Azure | 구독 | az CLI (`az login`) | 접근 가능한 모든 테넌트의 활성 구독 |

- Cloud 페이지에서 **프로필 추가** → 이 PC에서 찾은 프로필/프로젝트/구독 선택 → 바로 동기화
- 서버 → 호스트 변환 규칙 (기존 CDK 스크립트와 동일)
  - 이름: `Name` 태그(또는 인스턴스 이름)를 `이름:용도` 로 분리
  - 주소: 사설 IP 와 공인 IP(EIP 등)를 모두 저장. 기본 접속은 사설 IP (프로필 설정에서 공인 IP 를 기본으로, 호스트별 편집·우클릭 "공인/사설 IP로 연결" 로 선택)
  - Claude 명령 실행(run_command)은 사설 IP 를 먼저 시도하고 안 되면 공인 IP 로 (WinRM: 사설 5986→5985→공인 5986→5985, SSH: 사설→공인). 인증 실패면 계정 잠금 방지를 위해 바로 중단
  - Windows → RDP, 그 외 → SSH. 포트는 Settings → **기본 포트**(기본 22 / 3389)를 따르고, 프로필 설정에서 프로필별로 따로 지정할 수도 있다. 기본 포트를 바꾸면 그 포트를 쓰던 동기화 호스트도 함께 바뀐다 (호스트별로 직접 바꾼 포트는 유지)
  - terminated 제외, 중지된 서버는 흐리게 표시 (프로필 설정에서 제외 가능)
- 다시 동기화하면 이름·주소·상태는 갱신, 사용자가 바꾼 프로토콜·포트·계정은 유지, 사라진 서버는 제거
- 접속 계정: 호스트에 지정한 값 → 없으면 프로필 기본 계정 → 그것도 없으면 접속 시 입력
- 각 섹션의 **로그인** 버튼은 해당 CLI 로그인 명령을 새 콘솔 창에서 실행한다
- **인증 파일 경로**: 섹션마다 "경로 변경" 으로 직접 지정 (기본은 위 표의 표준 위치). AWS 는 `AWS_CONFIG_FILE`(같은 폴더의 `credentials` 포함), GCP 는 `GOOGLE_APPLICATION_CREDENTIALS`, Azure 는 `AZURE_CONFIG_DIR` 로 적용되어 로그인 버튼으로 띄우는 CLI 에도 같은 경로가 쓰인다. Tencent 는 지정한 폴더의 `*.credential` 을 읽는다
- 일부 리전/영역 조회가 실패하면 그 동기화에서는 **삭제하지 않는다** (일시 장애로 호스트·저장 계정이 사라지는 것 방지)
- 클라우드 호스트를 목록에서 삭제하면 해당 프로필에서 숨김 처리 (프로필 설정에서 해제). 복제하면 일반 호스트로 만들어진다

## Claude Code 패널

타이틀바의 **✦ Claude** 버튼(또는 `Ctrl+Shift+A`)으로 오른쪽 패널에 공식 Claude Code CLI 를 띄운다.

- 이 PC 의 `claude` CLI 를 로컬 터미널(ConPTY)로 실행 — 로그인·모델·슬래시 명령 등 CLI 기능 그대로 (처음엔 `/login`)
- 앱이 내장 MCP 서버(`127.0.0.1`, 실행마다 새 토큰)를 열고 `--mcp-config` 로 자동 연결
- 작업 폴더: `%APPDATA%\Nubis\claude` (여기 `CLAUDE.md` 는 자유롭게 수정 가능)

| MCP 도구 | 기능 | 승인 |
|---|---|---|
| `list_groups` / `list_hosts` | 그룹·호스트 검색 (비밀번호 제외) | - |
| `list_sessions` | 열린 탭 목록, 현재 보고 있는 탭 | - |
| `open_session` | "○○ 서버 접속해줘" → SSH/RDP 탭 열기 | - |
| `read_terminal` / `get_selection` | 현재(또는 지정) 터미널 화면·선택 영역 읽기 | - |
| `capture_screen` | 탭 화면을 이미지로 캡처 (RDP 화면을 Claude 가 직접 봄). 가려진 탭은 그 탭으로 전환 후 캡처 | - |
| `close_session` | 탭 닫기 | - |
| `diagnostics` | 앱 메모리·핸들·스레드 수 (누수 점검용) | - |
| `run_command` | 여러 서버 병렬 실행 — 리눅스 SSH / Windows WinRM(PowerShell·cmd) | **앱 승인 창** (설정 시 읽기 전용 명령은 생략) |
| `send_to_terminal` | 열린 터미널에 입력 | **앱 승인 창** (항상) |

- 승인 창: 대상 서버·명령 전체 표시, 위험 명령(rm -rf, reboot, Stop-Service 등) 경고, 뜬 직후 1.5초간 실행 버튼 잠금, Esc = 거부
- **읽기 전용 명령 자동 승인** (Settings → Claude, 기본 꺼짐): `run_command` 중 조회 명령만 승인 창 없이 실행. 허용 목록 방식 — 리눅스 `ls/cat/grep/df/ps/ss/journalctl`, `systemctl status`, `docker ps`, `kubectl get` 등 / PowerShell `Get-*`·`Select-/Where-/Sort-/Format-*`·`Test-*` / cmd·공통 `ipconfig`, `sc query`, `reg query`, `netstat`, `tasklist` 등. 파일로 출력(`>`), 명령 치환(`$( )`, 백틱), 메서드·.NET 호출, `Invoke-*`, 목록에 없는 명령·쓰기 옵션(`systemctl restart`, `find -delete`, `ipconfig /release` …)이 하나라도 있으면 승인 창. 위험 명령은 항상 승인. 자동 실행도 감사 로그에 "자동 승인(읽기 전용)" 으로 남음
- 모든 요청은 `%APPDATA%\Nubis\claude_audit.log` 에 기록 (패널 헤더의 문서 아이콘)
- `group:Production` 처럼 그룹 전체 지정 가능, 중지된 서버는 자동 제외

### WinRM (Windows 서버 명령 실행)

- **5986(HTTPS) 우선 → 실패 시 5985(HTTP)** 로 재시도. 셸 생성 이후 오류는 중복 실행 방지를 위해 재시도하지 않음
- 5986: TLS + NTLM/Negotiate (자체 서명 인증서 허용)
- 5985: NTLM 메시지 암호화를 직접 수행 (WinRM 기본 정책 `AllowUnencrypted=false` 대응) — 로컬 TrustedHosts 설정 불필요
- 계정: RDP 와 같은 호스트/클라우드 프로필 계정 (`DOMAIN\user`, `user@domain`, 로컬 계정)
- 서버 쪽 준비: WinRM 리스너(`winrm quickconfig`, HTTPS 는 인증서 바인딩) + 방화벽에서 5986/5985 허용

## SFTP 파일 전송

SSH 탭 왼쪽 도구 막대의 폴더 버튼으로 SFTP 패널을 연다 (경계선을 끌어 폭 조절).

- 탐색: 경로 직접 입력, 폴더 더블클릭, 상위(Backspace)·홈·새로고침(F5)
- **업로드**: 탐색기에서 파일·폴더를 끌어다 놓기 (폴더는 하위까지) 또는 업로드 버튼. 이미 있으면 덮어쓰기 확인
- **다운로드**: 선택 후 다운로드 버튼 → 폴더 선택 (기본: 다운로드 폴더). 파일 더블클릭 = 임시 폴더로 받아 기본 프로그램으로 열기
- 우클릭: 열기·다운로드·이름 변경(F2)·삭제(Delete, 폴더는 하위까지)·새 폴더·경로 복사
- 전송 진행률 표시, 취소 가능
- 인증: 터미널 세션이 로그인에 성공한 계정을 그대로 사용

## 로컬 터미널

타이틀바 **+** 버튼(또는 `Ctrl+Shift+T`)으로 로컬 PowerShell 탭. 터미널 포커스 중에도 `Ctrl+Tab` 탭 전환 동작.

## 라이선스

[MIT License](LICENSE) — 개인·상업 용도 관계없이 자유롭게 사용·수정·배포할 수 있다 (저작권 고지와 라이선스 문구 유지).

포함·사용한 오픈소스 구성요소와 테마 출처는 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) 참고.

## 알려진 제한

- RDP 화면에 포커스가 있으면 `Ctrl+Tab` 은 원격으로 전달된다 (탭 클릭으로 전환)
- 포트 포워딩, 점프 호스트, RDP 게이트웨이는 아직 없음
- SFTP 패널 → 탐색기로 끌어내기(drag-out)는 미지원 (다운로드 버튼 사용)
- 데이터 폴더 이름이 바뀌어 Claude 패널 작업 폴더 신뢰 확인이 한 번 더 뜬다
