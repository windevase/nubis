# Security Policy

**English** | [한국어](#한국어)

## Supported versions

Only the [latest release](https://github.com/windevase/nubis/releases/latest) receives security fixes.

## Reporting a vulnerability

Please **do not open a public issue** for security problems.

Report privately through GitHub instead: open the [Security tab](https://github.com/windevase/nubis/security) → **Report a vulnerability**. Only the maintainer can see the report.

Please include:
- the Nubis version (Settings shows it at the bottom of the sidebar)
- steps to reproduce, and what an attacker could gain
- any logs or screenshots, with passwords, keys, tokens and server addresses removed

You will get a reply as soon as possible. Once a fix is released, the report can be published as a security advisory, with credit if you want it.

## Scope

In scope: the Nubis app and its release builds. Examples:
- reading saved accounts without the user's Windows account
- the Claude panel getting around the approval prompt or the audit log
- host-key checks being bypassed

Out of scope: vulnerabilities in third-party components (report them to their projects, such as SSH.NET, xterm.js, WebView2 or the Claude Code CLI), and attacks that need code already running under the same Windows account. The [User Guide](docs/guide.md#123-security-model) explains why the second case is out of scope.

---

## 한국어

**지원 버전:** [최신 릴리스](https://github.com/windevase/nubis/releases/latest)만 보안 수정을 받습니다.

**신고 방법:** 보안 문제는 **공개 이슈로 올리지 말고**, [Security 탭](https://github.com/windevase/nubis/security) → **Report a vulnerability** 로 비공개 신고해 주세요. 관리자만 볼 수 있습니다.

신고에 다음 내용을 넣어 주세요. 로그나 화면에서는 비밀번호·키·토큰·서버 주소를 지워 주세요.
- Nubis 버전 (사이드바 맨 아래)
- 재현 방법과 공격자가 얻을 수 있는 것
- 로그나 화면 캡처

**범위:** Nubis 앱과 배포 파일이 대상입니다. 외부 구성요소(SSH.NET, xterm.js, WebView2, Claude Code CLI)의 취약점은 각 프로젝트에 신고해 주세요. 같은 Windows 계정에서 이미 실행 중인 코드가 필요한 공격도 대상이 아닙니다. 이유는 [사용자 가이드의 보안 모델](docs/guide.ko.md#123-보안-모델)에 있습니다.
