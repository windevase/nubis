<img src="nubis.png" width="96" align="right"/>

**English** | [한국어](README.ko.md) · 📖 [User Guide](docs/guide.md) · ⬇️ [Download](https://github.com/windevase/nubis/releases/latest) · 🛡️ [VirusTotal scan (in release notes)](https://github.com/windevase/nubis/releases/latest)

# Nubis

### Handy Remote Client for All Engineers

SSH, RDP and SFTP in one window, your cloud servers already in the list, and Claude sitting right next to the terminal — a Windows remote client that keeps everyday server work comfortable, safe and in one place.

> Name: Latin *nubis* = "of the cloud" + Korean *nubida* ("to roam through") — roaming the servers of many clouds.

## Why Nubis

I built Nubis for myself, adding the features I needed one at a time, so that I could work with my servers **comfortably and safely**.

Day to day I was switching between an SSH client, the Remote Desktop app, an SFTP tool, four cloud consoles to look up server IPs, and a separate AI chat I kept pasting error messages into. And it always bothered me that server accounts sat in plain-text config files, and that I could not really see what an AI was about to run on a production server.

So I brought the convenient parts into one place and made sure the risky parts always go through me. Accounts and server lists are stored encrypted, every change Claude makes to a server goes through an approval prompt and an audit log, and a changed host key always triggers a warning. Today it is the tool I use every day.

If your work looks like that too — a mix of Linux and Windows servers, spread across a few clouds — Nubis may save you the same trouble and the same worries.

## What it does

**🖥️ One window for every connection**
SSH terminals, RDP desktops and local PowerShell open side by side as tabs. Type `user@host` in the search box and you are connected — or press `Ctrl+K`, type part of a name or IP, pick with the arrow keys and hit Enter. Favorites and recent hosts sit at the top, and tabs can be reordered, renamed, reconnected and closed in bulk. It behaves like OpenSSH where it matters — ssh-agent and `~/.ssh` default keys, `known_hosts`, OTP / push MFA prompts (with remembered answers), jump hosts (ProxyJump) — and RDP follows your mstsc defaults, RD Gateway included.

**☁️ Your cloud servers, already in the list**
Import servers from AWS, Tencent Cloud, Google Cloud and Azure, profile by profile, using the credential profiles already saved on your PC (AWS profiles · Tencent credentials · GCP ADC · Azure CLI). They stay in sync (on demand, at startup or every few minutes) — names and IPs update, your own changes stay. Right-click a group to switch all of its servers to public or private IPs, or to assign one Vault account to every server in it — or `Ctrl`/`Shift`+click any cards and bulk-edit their account, username, port, group, IP choice or favorite at once.

**✦ Claude, right next to the terminal**
The built-in Claude panel sees the tab you are looking at. Ask "what is this error?", "check disk usage on the Production group" or "add this server and link the ops account". Anything that changes a server waits for your approval and is written to an audit log, and Claude cannot read your saved passwords or keys.

**📁 SFTP that follows you**
The file panel opens with every SSH session on the same connection (no second sign-in, even with OTP) and follows the terminal's current folder — and its user too: `sudo su -` in the terminal and the panel browses as root. Drag files in and out of Explorer. Open a file in VS Code, and every save goes straight back to the server.

**🔑 Accounts in one Vault**
Save shared usernames, passwords and SSH keys once. Set a default account for SSH and for RDP and every host without its own account uses it, assign one to a whole group with a right-click, or pick one per host. Change a password in one place and every host uses it.

**🔒 Safe by default**
All data is encrypted with Windows DPAPI on your PC, so it will not open on another PC or under another Windows account. No telemetry. An optional sign-in screen (GitHub · Google · Microsoft) keeps each account's data separate.

**🌐 Korean and English**
Choose the language on first launch and switch it any time in Settings. Claude answers in the same language.

## Get started

1. [Download](https://github.com/windevase/nubis/releases/latest) the portable zip, extract it and run `Nubis.exe` — no installer and no .NET install needed (Windows 10 / 11, 64-bit).
2. Pick a language, then follow the **Get started** cards: add a server, import a cloud, or ask Claude to walk you through it.
3. For the Claude panel, install the [Claude Code CLI](https://docs.claude.com/claude-code): `npm install -g @anthropic-ai/claude-code`.

Everything else — every feature, every setting and how it behaves — is in the **[User Guide](docs/guide.md)**.

## Built with

WPF on .NET 10 · [SSH.NET](https://github.com/sshnet/SSH.NET) · [xterm.js](https://xtermjs.org/) on WebView2 · the Windows built-in Remote Desktop engine (`mstscax`) · [Claude Code](https://docs.claude.com/claude-code) through a built-in MCP server.

```powershell
.\build.ps1            # Debug build, then run
.\build.ps1 test       # automated tests
.\build.ps1 publish    # publish\ (Nubis.exe + DLLs, self-contained) + dist\ portable zip
```

## Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

> Signing through SignPath Foundation has been applied for. Until it is approved, release builds are unsigned — check the SHA256 in `SHA256SUMS.txt` on the release page.

- Release binaries are built only by [GitHub Actions](.github/workflows/build.yml) from this repository's source (a `v*` tag builds, tests and publishes the release). Locally built files are never signed.
- Committers and reviewers: [windevase](https://github.com/windevase)
- Approvers: [windevase](https://github.com/windevase)

## Privacy

This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it.

Nubis connects only to the places you configure: the servers you connect to, the cloud APIs of the profiles you add (using the credential profiles saved on your PC), the OAuth provider you set for the sign-in screen, and — if you open the Claude panel — the Claude Code CLI installed on your PC, which talks to Anthropic under your own account. There is no telemetry, analytics or update check. All data stays in `%APPDATA%\Nubis`, encrypted with Windows DPAPI.

## License

[MIT License](LICENSE) — free to use, modify and distribute for personal or commercial purposes (keep the copyright notice and license text).

Open-source components and theme sources are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
