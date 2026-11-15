**Title:** Nubis – Open-source Windows client for SSH/RDP/SFTP with multi-cloud and on-prem inventory sync and a built-in Claude Code panel

- GitHub: https://github.com/windevase/nubis
- License: MIT
- Affiliation: Creator / Maintainer

Hey everyone,

I built Nubis to fix my own friction managing servers spread across several clouds and on-prem networks from a Windows workstation. It just reached 0.4.0, so here is what it does now.

**Key features:**

- **Multi-cloud inventory sync:** Imports server lists from AWS (EC2), Tencent Cloud (CVM), GCP (Compute Engine), Azure (VM) and Oracle Cloud (OCI). It uses the credential profiles already on your PC (AWS profiles / SSO, tccli, gcloud ADC, Azure CLI, `~/.oci/config`), and keeps the list in sync on demand, at startup or every few minutes. Names and IPs update; your own edits are kept.
- **On-prem import:** Pulls in the server lists you already have. Supported sources:
  - Excel/CSV paste
  - `~/.ssh/config` (with `Include` and `ProxyJump`)
  - mRemoteNG
  - Ansible inventories
  - Active Directory / Entra Domain Services

  You see a preview first, duplicates are detected, and passwords are never imported.
- **Unified sessions:** Tabbed SSH, RDP and local PowerShell in one native Windows app, plus an SFTP panel that rides on the same SSH connection.
  - The SFTP panel follows the terminal's current folder and user (`sudo su -` and it browses as root).
  - Files open in your editor and upload on save.
- **Works like OpenSSH where it matters:** ssh-agent and `~/.ssh` keys, `known_hosts`, jump hosts, and OTP / push MFA prompts.
- **Embedded Claude Code panel:** It sees the tab you're on, so you can ask it to troubleshoot an error, check disk usage across a group, or "bring in all our servers" (it finds cloud profiles and local server lists and imports them).
  - Anything that touches a server needs your approval and is written to an audit log.
  - Claude can't read your saved passwords or keys.
  - On OTP servers it runs commands over the tab you're already signed in to.
- **Keyboard-first:** `Ctrl+K` command palette for hosts, open tabs and actions; `Ctrl+Shift+R` reopens a closed tab. The UI is Windows 11 style (Mica), and closed tabs are fully released, so it stays light even after a long day.
- **Local and private:** No cloud proxy, no telemetry, no account required.
  - Hosts, Vault accounts and known hosts are encrypted with Windows DPAPI on your machine.
  - The Claude panel is optional and uses your own Claude Code install.
- **Korean and English UI.**

It's completely free and open source. Pre-built Windows binaries (portable zip, no installer, no sign-up) are on GitHub Releases, each scanned on VirusTotal, with links in the release notes. You can also audit the code or build it yourself.

Feedback, bug reports and feature suggestions are very welcome!
