<img src="../nubis.png" width="72" align="right"/>

**English** | [한국어](guide.ko.md)

# Nubis User Guide

Nubis is a Windows remote-access client. It gathers your cloud and on-premises servers by profile, connects to them over SSH, RDP and SFTP, and lets the built-in Claude panel check servers and change settings when you ask in plain words. This guide covers everything from installation to how each feature and setting behaves.

## Contents

1. [Getting started](#1-getting-started)
2. [Window layout](#2-window-layout)
3. [Managing hosts](#3-managing-hosts)
4. [SSH terminal](#4-ssh-terminal)
5. [SFTP file transfer](#5-sftp-file-transfer)
6. [RDP remote desktop](#6-rdp-remote-desktop)
7. [Vault — shared accounts](#7-vault--shared-accounts)
8. [Importing cloud servers](#8-importing-cloud-servers)
9. [Importing on-premises servers](#9-importing-on-premises-servers)
10. [Claude panel](#10-claude-panel)
11. [Sign-in screen and per-account profiles](#11-sign-in-screen-and-per-account-profiles)
12. [Settings reference](#12-settings-reference)
13. [Data files and security](#13-data-files-and-security)
14. [Keyboard shortcuts](#14-keyboard-shortcuts)
15. [Troubleshooting](#15-troubleshooting)
16. [Kubernetes](#16-kubernetes)

---

## 1. Getting started

### 1.1 Requirements

| Item | Details |
|---|---|
| OS | Windows 10 / 11 (64-bit) |
| Runtime | None needed — the release folder includes .NET |
| WebView2 | Included with Windows 10/11 (used for the terminal) |
| Claude panel (optional) | [Claude Code CLI](https://docs.claude.com/claude-code) — `npm install -g @anthropic-ai/claude-code` |
| Cloud import (optional) | A credential profile for each cloud you use — the ones created by the `aws`, `tccli`, `gcloud`, `az` and `oci` CLIs |
| Kubernetes (optional) | `kubectl` (`winget install Kubernetes.kubectl`), plus the cloud's CLI or auth plugin for cloud clusters ([16.1](#161-requirements)) |

### 1.2 Installation

1. Extract the release zip (`Nubis-<version>-portable-win-x64.zip`) to any folder.
2. Run `Nubis.exe` from that folder. Keep all the files in the folder together (DLLs and `wwwroot\`); the exe does not run on its own.
3. To build from source, run `.\build.ps1 publish` in the repository. This creates the `publish\` folder.

Nubis is a portable app with no installer. Your data is stored in `%APPDATA%\Nubis`, not in the app folder, so to update you replace the whole app folder with the new one (delete the old folder first rather than extracting over it).

Only one Nubis runs at a time. Starting it again while it is open (including with arguments such as `--connect`) hands over to the open window, so two copies never overwrite each other's data. Each save keeps the previous copy (`.bak`), which is restored automatically if a data file gets corrupted (`settings.json` excluded).

> **Note** Data files are encrypted. After you run a current version once, do not go back to a 0.1.x version. Older versions cannot read the encrypted files and start with an empty list.

### 1.3 First run

1. **Choose a language** — Korean (default) or English. You are asked only once. To change it later, see [Settings → Language](#121-language).
2. **Sign-in screen** — appears only if you have set it up ([chapter 11](#11-sign-in-screen-and-per-account-profiles)).
3. **Getting-started guide** — opens once, automatically, after the main window appears (not when you start with `--connect`, or when you already have hosts or cloud profiles). Each card has two buttons:
   - The left button (for example **New host** or **Open Cloud**) takes you straight to that feature's screen.
   - **Ask Claude** opens the Claude panel on the right and starts a conversation with a guide question. Claude checks your current setup and walks you through it step by step, opening the screens you need.

   You can open the guide again at any time with **Show the getting-started guide** in Settings.

Not sure where to start? If you use a cloud provider, begin with [importing cloud servers](#8-importing-cloud-servers). If your servers are listed in Excel, `~/.ssh/config`, mRemoteNG, Ansible or Active Directory, [import them](#9-importing-on-premises-servers). Otherwise, [add a host yourself](#31-adding-a-host).

---

## 2. Window layout

| Area | Description |
|---|---|
| Title-bar tabs | The **Hosts** home tab and your open session tabs. When there are too many tabs, scroll them with the mouse wheel or use the ▾ button (**Show all tabs**). On the right: **+** (local PowerShell), **Claude** (Claude panel) and the window buttons |
| Sidebar | **Hosts** · **Cloud** · **Kubernetes** · **Vault** · **Known Hosts** · **Settings** |
| Hosts page | Search / quick-connect box with **CONNECT**, **NEW HOST**, **IMPORT**, and host cards grouped by name |
| Editor panel | Opens on the right when you add or edit a host |
| Claude panel | Vertical panel on the right. Drag its edge to resize it |

- **Windows 11** — the title bar, sidebar and background use the Mica material, and the window buttons are the native Windows ones (Snap Layouts work). On older Windows the backgrounds are solid.
- **Toasts** — short results, such as bulk host changes, a copied public key, or SFTP warnings and errors, appear briefly at the bottom right. Click one to close it; hover to keep it open.
- **Command palette** — `Ctrl+K` searches hosts, open tabs and actions in one box ([14](#14-keyboard-shortcuts)).
- **Closed-tab history** — `Ctrl+Shift+R` (or right-click a tab → **Reopen closed tab**) reopens the last closed tab. Up to 10 tabs are remembered, newest first. Only saved hosts and local PowerShell tabs are remembered (quick-connect tabs are not), and the history is not kept after Nubis closes.

---

## 3. Managing hosts

### 3.1 Adding a host

Click **NEW HOST** (`Ctrl+N`) to open the editor panel.

| Field | Description |
|---|---|
| Protocol | SSH or RDP. Changing it resets the port to the default for that protocol |
| Address · Port | IP address or host name. The default port comes from [Settings → Default ports](#1210-default-ports) |
| Public IP (optional) | An Elastic IP or similar. Turn on "Connect via public IP" to use this address |
| Name | The name shown on the card (the address if left blank) |
| Group | The name cards are grouped under (for example, Production). Use the ▾ button next to it to pick an existing group so typos don't split groups (names that differ only in case are shown as one group) |
| Sign-in account | Pick a [Vault](#7-vault--shared-accounts) account to sign in with its username, password and key. If you don't pick one, the [default account](#73-default-accounts-and-group-assignment) is used (the editor shows which one) |
| Username · Password | Entered directly. **When you pick a Vault account these fields are hidden and the Vault account takes priority** (the username field shows only if the Vault account has none). Hidden values are not deleted — clear the Vault account and they reappear and are used again |
| Private key file (SSH) | OpenSSH, PuTTY (.ppk) or PEM key file. The key passphrase is taken from the password field. Without a key, Nubis tries keys in **ssh-agent** (the Windows OpenSSH agent, `SSH_AUTH_SOCK`) and then `id_ed25519` · `id_ecdsa` · `id_rsa` in `~/.ssh`, like OpenSSH (key files with a passphrase are skipped) |
| Jump host (SSH) | The SSH host to go through when this server can't be reached directly (ssh's ProxyJump). The jump host's own account, Vault and OTP settings are used. One hop only |
| RD Gateway (RDP) | Only for servers reached through an RD Gateway. Leave blank to follow the gateway in the mstsc defaults (`Default.rdp`). The same account signs in to the gateway |

Click **Save** (`Ctrl+S`) or **Save and connect**. Passwords are encrypted with Windows DPAPI before they are stored. If the address or port is invalid, the reason appears at the bottom of the editor. If you close the editor with unsaved changes (`Esc`, clicking outside, or X), Nubis asks before discarding them. **Duplicate** on the card menu adds the copy only when you click Save.

To add many servers at once, use **IMPORT** ([chapter 9](#9-importing-on-premises-servers)) or [cloud import](#8-importing-cloud-servers).

### 3.2 Connecting and quick connect

- **Double-click a card** to connect. Right-click for: Connect · Connect via public/private IP · Favorite · Edit · Duplicate · Delete.
- **Favorites and Recent** — favorite hosts and hosts you connected to in the last 30 days also appear in groups at the top of the list (Recent can be turned off in Settings).
- **Search** — `Ctrl+F` jumps to the search box on the Hosts page (not while a terminal has focus — there `Ctrl+F` goes to the shell). It matches name, private/public IP, user, group, instance ID, region, OS, role and profile name; several words separated by spaces must all match. `vault:name` shows hosts that sign in with that Vault account.
- **Keyboard selection** — in the search box, move the outline with `↑` `↓` (one row) or `Tab` (one card), press `Enter` to connect, `F2` to edit, and `Esc` to clear the search.
- **Command palette** — `Ctrl+K` (`Ctrl+Shift+K` in a terminal) finds hosts, open tabs and actions from anywhere ([14](#14-keyboard-shortcuts)).
- **Quick connect** — type `user@host[:port]` (SSH) or `rdp host[:port]` in the search box and press Enter or **CONNECT**. The host is not saved to the list. If the text matches a host's name or address exactly, or doesn't look like an address, Nubis connects to that host (or the outlined one) instead.
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
| Username · Port | Overwritten as entered (on hosts with a chosen Vault account, the username is used only if that account has none). You are warned when SSH and RDP are mixed |
| Group | Manually added hosts only (cloud hosts are grouped by profile). ▾ picks an existing group |
| Connect via | Private / public IP — hosts that have a public IP only |
| Favorite | Add / remove |

Accounts, ports and IP choice are not applied to local PowerShell entries. An account assigned to cloud hosts here applies to those hosts only; to set it for the whole profile (including servers synced later), use the group right-click.

---

## 4. SSH terminal

Copy and paste work in one of two styles, set under [Settings → Terminal](#126-terminal). Both switches can be on at the same time.

| Style (setting) | Copy | Paste |
|---|---|---|
| Windows Terminal style — **Ctrl+C to copy · Ctrl+V to paste** (on by default) | Select, then `Ctrl+C` (with nothing selected, `Ctrl+C` interrupts the running command as usual) | `Ctrl+V`. Inside full-screen programs such as vim or htop, `Ctrl+V` goes to the program (vim block selection and so on) — paste with `Ctrl+Shift+V` |
| PuTTY / mRemoteNG style — **Copy on select · right-click pastes** (off by default) | Releasing the mouse after a drag, double-click (word) or triple-click (line) copies at once | Right-click always pastes |
| Always available | `Ctrl+Shift+C`. With copy-on-select off, right-click copies when text is selected | `Ctrl+Shift+V`, `Shift+Insert`. With copy-on-select off, right-click pastes when nothing is selected |

Multi-line text asks first with a preview (not when the server has bracketed paste on). Turn both switches off to send `Ctrl+C` / `Ctrl+V` straight to the shell.

| Action | How |
|---|---|
| Find in output | `Ctrl+Shift+F` (a selected single line is filled in) → type, then `Enter` (next) · `Shift+Enter` (previous) · `Esc` |
| Font size (this tab only) | `Ctrl+wheel`, `Ctrl+=` / `Ctrl+-`, `Ctrl+0` to reset |
| Reconnect | Press Enter in a disconnected tab, or right-click the tab → Reconnect / Reconnect all disconnected tabs. Automatic reconnect (2·4·8·16·32 s, 5 tries) can be turned on in Settings — it does not reconnect after a clean `exit` or a rejected host key |
| Switch tabs | `Ctrl+Tab` / `Ctrl+Shift+Tab` (past either end you land on Hosts), `Ctrl+1~8` (that tab), `Ctrl+9` (last) — works even when the terminal has focus |

- **Host keys** — the key of a server you connect to for the first time is saved and trusted (you can turn on a confirmation prompt in Settings). **If the key differs from the saved one, Nubis always warns you.** Nubis also reads OpenSSH's `~/.ssh/known_hosts` (read-only): servers you already trust in `ssh` are not asked about, and a key that differs from the one `ssh` knows is warned about like a changed key (`@revoked` keys are rejected). If you reinstalled the server and its key changed, delete the old key in [Known Hosts](#132-known-hosts) and connect again.
- **Theme · font · syntax highlighting** — set these in [Settings → Terminal theme and font](#125-terminal-theme-and-font) and [Syntax highlighting](#127-syntax-highlighting-and-performance). Changes apply to open terminals right away.
- **OTP · additional authentication** — when the server asks for more than a password, such as an OTP (verification code), Nubis shows an input window with the server's own wording. The saved password is sent only to a password question, and only once per connection — if the server asks again, Nubis shows the input window instead of resending the same password (avoids account lockout). Servers that require a key plus an OTP, and servers that require an expired password to be changed, are also handled during sign-in. The server's pre-login notice (banner) is shown in the terminal.
- **Remember answers** — tick "Remember this answer and don't ask again" in the input window and Nubis answers it automatically for that host next time (for example `Choose [1] Push, or [2] TOTP` → `1`). Only answers to visible questions, and empty answers to hidden ones, are remembered — never OTP codes or passwords. Clear them in the host editor.
- **Which account is used** — the terminal's first line shows which account is being tried (`Vault account 'x'`, saved on this host, or entered), and a rejection says whose password it was. Card tooltips show it too. If you change the Vault or host account after opening a tab, reconnecting uses the new values.
- **Authentication failure hints** — Nubis looks at which methods the server allows (for example, keys only) and tells you the likely cause.
- **Claude on OTP servers** — Claude's `run_command` runs over an SSH tab you have already opened and signed in to, so it does not need the OTP again ([10.6](#106-running-commands-on-several-servers-ssh--winrm)). Without an open tab, and for Claude's SFTP tools, it cannot enter an OTP, so it stops and reports the reason.

---

## 5. SFTP file transfer

When an SSH sign-in succeeds, the SFTP panel attaches on the left automatically. It opens an extra SFTP channel on the terminal's SSH connection, so **you don't authenticate again** (an OTP is entered only once). If that isn't possible, for example because the server limits sessions, it opens a separate connection with the same account. Use the folder button on the toolbar to show or hide it, and drag its edge to resize it.

### 5.1 Browsing

- Type a path and press Enter (`~`, `~/x`, relative paths and `..` work), or double-click a folder. `Esc` in the path box puts back the current folder. Backspace goes up, F5 refreshes, the home button goes to your home folder, and `Ctrl+H` shows or hides hidden files (shown by default).
- **Follow the terminal folder** — when you `cd` in the terminal, the panel moves to the same folder. Nubis reads the folder from the prompt after each command, so the default prompts work without any server setup:
  - `user@host:/path$` (Ubuntu, Debian) — the path is read from the prompt.
  - `[user@host folder]$` (Amazon Linux, RHEL, CentOS) — only the folder name is shown, so Nubis works out the path from the `cd` you ran and follows only when the name matches. `cd -` and paths with variables are not followed.
  - For any other prompt, have the shell report its folder (OSC 7) by adding this to `~/.bashrc` on the server. When OSC 7 is present it is always used:
  ```bash
  PROMPT_COMMAND='printf "\e]7;file://%s%s\a" "$HOSTNAME" "$PWD"'
  ```
  The **terminal icon** button in the panel moves to the terminal folder on demand.
- **Follow user switches** — when you become another user (usually root) in the terminal with `sudo su -`, `sudo -i` and so on, Nubis sees the user in the prompt and reopens the panel with that user's permissions. A red user badge appears next to the panel title, and `exit` switches back to your own account.
  - Nubis runs the server's `sftp-server` through `sudo` on the same SSH connection. With passwordless sudo (NOPASSWD) it opens right away; otherwise it checks once with your sign-in password and asks for the sudo password if that fails or you signed in without a password. A user that failed is not retried during that connection.
  - Switching with `su -` (root password) without sudo rights, and servers whose sudo configuration uses `requiretty` or doesn't allow running `sftp-server`, are not followed — Nubis tells you why.
  - Files uploaded while the panel runs as root are owned by root.

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
2. Nubis downloads it to a temporary folder and opens it in [the editor you set](#129-sftp).
3. Each time you save in the editor and the content has changed, Nubis uploads it to the same path ("Saved → uploaded to server").
4. If the file on the server changed while you were editing (modification time or size), Nubis asks whether to overwrite it. If you choose **No**, that save is not uploaded.

> **Tip** If you set the editor to VS Code, edits made with Claude Code in that VS Code window are also uploaded the moment they are saved. Be careful when you keep production config files open.

Right-click → **Open read-only copy** downloads and opens the file without ever uploading it.

If you close a tab (or Nubis) while files from it are still open for editing, Nubis always asks first: changes saved so far are uploaded, but saves made after closing are not.

---

## 6. RDP remote desktop

Nubis hosts the Windows built-in Remote Desktop engine (`mstscax`, the same one mstsc uses) inside the app.

- Usernames in `DOMAIN\user` form are supported.
- When you resize the window, the remote resolution follows (Windows 8.1 / Server 2012 R2 or later).
- Connection quality, color depth, visual effects, redirection (clipboard, printers, drives, smart cards), sound, Windows-key passthrough and auto-reconnect all follow **mstsc's default settings file, `Documents\Default.rdp`**. Defaults you change in mstsc also apply to Nubis.
- **RD Gateway** — set it in the host editor's RD Gateway field; when blank, the gateway settings in `Default.rdp` are used.
- **Entra ID (Azure AD) joined PCs** — use the username `AzureAD\user@domain` and check **Don't use NLA (CredSSP)** under RD Gateway in the host editor (the same as `enablecredsspsupport:i:0` in an .rdp file). This is needed when this PC is not joined to the same Entra tenant. With NLA off, the connection reaches the remote sign-in screen before the account is checked, so turn it on only for the PCs that need it. If `Default.rdp` has `enablecredsspsupport:i:0`, NLA is off for every RDP host. Claude can set it with `rdp_disable_nla` on `add_host` / `update_host` (approval required).
- Only the server certificate warning follows a Nubis setting ([Settings → RDP](#1213-rdp)).
- **Full screen** — right-click the tab → **Full screen** (available while connected; there is no keyboard shortcut). The remote resolution matches the monitor, and in full screen the Windows key and Alt+Tab go to the remote computer. Use the connection bar at the top to come back; the session then resizes back to the tab.

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
| Priority | The host's chosen Vault account > values entered on the host > the cloud profile's default Vault account > the profile's default username/password > the default Vault account in Settings > asked when you connect |
| RDP | Uses the username and password only (the key is ignored) |
| Changing values | Applies to every host that uses the account |
| Password changed on the server | Enter and save the new password when connecting; the Vault account is updated |
| Deleting an account | Hosts that used it are unlinked (they are listed before you delete) |

**Show hosts** on the Vault page filters the host list to the hosts that sign in with that account (including hosts that use it as a default). In the account picker, search by name or username and choose with `↑` `↓` · `Enter`.

### 7.3 Default accounts and group assignment

- **Default account** — set one SSH (Linux) and one RDP (Windows) default with the **Default account** button on the Vault page or under Settings → Default Vault account. **Every host** without its own account then signs in with it.
- **Whole group** — right-click the group → Set Vault account ([3.4](#34-group-right-click--bulk-actions)). For a cloud group it becomes the profile's default, so newly synced servers use it too.
- **Selected hosts only** — `Ctrl/Shift+click` cards and use Bulk edit ([3.5](#35-selecting-several-hosts-and-bulk-editing)).
- **Imported hosts** — pick an SSH and an RDP Vault account in the import window ([9.5](#95-options-and-adding)).
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
| OCI | Profile | `oci session authenticate` (browser) or an API key via `oci setup config` | `~/.oci/config` (`[DEFAULT]` · `[x]`) |

The **Sign in** button in each section of the Cloud page runs that CLI's sign-in command in a new console window. If your credential files are not in the standard location, set them with **Change path**.

### 8.2 Importing

1. On the Cloud page, click **Add profile** for the cloud (project for GCP, subscription for Azure).
2. Pick from the entries found on this PC. They sync as soon as you add them.
3. They appear on the Hosts page as groups with a cloud badge.

You can also ask Claude to find and add them for you ([10.2](#102-what-you-can-ask-examples)).

### 8.3 Sync rules

| Item | Behavior |
|---|---|
| Name | The `Name` tag (or instance name) is split into `name:role`. If you rename a host in Nubis, later syncs keep your name |
| Address | Both the private and public IP are stored. The default connection uses the private IP (switch to public in the profile settings) |
| Protocol · Port | Windows → RDP, everything else → SSH. The port comes from [Default ports](#1210-default-ports) or a per-profile value |
| State | Terminated servers are excluded; stopped servers are dimmed. "Exclude stopped servers" in the profile settings only hides them from the list — the host's settings (account, port, …) are kept and used again when the server starts |
| Re-sync | Name, address and state are updated. Protocol, port, account and public-IP choices you changed are kept |
| Removed servers | A server is removed only after it is missing from **two complete syncs in a row**. After the first miss, the profile's status line says it will be removed if it is still missing next time. If a removed server was a jump host for other hosts, the status line tells you to pick a new one |
| Partial failure | If some regions fail to load, **nothing is deleted in that sync** (servers that did load are still added and updated). If regions are only denied by permissions, servers in those regions are kept and the other regions sync normally |
| Account | See the [7.2 priority](#72-linking-it-to-hosts). Right-click the group to set a default account for the whole profile |
| Auto sync | Settings → Cloud auto sync: when the app starts · every N minutes (off by default) |
| Remove from list | The server stays in the cloud and stays hidden in later syncs (undo in the profile settings) |

---

## 9. Importing on-premises servers

**IMPORT** on the Hosts page (also **Import...** on an empty host list, or **Import hosts** in the `Ctrl+K` palette) reads servers from another tool or list, shows a preview, and adds the ones you pick. **Passwords are never imported** — set accounts with Vault.

### 9.1 Sources

| Tab | How to load | What is read |
|---|---|---|
| **Paste Excel·CSV** | Copy a table in Excel and paste it (`Ctrl+V`). Pasting into the empty box previews right away; otherwise click **Preview** | Name, address, port, type, group, user, OS, note and jump host. Tab, `;` and `,` separators are detected. The first row is a header when it has known column names (Korean or English, such as `이름`/`name`, `주소`/`IP`/`host`, `포트`/`port`, `그룹`/`group`, `사용자`/`user`). Without a header, columns are guessed by value shape — IP or host name, port number, `ssh`/`rdp`, OS words. A cell like `user@host:port` or `rdp://host` also works. Merged group cells are filled from the row above. Password columns are ignored |
| **`~/.ssh/config`** | The path is filled in (`%USERPROFILE%\.ssh\config`). **Browse** · **Load** (or Enter) | `HostName` · `Port` · `User` · `ProxyJump` of each `Host`. `Include` is followed. Wildcard (`*`) Hosts are used only as defaults for other Hosts. For multi-hop ProxyJump only the first jump server is used. `IdentityFile` is not imported (its path goes into the note). Hosts go into the `SSH config` group |
| **mRemoteNG** | `confCons.xml` (default `%APPDATA%\mRemoteNG\confCons.xml`). **Browse** · **Load** | SSH and RDP connections. Folders become groups (`A / B`), and inherited user, port and protocol are resolved. A fully encrypted file must be re-exported without full-file encryption |
| **Ansible inventory** | An INI or YAML file, or paste the inventory text (pasted text takes priority) | Groups, `children`, ranges such as `web[01:10]`, `ansible_host` · `ansible_port` · `ansible_user`, and the jump host in `ansible_ssh_common_args` (`-J` / ProxyJump). When loaded from a file, `group_vars/` and `host_vars/` next to it are read too. `ansible_connection: winrm` hosts come in as RDP. Password variables, `!vault` and Jinja values are skipped with a warning |
| **Active Directory** | Domain and options, then **Import** ([9.2](#92-active-directory)) | Computer accounts: name, DNS name, OS, OU path as the group |

Sources that can't be read completely (unknown protocols, `Match` blocks, password columns and so on) are listed in the warnings panel of the preview.

### 9.2 Active Directory

Works with Active Directory and Microsoft Entra Domain Services (secure LDAP).

| Item | Behavior |
|---|---|
| Domain | For example `corp.example.com`. Filled in when this PC is joined to a domain |
| Domain controller (optional) | Found automatically when blank. `host:port` fixes the port |
| Search base OU (optional) | Limits the search to that OU |
| Sign-in | **Use current Windows sign-in** (on by default). Turn it off to enter `user@domain` and a password, or fill them from **From Vault** (used for this query only, not saved) |
| Connection | LDAPS 636 first, with normal certificate checks. If 636 can't be reached or TLS fails, LDAP 389 with signing and sealing. Never a plain-text bind. A failed sign-in is not retried (avoids account lockout) |
| Servers only (Windows Server · Linux) | On by default |
| Exclude disabled computers | On by default |
| Signed in within [N] days | On by default, 90 days (1–3650). Based on `lastLogonTimestamp`, which can lag by up to about 14 days |
| Convert to IP (DNS lookup) | Off by default. Stores the IP instead of the DNS name |

Windows computers come in as RDP and everything else as SSH. The OU path becomes the group (for example `Servers / Web`), and the description becomes the note. **Import** turns into **Cancel query** while it runs.

### 9.3 Preview

| Item | Behavior |
|---|---|
| Filters | **All** · **New host** · **Exists** · **Duplicates·errors** |
| Search | `Ctrl+F` focuses the search box (name, address, group, user, note). `Esc` clears it |
| Picking rows | Click the check box, or press `Space` on a row. **Select all** checks only the rows currently shown (after filter and search); **Clear selection** unchecks all |
| Status | New host · Exists · Duplicate · Error. Rows with the same address, port and type as an earlier row are duplicates and can't be checked |
| Warnings | The yellow `N warnings` bar expands or collapses the list (click, or `Enter` / `Space`) |

The bottom of the window shows how many new hosts will be added, how many existing hosts will be updated or skipped, and how many duplicates or errors are excluded.

### 9.4 Existing hosts

A row counts as an existing host when its **address, port and type** match a host you already have (for cloud hosts, the private and public IP are both compared). The name does not matter.

| Existing hosts | Behavior |
|---|---|
| **Skip** (default) | Existing hosts are left alone |
| **Update name·group** | Updates the name, group and username where the import has a value. Accounts, Vault and other settings are kept. Cloud-synced hosts are never changed |

### 9.5 Options and adding

| Option | Behavior |
|---|---|
| Group → **Prefix** (default) | Adds the text in front of the imported group (`Imported / web`). Leave it empty to keep the imported groups as they are |
| Group → **All into this group** | Ignores imported groups and puts every host in this group (empty = no group) |
| **SSH Vault account** · **RDP Vault account** | SSH hosts get the SSH account and RDP hosts the RDP account. With an account picked, the imported username is not stored. **Not set (default account)** follows the [default accounts](#73-default-accounts-and-group-assignment) |

Click **Add**. The result window shows `N added · N updated · N skipped` (and any jump hosts that couldn't be linked). **Show imported hosts** clears the search and selects them in the host list. Jump hosts from `~/.ssh/config` and Ansible are linked to the matching SSH host by name or address.

---

## 10. Claude panel

Click **Claude** in the title bar (or press `Ctrl+Shift+A`) to open the official Claude Code CLI on the right. Nubis starts a built-in MCP server (`127.0.0.1`, with a new token on every run) and connects it automatically, so Claude can work with Nubis directly.

### 10.1 First use

1. Install the Claude Code CLI: `npm install -g @anthropic-ai/claude-code`
2. When you first open the panel, Claude asks whether to trust the working folder (`%APPDATA%\Nubis\claude`). Choose **Yes**.
3. If you are not signed in, type `/login`.

### 10.2 What you can ask (examples)

| Goal | Say |
|---|---|
| Connect | "Connect to web-01" |
| Bring in servers | "Bring in all our servers" — Claude looks for cloud profiles, `~/.ssh/config`, mRemoteNG and the AD domain on this PC and suggests what to add |
| Import | "Import my ~/.ssh/config" · "Import the servers in AD domain corp.example.com, servers only" |
| Cloud | "Add all AWS profiles" · "Sign in to the prod profile" · "Re-sync the AWS profile" |
| Check several servers | "Check disk usage on the Production group" |
| Analyze the current screen | (while looking at a tab) "What is this error?" — the tab you are viewing is sent with every question |
| Read logs | "Show the end of /var/log/nginx/error.log on web-01" |
| Diagnose connection problems | "I can't connect to web-02, find out why" |
| Manage hosts | "Add 10.0.1.5 as web-02 in Production and link the ops account" · "Star db-replica" |
| Groups | "Rename the On-prem / Web group to Web" · "Set the default SSH account for PRODUCTION to ops" |
| Tabs | "Reconnect all disconnected tabs" |
| Remote files | "Create a folder /tmp/x on demo-sftp" |
| App settings | "Set the terminal theme to dracula and the font size to 16" · "Turn on copy-on-select" |
| Setup help | "Walk me through setting up GitHub sign-in" |

While Claude runs commands on servers, a **Running · Cancel** bar appears at the top of the panel. Cancelling returns immediately and skips servers that haven't started (commands already started may run to completion). With many servers the output per server is reduced, and long output keeps the beginning and **the end (where errors usually are)**. `capture_screen` switches back to the tab you were viewing after capturing.

### 10.3 Approval and logging

| Action | Approval |
|---|---|
| Lookups (host, group, tab, closed-tab, cloud-profile and Vault lists, reading the screen, terminal state (current folder, user), screen capture, connection diagnosis, reading settings, finding import sources), cloud re-sync | None |
| Showing a Vault account's public key (`vault_get_public_key`) or the stored host keys (`list_known_hosts`) | None (written to the audit log) |
| Screen and tab actions: opening, switching, renaming, reconnecting and reopening tabs (`open_session`, `select_session`, `rename_session`, `reconnect_session`, `reopen_closed_tab`), a new local PowerShell tab, showing the SFTP panel, collapsing groups, opening app screens (`open_app_page`), connecting up to 8 hosts at once (`connect_hosts`), starring a host, cancelling a cloud sync | None (written to the audit log) |
| Display and convenience settings (theme, font, copy-on-select, auto reconnect and so on) | None (written to the audit log) |
| Kubernetes: listing clusters (`list_k8s_clusters`, no credentials included) | None |
| Kubernetes: opening a cluster tab, a pod shell tab or a log tab (`open_k8s_session`) | None (written to the audit log) |
| Running `kubectl` on a cluster (`run_kubectl`) | Approval prompt (with read-only auto-approve on, only read-only commands such as `get`, `describe`, `logs` and `top` skip it; reading Secrets always asks and the prompt warns that the output may contain passwords or tokens) |
| Closing connected tabs (`close_session`, `close_sessions`) | Approval prompt (disconnected tabs close right away; closing several tabs takes one approval) |
| Connecting more than 8 hosts at once (`connect_hosts`) | Approval prompt |
| Reading remote files (`sftp_list`, `sftp_read`) | Approval prompt (skipped when read-only auto-approve is on; sensitive files such as private keys, `/etc/shadow` or `.env`, and reading with the tab's sudo rights, always ask) |
| Running commands on several servers (`run_command`) | Approval prompt (with read-only auto-approve on, only read-only commands skip it; SSH servers that look like Windows or whose OS is unknown always ask) |
| Typing into a terminal (`send_to_terminal`) | Approval prompt (with read-only auto-approve on, a one-line read-only command skips it while the screen shows a shell prompt — password prompts, vim, local PowerShell tabs and Windows or unknown-OS servers always ask) |
| Hosts and groups: adding, changing, duplicating or deleting hosts, bulk edits (`bulk_update_hosts`), group defaults (`set_group_defaults`), renaming a group (`rename_group`) | **Always an approval prompt** |
| Remote files: uploading, downloading, creating folders, renaming, deleting and saving (`sftp_upload`, `sftp_download`, `sftp_mkdir`, `sftp_rename`, `sftp_delete`, `sftp_write`) | **Always an approval prompt** (deletes show the item count; saving shows a summary of the changes) |
| Cloud: adding, changing or removing profiles, cloud CLI sign-in | **Always an approval prompt** |
| Vault and known hosts: setting or clearing the default Vault account, deleting a Vault account, deleting a stored host key | **Always an approval prompt** (the prompt lists what is affected) |
| Adding imported hosts | **Always an approval prompt** (previewed first and added with one approval) |
| Querying Active Directory for an import with a Vault account, another domain or a named domain controller | Approval prompt before the query |
| Changing security settings (RDP certificate warning, host-key prompt, auto-approve), or a default-port change that moves cloud hosts | **Approval prompt.** Claude can never turn auto-approve on, only off |
| Passwords, keys and key passphrases; sign-in screen settings (owner only); the SFTP editor path; cloud credential paths; turning read-only auto-approve on | **Claude can never do these.** For passwords, keys and sign-in, Claude only opens the right dialog or page (`open_app_page`) and you fill it in yourself |

The approval prompt shows the target servers and the full command. It flags risky commands (`rm -rf`, `reboot`, `Stop-Service` and so on), and the run button stays locked for 1.5 seconds after the prompt appears. Only one prompt is shown at a time, and if Claude cancels the request the prompt closes as rejected. Esc rejects. Every request is written to the audit log (the document icon in the panel header).

### 10.4 What Claude cannot do

- Read or decrypt saved passwords and keys. The account files (`hosts.json`, `vault.json` and others) are blocked from Claude's read tools and shell commands.
- Put a password or key on a host, a Vault account or a cloud profile. Accounts are attached only by linking a Vault account, and imports never carry passwords. When a secret is needed, Claude opens the dialog and you type it in.
- Upload the Nubis data folder, `~/.ssh` or private key files to a server, or import from them (`~/.ssh/config` excepted).
- Change the sign-in screen settings, the SFTP editor path or cloud credential paths, turn on read-only auto-approve, or edit `settings.json` directly.
- Delete a host key you rejected.

### 10.5 MCP tools

Claude has 61 tools. Anything you can do by hand in the app, Claude can do too. The exceptions are secrets (passwords, keys) and a few protected settings; see 10.3 and 10.4.

**Hosts and groups**

| Tool | Purpose |
|---|---|
| `list_groups` · `list_hosts` | Search groups and hosts |
| `add_host` · `update_host` · `delete_host` | Add, change (name, tags, address, account and so on) and delete hosts (starring a host needs no approval) |
| `duplicate_host` | Copy a host as a new one, account settings included |
| `bulk_update_hosts` | Change several hosts at once, with the same rules as bulk edit |
| `set_group_defaults` | A group's SSH/RDP Vault account and public-IP choice, like the group right-click menu |
| `rename_group` | Rename a group you created (merges into a group of the same name; cloud groups are renamed in the profile) |
| `set_group_collapsed` | Collapse or expand groups in the host list |
| `connect_hosts` | Open tabs to several hosts at once, like **Connect all** |

**Sessions and tabs**

| Tool | Purpose |
|---|---|
| `list_sessions` · `open_session` · `close_session` | List, open and close tabs |
| `select_session` | Show a tab, or the host list (home) |
| `rename_session` | Rename a tab |
| `reconnect_session` | Reconnect one tab or all disconnected tabs |
| `close_sessions` | Close other tabs, tabs to the right, or all disconnected tabs |
| `list_closed_tabs` · `reopen_closed_tab` | Closed-tab history and reopening (`Ctrl+Shift+R`) |
| `open_local_terminal` | Open a local PowerShell tab |
| `toggle_sftp_panel` | Show or hide an SSH tab's SFTP panel |

**Terminal**

| Tool | Purpose |
|---|---|
| `read_terminal` · `get_selection` · `capture_screen` | Terminal content, selected text, screen image |
| `get_terminal_info` | The terminal's current folder, sign-in account and sudo switch (answers instantly, no command sent) |
| `wait_for_output` | Wait until some text appears, the prompt returns or output goes quiet |
| `send_to_terminal` | Type into a terminal |

**Remote commands**

| Tool | Purpose |
|---|---|
| `run_command` | Run on several servers in parallel (SSH / WinRM) |
| `test_connection` | DNS, port, SSH-banner and WinRM diagnosis (does not sign in) |

**SFTP**

| Tool | Purpose |
|---|---|
| `sftp_list` · `sftp_read` | Browse folders and read text files |
| `sftp_download` · `sftp_upload` | Transfer files and folders (downloads go to `Downloads\Nubis\<host>`) |
| `sftp_mkdir` · `sftp_rename` · `sftp_delete` | Create a folder; rename or delete a file, folder or symbolic link (only the link itself, never its target) |
| `sftp_write` | Save a text file (refused if the file changed on the server after Claude read it) |

**Cloud**

| Tool | Purpose |
|---|---|
| `discover_sources` | Find what can be imported: cloud profiles on this PC (and whether sign-in is needed), `~/.ssh/config`, mRemoteNG and the AD domain |
| `cloud_list_profiles` | Cloud profiles and their settings (a default password only shows as set or unset) |
| `cloud_add_profile` · `cloud_login` | Add cloud profiles (synced right away), run a CLI sign-in in a new console window |
| `cloud_update_profile` · `cloud_remove_profile` | Change profile settings (ports, public IP, stopped servers, default Vault accounts); remove a profile and its hosts from Nubis (the cloud servers are untouched) |
| `cloud_sync` · `cloud_sync_cancel` | Re-sync, cancel a running sync |

**Vault and known hosts**

| Tool | Purpose |
|---|---|
| `list_vault` | Vault accounts (never passwords or keys) |
| `vault_set_default` | Set or clear the default SSH/RDP account in Settings |
| `vault_get_public_key` | The public-key line of an SSH-key account, for `authorized_keys` |
| `vault_delete` | Delete a Vault account (hosts, profiles and settings that use it are unlinked) |
| `list_known_hosts` · `delete_known_host` | Stored host keys; delete one after a server is reinstalled (rejected keys cannot be deleted) |

**Settings and app screens**

| Tool | Purpose |
|---|---|
| `get_settings` · `update_settings` | App settings |
| `get_setup_status` · `open_app_page` | Setup status; open screens and dialogs, including the Vault, host and cloud-profile dialogs where you type passwords yourself |
| `diagnostics` | App memory and handles |

**Import**

| Tool | Purpose |
|---|---|
| `import_hosts` | Import from Excel/CSV, `~/.ssh/config`, mRemoteNG, Ansible or AD — preview first, then add with one approval |

**Kubernetes**

| Tool | Purpose |
|---|---|
| `list_k8s_clusters` | Registered clusters (never credentials), and whether `kubectl` was found on this PC |
| `run_kubectl` | Run `kubectl` on a named cluster with this PC's `kubectl`, no shell in between. The cluster and credentials are fixed to the saved ones (`--kubeconfig`, `--context`, `--server`, `--token`, `--as` and similar are refused). Interactive or never-ending commands (`exec -it`, `logs -f`, `-w`, `port-forward`, `attach`, `edit`, `proxy`) are refused — use `open_k8s_session` instead |
| `open_k8s_session` | Open a cluster tab, a pod shell tab or a pod log tab ([16.6](#166-cluster-tab)) |

### 10.6 Running commands on several servers (SSH / WinRM)

`run_command` runs a command on several servers at once: over SSH for Linux servers and over WinRM (PowerShell · cmd) for Windows servers. You can target a whole group, such as `group:Production`, and stopped servers are skipped automatically.

| Item | Behavior |
|---|---|
| Address order | Private IP → public IP. SSH: private → public. WinRM: private 5986 → 5985 → public 5986 → 5985 |
| Retries | Moves to the next address or port **only when the connection fails**. An authentication failure stops immediately to avoid account lockouts, and errors after the shell is created are not retried so a command never runs twice |
| OTP / MFA servers | If an SSH tab to that server is open and signed in, the command runs over that tab's connection with no new sign-in. The approval prompt says so, and warns if the tab signed in with a different account than the host's. Without an open tab, Claude stops and asks you to open one (or opens it with `open_session`) so you can enter the OTP |
| WinRM 5986 | HTTPS + NTLM/Negotiate (self-signed certificates allowed) |
| WinRM 5985 | HTTP + NTLM message encryption (works with the default server policy `AllowUnencrypted=false`). No TrustedHosts setup is needed on this PC |
| Account | The same host or cloud-profile account as RDP (`DOMAIN\user`, `user@domain`, local accounts) |
| Server setup (Windows) | A WinRM listener (`winrm quickconfig`, plus a certificate binding for HTTPS) and firewall rules for 5986/5985 |

> **Note** WinRM has not yet been fully verified against real servers. If you run into problems, please open an issue.

**What read-only auto-approve covers** — when auto-approve is on in [Settings → Claude](#1212-claude), only read-only commands like the ones below run without an approval prompt. It is an allow list: anything not on it, or anything ambiguous, brings the prompt back.

| Kind | Runs without approval (examples) | Asks for approval (examples) |
|---|---|---|
| Linux | `ls` `cat` `grep` `df` `ps` `ss` `journalctl` `systemctl status` `docker ps` `kubectl get` | `systemctl restart`, `find -delete`, file redirection `>`, command substitution `$( )` or backticks, reading Secrets (`kubectl get secret -o yaml`), `kubectl config view --raw` |
| PowerShell | `Get-*`, `Select-/Where-/Sort-/Format-*`, `Test-*` | `Invoke-*`, method or .NET calls, unknown commands |
| cmd · common | `ipconfig` `sc query` `reg query` `netstat` `tasklist` | Write options such as `ipconfig /release` |

Dangerous commands always ask, whatever the setting, and commands that run automatically are still written to the audit log as "auto-approved (read-only)".

---

## 11. Sign-in screen and per-account profiles

### 11.1 Setup (GitHub example)

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

### 11.2 How sign-in works

| Item | Behavior |
|---|---|
| Method | OAuth Device Flow: approve the on-screen code in your browser. You type your password only in the browser; Nubis never receives it |
| Token | Used only to identify the account and never stored |
| Allowed | Account names (case-insensitive), `@company.com` (a whole domain), GitHub Organization members |
| Stay signed in | In hours. 0 = sign in on every start (maximum 720) |
| Offline grace | In days. When the sign-in server cannot be reached, you can get in if your last sign-in is within this period (0 = blocked, maximum 30) |
| Records | The last sign-in is stored encrypted in `login.json`. Successful and failed sign-ins go to the audit log |

### 11.3 Per-account profiles

With sign-in turned on, each account keeps its own data in `%APPDATA%\Nubis\users\<provider>_<account ID>\`.

| Stored per account | Shared |
|---|---|
| Hosts, Vault, cloud profiles, known_hosts, Claude working folder, audit log, display settings (theme, font, SFTP and so on) | Language, sign-in screen settings, last sign-in record |

- When a different account signs in, it starts empty, and no account can see another's data.
- The account ID never changes (GitHub user number, Google `sub`, Microsoft `oid`), so renaming your account still opens the same profile.
- **Owner** = the account that set up sign-in. Data from before sign-in was turned on is handed over to the owner on their first sign-in. Only the owner can change the sign-in settings. If you turn sign-in off, Nubis keeps using the owner's profile.

### 11.4 Signing out, turning sign-in off, or getting locked out

- To sign out: Settings → Sign-in screen → **Sign out (sign in on next launch)** (shown while sign-in is on; any account can use it). Nubis deletes the sign-in record, closes all tabs and restarts. The next start needs an online sign-in, and offline grace is unavailable until you sign in again.
- To turn it off: Settings → Sign-in screen → **Off** → **Save (turn off sign-in)** (owner only).
- If a wrong setting keeps you out, open `%APPDATA%\Nubis\settings.json` in Notepad, change `"Provider"` inside `"Login"` to `"None"`, and save.

---

## 12. Settings reference

Settings are saved as soon as you change them. The buttons at the top of the page jump to a section. The **Claude** column shows whether the Claude panel can change the setting with `update_settings`.

### 12.1 Language

| Setting | Default | Behavior | Claude |
|---|---|---|---|
| Language | Chosen on first run | 한국어 / English. Takes effect after a restart (Nubis asks whether to restart). The Claude panel answers in the same language | No |
| Getting started → **Show the getting-started guide** | — | Opens the getting-started window again | — |

### 12.2 Default Vault account

| Setting | Default | Behavior | Claude |
|---|---|---|---|
| SSH (Linux) · RDP (Windows) | Not set | **Select** / **Clear**. Used by every host without its own account ([7.3](#73-default-accounts-and-group-assignment)) | No |

### 12.3 Host list and tabs

Section name in the app: **Host list · Tabs**.

| Setting | Default | Behavior | Claude |
|---|---|---|---|
| Show Recent group | On | Hosts connected in the last 30 days at the top (favorites are always at the top) | No |
| Confirm before closing a connected tab | On | Prevents accidental closing with X, middle-click or `Ctrl+Shift+W`. Tabs with files still being edited over SFTP always ask | No |

### 12.4 Cloud auto sync

| Setting | Default | Behavior | Claude |
|---|---|---|---|
| Sync all when the app starts | Off | Syncs every cloud profile at startup | No |
| Sync periodically (minutes, 0 = off, min 5) | 0 | 5–1440 minutes. If some regions fail, nothing is removed in that run | No |

### 12.5 Terminal theme and font

| Setting | Default | Behavior | Claude |
|---|---|---|---|
| Terminal theme | Remote Dark | 9 dark and 2 light themes. Applies to open terminals immediately | Yes |
| Auto-adjust text contrast | On | Adjusts text colors that are too close to the background so they stay readable | Yes |
| Terminal font | Cascadia Mono | Shows the installed ones of Cascadia Mono/Code, D2Coding, JetBrains Mono, Fira Code, NanumGothicCoding, Consolas and Lucida Console. **Other font...** takes the name of any installed font | Yes (installed fonts only) |
| Font size | 14 | 9–28. `Ctrl+wheel` · `Ctrl+=` · `Ctrl+-` change one tab only (`Ctrl+0` resets) | Yes |

### 12.6 Terminal

Section name in the app: **Terminal** (the third terminal section). See [4](#4-ssh-terminal) for how the copy styles behave.

| Setting | Default | Behavior | Claude |
|---|---|---|---|
| Ctrl+C to copy · Ctrl+V to paste | On | Ctrl+C copies only when text is selected (otherwise it interrupts the running command). Same as Windows Terminal | No |
| Copy on select · right-click pastes | Off | Selecting text with the mouse copies it right away, and right-click always pastes. Same as PuTTY · mRemoteNG | No |
| Confirm multi-line paste | On | Prevents each pasted line from running immediately (right-click paste included) | No |

### 12.7 Syntax highlighting and performance

| Setting | Default | Behavior | Claude |
|---|---|---|---|
| Syntax highlighting: IP addresses · Numbers · Status keywords | All on | Adds color to output (status keywords: error·failed·denied red, warning·timeout yellow, ok·success·running green). Skips text the server already colored and full-screen programs such as vim and htop | Yes |
| Performance → Terminal GPU acceleration | Off | Turn on only for frequent heavy output; off saves about 70 MB of memory. Takes effect after a restart | Yes |

### 12.8 SSH

| Setting | Default | Behavior | Claude |
|---|---|---|---|
| Reconnect automatically when disconnected | Off | When the connection drops with an error: up to 5 tries at 2·4·8·16·32 s. Authentication failures, a clean exit such as `exit`, and rejected host keys are not retried | No |
| Confirm host key on first connection | Off | When on, the key of a server you have not seen before is shown in a confirmation prompt. When off, it is saved and you connect. **A changed key always triggers a warning, whatever this setting** | With approval |

### 12.9 SFTP

| Setting | Default | Behavior | Claude |
|---|---|---|---|
| Open SFTP panel automatically on SSH connect | On | Attaches the SFTP panel on the left after a successful sign-in | Yes |
| Follow terminal folder | On | Moves the panel to the terminal's current folder (read from the prompt, or OSC 7) | Yes |
| Open the folder in Explorer after downloading | On | Turn off to stop opening the folder after downloads | No |
| File editor | Windows default program | The program that opens files you double-click. **VS Code** finds Code.exe in one click; **Browse** picks any program | No |

### 12.10 Default ports

| Setting | Default | Behavior | Claude |
|---|---|---|---|
| SSH · RDP | 22 · 3389 | The port for new hosts and cloud-synced hosts. If cloud hosts still use the old default, Nubis asks: **Change them too** or **Change default only** (ports set per host or per profile are kept) | Yes (with approval when it moves cloud hosts) |

### 12.11 Sign-in screen

See [chapter 11](#11-sign-in-screen-and-per-account-profiles). Settings are saved only after a successful test sign-in, only the owner can change them, and Claude cannot change them. **Sign out (sign in on next launch)** is here too.

### 12.12 Claude

| Setting | Default | Behavior | Claude |
|---|---|---|---|
| Run read-only commands without approval | Off | Skips the approval prompt only for read-only requests in `run_command`, `send_to_terminal` (at a shell prompt), `sftp_list` and `sftp_read` (an allow list; anything ambiguous still asks). The audit log is still written | Off only |

### 12.13 RDP

| Setting | Default | Behavior | Claude |
|---|---|---|---|
| Show server certificate warnings | On | When off, Nubis connects without a warning even to servers whose certificate cannot be verified (self-signed). Applies from the next connection | With approval |

### 12.14 Cloud credential file paths

These are not on the Settings page. Set them with **Change path** in each section of the Cloud page (the standard location is the default). They are applied as `AWS_CONFIG_FILE` for AWS, `GOOGLE_APPLICATION_CREDENTIALS` for GCP, `AZURE_CONFIG_DIR` for Azure and `OCI_CLI_CONFIG_FILE` for OCI, so the CLI started by the Sign in button uses the same path. Claude cannot change them.

### 12.15 kubectl path

Not on the Settings page either. The Kubernetes page shows where `kubectl` was found; if it is not found, copy the install command (`winget install Kubernetes.kubectl`) or pick `kubectl.exe` with **Set path...** ([16.1](#161-requirements)). Claude cannot change it.

---

## 13. Data files and security

### 13.1 Data locations

| Location | Contents |
|---|---|
| `%APPDATA%\Nubis\settings.json` | Language and sign-in screen settings (all settings when sign-in is not used). Plain text |
| `%APPDATA%\Nubis\login.json` | Last sign-in record (encrypted) |
| `%APPDATA%\Nubis\users\<account>\` | Per-account profiles (when sign-in is used) |
| `hosts.json` · `vault.json` · `cloud_profiles.json` · `known_hosts.json` | Hosts, Vault, cloud profiles, host keys (**whole file encrypted**) |
| `k8s_clusters.json` | Kubernetes clusters (**whole file encrypted**; each cluster's kubeconfig is encrypted once more inside) |
| `k8s-run\` | Decrypted kubeconfigs, only while a cluster tab or `kubectl` is running (readable only by your Windows account, deleted when closed) |
| `claude\` · `claude_audit.log` | Claude panel working folder and audit log |
| `%LOCALAPPDATA%\Nubis\WebView2\` | Terminal view cache |

Set the `NUBIS_DATA_DIR` environment variable to use a different folder (for portable use or testing).

### 13.2 Known Hosts

The **Known Hosts** sidebar page lists the host-key fingerprints of SSH servers you trust. If you reinstalled a server and its key changed, delete it here and connect again (Nubis asks first, because the key seen on the next connection is trusted as new). Keys you rejected stay marked `REJECTED` and are never trusted automatically.

### 13.3 Security model

| Protection | Details |
|---|---|
| Encryption at rest | Whole data files are encrypted with Windows DPAPI (current user, AES-256). Server names, IPs and account names are hidden too, and the files open only on this PC under this Windows account. Passwords and keys are encrypted once more inside |
| Copying to another PC | Cannot be decrypted. The original is kept as `*.corrupt-<date>` and Nubis starts empty |
| Sign-in screen | Only allowed accounts can open the app, and each account's data is kept separate |
| Claude panel | Account-file reads, DPAPI decryption commands and settings-file edits are blocked; remote changes require approval and are audit-logged |
| Import | Passwords and keys are never imported from any source |
| Host keys | A changed key always triggers a warning |
| Kubernetes | Each cluster's credentials are encrypted separately, and decrypted only into a temporary file while in use ([16.7](#167-security)) |

**Limits** — the sign-in screen and per-account profiles are a door into the app and a separation inside the app's screens. Encryption works per Windows account, so it cannot stop another program running under the same Windows account from decrypting the files directly. If several people share one PC, give each of them their own Windows account.

### 13.4 Backup and moving

- Same PC, same Windows account: copy the whole `%APPDATA%\Nubis` folder.
- Another PC: encrypted files cannot be decrypted there. On the new PC, add your cloud profiles again and sync, and re-enter your Vault accounts.

---

## 14. Keyboard shortcuts

**Anywhere** (these also work while a terminal has focus unless noted)

| Shortcut | Action |
|---|---|
| `Ctrl+K` (`Ctrl+Shift+K` in a terminal) | Command palette — hosts, open tabs and actions |
| `Ctrl+F` | Host search box (not in a terminal, where it goes to the shell) |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | Next / previous tab (past either end: Hosts) |
| `Ctrl+1~8` · `Ctrl+9` | That tab · last tab |
| `Ctrl+Shift+W` · middle-click (tab) | Close the tab (asks if connected) |
| `Ctrl+Shift+R` | Reopen the last closed tab (up to 10 remembered) |
| `Ctrl+Shift+T` | New local PowerShell tab |
| `Ctrl+Shift+A` | Open or close the Claude panel |

**Command palette**

| Key | Action |
|---|---|
| Type | Space-separated words must all match. With nothing typed: recent hosts, open tabs, actions, favorites |
| `↑` `↓` · `PageUp` `PageDown` | Move |
| `Enter` | Connect to the host · switch to the tab · run the action |
| `Shift+Enter` (or `Alt+Enter`) | Connect via public IP |
| `Ctrl+Enter` | Edit the host |
| `Esc` | Close |

Actions: New host · Import hosts · New PowerShell tab · Reopen closed tab · Open/Close Claude panel · Sync all clouds · Cloud / Kubernetes / Vault / Known Hosts / Settings page. Kubernetes clusters are listed too; `Enter` opens the cluster tab.

**Host list**

| Shortcut | Action |
|---|---|
| `↑` `↓` · `Tab` / `Shift+Tab` (search box) | Move the outline one row · one card |
| `Enter` · `F2` | Connect · edit the outlined host |
| `Esc` (search box) | Clear the search (when it is empty, clear the host selection) |
| `Ctrl+N` | New host |
| `Ctrl+click` · `Shift+click` (card) · `Ctrl+A` | Select several hosts · a range · every visible host → bulk edit |
| `Delete` | Delete the selected hosts (after confirming) |
| `Ctrl+S` · `Esc` | Save · close the host editor |

**Terminal**

| Shortcut | Action |
|---|---|
| `Ctrl+C` / `Ctrl+V` | Copy (when selected) / paste — Windows Terminal style, on by default |
| `Ctrl+Shift+C` / `Ctrl+Shift+V` · `Shift+Insert` | Copy / paste (always) |
| Right-click | Copy if selected, otherwise paste. With **Copy on select** on: selecting copies, right-click always pastes |
| `Ctrl+Shift+F` | Find in output → `Enter` · `Shift+Enter` · `Esc` |
| `Ctrl+wheel` · `Ctrl+=` · `Ctrl+-` · `Ctrl+0` | Font size (this tab only) |
| `Enter` (disconnected tab) | Reconnect |

**SFTP panel**

| Shortcut | Action |
|---|---|
| `Enter` | Open a folder · edit a file (path box: go to the typed path) |
| `Esc` (path box) | Put back the current folder |
| `Backspace` · `F5` | Up · refresh |
| `F2` · `Delete` | Rename · delete |
| `Ctrl+H` | Show or hide hidden files |

**Import window** — `Ctrl+F` search, `Space` check or uncheck a row ([9.3](#93-preview)).

**Approval prompt** — `Esc` rejects.

**Tab right-click menu** — Reconnect · New tab to same host · New PowerShell tab (local tabs) · Full screen (RDP) · Rename tab... · Edit Host · Close · Close other tabs · Close tabs to the right · Reconnect all disconnected tabs · Close all disconnected tabs · Reopen closed tab. Drag tabs to reorder them; when they overflow, scroll with the wheel or use ▾ (**Show all tabs**) for the full list.

---

## 15. Troubleshooting

| Symptom | What to check |
|---|---|
| The host list is empty | Whether you signed in with a different account (per-account profiles; the sign-in section of Settings shows the current profile). Whether you ran an older version |
| `Ctrl+K` no longer jumps to the search box | `Ctrl+K` now opens the command palette, where you can search hosts too. `Ctrl+F` goes to the host search box |
| "Could not read the settings file" | The file was copied from another PC or is damaged. The original is kept as `*.corrupt-<date>` |
| Warning that the host key changed | If you reinstalled the server, delete the key in Known Hosts and connect again. If not, it may be a man-in-the-middle attack — do not connect |
| The SFTP panel does not follow the terminal folder | Customized prompts that show neither the path nor `[user@host folder]` cannot be read — enable OSC 7 ([5.1](#51-browsing)). Use the terminal icon button in the panel |
| Files starting with `.` are missing in SFTP | Hidden files were turned off — press `Ctrl+H` in the panel |
| An edited file is not uploaded | Check the status line at the bottom of the panel. You may have chosen "No" because the server file changed, or you may lack permission. Save again to retry |
| An import finds fewer servers than expected | Open the warnings panel in the preview. Check the filters, and for AD the options (servers only, disabled computers, recent sign-in days) |
| Claude's `run_command` says the server needs additional authentication | The server asks for an OTP. Open an SSH tab to it, finish signing in, and ask again — the command then runs over that tab |
| Claude panel says "claude not found" | Run `npm install -g @anthropic-ai/claude-code`, then press Enter in the panel (it looks again) |
| Claude looks at the wrong tab | The tab you are viewing is sent with each question. Switch tabs, then send a new message |
| Cloud sync fails | Sign in again with that CLI (the section's Sign in button) and check the credential file path. Ask Claude "cloud sync isn't working" and it will check the status |
| A server deleted in the cloud is still listed | It is removed after it is missing from two complete syncs in a row ([8.3](#83-sync-rules)) |
| Sign-in screen says no OAuth app matches the Client ID | Check the Client ID for typos and that Enable Device Flow is ticked in the GitHub app |
| A wrong sign-in setting locks you out | See [11.4](#114-signing-out-turning-sign-in-off-or-getting-locked-out) |
| Certificate warning right after an RDP connection | In environments with self-signed certificates, turn off the certificate warning in Settings → RDP |

---

## 16. Kubernetes

The **Kubernetes** page in the sidebar (between Cloud and Vault) keeps your Kubernetes clusters. Each cluster's credentials (kubeconfig) are stored separately: Nubis cuts the kubeconfig down to a standalone one that contains only that cluster, encrypts it with Windows DPAPI and saves it in `k8s_clusters.json` (the whole file is encrypted as well). One cluster's token never mixes with another's.

### 16.1 Requirements

| Item | Details |
|---|---|
| kubectl | Required. The page shows the path where `kubectl` was found. If it is not found, **Copy install command** copies `winget install Kubernetes.kubectl`, or pick `kubectl.exe` with **Set path...** |
| EKS (AWS) | AWS CLI (`winget install Amazon.AWSCLI`) |
| GKE (GCP) | `gke-gcloud-auth-plugin` (`gcloud components install gke-gcloud-auth-plugin`) |
| AKS (Azure) | `kubelogin` (`winget install Microsoft.Azure.Kubelogin`) for clusters that use Microsoft Entra (AAD) sign-in. Clusters with local accounts need nothing more |
| OKE (OCI) | OCI CLI (`pip install oci-cli`) |
| TKE (Tencent) | Nothing more |

If a cluster needs a command that is not on this PC, its row shows a warning badge with install instructions.

### 16.2 Importing a kubeconfig

**Import kubeconfig** reads contexts from one of these:

- `~/.kube/config` — or the first file in the `KUBECONFIG` environment variable when it is set
- **File...** — a kubeconfig file you choose
- **Paste** — kubeconfig YAML pasted into the box

Pick the contexts to import (optionally with a group). Each selected context is saved separately with only that cluster's credentials. Certificates and tokens that the kubeconfig points to by file path are read and stored inline, so the cluster keeps working if those files move; a pasted kubeconfig that points to files must be imported from a file instead. The original file is never changed. Contexts you have already imported are marked **Exists**.

### 16.3 Finding clusters in the cloud

**Find in cloud** looks for clusters with the cloud profiles you added on the Cloud page ([8](#8-importing-cloud-servers)), using read-only lookup APIs. It first opens a list of your profiles so you choose which ones to search:

- Each row shows the provider, the profile, whether cluster sync is on and how many clusters it has. Profiles that already have cluster sync on are checked; the first time (none on yet) every profile is checked, since you don't know yet which accounts have clusters. **Select all** / **Clear all** are above the list.
- You never pick a region: every region where the account can have clusters is checked automatically (AWS and Tencent: all available regions; GCP: all locations of the project; Azure: the whole subscription; OCI: all subscribed regions and compartments).
- Checked profiles are searched and get cluster sync turned on, so their clusters update with every sync from then on. Unchecked profiles get cluster sync turned off; their existing cluster cards stay unless you tick **Remove the clusters of unselected profiles from the list** — then you are asked to confirm with the number of clusters before they are removed (only from Nubis; the clusters themselves are not affected).

| Cloud | Clusters | Authentication |
|---|---|---|
| AWS | EKS | exec plugin `aws eks get-token`, with `AWS_PROFILE` set to the profile |
| GCP | GKE | exec plugin `gke-gcloud-auth-plugin` |
| Azure | AKS | Microsoft Entra (AAD) clusters: exec plugin `kubelogin` (Azure CLI sign-in). Local-account clusters: the kubeconfig Azure returns (certificates and token included) is stored encrypted |
| OCI | OKE | exec plugin `oci ce cluster generate-token` with `--profile` (and `--auth security_token` for session profiles) |
| Tencent | TKE | The kubeconfig Tencent Cloud returns (certificates and token included) is stored encrypted |

If a cluster you imported yourself has the same API server address, no separate cloud entry is created for it.

### 16.4 Sync rules

Profiles picked in **Find in cloud** update their clusters together with that profile's server sync from then on (on demand, at startup or periodically — [12.4](#124-cloud-auto-sync)).

| Item | Behavior |
|---|---|
| Partial failure | If some lookups fail, **nothing is removed in that sync** |
| Permission denied | Clusters in regions denied by permissions are kept |
| Removed clusters | A cluster is removed only after it is missing from **two syncs in a row** |
| Your changes | A name or default namespace you changed is kept |

### 16.5 The Kubernetes page

Clusters are grouped as **Added manually** and one group per cloud profile (`[AWS] <profile>` and so on). Each row shows an authentication badge (`kubeconfig` · `aws` · `gcloud` · `azure` · `oci` · `exec`), the server version and, when something is missing, a warning badge.

| Action | Behavior |
|---|---|
| Open (double-click · `Enter`) | Opens the cluster tab ([16.6](#166-cluster-tab)) |
| Test connection | Shows the server version and checks `kubectl auth can-i list pods` |
| More → Rename (`F2`) | Renames the cluster in Nubis |
| More → Default namespace | The namespace the tab starts in (empty = the kubeconfig's value, or `default`) |
| More → Group | Moves the cluster to a group |
| More → Export kubeconfig... | Saves this cluster's kubeconfig to a file. The file is **not encrypted** (it holds tokens and client keys), and Nubis warns you before saving |
| More → Delete (`Delete`) | Removes the cluster and its saved credentials from Nubis (after confirming). The cluster itself is not affected. A cluster found in the cloud may come back on the next sync |

### 16.6 Cluster tab

Opening a cluster opens a tab with two parts.

- **Left: resource panel** — **Pods** · **Deploy** · **Svc** · **Nodes**, a namespace picker (including **All namespaces**), **Find by name**, auto refresh every 5 seconds (click to turn it off or on) and `F5` to refresh.
- **Right: kubectl terminal** — a PowerShell whose `KUBECONFIG` is this cluster, so `kubectl`, `k9s` and `helm` all point at it.

| Resource | Actions |
|---|---|
| Pod | Double-click or `Enter`: shell tab (`kubectl exec -it`, bash or sh when bash is missing). Right-click: **Open shell** · **View logs** (`logs -f --tail=500`; a submenu when the pod has several containers) · **Previous container logs** · describe · **View YAML** · **Copy name** · **Delete** (after confirming; a pod managed by a controller is created again) |
| Deployment | describe · **View YAML** · **Restart** (`rollout restart`, after confirming) |
| Service · Node | describe · **View YAML** |

describe and YAML run in the terminal on the right. Closing a shell or log tab ends `kubectl` and its child processes. A closed cluster tab can be reopened with `Ctrl+Shift+R`, and clusters and the **Kubernetes page** also appear in the command palette (`Ctrl+K`).

### 16.7 Security

| Protection | Details |
|---|---|
| Storage | Each cluster's kubeconfig is encrypted separately with DPAPI, inside `k8s_clusters.json`, which is encrypted as a whole |
| While in use | The decrypted kubeconfig is a temporary file under `k8s-run\` in the data folder, readable only by your Windows account, and exists only while a cluster tab is open or `kubectl` is running. It is deleted when they close; files left behind by a crash are cleaned up at the next start |
| Running kubectl | Nubis starts `kubectl` directly, without a shell. Claude's `run_kubectl` refuses `--kubeconfig`, `--context`, `--cluster`, `--user`, `-s`/`--server`, `--token`, `--as` and similar options, so saved credentials cannot be sent to another server or swapped for another kubeconfig |
| Claude panel | Reading `k8s_clusters.json` and `k8s-run` is blocked by the deny rules (a second safety net). `run_kubectl` needs approval and is audit-logged ([10.3](#103-approval-and-logging)) |
| Export | An exported kubeconfig is a plain, unencrypted file — keep it somewhere safe |
