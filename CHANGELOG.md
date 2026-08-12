# Changelog

All notable changes to TaskbarMonitor are documented in this file.

## [1.0.0] - 2026-08-12

### Added
- Real Windows taskbar embedding through `Deskband11Lib.Wpf` with Explorer-restart recovery.
- Text-only CPU, RAM, GPU, temperature, disk, and compact paired network metrics.
- Fixed 3 × 2 Grid layout with bounded text so value changes cannot shift neighbouring pods.
- Native tray-attached settings menu, plus searchable advanced palette fallback.
- Optional local usage readers and opt-in quota readers for selected AI coding agents.
- Regression coverage for metrics, configuration, layout, palette, usage parsing/redaction, and taskbar recovery.
- GitHub Actions build, test, and package-vulnerability checks.

### Changed
- Grid network display is one compact cell beside RAM: `↑ upload ↓ download`.
- Widget content is vertically centred in the taskbar.
- GPU usage is cached for 5 seconds; disk and temperature readers for 10 seconds; metric sampling runs every 2 seconds to reduce idle native-counter work.
- Network-backed quota providers are opt-in in the committed configuration.

### Security
- No OAuth client secrets are embedded.
- The Antigravity reader does not refresh/exchange OAuth refresh tokens.
- Credential values are read only from provider-managed local stores, retained only in memory while required, and redacted from errors.
- The app does not listen on TCP or UDP ports.
