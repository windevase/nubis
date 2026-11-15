# Accessibility

**English** | [한국어](#한국어)

Nubis aims to be usable by everyone who works on servers, including people who rely on the keyboard, need larger text or higher contrast. This page describes what works today, the known gaps, and how to report a barrier.

## What works today

- **Keyboard-first use:** `Ctrl+K` opens a command palette (hosts, open tabs and actions) from anywhere; `Ctrl+F` jumps to host search, where `↑` `↓` `Tab` move through the host list, `Enter` connects and `F2` edits. Switch tabs with `Ctrl+Tab` / `Ctrl+Shift+Tab` or `Ctrl+1~8` (`Ctrl+9` for the last tab), and reopen a closed tab with `Ctrl+Shift+R`. Keyboard focus is shown with a visible focus ring. All shortcuts are listed in the [User Guide](docs/guide.md#14-keyboard-shortcuts).
- **Text size:** the terminal font size can be changed per tab (`Ctrl+wheel`, `Ctrl+=` / `Ctrl+-`, `Ctrl+0` to reset), and the default font and size are set in Settings.
- **Contrast:** light terminal themes (GitHub Light, Solarized Light) are available, and the terminal can automatically raise text that has too little contrast against the background (minimum contrast ratio 4.5:1).
- **Screen readers:** host cards, tabs and icon-only buttons have accessible names (host cards also expose their protocol and address as help text), and result notifications (toasts) are announced as a polite live region.
- **Language:** the interface is available in Korean and English, and the Claude panel replies in the same language.

## Known limitations

- The app window itself only has a dark theme; there is no light or high-contrast app theme yet, and Windows high-contrast themes are not fully applied to custom-styled controls.
- Screen-reader support has not been tested end to end with a real screen reader. The terminal (xterm.js in WebView2) does not currently enable its screen-reader mode, so terminal output is not read aloud, and some input fields may still lack a descriptive name.
- Remote desktop (RDP) content is a picture of the remote screen; its accessibility depends on the remote Windows system.

## Reporting a barrier

If something gets in your way, please open an issue with the **Bug report** template and choose the area that applies. Describe what you were trying to do, the assistive technology you use (screen reader, magnifier, keyboard only, voice control…), and what happened. Accessibility issues are treated as bugs.

---

## 한국어

Nubis 는 키보드로 작업하는 분, 큰 글자나 높은 대비가 필요한 분을 포함해 누구나 쓸 수 있는 것을 목표로 합니다.

**지금 되는 것**
- **키보드로 쓰기:** 어디서든 `Ctrl+K` 로 명령 팔레트(호스트·열린 탭·동작)를 엽니다. `Ctrl+F` 로 호스트 검색으로 가면 `↑` `↓` `Tab` 으로 호스트 목록을 오가고 `Enter` 로 접속, `F2` 로 편집합니다. 탭 전환은 `Ctrl+Tab` / `Ctrl+Shift+Tab` 또는 `Ctrl+1~8` (`Ctrl+9` 는 마지막 탭), 닫은 탭은 `Ctrl+Shift+R` 로 다시 엽니다. 키보드 포커스는 테두리로 표시됩니다. 전체 단축키는 [사용자 가이드](docs/guide.ko.md#14-단축키)에 있습니다.
- **글자 크기:** 터미널 글꼴 크기를 탭마다 바꿀 수 있고(`Ctrl+휠`, `Ctrl+=` / `Ctrl+-`, `Ctrl+0`), 기본 글꼴과 크기는 Settings 에서 정합니다.
- **대비:** 밝은 터미널 테마(GitHub Light, Solarized Light)가 있고, 배경과 대비가 낮은 글자를 자동으로 보정할 수 있습니다(최소 대비 4.5:1).
- **스크린 리더:** 호스트 카드·탭·아이콘 버튼에 접근성 이름이 있고(호스트 카드는 프로토콜과 주소를 도움말로 함께 제공), 작업 결과 알림(토스트)은 라이브 영역으로 읽힙니다.
- **언어:** 한국어와 영어를 지원하고, Claude 패널도 같은 언어로 답합니다.

**알려진 한계**
- 앱 창은 어두운 테마만 있습니다. 밝은 테마나 고대비 앱 테마는 아직 없고, Windows 고대비 테마가 직접 꾸민 컨트롤에는 다 적용되지 않습니다.
- 실제 스크린 리더로 처음부터 끝까지 확인하지는 않았습니다. 터미널(WebView2 위 xterm.js)의 스크린 리더 모드는 켜져 있지 않아 터미널 출력은 읽어 주지 않고, 일부 입력란에는 설명 이름이 없을 수 있습니다.
- 원격 데스크톱(RDP) 화면은 원격 화면의 그림이라, 접근성은 원격 Windows 에 달려 있습니다.

**불편 신고:** 막히는 부분이 있으면 **버그 신고** 템플릿으로 이슈를 올려 주세요. 하려던 일, 쓰는 보조 기술(스크린 리더, 돋보기, 키보드만, 음성 제어 등), 실제로 일어난 일을 적어 주세요. 접근성 문제는 버그로 다룹니다.
