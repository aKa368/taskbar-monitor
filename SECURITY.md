# Security policy

## Supported release line

Security fixes are applied to the current `main` branch and the latest published release.

## Security design

TaskbarMonitor is a local Windows desktop widget. It does not run a server and is designed to avoid storing credentials itself.

- Build outputs, local environment files, private keys, certificates, job artifacts, and internal plans are ignored by Git.
- Local SQLite usage readers use read-only connections and do not query provider API-key tables.
- Optional quota clients read credentials only from locations already managed by the relevant CLI or Windows Credential Manager.
- Access tokens are kept in memory only for outbound quota requests, cleared after the request path, and redacted from error strings.
- The Antigravity reader does not embed an OAuth client secret and does not exchange refresh tokens.
- Network-backed quota readers are disabled by default in the committed configuration.
- Per-user autostart is optional, disabled by default, and uses only `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` when enabled.

## Network destinations

No network request is made until an optional provider is enabled. When enabled, the application may contact only these provider-owned endpoints for usage data:

- `chatgpt.com` — ChatGPT/Codex usage
- `*.googleapis.com` — Antigravity Cloud Code Assist usage
- `api.anthropic.com` — Claude Code usage

The app does not expose a listener port.

## Reporting a vulnerability

Please do **not** open a public issue for a suspected credential leak, unsafe local-file access, network request that is not documented above, or a remote-code-execution path.

Instead, report it privately to the repository owner through the contact method listed on their GitHub profile. Include:

1. A clear description and affected version/commit.
2. Safe reproduction steps.
3. Potential impact.
4. A proof of concept that contains no real account credentials or personal data.

The maintainer will acknowledge the report, investigate, and coordinate disclosure before publishing a fix.
