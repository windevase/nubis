<img src="nubis.png" width="96" align="right"/>

# Nubis

**Cloud & AI Remote Client** — 클라우드 서버를 프로필별로 모아 SSH/RDP/SFTP 로 접속하고, Claude Code 로 여러 서버를 함께 다루는 Windows 원격 접속 클라이언트.

> 이름: 라틴어 *nubis* = "구름의" + 우리말 "누비다" (여러 클라우드의 서버를 누비며 접속한다)

## 주요 기능

- **앱 안의 Claude 에게 말로 시킨다** — 오른쪽 Claude 패널(공식 Claude Code CLI)이 내장 MCP 서버로 Nubis 를 직접 다룬다
  - 서버: "web-01 접속해줘", "Production 그룹 디스크 사용량 확인해줘", "nginx 에러 로그 끝부분 보여줘", "접속이 안 되는데 원인 찾아줘"
  - 관리: "10.0.1.5 를 web-02 로 Production 에 추가하고 운영 계정 연결해줘", "AWS 프로필 다시 동기화해줘"
  - 앱 설정: "터미널 테마 dracula 로, 글꼴 16 으로", "SFTP 자동 열기 꺼줘"
  - 지금 보고 있는 탭을 질문마다 자동으로 알려줘 "이 에러 뭐야?" 만으로 그 화면을 본다 (RDP 화면은 캡처해서 본다)
  - 서버를 바꾸는 명령·호스트 변경·파일 전송·보안 설정은 **항상 앱 승인 창**을 거치고 감사 로그에 남는다. 저장된 비밀번호·키는 Claude 가 읽을 수 없다
- **클라우드 서버 한 번에 가져오기** — AWS · Tencent · GCP · Azure 의 서버를 이 PC 의 CLI 로그인으로 프로필별 그룹으로 동기화
- **SSH · RDP · SFTP 한 창에서** — 탭 전환, 사설/공인 IP 선택, 여러 서버 동시 명령(SSH / WinRM)
- **SFTP 사이드바** — SSH 접속 시 자동으로 붙고, 탐색기와 끌어다 놓기로 주고받고, 파일을 편집기(VS Code 등)로 열어 **저장하면 바로 서버에 반영**, 터미널 폴더 따라가기
- **Vault** — 여러 서버가 함께 쓰는 계정(사용자명 + 비밀번호 / SSH 키)을 한 곳에 저장해 골라 쓴다
- **로컬 데이터 암호화 · 시작 화면 로그인** — 서버 목록·계정 파일 전체를 Windows DPAPI 로 암호화, 선택적으로 GitHub · Google · Microsoft 계정 로그인

## 구성

| 영역 | 구현 |
|---|---|
| UI | WPF (.NET 10), 다크 테마, 커스텀 타이틀바 탭 |
| SSH | [SSH.NET](https://github.com/sshnet/SSH.NET) + WebView2 위의 [xterm.js](https://xtermjs.org/) |
| RDP | Windows 내장 RDP ActiveX(`mstscax.dll`, mstsc 와 같은 엔진)를 `WindowsFormsHost` 로 임베드 |
| 저장 | `%APPDATA%\Nubis\` 의 `hosts.json` · `cloud_profiles.json` · `known_hosts.json` · `vault.json` 은 **파일 전체를 Windows DPAPI(현재 사용자, AES-256)로 암호화** — 서버 이름·IP·계정 이름도 보이지 않고, 이 PC·이 Windows 계정에서만 풀린다. 비밀번호·키는 그 안에서 한 번 더 DPAPI 로 암호화. 예전 평문 파일은 처음 읽을 때 자동으로 암호화 |
| 시작 화면 로그인 | 선택: GitHub · Google · Microsoft(Entra ID) 계정으로 로그인해야 앱이 열림 (Device Flow, 허용 계정·GitHub Organization). 아래 참고 |
| Vault | `%APPDATA%\Nubis\vault.json`. 여러 호스트가 함께 쓰는 비밀번호·SSH 개인키(+키 암호)를 DPAPI 로 암호화해 저장 |
| 호스트키 | `%APPDATA%\Nubis\known_hosts.json` (SHA256 지문). 처음 접속은 확인 없이 저장(Settings 에서 확인 창 켜기 가능), 키가 바뀌면 항상 경고 |
| SFTP | SSH.NET SftpClient (터미널과 같은 계정으로 별도 연결) |
| 이전 데이터 | 처음 실행 시 `%APPDATA%\RemoteClient` 를 `%APPDATA%\Nubis` 로 자동 이전 (옮기지 못하면 빠진 파일만 복사) |
| 저장 안전성 | 모든 설정 파일은 임시 파일에 쓴 뒤 교체. 읽지 못한 파일은 `*.corrupt-날짜` 로 보관하고 시작 시 경고 |
| 데이터 폴더 지정 | 환경변수 `NUBIS_DATA_DIR` 로 다른 폴더 사용 (포터블 실행·테스트) |

## 파일

```
RemoteClient/            (프로젝트: Nubis.csproj, 네임스페이스는 모두 Nubis)
  App.xaml               테마(색상, 버튼·입력창·스크롤바 스타일)
  MainWindow.*           창 + 화면별 partial (Hosts·HostEditor·Vault·Settings·Cloud·Tabs)
  Claude/                Claude 패널, 내장 MCP 서버·도구, 권한 차단 규칙, 읽기 전용 명령 판별
  Sessions/              SSH·RDP·로컬 터미널 세션, xterm.js 화면, ConPTY, 터미널 테마
  Sftp/                  SFTP 패널 (탐색·전송·끌어내기·편집 동기화)
  Remote/                여러 서버 비대화형 실행 (SSH / WinRM)
  Cloud/                 클라우드 프로필·동기화, 제공자 4종 (AWS·Tencent·GCP·Azure)
  Vault/                 Vault 계정 모델·저장, 추가·선택 대화상자
  Core/                  호스트 모델, JSON 저장·DPAPI·설정, 로그, 아이콘
  Dialogs/               승인·계정 입력·메시지·프로필·입력 대화상자
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
- **Vault**: 여러 서버가 함께 쓰는 로그인 계정을 한 곳에 저장해 두고 호스트에서 골라 쓴다
  - Vault 계정 = 사용자명 + 비밀번호 및/또는 SSH 개인키(+키 암호). 사이드바 *Vault* → "계정 추가" (키는 파일에서 가져오거나 내용 붙여넣기, OpenSSH·PuTTY .ppk·PEM). 저장할 때 키를 읽어 보고 공개키를 만들어 둔다 → "공개키 복사" 로 서버 `~/.ssh/authorized_keys` 에 등록
  - 호스트 편집 *로그인 계정* 에서 Vault 계정을 고르면 카드에 "사용자 root · 비밀번호 · ssh-ed25519 키" 처럼 무엇으로 로그인하는지 표시. 아래 사용자명·비밀번호·키 파일 칸은 그대로 쓸 수 있고, **비워 두면 Vault 값, 적으면 그 값이 우선**
  - RDP 는 Vault 계정의 사용자명·비밀번호만 쓴다 (키는 무시)
  - Vault 계정의 값을 바꾸면 그 계정을 쓰는 모든 호스트에 적용
  - Vault 비밀번호로 접속하던 호스트에서 인증이 실패해 새 비밀번호를 입력·저장하면 그 Vault 계정이 갱신된다
  - 계정을 지우면 쓰던 호스트에서 연결이 해제된다 (삭제 전 목록 표시)
- **Settings**: 터미널 테마(다크 9종 + 라이트 2종), 글꼴, 글꼴 크기. 열려 있는 터미널에 즉시 반영
  - *구문 강조*: IP 주소(CIDR·포트 포함), 숫자(단위 포함), 상태 키워드(error/warn/ok 및 한글 오류·경고·성공)에 색을 덧입힘. 출력 데이터는 변경하지 않고, 서버가 색을 지정한 글자와 vim·htop 같은 전체 화면 프로그램은 제외
  - *터미널 GPU 가속*: 기본 꺼짐 (WebView2 메모리 약 70MB 절약). 대량 출력이 잦으면 켠다 — 앱 재시작 후 적용
  - *글자색 자동 대비 보정*: 배경과 대비가 낮은 글자색(밝은 테마의 흰 글자 등)을 읽을 수 있게 자동 조정 (xterm.js `minimumContrastRatio` 4.5)
  - *SSH*: 처음 접속하는 서버의 호스트키 확인 창 / *SFTP*: 자동 열기 · 터미널 폴더 따라가기 · 파일 편집기 ([SFTP](#sftp-파일-전송))
  - *시작 화면 로그인*: GitHub · Google · Microsoft ([아래](#시작-화면-로그인-선택)) / *기본 포트*: 새 호스트·클라우드 동기화 포트
  - *Claude*: 읽기 전용 명령 자동 승인 / *RDP*: 서버 인증서 경고
  - 대부분의 설정은 Claude 패널에서 말로도 바꿀 수 있다 (`update_settings`. 보안 설정은 승인 창, 로그인·편집기 경로는 Claude 가 바꿀 수 없음)

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

## 시작 화면 로그인 (선택)

Settings → **시작 화면 로그인** 에서 GitHub · Google · Microsoft 중 하나를 고르면, Nubis 를 열 때 그 계정으로 로그인해야 들어갈 수 있다.

- 방식: OAuth Device Flow — 화면에 나온 코드를 브라우저에서 입력하고 승인. 비밀번호는 브라우저(GitHub/Google/Microsoft)에서만 입력하고 Nubis 는 받지 않는다. 받은 토큰은 계정 확인에만 쓰고 저장하지 않는다
- 허용: 계정 이름(GitHub 로그인 이름 / 이메일), `@회사.com` 은 그 도메인 전체, GitHub 는 Organization 멤버도 가능
- 로그인 유지(시간, 0 = 매번)·오프라인 허용(일, 0 = 막음). 마지막 로그인 기록은 `login.json` 에 암호화해 저장
- **테스트 로그인에 성공해야 저장**된다 (잘못된 설정으로 못 들어오는 일 방지). 그래도 못 들어오면 `%APPDATA%\Nubis\settings.json` 의 `"Login"` → `"Provider"` 를 `"None"` 으로
- 등록
  - GitHub: github.com/settings/developers → OAuth Apps → New OAuth App (Homepage·Callback URL 은 아무 주소) → **Enable Device Flow** 체크 → Client ID
  - Google: Google Cloud 콘솔 → 사용자 인증 정보 → OAuth 클라이언트 ID → 유형 **TV 및 입력 제한 기기** → Client ID·Secret
  - Microsoft: Entra 관리 센터 → 앱 등록 → 인증 → **공용 클라이언트 흐름 허용** → 애플리케이션(클라이언트) ID, 테넌트는 `organizations` 또는 회사 테넌트
- 한계: 로그인은 "앱에 들어가는 문" 이다. 데이터 암호화(DPAPI)와 묶여 있지 않으므로, 같은 Windows 계정에서 도는 프로그램이 파일을 직접 푸는 것까지 막지는 못한다 (다른 PC·계정으로 파일이 새는 것은 DPAPI 가 막음)

## Claude Code 패널

타이틀바의 **✦ Claude** 버튼(또는 `Ctrl+Shift+A`)으로 오른쪽 패널에 공식 Claude Code CLI 를 띄운다.

- 이 PC 의 `claude` CLI 를 로컬 터미널(ConPTY)로 실행 — 로그인·모델·슬래시 명령 등 CLI 기능 그대로 (처음엔 `/login`)
- 앱이 내장 MCP 서버(`127.0.0.1`, 실행마다 새 토큰)를 열고 `--mcp-config` 로 자동 연결
- 작업 폴더: `%APPDATA%\Nubis\claude` (여기 `CLAUDE.md` 는 자유롭게 수정 가능)
- **지금 보고 있는 탭을 자동으로 알려줌**: 탭을 바꾸거나 연결 상태가 바뀔 때마다 앱이 `claude\active-session.txt` 에 현재 탭(session_id·제목·SSH/RDP·주소·상태, 계정 정보 없음)을 써 두고, Claude Code 의 `UserPromptSubmit` hook 이 메시지를 보낼 때마다 그 한 줄을 질문에 붙인다. 그래서 탭을 옮긴 뒤 "이 에러 뭐야?" 라고만 해도 Claude 가 그 탭을 본다

| MCP 도구 | 기능 | 승인 |
|---|---|---|
| `list_groups` / `list_hosts` | 그룹·호스트 검색 (비밀번호 제외) | - |
| `list_sessions` | 열린 탭 목록, 현재 보고 있는 탭 | - |
| `open_session` | "○○ 서버 접속해줘" → SSH/RDP 탭 열기 | - |
| `read_terminal` / `get_selection` | 현재(또는 지정) 터미널 화면·선택 영역 읽기 | - |
| `capture_screen` | 탭 화면을 이미지로 캡처 (RDP 화면을 Claude 가 직접 봄). 가려진 탭은 그 탭으로 전환 후 캡처 | - |
| `close_session` | 탭 닫기 | - |
| `diagnostics` | 앱 메모리·핸들·스레드 수 (누수 점검용) | - |
| `list_vault` | Vault 계정 목록 (이름·사용자명·로그인 방식만, 비밀 값 없음) | - |
| `add_host` / `update_host` / `delete_host` | "10.0.1.5 를 web-01 로 Production 에 추가하고 운영 root 계정 연결" — 비밀번호는 못 넣고 Vault 계정 연결만. 클라우드 호스트는 주소·그룹 변경 불가 | **앱 승인 창** (항상 — 주소·Vault 를 바꿔 계정이 다른 서버로 가는 것 방지) |
| `test_connection` | 접속 안 될 때: 주소별(사설→공인) DNS, TCP 포트, SSH 배너, RDP 호스트는 WinRM 5986/5985 (로그인 안 함) | - |
| `wait_for_output` | 터미널에 특정 문자열이 나오거나 출력이 멈출 때까지 기다린 뒤 결과 반환 (`send_to_terminal` 다음에) | - |
| `sftp_list` / `sftp_read` | 원격 폴더 목록·텍스트 파일 읽기 (로그 끝부분 `tail`, 바이너리 거부) | `run_command` 와 같음 (읽기 전용 자동 승인 설정 따름) |
| `sftp_download` / `sftp_upload` | 다운로드는 `다운로드\Nubis\<호스트>` 로만(덮어쓰지 않음), 업로드는 Nubis 데이터 폴더·`~/.ssh`·개인키 파일 거부 | **앱 승인 창** (항상) |
| `cloud_sync` | 클라우드 프로필 서버 목록 다시 가져오기 (조회 API 만) | - |
| `get_settings` / `update_settings` | "테마 dracula 로, 글꼴 16" 처럼 앱 설정 조회·변경 (테마·글꼴·구문 강조·GPU·기본 포트) | 보안 설정(RDP 인증서 경고·호스트키 확인 창·자동 승인)만 **앱 승인 창**. 자동 승인은 Claude 가 켤 수 없고 끄기만 가능 |
| `run_command` | 여러 서버 병렬 실행 — 리눅스 SSH / Windows WinRM(PowerShell·cmd) | **앱 승인 창** (설정 시 읽기 전용 명령은 생략) |
| `send_to_terminal` | 열린 터미널에 입력 | **앱 승인 창** (항상) |

- 승인 창: 대상 서버·명령 전체 표시, 위험 명령(rm -rf, reboot, Stop-Service 등) 경고, 뜬 직후 1.5초간 실행 버튼 잠금, Esc = 거부
- **읽기 전용 명령 자동 승인** (Settings → Claude, 기본 꺼짐): `run_command` 중 조회 명령만 승인 창 없이 실행. 허용 목록 방식 — 리눅스 `ls/cat/grep/df/ps/ss/journalctl`, `systemctl status`, `docker ps`, `kubectl get` 등 / PowerShell `Get-*`·`Select-/Where-/Sort-/Format-*`·`Test-*` / cmd·공통 `ipconfig`, `sc query`, `reg query`, `netstat`, `tasklist` 등. 파일로 출력(`>`), 명령 치환(`$( )`, 백틱), 메서드·.NET 호출, `Invoke-*`, 목록에 없는 명령·쓰기 옵션(`systemctl restart`, `find -delete`, `ipconfig /release` …)이 하나라도 있으면 승인 창. 위험 명령은 항상 승인. 자동 실행도 감사 로그에 "자동 승인(읽기 전용)" 으로 남음
- 모든 요청은 `%APPDATA%\Nubis\claude_audit.log` 에 기록 (패널 헤더의 문서 아이콘)
- **저장된 계정 보호**: 패널의 Claude 는 계정·암호·키·서버 목록이 든 `hosts.json`·`cloud_profiles.json`·`vault.json`·`known_hosts.json`·`login.json` 을 읽을 수 없다 (Read 도구, 그 파일 이름이 들어간 셸 명령, DPAPI 복호화 명령을 `claude --settings` 거부 규칙으로 차단). 감사 로그·MCP 설정·`settings.json` 은 수정 불가 (앱 설정은 `update_settings` 의 검증·승인을 거쳐서만 바뀜). 같은 Windows 계정에서 도는 프로그램이라 완전한 격리는 아니며, 파일 자체는 DPAPI 로 암호화되어 이 PC·이 Windows 계정에서만 풀린다
- `group:Production` 처럼 그룹 전체 지정 가능, 중지된 서버는 자동 제외

### WinRM (Windows 서버 명령 실행)

- **5986(HTTPS) 우선 → 실패 시 5985(HTTP)** 로 재시도. 셸 생성 이후 오류는 중복 실행 방지를 위해 재시도하지 않음
- 5986: TLS + NTLM/Negotiate (자체 서명 인증서 허용)
- 5985: NTLM 메시지 암호화를 직접 수행 (WinRM 기본 정책 `AllowUnencrypted=false` 대응) — 로컬 TrustedHosts 설정 불필요
- 계정: RDP 와 같은 호스트/클라우드 프로필 계정 (`DOMAIN\user`, `user@domain`, 로컬 계정)
- 서버 쪽 준비: WinRM 리스너(`winrm quickconfig`, HTTPS 는 인증서 바인딩) + 방화벽에서 5986/5985 허용

## SFTP 파일 전송

SSH 로그인에 성공하면 왼쪽에 SFTP 패널이 자동으로 붙는다 (Settings → SFTP 에서 끌 수 있음. 도구 막대의 폴더 버튼으로 열고 닫기, 경계선을 끌어 폭 조절). 인증은 터미널이 로그인한 계정을 그대로 쓴다.

- **탐색**: 경로 직접 입력, 폴더 더블클릭, 상위(Backspace)·홈·새로고침(F5)
- **터미널 폴더 따라가기**: 셸이 현재 폴더를 알려주면(OSC 7) 터미널에서 `cd` 할 때 패널도 그 폴더로. 알려주지 않는 셸은 패널의 터미널 아이콘 버튼으로 이동 (프롬프트 `user@host:/경로$` 에서 읽음)
  - 서버에서 OSC 7 켜기 (bash): `~/.bashrc` 에 `PROMPT_COMMAND='printf "\e]7;file://%s%s\a" "$HOSTNAME" "$PWD"'`
- **업로드**: 탐색기에서 파일·폴더를 끌어다 놓기 (폴더는 하위까지) 또는 업로드 버튼. 목록의 폴더 행 위에 놓으면 그 폴더로 올린다. 이미 있으면 덮어쓰기 확인
- **다운로드**: 선택한 항목을 탐색기(바탕화면 등)로 끌어내기, 또는 다운로드 버튼 → 폴더 선택 (기본: 다운로드 폴더)
  - 끌어내기는 마우스를 놓는 순간 받기 시작한다 (진행률·취소는 SFTP 패널 아래에 표시, 받는 동안 탐색기는 기다린다)
  - 다른 SSH 탭의 SFTP 패널로 끌어다 놓으면 서버 간 복사 (이 PC 임시 폴더를 거친다)
- **편집 → 저장하면 서버에 반영**: 파일 더블클릭(또는 우클릭 "편집")으로 편집기에서 열고, 저장할 때마다 내용이 바뀌었으면 같은 경로로 자동 업로드 (패널 아래에 "저장됨 → 서버에 올림" 표시)
  - 편집기는 Settings → SFTP 에서 지정 (비우면 Windows 기본 프로그램, "VS Code" 버튼으로 바로 지정). VS Code 로 열면 그 창의 Claude Code 로 고친 내용도 저장하는 순간 서버에 올라간다
  - 편집하는 동안 서버의 파일이 바뀌었으면(수정 시각·크기) 덮어쓸지 묻는다 (아니요 = 이번 저장은 올리지 않음)
  - 같은 파일을 다시 열면 받은 사본을 그대로 연다 (편집 중인 내용 유지). 탭을 닫으면 감시를 멈춘다
- **우클릭**: 편집 · 읽기 전용 사본 열기 · 다운로드 · 이름 변경(F2) · 삭제(Delete, 폴더는 하위까지) · 새 폴더 · 경로 복사
- 전송 진행률 표시, 취소 가능

## 로컬 터미널

타이틀바 **+** 버튼(또는 `Ctrl+Shift+T`)으로 로컬 PowerShell 탭. 터미널 포커스 중에도 `Ctrl+Tab` 탭 전환 동작.

## 라이선스

[MIT License](LICENSE) — 개인·상업 용도 관계없이 자유롭게 사용·수정·배포할 수 있다 (저작권 고지와 라이선스 문구 유지).

포함·사용한 오픈소스 구성요소와 테마 출처는 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) 참고.

## 알려진 제한

- RDP 화면에 포커스가 있으면 `Ctrl+Tab` 은 원격으로 전달된다 (탭 클릭으로 전환)
- 포트 포워딩, 점프 호스트, RDP 게이트웨이는 아직 없음
- 데이터 파일이 암호화된 뒤에는 이전 버전(0.1.2 이하)이 데이터를 읽지 못한다 — 새 버전으로 실행한 뒤 옛 exe 를 쓰지 않는다
- WinRM(Windows 서버 명령 실행)은 아직 실서버에서 충분히 검증하지 못했다
- 데이터 폴더 이름이 바뀌어 Claude 패널 작업 폴더 신뢰 확인이 한 번 더 뜬다
