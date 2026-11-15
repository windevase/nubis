<img src="nubis.png" width="96" align="right"/>

[English](README.md) | **한국어** · 📖 [사용자 가이드](docs/guide.ko.md) · ⬇️ [다운로드](https://github.com/windevase/nubis/releases/latest) · 🛡️ [VirusTotal 검사 결과 (릴리스 설명)](https://github.com/windevase/nubis/releases/latest)

# Nubis

### Handy Remote Client for All Engineers

SSH · RDP · SFTP 를 한 창에서, 클라우드 서버는 이미 목록에, Claude 는 터미널 바로 옆에 — 매일 하는 서버 작업을 편하고 안전하게 한곳에 모은 Windows 원격 접속 클라이언트.

> 이름: 라틴어 *nubis* = "구름의" + 우리말 "누비다" (여러 클라우드의 서버를 누비며 접속한다)

## 왜 만들었나

Nubis 는 제가 **편하고 안전하게** 쓰려고, 필요한 기능을 하나씩 직접 만든 앱입니다.

매일 SSH 클라이언트, 원격 데스크톱, SFTP 도구를 오가고, 서버 IP 를 찾으려고 클라우드 콘솔 네 곳을 열고, 에러 메시지를 따로 띄운 AI 채팅창에 복사해 붙이곤 했습니다. 그러면서 서버 계정이 평문 설정 파일에 남아 있거나, AI 가 운영 서버에서 무엇을 실행하는지 제대로 확인하지 못하는 것도 늘 마음에 걸렸습니다.

그래서 편한 것은 한곳에 모으고, 위험한 것은 반드시 제 확인을 거치게 만들었습니다. 계정과 서버 목록은 암호화해 저장하고, Claude 가 서버를 바꾸는 작업은 전부 승인 창과 감사 로그를 거치며, 서버의 호스트키가 바뀌면 항상 경고합니다. 지금은 제가 매일 쓰는 도구입니다.

Linux 와 Windows 서버가 섞여 있고 여러 클라우드에 흩어져 있는 환경이라면, 같은 번거로움과 걱정을 덜어 줄 수 있을 겁니다.

## 이런 걸 합니다

**🖥️ 모든 접속을 한 창에서**
SSH 터미널, RDP 데스크톱, 로컬 PowerShell 을 탭으로 나란히 엽니다. 검색창에 `user@host` 만 입력하면 바로 접속되고, `Ctrl+K` 를 누르고 이름·IP·열린 탭·동작 이름 일부를 입력해 방향키로 고른 뒤 Enter 로 실행합니다. 즐겨찾기와 최근 접속 호스트가 맨 위에 있습니다. 탭은 순서 바꾸기·이름 바꾸기·다시 연결·한꺼번에 닫기가 되고, 방금 닫은 탭은 `Ctrl+Shift+R` 로 다시 엽니다. 복사·붙여넣기는 Windows Terminal 방식(`Ctrl+C`/`Ctrl+V`)이나 PuTTY · mRemoteNG 방식(선택하면 복사, 우클릭으로 붙여넣기) 중 익숙한 쪽으로 고를 수 있습니다. ssh-agent 와 `~/.ssh` 기본 키, `known_hosts`, OTP·Push 2단계 인증(답 기억하기), 점프 서버(ProxyJump)까지 OpenSSH 처럼 동작하고, RDP 는 RD 게이트웨이를 포함해 mstsc 기본 설정을 따릅니다.

**☁️ 클라우드 서버가 이미 목록에**
이 PC 에 저장된 인증 프로필(AWS 프로필 · Tencent 자격 증명 · GCP ADC · Azure CLI · OCI `~/.oci/config` 프로필 — `oci session authenticate` 브라우저 로그인 또는 API 키)로 AWS · Tencent Cloud · Google Cloud · Azure · Oracle Cloud(OCI) 의 서버를 프로필별로 가져옵니다. 필요할 때·앱을 켤 때·몇 분마다 동기화되어 이름과 IP 는 갱신되고, 직접 바꾼 설정은 그대로 남습니다. 그룹을 우클릭하면 그 그룹 서버 전체를 공인/사설 IP 로 바꾸거나 Vault 계정 하나를 한 번에 지정하고, 카드를 `Ctrl`/`Shift`+클릭으로 골라 계정·사용자명·포트·그룹·접속 IP·즐겨찾기를 한꺼번에 편집할 수도 있습니다.

**🏢 온프레미스 서버도 한 번에**
`IMPORT` 로 이미 가진 서버 목록을 그대로 가져옵니다 — 엑셀·CSV 붙여넣기(한글·영문 머리글 자동 인식), `~/.ssh/config`(`Include`·`ProxyJump` 포함), mRemoteNG `confCons.xml`(폴더가 그룹으로), Ansible 인벤토리(INI·YAML), 그리고 Microsoft Entra Domain Services 를 포함한 Active Directory(LDAPS 우선, 현재 Windows 로그인 또는 입력한 계정). 미리보기에서 등록할 서버를 고르고 SSH · RDP 용 Vault 계정을 정하면 되고, 이미 있는 호스트는 알아서 걸러져 두 번 등록되지 않습니다. 비밀번호는 어떤 출처에서도 가져오지 않습니다.

**✦ 터미널 바로 옆의 Claude**
내장 Claude 패널은 지금 보고 있는 탭을 압니다. "이 에러 뭐야?", "Production 그룹 디스크 확인해줘", "이 서버 추가하고 운영 계정 연결해줘" 처럼 말하면 됩니다. 서버를 바꾸는 작업은 모두 승인을 거치고 감사 로그에 남으며, Claude 는 저장된 비밀번호와 키를 읽을 수 없습니다. 호스트·그룹·탭·SFTP 파일·클라우드 프로필·Vault 기본 계정·설정처럼 손으로 하던 일도 Claude 에게 맡길 수 있습니다 — 서버·계정·데이터를 바꾸는 일은 여전히 승인을 기다리고(즐겨찾기 같은 화면 정리는 기록만 남김), 비밀번호는 직접 입력합니다. OTP·Push 인증을 요구하는 서버는 이미 로그인한 탭의 연결로 명령을 실행해 다시 인증하지 않습니다. "우리 서버 다 가져와줘" 라고 하면 Claude 가 이 PC 의 환경(클라우드 CLI 프로필, `~/.ssh/config`, mRemoteNG, 도메인)을 살펴 클라우드 프로필을 추가하고, 로그인이 필요하면 로그인 창을 띄우고, 온프레미스 목록을 가져옵니다 — 먼저 미리보기를 보여 주고, 등록 전에 한 번 승인을 받습니다.

**📁 따라다니는 SFTP**
SSH 를 열면 파일 패널이 같은 연결 위에 함께 열리고(OTP 서버도 다시 인증하지 않음), 터미널의 현재 폴더와 사용자까지 따라갑니다 — 터미널에서 `sudo su -` 하면 패널도 root 권한으로 바뀝니다. 탐색기와 끌어다 놓기로 주고받고, VS Code 로 연 파일은 저장할 때마다 서버에 바로 올라갑니다.

**🔑 계정은 Vault 하나로**
여러 서버가 같이 쓰는 사용자명 · 비밀번호 · SSH 키를 한 번 저장합니다. SSH · RDP 기본 계정을 정해 두면 계정을 따로 정하지 않은 모든 호스트가 쓰고, 그룹 우클릭으로 그룹 전체에, 또는 호스트마다 고를 수 있습니다. 비밀번호를 한곳에서 바꾸면 모든 호스트에 적용됩니다.

**🔒 기본이 안전하게**
모든 데이터는 이 PC 에서 Windows DPAPI 로 암호화되어, 다른 PC 나 다른 Windows 계정에서는 열리지 않습니다. 사용 통계 수집은 없습니다. 선택 사항인 시작 화면 로그인(GitHub · Google · Microsoft)을 켜면 계정마다 데이터를 나눕니다.

**🪶 가볍고 Windows 답게**
Mica 를 입힌 Windows 11 Fluent 디자인(Windows 10 에서는 단색 어두운 창)이고, 작업 결과는 토스트로 잠깐 알려 줍니다. 호스트 목록·명령 팔레트·대화상자는 키보드로 다룰 수 있고, 포커스가 눈에 보이며 스크린 리더가 읽을 이름도 붙어 있습니다. 가볍게 유지하는 데도 신경 썼습니다 — 가만히 있을 때 움직이는 애니메이션이 없고, 탭을 닫으면 그 탭이 쓰던 자원을 모두 돌려놓습니다.

**🌐 한국어 · 영어**
처음 실행할 때 언어를 고르고, Settings 에서 언제든 바꿉니다. Claude 도 같은 언어로 답합니다.

## 시작하기

1. [다운로드](https://github.com/windevase/nubis/releases/latest)에서 `Nubis-<버전>-portable-win-x64.zip` 을 받아 풀고 `Nubis` 폴더의 `Nubis.exe` 를 실행합니다 — 앱 DLL 이 exe 옆에 함께 있으니 폴더째로 두세요. 설치 프로그램도 .NET 설치도 필요 없습니다: Windows 10 / 11 64비트, 그리고 Windows 에 보통 이미 들어 있는 Microsoft Edge WebView2 런타임이면 됩니다.
2. 언어를 고르고 **시작하기** 카드를 따라갑니다: 서버 추가, 클라우드 가져오기, 또는 Claude 에게 안내를 받으세요.
3. Claude 패널을 쓰려면 [Claude Code CLI](https://docs.claude.com/claude-code) 를 설치합니다: `npm install -g @anthropic-ai/claude-code`

모든 기능과 설정, 각 설정이 어떻게 동작하는지는 **[사용자 가이드](docs/guide.ko.md)** 에 있습니다.

## 만든 재료

WPF (.NET 10) · [SSH.NET](https://github.com/sshnet/SSH.NET) (조금 고친 빌드, `third_party/SSH.NET` 참고) · WebView2 위의 [xterm.js](https://xtermjs.org/) · Windows 내장 원격 데스크톱 엔진(`mstscax`) · AWS SDK for .NET·Azure.Identity·Google.Apis.Auth(클라우드 인증) · YamlDotNet·System.DirectoryServices.Protocols(Ansible·Active Directory 가져오기) · 내장 MCP 서버로 연결한 [Claude Code](https://docs.claude.com/claude-code).

```powershell
.\build.ps1            # Debug 빌드 후 실행
.\build.ps1 test       # 자동 테스트
.\build.ps1 publish    # publish\ (Nubis.exe + DLL, .NET 포함) + dist\ 포터블 zip
```

## 코드 서명 정책

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

> SignPath Foundation 에 무료 코드 서명을 신청했다. 승인 전까지 배포 파일은 서명되지 않으므로, 릴리스 페이지의 `SHA256SUMS.txt` 로 SHA256 을 확인한다.

- 배포 파일은 이 저장소의 소스로 [GitHub Actions](.github/workflows/build.yml) 만 만든다 (`v*` 태그를 푸시하면 테스트·빌드 후 릴리스). 로컬에서 만든 파일은 서명하지 않는다.
- 커밋·리뷰: [windevase](https://github.com/windevase)
- 서명 승인: [windevase](https://github.com/windevase)

## 개인정보

이 프로그램은 사용자(또는 설치·운영하는 사람)가 요청하지 않는 한 어떤 정보도 다른 네트워크 시스템으로 보내지 않는다.

Nubis 가 연결하는 곳은 사용자가 설정한 곳뿐이다: 접속하는 서버, 추가한 클라우드 프로필의 API (이 PC 에 저장된 인증 프로필 사용), 시작 화면 로그인에 설정한 OAuth 제공자, 그리고 Claude 패널을 열면 이 PC 에 설치된 Claude Code CLI (사용자 본인 계정으로 Anthropic 과 통신). 사용 통계·분석·업데이트 확인은 없다. 모든 데이터는 `%APPDATA%\Nubis` 에 Windows DPAPI 로 암호화되어 남는다.

## 라이선스

[MIT License](LICENSE) — 개인·상업 용도 관계없이 자유롭게 사용·수정·배포할 수 있다 (저작권 고지와 라이선스 문구 유지).

포함·사용한 오픈소스 구성요소와 테마 출처는 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md), 접근성 현황과 알려진 한계는 [ACCESSIBILITY.md](ACCESSIBILITY.md) 참고.
