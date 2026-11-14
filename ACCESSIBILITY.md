# Accessibility

**English** | [한국어](#한국어)

Nubis aims to be usable by everyone who works on servers, including people who rely on the keyboard, need larger text or higher contrast. This page describes what works today, the known gaps, and how to report a barrier.

## What works today

- **Keyboard-first use:** search hosts from anywhere with `Ctrl+K`, move through results with `↑` `↓` `Tab`, connect with `Enter`; switch tabs with `Ctrl+Tab` / `Ctrl+Shift+Tab` or `Ctrl+1~8` (`Ctrl+9` for the last tab). All shortcuts are listed in the [User Guide](docs/guide.md#13-keyboard-shortcuts).
- **Text size:** the terminal font size can be changed per tab (`Ctrl+wheel`, `Ctrl+=` / `Ctrl+-`, `Ctrl+0` to reset), and the default font and size are set in Settings.
- **Contrast:** light terminal themes (GitHub Light, Solarized Light) are available, and the terminal can automatically raise text that has too little contrast against the background (minimum contrast ratio 4.5:1).
- **Language:** the interface is available in Korean and English, and the Claude panel replies in the same language.

## Known limitations

- The app window itself only has a dark theme; there is no light or high-contrast app theme yet, and Windows high-contrast themes are not fully applied to custom-styled controls.
- Screen-reader support has not been tested. The terminal (xterm.js in WebView2) does not currently enable its screen-reader mode, and some custom controls (host cards, icon buttons) may lack descriptive names.
- Remote desktop (RDP) content is a picture of the remote screen; its accessibility depends on the remote Windows system.

## Reporting a barrier

If something gets in your way, please open an issue with the **Bug report** template and choose the area that applies. Describe what you were trying to do, the assistive technology you use (screen reader, magnifier, keyboard only, voice control…), and what happened. Accessibility issues are treated as bugs.

---

## 한국어

Nubis 는 키보드로 작업하는 분, 큰 글자나 높은 대비가 필요한 분을 포함해 누구나 쓸 수 있는 것을 목표로 합니다.

**지금 되는 것**
- **키보드로 쓰기:** 어디서든 `Ctrl+K` 로 호스트를 검색하고, `↑` `↓` `Tab` 으로 고른 뒤 `Enter` 로 접속합니다. 탭 전환은 `Ctrl+Tab` / `Ctrl+Shift+Tab` 또는 `Ctrl+1~8` (`Ctrl+9` 는 마지막 탭). 전체 단축키는 [사용자 가이드](docs/guide.ko.md#13-단축키)에 있습니다.
- **글자 크기:** 터미널 글꼴 크기를 탭마다 바꿀 수 있고(`Ctrl+휠`, `Ctrl+=` / `Ctrl+-`, `Ctrl+0`), 기본 글꼴과 크기는 Settings 에서 정합니다.
- **대비:** 밝은 터미널 테마(GitHub Light, Solarized Light)가 있고, 배경과 대비가 낮은 글자를 자동으로 보정할 수 있습니다(최소 대비 4.5:1).
- **언어:** 한국어와 영어를 지원하고, Claude 패널도 같은 언어로 답합니다.

**알려진 한계**
- 앱 창은 어두운 테마만 있습니다. 밝은 테마나 고대비 앱 테마는 아직 없고, Windows 고대비 테마가 직접 꾸민 컨트롤에는 다 적용되지 않습니다.
- 스크린 리더는 확인하지 않았습니다. 터미널(WebView2 위 xterm.js)의 스크린 리더 모드는 켜져 있지 않고, 일부 컨트롤(호스트 카드, 아이콘 버튼)에는 설명 이름이 없을 수 있습니다.
- 원격 데스크톱(RDP) 화면은 원격 화면의 그림이라, 접근성은 원격 Windows 에 달려 있습니다.

**불편 신고:** 막히는 부분이 있으면 **버그 신고** 템플릿으로 이슈를 올려 주세요. 하려던 일, 쓰는 보조 기술(스크린 리더, 돋보기, 키보드만, 음성 제어 등), 실제로 일어난 일을 적어 주세요. 접근성 문제는 버그로 다룹니다.
