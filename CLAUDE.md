# Nubis — Working Guide

SSH / RDP / SFTP remote access client for Windows (WPF, .NET 10). It imports cloud (AWS · Tencent · GCP · Azure · OCI) servers per profile, and the in-app Claude Code panel drives the app through a built-in MCP server. See `README.md` for features.

## Build · Run · Test

The .NET SDK is a **per-user install** (`%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe`, not on PATH, no admin rights). `build.ps1` finds it automatically.

```powershell
.\build.ps1            # Debug build, then run (RemoteClient\bin\Debug\net10.0-windows\Nubis.exe)
.\build.ps1 test       # xUnit (tests\Nubis.Tests)
.\build.ps1 publish    # publish\ self-contained folder layout (Nubis.exe + Nubis.dll + runtime DLLs + wwwroot\) + dist\ portable zip · SHA256SUMS.txt
```

- When calling `dotnet` directly, set `$env:DOTNET_ROOT="$env:LOCALAPPDATA\Microsoft\dotnet"` or the built exe / test exe won't run.
- **Do not use single-file publishing (PublishSingleFile).** Antivirus and sandboxes (VirusTotal CAPE, SentinelOne, etc.) frequently flagged the bundled exe as a dropper, so since 0.2.5 we ship a folder layout. Do not go back to single-file, compression, or `IncludeNativeLibrariesForSelfExtract`. ReadyToRun was also dropped — it only grew the size for little gain.
- Release VirusTotal scanning is `tools\vt-scan.ps1` (Nubis.exe · zip; detecting engines and verdict names are tabulated in the Actions run summary). To rescan an existing release, run the `vt-scan` workflow manually.
  - **Do not upload Nubis.dll to VT on its own.** Sandboxes (CAPE etc.) run the DLL as a standalone exe, it crashes, and they report crash / suspicious behavior. It is covered by the zip scan.
  - `Deterministic=false` in the csproj puts the real build time in the PE timestamp field to remove the sandbox "timestomp" flag — do not revert it.
  - No code signing (it costs money). The sandbox "invalid Authenticode" flag is accepted.
- **If the user is running `publish\Nubis.exe`, publish fails on a file lock.** Do not kill the user's Nubis; ask them to close it. Check with:
  `Get-CimInstance Win32_Process -Filter "Name='Nubis.exe'" | ? ExecutablePath -like '*\publish\Nubis.exe'`
- After changes, confirm 0 build warnings and that all tests pass.
- **SSH.NET is a patched fork** (`2026.0.1.1-nubis`, restored from `third_party\SSH.NET\packages` via `nuget.config`). The patch (`third_party\SSH.NET\nubis-sftp-exec.patch`) adds `SftpClient.SftpServerCommand` / `SftpServerPreamble` / `SftpServerError` — open SFTP by running a command (sudo sftp-server) instead of the "sftp" subsystem. Rebuild with `tools\build-sshnet.ps1` (clone tag → apply patch → pack). To upgrade SSH.NET, bump `-Tag`/`-Version`, fix the patch if it no longer applies, and update the csproj. Keep the fork version **above** the upstream one (4-part `x.y.z.1-nubis`) — `x.y.z-nubis` sorts below `x.y.z` and NuGet flags the advisories fixed in it (NU1903).
- `SharedSftpClient` opens the SFTP panel on the terminal's SSH session by setting SSH.NET's internal `BaseClient.Session` via reflection; `KbdAuthTests` asserts it still exists after an upgrade. The fallback is a separate connection (re-authentication).

## Structure

The source folder is named `RemoteClient\`, but the project / assembly / namespace is `Nubis` (VS Code holds the folder so it can't be renamed; if renamed, also fix the two paths in `build.ps1`).

| Location | Role |
|---|---|
| `MainWindow.xaml(.cs)` | Window init · sidebar switching · window controls. Per-screen code is split into partials ↓ |
| `MainWindow.Hosts.cs` / `.HostDuplicate.cs` / `.HostEditor.cs` (+ `.HostEditorJump.cs`) / `.Vault.cs` / `.Settings.cs` / `.Cloud.cs` / `.CloudSync.cs` / `.CloudAuth.cs` / `.Tabs.cs` | Host list (virtualized cards · favorites/recent · keyboard selection in search) · host duplicate (`MakeDuplicate`, shared with MCP `duplicate_host`) · host editor panel · Vault page · Settings page · Cloud page (profile add · settings · remove; `.Cloud.cs`) · sync · auto-sync · cancel (`.CloudSync.cs`) · auth file path · CLI login (`.CloudAuth.cs`) · session tabs (context menu · drag to reorder · shortcuts) |
| `MainWindow.HostKeys.cs` | Host list keyboard navigation (grid ↑↓/Tab, Enter connect, F2 edit, Ctrl+N, Ctrl+F search) · quick connect |
| `Dialogs\CommandPalette` + `CommandPaletteIndex.cs` + `MainWindow.Palette.cs` | Ctrl+K command palette (hosts · open tabs · actions), separate owned window so it shows above WebView2/RDP airspace |
| `Themes\Tokens.xaml` + `App.xaml` + `Core\Ui.cs` / `Anim.cs` / `Toast.cs` / `WindowBackdrop.cs` | **Design tokens** (Layer1–3/Overlay surfaces, Stroke, Text*, Accent*, semantic colors, Radius*, Space*, Shadow*, Text* type styles) and control styles — use tokens, never hardcoded colors. Mica on Win11 (title bar/sidebar backgrounds must stay `DynamicResource *Backdrop`). `Toast.Show(text, kind)` for transient results. **Perf rules: no DropShadow/Blur on virtualized list items, no idle/looping animations** |
| `Core\HwndHostRelease.cs` | **Call `HwndHostRelease.Detach(host)` before disposing any HwndHost (WebView2, WindowsFormsHost)** — otherwise the main window's keyboard-input-site list keeps the closed tab alive forever (verified leak) |
| `Import\*.cs` + `Dialogs\ImportHostsDialog` (+ `.Input.cs` · `.Ad.cs` · `.Rdp.cs`) | IMPORT: parsers (`TableImport` Excel/CSV paste · `SshConfigImport` · `MRemoteNgImport` · `AnsibleInventoryImport` (+ `.Vars.cs` group_vars/host_vars) · `ActiveDirectoryImport` LDAPS→389 sealed · `RdpFileImport` .rdp files — one file = one RDP host: address · port · user · NLA · RD gateway, never the saved password; `.Rdp.cs` = file picker · drop onto the dialog or the host list) → `ImportedHost` → `ImportMerge` (plan · dedupe against existing · apply). Passwords are never imported |
| `MainWindow.Groups.cs` / `.Preferences.cs` | Group context-menu bulk actions (Vault account · public/private IP · connect all) / default Vault account · convenience settings. Account precedence is decided in one place: `HostEntry.ResolveVault` |
| `MainWindow.Selection.cs` + `Core\BulkEdit.cs` + `Dialogs\BulkEditDialog` | Multi-card selection (Ctrl/Shift+click · Ctrl+A, selection bar · selection menu) / bulk-edit apply logic (UI-independent, `BulkEditTests`) / bulk-edit window |
| `Claude\MainWindow.ClaudePanel.cs` | Claude panel (`claude` CLI via ConPTY), current-tab hint hook (`active-session.txt`) |
| `Claude\MainWindow.McpTools.cs` | **The full MCP tool registration list** (`BuildTools`, `ExtraTools`) + basic tools · settings-change implementation. Register new tools here (terminal tools live in `MainWindow.McpTerminal.cs` and are yielded from `BuildTools`) |
| `Claude\MainWindow.McpApproval.cs` | `AskApprovalAsync` — every MCP approval goes through it: one dialog at a time, closed as rejected when Claude cancels the request (`notifications/cancelled` → `McpRequest.Token`) |
| `Claude\MainWindow.Mcp*.cs` | Tool implementations: `McpHosts` (add · edit · delete hosts, Vault list, cloud sync; `McpHostRules` jump/gateway checks · protocol-only values cleared like the editor: jump host · RD gateway · port forwards) · `McpHostsMore` (bulk_update_hosts · duplicate_host · connect_hosts + registration of the group tools) · `McpGroups` (set_group_defaults · rename_group · set_group_collapsed, group spec resolution) · `McpDiagnostics` (test_connection, wait_for_output) · `McpSftp` (sftp_list · sftp_read, shared `SftpTarget`: `session_id` opens SFTP on the tab's SSH session — sudo run-as follows the tab via `Sessions\SshSessionView.McpSftp.cs` / `Sftp\SftpPanel.Mcp.cs`, the sudo password never leaves the tab) · `McpSftpTransfer` (sftp_download · sftp_upload) · `McpSftpWrite` (sftp_mkdir · sftp_rename · sftp_delete · sftp_write: inspect → approval → re-check → act, audited approve/deny/done/fail; UI-independent checks in `McpSftpRules`) · `McpCapture` · `McpRunCommand` |
| `Claude\MainWindow.McpCloud.cs` / `.McpCloudProfiles.cs` / `.McpImport.cs` + `McpImportArgs.cs` | `discover_sources` · `cloud_add_profile` · `cloud_login` (login command built only from discovered names + allowlist check) / `cloud_list_profiles` · `cloud_update_profile` · `cloud_remove_profile` · `cloud_sync_cancel` (settings · cascade · removal shared with the UI via `CloudProfileEdit`; default password never read or set; refused while the profile's settings dialog is open) / `import_hosts` (preview → `apply` with one approval; no password args — AD via `ad_vault`; data folder · private keys · UNC paths blocked) |
| `MainWindow.Kubernetes.cs` / `.KubernetesCloud.cs` + `Kubernetes\*.cs` + `Sessions\K8sSessionView.cs` + `Claude\MainWindow.McpK8s*.cs` / `McpK8sRules.cs` | Kubernetes page (per-cluster kubeconfig, DPAPI) · cloud cluster discovery · cluster tab (resource list + kubectl terminal) / MCP `run_kubectl` · `open_k8s_session` · cluster management. **Port forwarding**: `Kubernetes\K8sPortForward.cs` (args · local-port suggestion · output parsing, UI-independent, `K8sPortForwardTests`) / `Kubernetes\K8sForwardProcess.cs` (`K8sForward`: shell-less `kubectl port-forward --address 127.0.0.1`, temp kubeconfig only while running) / `Core\ProcessJob.cs` (KILL_ON_JOB_CLOSE job so kubectl never outlives the app) / `Claude\MainWindow.McpK8sForward.cs` (`k8s_port_forward` list · start (approval + audit) · stop) |
| `Core\LocalPortRegistry.cs` | In-app local port registry shared by SSH and Kubernetes forwards: running kubectl forwards reserve their port (SSH refuses it), SSH tabs' enabled rules (incl. waiting) are claims the Kubernetes free-port suggestion avoids |
| `Core\UpdateCheck.cs` + `MainWindow.UpdateCheck.cs` | New-version notice: anonymous GitHub latest-release check once a day (fixed User-Agent only), pure version compare · parse (`UpdateCheckTests`) / background check · toast · sidebar "new version" (release notes · skip this version). The setting is PC-wide (`Global`) |
| `Claude\McpServer.cs` | Minimal MCP server (Streamable HTTP, JSON responses, 127.0.0.1 + Bearer token). Tool results are a string or `McpImage` |
| `Claude\ClaudeGuard.cs` | Claude panel permission deny rules (`<data>\claude\nubis-permissions.json`, rewritten every launch) — blocks reading account files · shell access · DPAPI decryption · editing settings.json |
| `Claude\ReadOnlyCommands.cs` | Read-only detection for run_command (skips approval when the `AutoApproveReadOnly` setting is on). Allowlist-based; when unsure, false |
| `Claude\SettingsCatalog.cs` / `McpHelpers.cs` | Settings update_settings may touch and their validation (security settings need approval, editor path excluded) / UI-independent decision logic for MCP tools (tested) |
| `Sessions\XtermView.cs` + `wwwroot\terminal.html` | Shared xterm.js view on WebView2 (themes · syntax highlighting · buffer reading · shortcuts · OSC 7 current directory) |
| `Sessions\SshSessionView.cs` + `.Connect.cs` / `.HostKey.cs` / `.Sftp.cs` | SSH tab (SSH.NET ShellStream — read via a read loop: `DataReceived` alone leaves the internal buffer growing) + auto-opening left SFTP panel · following the terminal directory. Output flow control: terminal.html acks rendered chars, XtermView pauses above 256K unrendered |
| `Remote\PortForward.cs` / `SshPortForwardSet.cs` + `Sessions\SshSessionView.Forwards.cs` / `PortForwardWindow.cs` / `PortForwardControls.cs` + `MainWindow.HostForwards.cs` + `Claude\MainWindow.McpPortForward.cs` | SSH local port forwarding (`ssh -L`): `HostEntry.PortForwards` rules, UI-independent validation (`PortForwards`, `PortForwardTests`) / per-tab `ForwardedPortLocal` set on the tab's SshClient (no re-auth; **bind 127.0.0.1 only**; opened on connect, closed on disconnect · tab close) / tab ⇄ window · reusable row `PortForwardRow` + input `PortForwardForm` / host editor list / MCP `list_port_forwards` · `add_port_forward` (approval + audit) · `close_port_forward` |
| `MainWindow.TabStrip.cs` | Tab strip: wheel scroll · overflow tab list · tab context menu · drag to reorder |
| `MainWindow.TabHistory.cs` | Closed-tab history (Ctrl+Shift+R reopen) · close confirmation when SFTP files are being edited |
| `MainWindow.SplitView.cs` + `Core\SplitLayout.cs` | Side-by-side view: one pair (`_split`) of tabs shown in `SessionsHost` columns 0 / 2 (views only change column — HwndHosts are never re-parented). `ShowSessionLayout` is the single place that sets tab visibility (`SelectTab`, capture/RDP-input tab switches). The focused side is the selected tab — tracked by a focus WinEvent hook while split (WebView2/RDP clicks give no WPF focus event). Pure pairing/close rules in `SplitLayout` (`SplitLayoutTests`) |
| `Remote\SshServerOs.cs` / `SshErrors` (in `SshAuth.cs`) | SSH banner → server OS (auto-approve needs a known sh) / SSH.NET exception → Korean message |
| `Sessions\RdpSessionView.cs` / `RdpAxHost.cs` / `RdpDefaults.cs` | RDP tab (built-in Windows mstscax ActiveX, applies `Default.rdp` values). Controls are reused via the `RdpAxHost.Acquire/Release` pool — avoids the mstscax leak where every released instance leaves handles · threads behind |
| `Sessions\LocalTerminalView.cs` / `ConPty.cs` / `TerminalThemes.cs` | Local PowerShell tab · Claude panel (ConPTY, child environment cleanup `ChildEnvironment`) / terminal themes |
| `Sftp\SftpPanel.List.cs` / `.FileOps.cs` + `Remote\SftpRaw.cs` | SFTP list sorting · hidden files (Ctrl+H) · path resolution · name validation · `RemoteNameCache` · `TransferMeter` / new folder · rename · delete. **Symlinks are removed/renamed via `SftpRaw` (no REALPATH) — never `DeleteFile`/`RenameFile` on a link**. `SftpRaw` also has `LStat` (link itself) and `RemoveDirectory` (RMDIR without REALPATH); the MCP write tools refuse to run if any of them is unavailable |
| `Sftp\SftpPanel.xaml(.cs)` + `.Transfer.cs` / `.UploadReplace.cs` / `.DragOut.cs` / `.Edit.cs` | SFTP panel: browsing · upload/download · single-file upload with temp-file swap (`UploadOneAsync`, shared with edit-save and MCP sftp_write) · drag out to Explorer (delayed rendering) · edit and auto-upload on save (conflict check) |
| `Remote\SshExec.cs` / `WinRm.cs` | Non-interactive multi-server command execution · SFTP connection (SSH / hand-written WinRM: 5986 HTTPS → 5985 HTTP + NTLM encryption) |
| `Remote\SshAuth.cs` + `Dialogs\AuthPromptDialog` | SSH auth methods · keyboard-interactive answers (`KbdAuth`: saved password only to a password prompt, once per connection; OTP etc. asked via `AuthPrompter` in interactive paths, error in non-interactive ones) · password-expired change · failure hints |
| `Remote\SshAgent.cs` / `JumpTunnel.cs` / `OpenSshKnownHosts.cs` | OpenSSH compatibility: ssh-agent keys (named pipe, signing delegated via a custom `HostAlgorithm`) · ProxyJump (`HostEntry.JumpHostId` → SSH.NET local forward; host keys still checked against the target address) · read-only `~/.ssh/known_hosts` lookup |
| `Remote\SharedSftpClient.cs` / `SudoSftp.cs` + `Sftp\SftpPanel.RunAs.cs` | SFTP channel on the terminal's SSH session (no re-auth) / SFTP as another user via `sudo sftp-server` when the terminal prompt shows a different user (`TerminalCwd.PromptUser`) |
| `Cloud\Cloud.cs` / `CloudSync.cs` / `*Provider.cs` | Cloud profiles · `CloudPaths` · provider list / storage · `CloudSync.Apply` merge / 5 providers (AWS SDK, Tencent TC3 hand-signed, GCP · Azure REST, OCI hand-signed REST — `OciAuth.cs` reads `~/.oci/config`) |
| `Vault\Vault.cs` + dialogs | Vault accounts (username + password / SSH key, `vault.json`). Hosts reference a single `VaultId`, and `WithDefaults` fills values only into the copy used for connecting (`KeyMaterial` · `KeyPassphrase` are `[JsonIgnore]`). A Vault account **picked on the host** wins over the host's own username/password/key (they stay saved but hidden in the editor and come back when the Vault is cleared); default accounts (profile / settings) only fill blanks. The old split form (`Kind`/`ProtectedSecret`, `VaultPasswordId`/`VaultKeyId`) is converted on read |
| `Core\Models.cs` / `SessionTab.cs` / `Storage.cs` / `Logging.cs` / `BrandIcons.cs` | Host model · session tab model (`SessionTab` · `ISessionView`) · JSON storage (`SafeFile`) · DPAPI (`Secret`) · known_hosts · settings · `AuditLog` · `LogFiles` · vector icons |
| `Dialogs\*Dialog.xaml` | App-themed dialogs. **Use `Dialog.Show` instead of `MessageBox.Show`** (same argument order) |
| `tools\IconGen` | Draws the app icon with WPF and generates `Assets\nubis.ico/png` |

- The namespace is always `Nubis` regardless of folder (folders are for organization only). `MainWindow.xaml` uses relative `Assets/...` paths, so it stays at the root.
- When a file starts to exceed 500 lines, split it into partials / files by responsibility.

## Data Locations

- User data: `%APPDATA%\Nubis\` (`hosts.json`, `cloud_profiles.json`, `known_hosts.json`, `vault.json`, `login.json`, `settings.json`, `claude\`, `claude_audit.log`). The repository contains no data.
- **Per-account profiles**: when start-screen login is used, `AppPaths.DataDir` switches to `users\<provider>_<account ID>\` (`Profiles`) — once at app startup, before the main window. The shared folder (`AppPaths.RootDir`) holds only language · login settings · update-check settings (`CheckForUpdates`, last check, latest / skipped version) (`SettingsStore.Global`) and `login.json`. UI settings are `SettingsStore.Current` (per account). Language and login must always go through `Global`/`UpdateGlobal`. Only the owner (`Login.OwnerKey`) may change login settings.
- `hosts.json` · `cloud_profiles.json` · `known_hosts.json` · `vault.json` · `login.json` are **whole-file DPAPI encrypted** (`SafeFile.LoadProtected/WriteProtected`, marker `NUBIS-DPAPI1`, entropy `Nubis.DataFile.v1` — do not change). Plaintext without the marker is encrypted automatically on read. `settings.json` stays plaintext (no secrets, so users can fix it by hand if they misconfigure login). New data files must use `WriteProtected` and be added to `ClaudeGuard.SecretFiles`.
- **Older versions (0.1.x) cannot read encrypted data** — the read fails and the list comes up empty, so tell users not to run old exes.
- Cache: `%LOCALAPPDATA%\Nubis\WebView2\`
- The `NUBIS_DATA_DIR` environment variable overrides the data folder → **always test in a separate folder via this variable**. For xUnit, the `[ModuleInitializer]` in `VaultTests.cs` points it at a temp folder.
- The old folder `%APPDATA%\RemoteClient` is migrated automatically on startup.

## Must Follow

- **Do not touch user data.** Never overwrite or delete `%APPDATA%\Nubis\hosts.json` etc. for testing (this once wiped the user's synced hosts). Test data goes in a temp folder via `NUBIS_DATA_DIR`.
- **Never kill the user's Nubis processes by name.** Only kill processes you started, by PID.
- Do not change the DPAPI entropy string `"RemoteClient.v1"` in `Secret` (changing it makes saved passwords undecryptable).
- Actions that affect remote servers (Claude's `run_command`, `send_to_terminal`) must go through **`ApprovalDialog` approval + `AuditLog` recording**. The same applies to any new execution path. The only exceptions are read-only commands under the user-enabled `AutoApproveReadOnly`, and closing or stopping a local port forward the app itself opened on this PC (`close_port_forward`, `k8s_port_forward` stop — it only reduces exposure and changes nothing on the server); both still write the audit log. Stopping or closing anything on a remote server is not an exception. When widening the `ReadOnlyCommands` allowlist, block writable subcommands · options and add counterexamples to `ReadOnlyCommandTests`. Never put passwords in MCP results or logs. Any new file that stores accounts must also be added to `ClaudeGuard.SecretFiles`.
- Host keys: unknown servers are accept-new (interactive connections show a confirmation window when Settings `SshHostKeyPrompt` is on; non-interactive run_command · SFTP use `SshAuth.VerifyAcceptNew`). **A key that differs from the stored one is always warned / rejected** — never remove this warning. Keys the user rejected stay marked `REJECTED` and are never auto-trusted.
- Cloud sync: if some region / zone queries fail (`CloudFetchResult.IsPartial`), **do not delete anything**. Keep user-changed protocol · port · account · `UsePublicIp`; only update name · address · state.
- Command execution retry: only connection failures move on to the next port · address (private → public). **Do not retry auth failures or errors after the shell is created** (prevents account lockout · duplicate execution).
- WPF and WinForms are used together, so `ImplicitUsings` is off. Use fully qualified names for `System.Drawing` etc.
- Dark theme: `ListBox` and similar re-apply the Windows default text color (black), so set `Foreground="{StaticResource Text}"` explicitly.

## UI Text Localization (Korean default · English)

- User-visible text is wrapped **using the Korean original as the key** (`Core\Localization.cs`). English lives in `RemoteClient\Lang\en\*.json` (`"Korean original": "English"`). Without a translation, the Korean is shown as-is.
  - C#: `L.T("호스트 추가")`. With values, `L.F("{0} 에 업로드", path)` — never wrap an interpolated string `$"..."` in `L.T` (the key changes every time). Put formats in the placeholder, like `{0:HH:mm}`.
  - XAML: put `xmlns:l="clr-namespace:Nubis"` on the root and use `Text="{l:Tr '주소'}"`. Escape single quotes as `\'`; commas · braces are fine inside the single quotes. `<Run Text="{l:Tr '...'}"/>` works the same.
  - `L.T` takes **exactly one** string literal (no concatenation like `"a" + "b"` — the tests can't find the key).
- Not translated: MCP tool descriptions · tool results · `McpToolException` messages (only Claude reads them), audit log contents, log files, regexes · syntax-highlighting keywords, file names · setting keys, internal identifiers other than names UI automation looks up. But even on MCP paths, `ApprovalDialog.Ask` titles · descriptions, `Dialog.Show`, and status text **shown on the user's screen** are translated.
- `LocalizationTests` checks that every wrapped key has an English translation, placeholders match, and no Korean remains in English values. When adding new text, add it to the English dictionary in the same change.
- Language is the shared setting `Language` (`ko`/`en`, chosen via `LanguageWindow` on first run). Changing it requires a restart. The Claude panel's reply language follows it too.

## Encoding Caveats (Windows PowerShell 5.1)

- PowerShell 5.1 `Get-Content`/`Set-Content` read and write BOM-less UTF-8 as ANSI, **corrupting Korean text** (the csproj comments were once corrupted this way). Edit files with the Edit/Write tools or `[IO.File]::ReadAllText/WriteAllText(..., UTF8Encoding)`.
- `.ps1` files executed by PowerShell, like `build.ps1`, must be saved as **UTF-8 with BOM** so Korean isn't corrupted.
- `.cs`/`.xaml` are UTF-8 without BOM.

## Testing Tips

- Public SSH/SFTP demo server: `test.rebex.net:22`, `demo` / `password` (read-only; it mangles Korean in responses, so unsuitable for UTF-8 verification). Host key `ssh-ed25519 SHA256:d7Te2DHmvBNSWJNBWik2KbDTjmWtYHe2bvXTMM9lVg4`.
- A test host's `ProtectedPassword` is made with DPAPI (entropy `RemoteClient.v1`, CurrentUser) and put into `hosts.json`. A test `hosts.json` can be written as plaintext; the app encrypts it on first read.
- Start-screen login is toggled with `Login` in `settings.json` (in the test data folder). Verifying real login requires the user's OAuth app Client ID and browser approval.
- `Nubis.exe --connect "host name"` connects right at startup (can be given multiple times). `--claude` opens the Claude panel at startup.
- Terminal internals: launching with `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS="--remote-debugging-port=9222"` lets you call `readBuffer(n)`, `term.write()`, etc. over CDP.
- The MCP server starts only when the Claude panel is opened. Address · token are in `<data folder>\claude\mcp-config.json`. Never print the token.
- Check the screen by capturing the window with PrintWindow (`PW_RENDERFULLCONTENT`) — this includes WebView2 · RDP content. Synthetic mouse clicks miss when the window is covered, so verify results via capture.
- To check cloud queries, reference `Nubis.csproj` from a separate console project and call `CloudProviders.Get(...).FetchAsync` (query APIs only, never modify resources).

## Documentation

- When features change, update **both** `README.md` (English, GitHub default) and `README.ko.md` (Korean) with the same content. Keep the language-switch links at the top of both files. Add new open-source dependencies to `THIRD-PARTY-NOTICES.md`.
- Code comments and UI text are in Korean.
- `.coderabbit.yaml` restates the Must Follow rules as CodeRabbit path instructions and pre-merge checks (free open-source tier only — no paid-plan keys). When a rule here changes, update it there too. The repo has fewer than 10 stars, so the first review of a PR is requested by commenting `@coderabbitai review`; after that, each new push is reviewed incrementally on its own. Re-commenting on a commit that was already reviewed does not create a new review (the command is for starting or resuming paused reviews).
