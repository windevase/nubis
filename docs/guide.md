<img src="../nubis.png" width="72" align="right"/>

**English** | [한국어](guide.ko.md)

# Nubis User Guide

Nubis is a Windows remote-access client. It gathers your cloud servers by profile, connects to them over SSH, RDP and SFTP, and lets the built-in Claude panel check servers and change settings when you ask in plain words. This guide covers everything from installation to how each feature and setting behaves.

## Contents

1. [Getting started](#1-getting-started)
2. [Window layout](#2-window-layout)
3. [Managing hosts](#3-managing-hosts)
4. [SSH terminal](#4-ssh-terminal)
5. [SFTP file transfer](#5-sftp-file-transfer)
6. [RDP remote desktop](#6-rdp-remote-desktop)
7. [Vault — shared accounts](#7-vault--shared-accounts)
8. [Importing cloud servers](#8-importing-cloud-servers)
9. [Claude panel](#9-claude-panel)
10. [Sign-in screen and per-account profiles](#10-sign-in-screen-and-per-account-profiles)
11. [Settings reference](#11-settings-reference)
12. [Data files and security](#12-data-files-and-security)
13. [Keyboard shortcuts](#13-keyboard-shortcuts)
14. [Troubleshooting](#14-troubleshooting)

---

## 1. Getting started

### 1.1 Requirements

| Item | Details |
|---|---|
| OS | Windows 10 / 11 (64-bit) |
| Runtime | None needed — the release exe is a single file that bundles .NET |
| WebView2 | Included with Windows 10/11 (used for the terminal) |
| Claude panel (optional) | [Claude Code CLI](https://docs.claude.com/claude-code) — `npm install -g @anthropic-ai/claude-code` |
| Cloud import (optional) | A credential profile for each cloud you use — the ones created by the `aws`, `tccli`, `gcloud` and `az` CLIs |

### 1.2 Installation

1. Extract the release zip (`Nubis-<version>-portable-win-x64.zip`) to any folder.
2. Run `Nubis.exe` from that folder. Keep the `wwwroot\` folder next to the exe; the terminal needs it.
3. To build from source, run `.\build.ps1 publish` in the repository. This creates `publish\Nubis.exe`.

Nubis is a portable app with no installer. Your data is stored in `%APPDATA%\Nubis`, not in the exe folder, so to update you only replace the exe.

> **Note** Starting with this version, data files are encrypted. After you run the new version once, do not go back to a 0.1.x version. Older versions cannot read the encrypted files and start with an empty list.

### 1.3 First run

1. **Choose a language** — Korean (default) or English. You are asked only once. To change it later, see [Settings → Language](#111-language).
2. **Sign-in screen** — appears only if you have set it up ([chapter 10](#10-sign-in-screen-and-per-account-profiles)).
3. **Getting-started guide** — opens once, automatically, after the main window appears. Each card has two buttons:
   - The left button (for example **New host** or **Open Cloud**) takes you straight to that feature's screen.
   - **Ask Claude** opens the Claude panel on the right and starts a conversation with a guide question. Claude checks your current setup and walks you through it step by step, opening the screens you need.

   You can open the guide again at any time with **Show the getting-started guide** in Settings.

Not sure where to start? If you use a cloud provider, begin with [importing cloud servers](#8-importing-cloud-servers). Otherwise, [add a host yourself](#31-adding-a-host).

---

## 2. Window layout

| Area | Description |
|---|---|
| Title-bar tabs | The **Hosts** home tab and your open session tabs. On the right: **+** (local PowerShell), **✦ Claude** (Claude panel) and the window buttons |
| Sidebar | **Hosts** · **Cloud** · **Vault** · **Known Hosts** · **Settings** |
| Hosts page | Search / quick-connect box, **NEW HOST**, and host cards grouped by name |
| Editor panel | Opens on the right when you add or edit a host |
| Claude panel | Vertical panel on the right. Drag its edge to resize it |

---

## 3. Managing hosts

### 3.1 Adding a host

Click **NEW HOST** to open the editor panel.

| Field | Description |
|---|---|
| Protocol | SSH or RDP. Changing it resets the port to the default for that protocol |
| Address · Port | IP address or host name. The default port comes from [Settings → Default ports](#115-default-ports) |
| Public IP (optional) | An Elastic IP or similar. Turn on "Connect via public IP" to use this address |
| Name | The name shown on the card (the address if left blank) |
| Group | The name cards are grouped under (for example, Production). Use the ▾ button next to it to pick an existing group so typos don't split groups (names that differ only in case are shown as one group) |
| Sign-in account | Pick a [Vault](#7-vault--shared-accounts) account to sign in with its username, password and key. If you don't pick one, the [default account](#73-default-accounts-and-group-assignment) is used (the editor shows which one) |
| Username · Password | Entered directly. If you picked a Vault account, **leave these blank to use the Vault values; anything you type here wins** |
| Private key file (SSH) | OpenSSH, PuTTY (.ppk) or PEM key file. The key passphrase is taken from the password field |

Click **Save** (`Ctrl+S`) or **Save and connect**. Passwords are encrypted with Windows DPAPI before they are stored. If the address or port is invalid, the reason appears at the bottom of the editor. If you close the editor with unsaved changes (`Esc`, clicking outside, or X), Nubis asks before discarding them. **Duplicate** on the card menu adds the copy only when you click Save.

### 3.2 Connecting and quick connect

- **Double-click a card** to connect. Right-click for: Connect · Connect via public/private IP · Favorite · Edit · Duplicate · Delete.
- **Favorites and Recent** — favorite hosts and hosts you connected to in the last 30 days also appear in groups at the top of the list (Recent can be turned off in Settings).
- **Search** — `Ctrl+K` (`Ctrl+Shift+K` in a terminal) jumps to the search box from anywhere. It matches name, private/public IP, user, group, instance ID, region, OS, role and profile name; several words separated by spaces must all match. `vault:name` shows hosts that sign in with that Vault account.
- **Keyboard selection** — while searching, the first result is outlined. Move with `↑` `↓` (one row) or `Tab` (one card) and press `Enter` to connect.
- **Quick connect** — type `user@host[:port]` (SSH) or `rdp host[:port]` in the search box and press Enter. The host is not saved to the list. If the text doesn't look like an address, Enter connects to the outlined host instead.
- **Command line** — `Nubis.exe --connect "host name"` connects at startup (you can repeat the option). `--claude` starts with the Claude panel open.
- If a host has no account, Nubis asks when you connect. If you tick "Save", only an account that signs in successfully is saved.

### 3.3 Private and public IPs

A host can have both a private IP (the default address) and a public IP. Nubis connects to the private IP by default. To switch, use "Connect via public IP" in the host editor, right-click the card (this time only), or right-click the group (the whole group, see [3.4](#34-group-right-click--bulk-actions)). The SSH terminal and Claude's multi-server commands retry once on the public IP **when the private IP can't be reached**; an authentication failure is never retried.

### 3.4 Group right-click — bulk actions

Right-click a group name in the host list to apply a change to the whole group, so hundreds of synced cloud servers don't need editing one by one.

| Menu | Behavior |
|---|---|
| Connect all | Connects to every running host in the group (asks first for 9 or more) |
| Select all hosts | Selects every host in the group for [bulk editing](#35-selecting-several-hosts-and-bulk-editing) |
| Set Vault account for SSH / RDP hosts | Assigns the chosen account to all SSH (or RDP) hosts in the group. For hosts with their own credentials, you choose to clear or keep them. **For a cloud group it becomes the profile's default account**, so servers added by later syncs use it too |
| Clear Vault account | Removes the group assignment so the hosts follow the defaults |
| Connect via public / private IP for all | All hosts that have a public IP. For a cloud group the profile default changes too, so new servers follow it |
| Rename group | Groups you created yourself |
| Sync · CLI login (this profile) · Profile settings | Cloud groups. Login runs for that profile (useful with several SSO portals) |
| Expand / collapse all groups | Collapsed state is remembered across restarts |

### 3.5 Selecting several hosts and bulk editing

Pick exactly the hosts you want, regardless of group, and change them in one go.

- **Selecting** — `Ctrl+click` a card to add or remove it, `Shift+click` for a range, `Ctrl+A` (after clicking an empty spot) for every visible host, or right-click a group → Select all hosts. The selection survives changing the search text, so you can collect hosts over several searches. A plain click or `Esc` clears it.
- **Selection bar** — while hosts are selected, a `N selected` bar appears above the list: Bulk edit · Connect · Delete · More (Vault account, public/private IP, favorites). Right-clicking a selected card opens the same menu. `Delete` deletes (after confirming).
- **Bulk edit window** — only checked items change; everything else stays as it is on each host. Values shared by all selected hosts are filled in for you.

| Item | Behavior |
|---|---|
| Vault account | Assign an account, or clear it (use the default account) |
| Clear entered credentials | Removes usernames, passwords and key files entered on the hosts so the Vault account is used |
| Username · Port | Overwritten as entered (a username wins over the Vault account). You are warned when SSH and RDP are mixed |
| Group | Manually added hosts only (cloud hosts are grouped by profile). ▾ picks an existing group |
| Connect via | Private / public IP — hosts that have a public IP only |
| Favorite | Add / remove |

Accounts, ports and IP choice are not applied to local PowerShell entries. An account assigned to cloud hosts here applies to those hosts only; to set it for the whole profile (including servers synced later), use the group right-click.

---

## 4. SSH terminal

| Action | How |
|---|---|
| Copy | Select by dragging, then right-click, `Ctrl+Shift+C`, or `Ctrl+C` while text is selected |
| Paste | Right-click with nothing selected, `Ctrl+V`, `Ctrl+Shift+V` or `Shift+Insert`. Multi-line text asks first with a preview (not when the server has bracketed paste on). Inside full-screen programs such as vim or htop, `Ctrl+V` goes to the program (vim block selection and so on) — paste with `Ctrl+Shift+V` |
| Find in output | `Ctrl+Shift+F` → type, then `Enter` (next) · `Shift+Enter` (previous) · `Esc` |
| Font size (this tab only) | `Ctrl+wheel`, `Ctrl+=` / `Ctrl+-`, `Ctrl+0` to reset |
| Reconnect | Press Enter in a disconnected tab, or right-click the tab → Reconnect / Reconnect all disconnected tabs. Automatic reconnect (2·4·8·16·32 s, 5 tries) can be turned on in Settings — it does not reconnect after a clean `exit` or a rejected host key |
| Switch tabs | `Ctrl+Tab` / `Ctrl+Shift+Tab`, `Ctrl+1~8` (that tab), `Ctrl+9` (last) — works even when the terminal has focus |

To send `Ctrl+C` / `Ctrl+V` straight to the shell instead, turn them off under Settings → Terminal.

- **Host keys** — the key of a server you connect to for the first time is saved and trusted (you can turn on a confirmation prompt in Settings). **If the key differs from the saved one, Nubis always warns you.** If you reinstalled the server and its key changed, delete the old key in [Known Hosts](#122-known-hosts) and connect again.
- **Theme · font · syntax highlighting** — set these in [Settings → Terminal](#112-terminal). Changes apply to open terminals right away.

---

## 5. SFTP file transfer

When an SSH sign-in succeeds, the SFTP panel attaches on the left automatically. It opens a separate SFTP connection with the account the terminal signed in with. Use the folder button on the toolbar to show or hide it, and drag its edge to resize it.

### 5.1 Browsing

- Type a path, or double-click a folder. Backspace goes up, F5 refreshes, and the home button goes to your home folder.
- **Follow the terminal folder** — when you `cd` in the terminal, the panel moves to the same folder. This needs the shell to report its current folder (OSC 7). If yours does not, add this to `~/.bashrc` on the server:
  ```bash
  PROMPT_COMMAND='printf "\e]7;file://%s%s\a" "$HOSTNAME" "$PWD"'
  ```
  If the shell cannot report it, click the **terminal icon** button in the panel. It reads the path from the prompt (`user@host:/path$`) and moves there.

### 5.2 Upload and download

| Action | How |
|---|---|
| Upload | Drag files or folders in from File Explorer (drop on a folder row to upload into that folder), or use the upload button |
| Download | Drag items out to File Explorer or the desktop, or use the download button and pick a folder |
| Server-to-server copy | Drag into the SFTP panel of another SSH tab (goes through a temporary folder on this PC) |

When you drag out, the download starts the moment you release the mouse button. Progress and a cancel button appear at the bottom of the panel.

- If files with the same names exist on the server (including inside folders), choose **Overwrite / Skip / Cancel**.
- An existing file is uploaded under a temporary name, given the original's permissions, then swapped in — so if you cancel or the upload fails, **the original file stays intact**. A new file that stops halfway is removed.
  - Symbolic links, folders where you can write the file but not create new files (such as some config files), and servers that cannot rename over a file are overwritten in place instead.
- Downloads are written to `.part` and renamed when complete. Opening File Explorer afterwards can be turned off in Settings.
- Dropping files during another transfer tells you a transfer is in progress. Deleting can also be cancelled (items already deleted can't be restored).

### 5.3 Edit a file and save it back to the server

1. Double-click a file (or right-click → **Edit**).
2. Nubis downloads it to a temporary folder and opens it in [the editor you set](#114-sftp).
3. Each time you save in the editor and the content has changed, Nubis uploads it to the same path ("Saved → uploaded to server").
4. If the file on the server changed while you were editing (modification time or size), Nubis asks whether to overwrite it. If you choose **No**, that save is not uploaded.

> **Tip** If you set the editor to VS Code, edits made with Claude Code in that VS Code window are also uploaded the moment they are saved. Be careful when you keep production config files open.

Right-click → **Open read-only copy** downloads and opens the file without ever uploading it.

---

## 6. RDP remote desktop

Nubis hosts the Windows built-in Remote Desktop engine (`mstscax`, the same one mstsc uses) inside the app.

- Usernames in `DOMAIN\user` form are supported.
- When you resize the window, the remote resolution follows (Windows 8.1 / Server 2012 R2 or later).
- Connection quality, color depth, visual effects, redirection (clipboard, printers, drives, smart cards), sound, Windows-key passthrough and auto-reconnect all follow **mstsc's default settings file, `Documents\Default.rdp`**. Defaults you change in mstsc also apply to Nubis.
- Only the server certificate warning follows a Nubis setting ([Settings → RDP](#117-claude--rdp)).
- While the RDP screen has focus, `Ctrl+Tab` goes to the remote computer. Click a tab to switch.
- **Full screen** — right-click the tab → Full screen. The remote resolution matches the monitor, and in full screen the Windows key and Alt+Tab go to the remote computer. Use the connection bar at the top to come back.

---

## 7. Vault — shared accounts

When several servers use the same account, save it in the Vault once and pick it for each host.

### 7.1 Adding an account

Sidebar **Vault** → **Add account**.

| Field | Description |
|---|---|
| Name | The name shown in the list (for example, Production root) |
| Username | Used when the host has no username of its own |
| Password (optional) | SSH and RDP sign-in password |
| SSH private key (optional) | Import from a file or paste the content. OpenSSH, PuTTY (.ppk) or PEM |
| Key passphrase | If the key is protected by a passphrase |

You need at least a password or a key. Nubis actually loads the key when you save it and derives its public key. Use **Copy public key** and paste the result into `~/.ssh/authorized_keys` on the server.

### 7.2 Linking it to hosts

Host editor → **Sign-in account** → **Choose a Vault account**. The card shows what it signs in with, for example "user root · password · ssh-ed25519 key".

| Rule | Behavior |
|---|---|
| Priority | The host's chosen Vault account or its own values > the cloud profile's default Vault account > the profile's default username/password > the default Vault account in Settings > asked when you connect |
| RDP | Uses the username and password only (the key is ignored) |
| Changing values | Applies to every host that uses the account |
| Password changed on the server | Enter and save the new password when connecting; the Vault account is updated |
| Deleting an account | Hosts that used it are unlinked (they are listed before you delete) |

**Show hosts** on the Vault page filters the host list to the hosts that sign in with that account (including hosts that use it as a default). In the account picker, search by name or username and choose with `↑` `↓` · `Enter`.

### 7.3 Default accounts and group assignment

- **Default account** — set one SSH (Linux) and one RDP (Windows) default with the **Default account** button on the Vault page or under Settings → Default Vault account. **Every host** without its own account then signs in with it.
- **Whole group** — right-click the group → Set Vault account ([3.4](#34-group-right-click--bulk-actions)). For a cloud group it becomes the profile's default, so newly synced servers use it too.
- **Selected hosts only** — `Ctrl/Shift+click` cards and use Bulk edit ([3.5](#35-selecting-several-hosts-and-bulk-editing)).
- The host editor shows which default account will be used when you don't pick one.

---

## 8. Importing cloud servers

Nubis reuses the **credential profiles** already saved on this PC — the profile and credential files each cloud CLI creates. You do not enter keys into Nubis.

### 8.1 Preparation

| Cloud | Grouped by | Do this first | Read from |
|---|---|---|---|
| AWS | Profile | `aws configure` or `aws sso login` | `~/.aws/config` (`[default]` · `[profile x]`) · `~/.aws/credentials` |
| Tencent | Profile | `tccli configure` | `~/.tccli/<profile>.credential` (including `default`) |
| GCP | Project | `gcloud auth application-default login` | ADC file |
| Azure | Subscription | `az login` | `~/.azure` |

The **Sign in** button in each section of the Cloud page runs that CLI's sign-in command in a new console window. If your credential files are not in the standard location, set them with **Change path**.

### 8.2 Importing

1. On the Cloud page, click **Add profile** for the cloud (project for GCP, subscription for Azure).
2. Pick from the entries found on this PC. They sync as soon as you add them.
3. They appear on the Hosts page as groups with a cloud badge.

### 8.3 Sync rules

| Item | Behavior |
|---|---|
| Name | The `Name` tag (or instance name) is split into `name:role`. If you rename a host in Nubis, later syncs keep your name |
| Address | Both the private and public IP are stored. The default connection uses the private IP (switch to public in the profile settings) |
| Protocol · Port | Windows → RDP, everything else → SSH. The port comes from [Default ports](#115-default-ports) or a per-profile value |
| State | Terminated servers are excluded; stopped servers are dimmed. "Exclude stopped servers" in the profile settings only hides them from the list — the host's settings (account, port, …) are kept and used again when the server starts |
| Re-sync | Name, address and state are updated. Protocol, port, account and public-IP choices you changed are kept. Servers that disappeared are removed |
| Partial failure | If some regions fail to load, **nothing is deleted in that sync** |
| Account | See the [7.2 priority](#72-linking-it-to-hosts). Right-click the group to set a default account for the whole profile |
| Auto sync | Settings → Cloud auto sync: when the app starts · every N minutes (off by default) |
| Remove from list | The server stays in the cloud and stays hidden in later syncs (undo in the profile settings) |

---

## 9. Claude panel

Click **✦ Claude** in the title bar (or press `Ctrl+Shift+A`) to open the official Claude Code CLI on the right. Nubis starts a built-in MCP server (`127.0.0.1`, with a new token on every run) and connects it automatically, so Claude can work with Nubis directly.

### 9.1 First use

1. Install the Claude Code CLI: `npm install -g @anthropic-ai/claude-code`
2. When you first open the panel, Claude asks whether to trust the working folder (`%APPDATA%\Nubis\claude`). Choose **Yes**.
3. If you are not signed in, type `/login`.

### 9.2 What you can ask (examples)

| Goal | Say |
|---|---|
| Connect | "Connect to web-01" |
| Check several servers | "Check disk usage on the Production group" |
| Analyze the current screen | (while looking at a tab) "What is this error?" — the tab you are viewing is sent with every question |
| Read logs | "Show the end of /var/log/nginx/error.log on web-01" |
| Diagnose connection problems | "I can't connect to web-02, find out why" |
| Manage hosts | "Add 10.0.1.5 as web-02 in Production and link the ops account" |
| Cloud | "Re-sync the AWS profile" |
| App settings | "Set the terminal theme to dracula and the font size to 16" |
| Setup help | "Walk me through setting up GitHub sign-in" |

While Claude runs commands on servers, a **Running · Cancel** bar appears at the top of the panel. Cancelling returns immediately and skips servers that haven't started (commands already started may run to completion). With many servers the output per server is reduced, and long output keeps the beginning and **the end (where errors usually are)**. `capture_screen` switches back to the tab you were viewing after capturing.

### 9.3 Approval and logging

| Action | Approval |
|---|---|
| Lookups (host list, reading the screen, screen capture, connection diagnosis, reading settings) | None |
| Reading remote files (`sftp_list`, `sftp_read`) | Approval prompt (skipped when read-only auto-approve is on) |
| Running commands on several servers (`run_command`) | Approval prompt (with read-only auto-approve on, only read-only commands skip it) |
| Typing into a terminal; adding, changing or deleting hosts; uploading or downloading files | **Always an approval prompt** |
| Changing security settings (RDP certificate warning, host-key prompt, auto-approve) | **Approval prompt.** Claude can never turn auto-approve on, only off |
| Sign-in screen · editor path | Claude cannot change them |

The approval prompt shows the target servers and the full command. It flags risky commands (`rm -rf`, `reboot`, `Stop-Service` and so on), and the run button stays locked for 1.5 seconds after the prompt appears. Esc rejects. Every request is written to the audit log (the document icon in the panel header).

### 9.4 What Claude cannot do

- Read or decrypt saved passwords and keys. The account files (`hosts.json`, `vault.json` and others) are blocked from Claude's read tools and shell commands.
- Put a password on a host. Accounts are attached only by linking a Vault account.
- Upload the Nubis data folder, `~/.ssh` or private key files to a server.
- Change the sign-in screen settings or the editor path, or edit `settings.json` directly.

### 9.5 MCP tools

| Tool | Purpose |
|---|---|
| `list_groups` · `list_hosts` | Search groups and hosts |
| `list_sessions` · `open_session` · `close_session` | List, open and close tabs |
| `read_terminal` · `get_selection` · `capture_screen` | Terminal content, selected text, screen image |
| `wait_for_output` | Wait until some text appears or output goes quiet |
| `send_to_terminal` | Type into a terminal |
| `run_command` | Run on several servers in parallel (SSH / WinRM) |
| `test_connection` | DNS, port and SSH-banner diagnosis |
| `sftp_list` · `sftp_read` · `sftp_download` · `sftp_upload` | Remote files |
| `add_host` · `update_host` · `delete_host` · `list_vault` | Host management and the Vault list |
| `cloud_sync` | Cloud sync |
| `get_settings` · `update_settings` | App settings |
| `get_setup_status` · `open_app_page` | Setup status and opening screens (for setup guidance) |
| `diagnostics` | App memory and handles |

### 9.6 Running commands on several servers (SSH / WinRM)

`run_command` runs a command on several servers at once: over SSH for Linux servers and over WinRM (PowerShell · cmd) for Windows servers. You can target a whole group, such as `group:Production`, and stopped servers are skipped automatically.

| Item | Behavior |
|---|---|
| Address order | Private IP → public IP. SSH: private → public. WinRM: private 5986 → 5985 → public 5986 → 5985 |
| Retries | Moves to the next address or port **only when the connection fails**. An authentication failure stops immediately to avoid account lockouts, and errors after the shell is created are not retried so a command never runs twice |
| WinRM 5986 | HTTPS + NTLM/Negotiate (self-signed certificates allowed) |
| WinRM 5985 | HTTP + NTLM message encryption (works with the default server policy `AllowUnencrypted=false`). No TrustedHosts setup is needed on this PC |
| Account | The same host or cloud-profile account as RDP (`DOMAIN\user`, `user@domain`, local accounts) |
| Server setup (Windows) | A WinRM listener (`winrm quickconfig`, plus a certificate binding for HTTPS) and firewall rules for 5986/5985 |

> **Note** WinRM has not yet been fully verified against real servers. If you run into problems, please open an issue.

**What read-only auto-approve covers** — when auto-approve is on in [Settings → Claude](#117-claude--rdp), only read-only commands like the ones below run without an approval prompt. It is an allow list: anything not on it, or anything ambiguous, brings the prompt back.

| Kind | Runs without approval (examples) | Asks for approval (examples) |
|---|---|---|
| Linux | `ls` `cat` `grep` `df` `ps` `ss` `journalctl` `systemctl status` `docker ps` `kubectl get` | `systemctl restart`, `find -delete`, file redirection `>`, command substitution `$( )` or backticks |
| PowerShell | `Get-*`, `Select-/Where-/Sort-/Format-*`, `Test-*` | `Invoke-*`, method or .NET calls, unknown commands |
| cmd · common | `ipconfig` `sc query` `reg query` `netstat` `tasklist` | Write options such as `ipconfig /release` |

Dangerous commands always ask, whatever the setting, and commands that run automatically are still written to the audit log as "auto-approved (read-only)".

---

## 10. Sign-in screen and per-account profiles

### 10.1 Setup (GitHub example)

1. Go to github.com/settings/developers → **OAuth Apps** → **New OAuth App**.
   - Application name: `Nubis` (any name works)
   - Homepage URL and Authorization callback URL: any address (for example `http://localhost`). Device Flow does not use them.
2. In the new app's settings, tick **Enable Device Flow** and copy the **Client ID**. You do not need a client secret.
3. In Nubis, open Settings → **Sign-in screen** → **GitHub**.
4. Enter the Client ID and the **allowed accounts** (GitHub login names, separated by commas). To allow a whole organization, enter its name under **GitHub Organization**.
5. Click **Test sign-in and save**. Enter the code shown on screen in your browser and approve it; the settings are then saved. They are saved only after a successful test sign-in, so a wrong setting cannot lock you out.
6. The sign-in screen appears the next time you start Nubis.

| Provider | Where to register | Values needed |
|---|---|---|
| GitHub | github.com/settings/developers → OAuth App, tick **Enable Device Flow** | Client ID |
| Google | Google Cloud console → Credentials → OAuth client ID, type **TVs and Limited Input devices** | Client ID · Client secret |
| Microsoft | Entra admin center → App registrations → Authentication → **Allow public client flows** | Application (client) ID, tenant (`organizations` or your company tenant) |

> **Tip** Click **Ask Claude** on the "Sign-in screen (SSO)" card of the getting-started guide, and Claude walks you through these steps, opening each screen as you go. You enter and save the values yourself.

### 10.2 How sign-in works

| Item | Behavior |
|---|---|
| Method | OAuth Device Flow: approve the on-screen code in your browser. You type your password only in the browser; Nubis never receives it |
| Token | Used only to identify the account and never stored |
| Allowed | Account names (case-insensitive), `@company.com` (a whole domain), GitHub Organization members |
| Stay signed in | In hours. 0 = sign in on every start (maximum 720) |
| Offline grace | In days. When the sign-in server cannot be reached, you can get in if your last sign-in is within this period (0 = blocked, maximum 30) |
| Records | The last sign-in is stored encrypted in `login.json`. Successful and failed sign-ins go to the audit log |

### 10.3 Per-account profiles

With sign-in turned on, each account keeps its own data in `%APPDATA%\Nubis\users\<provider>_<account ID>\`.

| Stored per account | Shared |
|---|---|
| Hosts, Vault, cloud profiles, known_hosts, Claude working folder, audit log, display settings (theme, font, SFTP and so on) | Language, sign-in screen settings, last sign-in record |

- When a different account signs in, it starts empty, and no account can see another's data.
- The account ID never changes (GitHub user number, Google `sub`, Microsoft `oid`), so renaming your account still opens the same profile.
- **Owner** = the account that set up sign-in. Data from before sign-in was turned on is handed over to the owner on their first sign-in. Only the owner can change the sign-in settings. If you turn sign-in off, Nubis keeps using the owner's profile.

### 10.4 Turning sign-in off, or getting locked out

- To turn it off: Settings → Sign-in screen → **Off** → **Save (turn off sign-in)** (owner only).
- If a wrong setting keeps you out, open `%APPDATA%\Nubis\settings.json` in Notepad, change `"Provider"` inside `"Login"` to `"None"`, and save.

---

## 11. Settings reference

Settings are saved as soon as you change them. The **Claude** column shows whether the Claude panel can change the setting with `update_settings`.

### 11.1 Language

| Setting | Default | Behavior | Claude |
|---|---|---|---|
| Language | Korean | Display language. Takes effect after a restart (Nubis asks whether to restart). The Claude panel answers in the same language | No |
| Show the getting-started guide | — | Opens the getting-started window again | — |

### 11.2 Terminal

| Setting | Default | Behavior | Claude |
|---|---|---|---|
| Terminal theme | Remote Dark | 9 dark and 2 light themes. Applies to open terminals immediately | Yes |
| Font · Font size | Cascadia Mono · 14 | Any installed monospace font, 9–28 | Yes |
| Automatic text contrast | On | Adjusts text colors that are too close to the background so they stay readable | Yes |
| Syntax highlighting: IP addresses · numbers · status keywords | All on | Adds color to output. Skips text the server already colored and full-screen programs such as vim and htop | Yes |
| Terminal GPU acceleration | Off | Speeds up heavy output and uses about 70 MB more memory. Takes effect after a restart | Yes |
| Other font | — | Type the name of an installed font that isn't in the list | — |
| Ctrl+C to copy · Ctrl+V to paste | On | Ctrl+C copies only when text is selected (like Windows Terminal) | No |
| Confirm multi-line paste | On | Prevents each pasted line from running immediately | No |

### 11.2.1 Default Vault account · Host list · Cloud auto sync

| Setting | Default | Behavior | Claude |
|---|---|---|---|
| Default Vault account (SSH · RDP) | Not set | Used by every host without its own account ([7.3](#73-default-accounts-and-group-assignment)) | No |
| Show Recent group | On | Hosts connected in the last 30 days at the top | No |
| Confirm before closing a connected tab | On | Prevents accidental closing with X, middle-click or `Ctrl+Shift+W` | No |
| Sync all on start · interval (minutes) | Off · 0 | Cloud auto sync (minimum 5 minutes) | No |

### 11.3 SSH

| Setting | Default | Behavior | Claude |
|---|---|---|---|
| Reconnect automatically when disconnected | Off | When the connection drops with an error: up to 5 tries at 2·4·8·16·32 s. Authentication failures, a clean exit such as `exit`, and rejected host keys are not retried | No |
| Confirm host keys of new servers | Off | When on, the key of a server you have not seen before is shown in a confirmation prompt. When off, it is saved and you connect. **A changed key always triggers a warning, whatever this setting** | With approval |

### 11.4 SFTP

| Setting | Default | Behavior | Claude |
|---|---|---|---|
| Open the SFTP panel on SSH connect | On | Attaches the SFTP panel on the left after a successful sign-in | Yes |
| Follow the terminal folder | On | Moves the panel to the current folder the shell reports with OSC 7 | Yes |
| Open the folder in Explorer after downloading | On | Turn off to stop opening the folder after downloads | No |
| File editor | Windows default app | The program that opens files you double-click. The "VS Code" button sets it in one click | No |

### 11.5 Default ports

| Setting | Default | Behavior | Claude |
|---|---|---|---|
| SSH · RDP default port | 22 · 3389 | The port for new hosts and cloud-synced hosts. When you change it, synced hosts that used the old default move to the new one (ports you set per host are kept) | Yes |

### 11.6 Sign-in screen

See [chapter 10](#10-sign-in-screen-and-per-account-profiles). Settings are saved only after a successful test sign-in, only the owner can change them, and Claude cannot change them.

### 11.7 Claude · RDP

| Setting | Default | Behavior | Claude |
|---|---|---|---|
| Run read-only commands without approval | Off | Skips the approval prompt only for read-only requests in `run_command`, `sftp_list` and `sftp_read` (an allow list; anything ambiguous still asks). The audit log is still written | Off only |
| Show RDP server certificate warnings | On | When off, Nubis connects without a warning even to servers whose certificate cannot be verified (self-signed). Applies from the next connection | With approval |

### 11.8 Cloud credential file paths

Set these with **Change path** in each section of the Cloud page (the standard location is the default). They are applied as `AWS_CONFIG_FILE` for AWS, `GOOGLE_APPLICATION_CREDENTIALS` for GCP and `AZURE_CONFIG_DIR` for Azure, so the CLI started by the Sign in button uses the same path. Claude cannot change them.

---

## 12. Data files and security

### 12.1 Data locations

| Location | Contents |
|---|---|
| `%APPDATA%\Nubis\settings.json` | Language and sign-in screen settings (all settings when sign-in is not used). Plain text |
| `%APPDATA%\Nubis\login.json` | Last sign-in record (encrypted) |
| `%APPDATA%\Nubis\users\<account>\` | Per-account profiles (when sign-in is used) |
| `hosts.json` · `vault.json` · `cloud_profiles.json` · `known_hosts.json` | Hosts, Vault, cloud profiles, host keys (**whole file encrypted**) |
| `claude\` · `claude_audit.log` | Claude panel working folder and audit log |
| `%LOCALAPPDATA%\Nubis\WebView2\` | Terminal view cache |

Set the `NUBIS_DATA_DIR` environment variable to use a different folder (for portable use or testing).

### 12.2 Known Hosts

The **Known Hosts** sidebar page lists the host-key fingerprints of SSH servers you trust. If you reinstalled a server and its key changed, delete it here and connect again (Nubis asks first, because the key seen on the next connection is trusted as new). Keys you rejected stay marked `REJECTED` and are never trusted automatically.

### 12.3 Security model

| Protection | Details |
|---|---|
| Encryption at rest | Whole data files are encrypted with Windows DPAPI (current user, AES-256). Server names, IPs and account names are hidden too, and the files open only on this PC under this Windows account. Passwords and keys are encrypted once more inside |
| Copying to another PC | Cannot be decrypted. The original is kept as `*.corrupt-<date>` and Nubis starts empty |
| Sign-in screen | Only allowed accounts can open the app, and each account's data is kept separate |
| Claude panel | Account-file reads, DPAPI decryption commands and settings-file edits are blocked; remote changes require approval and are audit-logged |
| Host keys | A changed key always triggers a warning |

**Limits** — the sign-in screen and per-account profiles are a door into the app and a separation inside the app's screens. Encryption works per Windows account, so it cannot stop another program running under the same Windows account from decrypting the files directly. If several people share one PC, give each of them their own Windows account.

### 12.4 Backup and moving

- Same PC, same Windows account: copy the whole `%APPDATA%\Nubis` folder.
- Another PC: encrypted files cannot be decrypted there. On the new PC, add your cloud profiles again and sync, and re-enter your Vault accounts.

---

## 13. Keyboard shortcuts

| Shortcut | Action |
|---|---|
| `Ctrl+K` · `Ctrl+F` (`Ctrl+Shift+K` in a terminal) | Search hosts → pick with `↑` `↓` `Tab`, connect with `Enter` |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | Next / previous tab |
| `Ctrl+1~8` · `Ctrl+9` | That tab · last tab |
| `Ctrl+Shift+W` · middle-click (tab) | Close the tab (asks if connected) |
| `Ctrl+Shift+A` | Open or close the Claude panel |
| `Ctrl+Shift+T` | Local PowerShell tab |
| `Ctrl+C` / `Ctrl+V` (copy when selected) · `Ctrl+Shift+C` / `Ctrl+Shift+V` · `Shift+Insert` | Copy / paste in the terminal |
| `Ctrl+Shift+F` | Find in terminal output |
| `Ctrl+wheel` · `Ctrl+=` · `Ctrl+-` · `Ctrl+0` | Terminal font size (this tab only) |
| `Ctrl+click` · `Shift+click` (card) · `Ctrl+A` | Select several hosts · a range · every visible host → bulk edit |
| `Delete` (hosts selected) | Delete the selected hosts (after confirming) |
| `Ctrl+S` | Save in the host editor |
| `Esc` | Close the host editor; clear the host selection; reject an approval prompt |
| Right-click a tab | Reconnect · New tab to same host · Full screen (RDP) · Rename tab · Edit host · Close other / right / disconnected tabs · Reconnect all disconnected tabs. Drag tabs to reorder; when they overflow, scroll with the wheel or use ▾ for the full list |
| SFTP: `Enter` · `Backspace` · `F5` · `F2` · `Delete` | Open/edit · up · refresh · rename · delete |

---

## 14. Troubleshooting

| Symptom | What to check |
|---|---|
| The host list is empty | Whether you signed in with a different account (per-account profiles; the sign-in section of Settings shows the current profile). Whether you ran an older version |
| "Could not read the settings file" | The file was copied from another PC or is damaged. The original is kept as `*.corrupt-<date>` |
| Warning that the host key changed | If you reinstalled the server, delete the key in Known Hosts and connect again. If not, it may be a man-in-the-middle attack — do not connect |
| The SFTP panel does not follow the terminal folder | Whether the server shell sends OSC 7 ([5.1](#51-browsing)). Use the terminal icon button in the panel |
| An edited file is not uploaded | Check the status line at the bottom of the panel. You may have chosen "No" because the server file changed, or you may lack permission. Save again to retry |
| Claude panel says "claude not found" | Run `npm install -g @anthropic-ai/claude-code`, then press Enter in the panel (it looks again) |
| Claude looks at the wrong tab | The tab you are viewing is sent with each question. Switch tabs, then send a new message |
| Cloud sync fails | Sign in again with that CLI (the section's Sign in button) and check the credential file path. Ask Claude "cloud sync isn't working" and it will check the status |
| Sign-in screen says no OAuth app matches the Client ID | Check the Client ID for typos and that Enable Device Flow is ticked in the GitHub app |
| A wrong sign-in setting locks you out | See [10.4](#104-turning-sign-in-off-or-getting-locked-out) |
| Certificate warning right after an RDP connection | In environments with self-signed certificates, turn off the certificate warning in Settings → RDP |
