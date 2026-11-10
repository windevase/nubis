<img src="nubis.png" width="96" align="right"/>

**English** | [한국어](README.ko.md)

# Nubis

**Cloud & AI Remote Client** — a Windows remote access client that gathers your cloud servers into per-profile groups, connects over SSH / RDP / SFTP, and lets Claude Code work across many servers at once.

> Name: Latin *nubis* = "of the cloud" + Korean *nubida* ("to roam through") — roaming the servers of many clouds.

> **Note:** the app UI is currently in Korean only. This document describes every screen and option in English.

## Highlights

- **Just ask the built-in Claude** — the Claude panel on the right runs the official Claude Code CLI and drives Nubis through a built-in MCP server
  - Servers: "connect to web-01", "check disk usage on the Production group", "show the tail of the nginx error log", "I can't connect — find out why"
  - Management: "add 10.0.1.5 as web-02 in Production and link the ops account", "re-sync the AWS profile"
  - App settings: "switch the terminal theme to dracula, font size 16", "turn off SFTP auto-open"
  - The tab you are looking at is attached to every question automatically, so "what is this error?" is enough (RDP screens are captured and viewed as images)
  - Commands that change servers, host edits, file transfers and security settings **always go through an in-app approval dialog** and are written to an audit log. Claude cannot read saved passwords or keys
- **Import cloud servers in one go** — sync servers from AWS · Tencent · GCP · Azure into per-profile groups using the CLI logins already on this PC
- **SSH · RDP · SFTP in one window** — tabs, private/public IP selection, run a command on many servers at once (SSH / WinRM)
- **SFTP sidebar** — attaches automatically on SSH login, drag and drop to and from Explorer, open a file in your editor (VS Code etc.) and it is **uploaded back the moment you save**, follows the terminal's current folder
- **Vault** — store shared accounts (username + password / SSH key) once and pick them per host
- **Local data encryption · sign-in screen** — server lists and account files are fully encrypted with Windows DPAPI; optional sign-in with a GitHub · Google · Microsoft account

## Architecture

| Area | Implementation |
|---|---|
| UI | WPF (.NET 10), dark theme, custom title-bar tabs |
| SSH | [SSH.NET](https://github.com/sshnet/SSH.NET) + [xterm.js](https://xtermjs.org/) on WebView2 |
| RDP | Windows built-in RDP ActiveX (`mstscax.dll`, the same engine as mstsc) embedded via `WindowsFormsHost` |
| Storage | `hosts.json` · `cloud_profiles.json` · `known_hosts.json` · `vault.json` under `%APPDATA%\Nubis\` are **encrypted as whole files with Windows DPAPI (current user, AES-256)** — server names, IPs and account names are not visible, and the files only open on this PC under this Windows account. Passwords and keys are DPAPI-encrypted once more inside. Old plaintext files are encrypted automatically the first time they are read |
| Sign-in screen | Optional: the app opens only after signing in with a GitHub · Google · Microsoft (Entra ID) account (Device Flow, allowed accounts · GitHub Organization). See below |
| Vault | `%APPDATA%\Nubis\vault.json`. Passwords and SSH private keys (+ passphrase) shared by several hosts, encrypted with DPAPI |
| Host keys | `%APPDATA%\Nubis\known_hosts.json` (SHA256 fingerprints). First connection is saved without a prompt (a confirmation dialog can be enabled in Settings); a changed key always warns |
| SFTP | SSH.NET SftpClient (a separate connection with the same account as the terminal) |
| Legacy data | On first run `%APPDATA%\RemoteClient` is migrated to `%APPDATA%\Nubis` (copies only missing files if it cannot be moved) |
| Safe writes | Every data file is written to a temp file and then swapped in. Unreadable files are kept as `*.corrupt-<date>` with a warning at startup |
| Data folder | Use another folder with the `NUBIS_DATA_DIR` environment variable (portable use · tests) |

## Source layout

```
RemoteClient/            (project: Nubis.csproj, every namespace is Nubis)
  App.xaml               theme (colors, button · input · scrollbar styles)
  MainWindow.*           window + per-screen partials (Hosts · HostEditor · Vault · Settings · Cloud · Tabs)
  Claude/                Claude panel, built-in MCP server and tools, permission deny rules, read-only command check
  Sessions/              SSH · RDP · local terminal sessions, xterm.js view, ConPTY, terminal themes
  Sftp/                  SFTP panel (browse · transfer · drag-out · edit sync)
  Remote/                non-interactive execution on many servers (SSH / WinRM)
  Cloud/                 cloud profiles · sync, 4 providers (AWS · Tencent · GCP · Azure)
  Vault/                 Vault account model · storage, add / pick dialogs
  Core/                  host model, JSON storage · DPAPI · settings, logs, icons
  Dialogs/               approval · credential · message · profile · prompt dialogs
  wwwroot/               terminal.html + xterm.js 5.5 (offline bundle)
  Assets/                nubis.ico, nubis-256.png
tests/Nubis.Tests/       automated tests (xUnit)
tools/IconGen/           icon generator (draws the icon with WPF, writes ico/png)
build.ps1                build/run, test, publish
```

## Build

```powershell
.\build.ps1            # Debug build, then run
.\build.ps1 test       # automated tests
.\build.ps1 publish    # publish\Nubis.exe (self-contained single exe, no .NET install needed)
```

To deploy, copy the whole `publish\` folder (`Nubis.exe` + `wwwroot\`).
Single-file compression is turned off: it unpacks the DLLs into memory on every start and costs about 100 MB more (an exe of about 180 MB is expected).
The WebView2 runtime ships with Windows 10/11.

## Usage

- **NEW HOST**: add an SSH/RDP host (shown as cards grouped by group)
- **Double-click a card**: connect / right-click: connect · edit · duplicate · delete
- **Quick connect**: type `user@host[:port]` (SSH) or `rdp host[:port]` in the search box and press Enter
- **Tabs**: middle-click to close, `Ctrl+Tab` / `Ctrl+Shift+Tab` to switch (when WPF has focus)
- **Terminal**: drag to select, then right-click = copy; right-click with no selection = paste; `Ctrl+Shift+C/V`
- **Disconnected SSH session**: press Enter to reconnect / RDP: "reconnect" button
- **RDP user**: `DOMAIN\user` is supported. Resizing the window resizes the remote desktop (Windows 8.1/2012R2 or later)
- **RDP settings**: follows the mstsc default file (`Documents\Default.rdp`) — connection quality (auto-detect), color depth, visual effects, clipboard · printer · drive · smart card redirection, audio, Windows key forwarding, auto-reconnect. Without the file, mstsc defaults apply. Only the server certificate warning follows Nubis settings (default: warn when the certificate cannot be verified; turn it off in Settings for self-signed environments)
- **Shortcut**: `Nubis.exe --connect "host name"` connects right after start
- **Cloud**: import the servers of a cloud account as profile groups (see below)
- **Vault**: store login accounts shared by several servers in one place and pick them per host
  - A Vault account = username + password and/or SSH private key (+ passphrase). Sidebar *Vault* → "add account" (import a key file or paste it; OpenSSH · PuTTY .ppk · PEM). The key is parsed on save and a public key is generated → "copy public key" to add it to the server's `~/.ssh/authorized_keys`
  - Pick a Vault account under *login account* in the host editor and the card shows what you log in with, e.g. "user root · password · ssh-ed25519 key". The username · password · key file fields below still work: **leave them empty to use the Vault values, fill them in to override**
  - RDP uses only the Vault account's username and password (keys are ignored)
  - Changing a Vault account applies to every host using it
  - If a host that used a Vault password fails authentication and you enter and save a new password, the Vault account is updated
  - Deleting an account unlinks it from its hosts (the list is shown before deleting)
- **Settings**: terminal theme (9 dark + 2 light), font, font size — applied to open terminals immediately
  - *Syntax highlighting*: adds color to IP addresses (incl. CIDR · port), numbers (incl. units) and status keywords (error/warn/ok and their Korean equivalents). The output itself is not changed; text already colored by the server and full-screen programs such as vim · htop are left alone
  - *Terminal GPU acceleration*: off by default (saves about 70 MB of WebView2 memory). Turn it on if you often print large output — applies after restart
  - *Automatic contrast*: adjusts low-contrast text colors (e.g. white text on a light theme) so they stay readable (xterm.js `minimumContrastRatio` 4.5)
  - *SSH*: host key confirmation for first-time servers / *SFTP*: auto-open · follow terminal folder · file editor ([SFTP](#sftp-file-transfer))
  - *Sign-in screen*: GitHub · Google · Microsoft ([below](#sign-in-screen-optional)) / *Default ports*: ports for new hosts and cloud sync
  - *Claude*: auto-approve read-only commands / *RDP*: server certificate warning
  - Most settings can also be changed by asking the Claude panel (`update_settings`; security settings need approval, and Claude cannot change the sign-in settings or the editor path)

## Cloud profile sync

Uses the CLI logins already on this PC. No keys to type in.

| Cloud | Group unit | Authentication | Discovered from |
|---|---|---|---|
| AWS | profile | `~/.aws/config` (SSO: `aws sso login`) | `[profile …]` entries in config |
| Tencent | profile | `~/.tccli/<profile>.credential` | `.credential` files |
| GCP | project | ADC (`gcloud auth application-default login`) | ACTIVE projects (excluding `sys-`, `gen-lang-client-`) |
| Azure | subscription | az CLI (`az login`) | active subscriptions in every accessible tenant |

- On the Cloud page, **add profile** → pick the profiles / projects / subscriptions found on this PC → synced right away
- Server → host rules
  - Name: the `Name` tag (or instance name) is split into `name:purpose`
  - Address: both the private IP and the public IP (EIP etc.) are stored. Connections use the private IP by default (make the public IP the default in profile settings, or choose per host in the editor or with right-click "connect via public/private IP")
  - Claude's run_command tries the private IP first and then the public IP (WinRM: private 5986→5985→public 5986→5985, SSH: private→public). An authentication failure stops immediately to avoid account lockouts
  - Windows → RDP, everything else → SSH. Ports follow Settings → **Default ports** (22 / 3389 by default) and can be overridden per profile. Changing the default port also updates synced hosts that used it (ports you changed per host are kept)
  - Terminated instances are excluded; stopped servers are shown dimmed (can be excluded in profile settings)
- Re-syncing updates names · addresses · state, keeps the protocol · port · account you changed, and removes servers that are gone
- Login account: the value set on the host → otherwise the profile's default account → otherwise asked when connecting
- Each section's **login** button runs that CLI's login command in a new console window
- **Credential file paths**: set them per section with "change path" (defaults are the standard locations in the table). AWS uses `AWS_CONFIG_FILE` (including `credentials` in the same folder), GCP uses `GOOGLE_APPLICATION_CREDENTIALS`, Azure uses `AZURE_CONFIG_DIR`, so the CLI launched by the login button uses the same path. Tencent reads `*.credential` in the chosen folder
- If some regions / zones fail to load, that sync **does not delete anything** (so a temporary outage cannot wipe hosts and their saved accounts)
- Deleting a cloud host from the list hides it in that profile (undo in profile settings). Duplicating it creates a regular host

## Sign-in screen (optional)

Choose GitHub · Google · Microsoft under Settings → **Sign-in screen**, and Nubis can only be opened after signing in with that account.

- Method: OAuth Device Flow — enter the code shown on screen in your browser and approve. Your password is typed only in the browser (GitHub/Google/Microsoft); Nubis never receives it. The token is used only to identify the account and is not stored
- Allowed: account names (GitHub login / email), `@company.com` for a whole domain, and for GitHub the members of an Organization
- Stay signed in (hours, 0 = every time) · allow offline (days, 0 = never). The last sign-in is stored encrypted in `login.json`
- **Per-account profiles**: each signed-in account gets its own hosts · Vault · cloud profiles · known_hosts · Claude working folder · audit log · display settings under `%APPDATA%\Nubis\users\<provider>_<account id>\`. Another account starts empty and never sees yours (only language and sign-in settings are shared). The account id never changes (e.g. the GitHub user number), so renaming your login keeps the same profile
- **Owner** = the account that set up sign-in. Data from before sign-in was enabled moves to the owner on their first sign-in, and only the owner can change the sign-in settings. Turning sign-in off keeps using the owner's profile
- **Settings are saved only after a successful test sign-in** (so a wrong setting cannot lock you out). If you are locked out anyway, set `"Login"` → `"Provider"` to `"None"` in `%APPDATA%\Nubis\settings.json`
- Registration
  - GitHub: github.com/settings/developers → OAuth Apps → New OAuth App (any Homepage · Callback URL) → check **Enable Device Flow** → Client ID
  - Google: Google Cloud console → Credentials → OAuth client ID → type **TVs and Limited Input devices** → Client ID · Secret
  - Microsoft: Entra admin center → App registrations → Authentication → **Allow public client flows** → Application (client) ID; tenant `organizations` or your company tenant
- Limitation: the sign-in and per-account profiles are "the door into the app" and "separation inside the app". It is not tied to the data encryption (DPAPI), so it does not stop a program running under the same Windows account from decrypting the files directly (DPAPI does stop files leaking to another PC or account)

## Claude Code panel

The **✦ Claude** button in the title bar (or `Ctrl+Shift+A`) opens the official Claude Code CLI in a panel on the right.

- Runs this PC's `claude` CLI in a local terminal (ConPTY) — sign-in, models, slash commands and every other CLI feature work as usual (start with `/login`)
- The app starts a built-in MCP server (`127.0.0.1`, a new token every run) and connects it with `--mcp-config`
- Working folder: `%APPDATA%\Nubis\claude` (edit the `CLAUDE.md` there freely)
- **Tells Claude which tab you are looking at**: whenever you switch tabs or a connection state changes, the app writes the current tab (session_id · title · SSH/RDP · address · state, no account data) to `claude\active-session.txt`, and Claude Code's `UserPromptSubmit` hook attaches that line to every message. Switch tabs and ask "what is this error?" — Claude looks at that tab

| MCP tool | What it does | Approval |
|---|---|---|
| `list_groups` / `list_hosts` | search groups and hosts (no passwords) | - |
| `list_sessions` | open tabs, the tab you are looking at | - |
| `open_session` | "connect to server X" → opens an SSH/RDP tab | - |
| `read_terminal` / `get_selection` | read the current (or a given) terminal screen and selection | - |
| `capture_screen` | capture a tab as an image (Claude sees RDP screens). A hidden tab is brought to front first | - |
| `close_session` | close a tab | - |
| `diagnostics` | app memory · handles · threads (leak checks) | - |
| `list_vault` | Vault accounts (name · username · login method only, no secrets) | - |
| `add_host` / `update_host` / `delete_host` | "add 10.0.1.5 as web-01 in Production and link the ops account" — no passwords, only Vault links. Cloud hosts cannot change address or group | **in-app approval** (always — so an address or Vault change cannot send an account to another server) |
| `test_connection` | when a connection fails: per address (private→public) DNS, TCP ports, SSH banner, WinRM 5986/5985 for RDP hosts (no login) | - |
| `wait_for_output` | wait until a string appears or output goes quiet, then return the screen (use after `send_to_terminal`) | - |
| `sftp_list` / `sftp_read` | list a remote folder · read a text file (`tail` for logs, binaries refused) | same as `run_command` (follows the read-only auto-approve setting) |
| `sftp_download` / `sftp_upload` | downloads go only to `Downloads\Nubis\<host>` (never overwrite); uploads refuse the Nubis data folder, `~/.ssh` and private key files | **in-app approval** (always) |
| `cloud_sync` | re-fetch a cloud profile's servers (read-only APIs) | - |
| `get_settings` / `update_settings` | read and change app settings like "theme dracula, font 16" (theme · font · highlighting · GPU · default ports) | only security settings (RDP certificate warning · host key prompt · auto-approve) need **in-app approval**. Claude can turn auto-approve off but never on |
| `run_command` | run on many servers in parallel — Linux over SSH / Windows over WinRM (PowerShell · cmd) | **in-app approval** (read-only commands can skip it if enabled) |
| `send_to_terminal` | type into an open terminal | **in-app approval** (always) |

- Approval dialog: shows every target server and the full command, warns about dangerous commands (rm -rf, reboot, Stop-Service …), locks the run button for 1.5 s after it appears, Esc = deny
- **Auto-approve read-only commands** (Settings → Claude, off by default): only read-only `run_command` commands run without the dialog. Allow-list based — Linux `ls/cat/grep/df/ps/ss/journalctl`, `systemctl status`, `docker ps`, `kubectl get` … / PowerShell `Get-*` · `Select-/Where-/Sort-/Format-*` · `Test-*` / cmd and common `ipconfig`, `sc query`, `reg query`, `netstat`, `tasklist` …. Any file redirection (`>`), command substitution (`$( )`, backticks), method or .NET calls, `Invoke-*`, unknown commands or write options (`systemctl restart`, `find -delete`, `ipconfig /release` …) bring the dialog back. Dangerous commands always ask. Auto-run commands are still logged as "auto-approved (read-only)"
- Every request is logged to `%APPDATA%\Nubis\claude_audit.log` (document icon in the panel header)
- **Saved accounts are protected**: Claude in the panel cannot read `hosts.json` · `cloud_profiles.json` · `vault.json` · `known_hosts.json` · `login.json`, which hold accounts, passwords, keys and server lists (the Read tool, shell commands naming those files and DPAPI decryption commands are denied through `claude --settings`). The audit log, MCP config and `settings.json` cannot be modified (app settings change only through `update_settings` validation and approval). It is a program running under the same Windows account, so this is not full isolation; the files themselves are DPAPI-encrypted and open only on this PC under this Windows account
- Whole groups can be targeted like `group:Production`; stopped servers are skipped automatically

### WinRM (running commands on Windows servers)

- **5986 (HTTPS) first → retry on 5985 (HTTP)** if it fails. Errors after the shell is created are not retried, to avoid running a command twice
- 5986: TLS + NTLM/Negotiate (self-signed certificates allowed)
- 5985: NTLM message encryption implemented directly (works with the default `AllowUnencrypted=false` policy) — no local TrustedHosts setup needed
- Account: the same host / cloud profile account as RDP (`DOMAIN\user`, `user@domain`, local accounts)
- Server side: a WinRM listener (`winrm quickconfig`, a certificate binding for HTTPS) + firewall rules for 5986/5985

## SFTP file transfer

When SSH login succeeds, the SFTP panel attaches on the left automatically (turn it off in Settings → SFTP; open/close with the folder button in the toolbar, drag the divider to resize). It uses the account the terminal logged in with.

- **Browse**: type a path, double-click folders, up (Backspace) · home · refresh (F5)
- **Follow the terminal folder**: if the shell reports its current folder (OSC 7), the panel follows every `cd` in the terminal. For shells that do not, use the terminal icon button in the panel (reads the `user@host:/path$` prompt)
  - Enable OSC 7 on the server (bash): add `PROMPT_COMMAND='printf "\e]7;file://%s%s\a" "$HOSTNAME" "$PWD"'` to `~/.bashrc`
- **Upload**: drag files · folders from Explorer (folders recursively) or use the upload button. Drop onto a folder row to upload into that folder. Existing files ask before overwriting
- **Download**: drag items out to Explorer (desktop etc.), or the download button → pick a folder (default: Downloads)
  - Dragging out starts the download when you release the mouse (progress · cancel at the bottom of the panel; Explorer waits meanwhile)
  - Drop onto the SFTP panel of another SSH tab to copy between servers (goes through a temp folder on this PC)
- **Edit → saving uploads it**: double-click a file (or right-click "edit") to open it in your editor; every save with changed content is uploaded back to the same path ("saved → uploaded" at the bottom of the panel)
  - Pick the editor in Settings → SFTP (empty = Windows default app; the "VS Code" button sets it directly). Open it in VS Code and changes made by the Claude Code in that window are uploaded the moment they are saved
  - If the server's file changed while you were editing (modified time · size), you are asked before overwriting (No = skip this save)
  - Opening the same file again reopens the downloaded copy (your edits stay). Closing the tab stops watching
- **Right-click**: edit · open read-only copy · download · rename (F2) · delete (Delete, folders recursively) · new folder · copy path
- Transfer progress and cancel

## Local terminal

The **+** button in the title bar (or `Ctrl+Shift+T`) opens a local PowerShell tab. `Ctrl+Tab` switches tabs even while the terminal has focus.

## License

[MIT License](LICENSE) — free to use, modify and distribute for personal or commercial purposes (keep the copyright notice and license text).

Open-source components and theme sources are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Known limitations

- The app UI is Korean only for now
- While the RDP screen has focus, `Ctrl+Tab` goes to the remote machine (click a tab to switch)
- No port forwarding, jump hosts or RDP gateway yet
- Once the data files are encrypted, older versions (0.1.2 or earlier) cannot read them — do not go back to an old exe after running the new version
- WinRM (running commands on Windows servers) has not been fully verified against real servers yet
- Because the data folder was renamed, the Claude panel asks once more to trust its working folder
