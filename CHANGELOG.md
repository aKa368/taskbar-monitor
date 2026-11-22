# Changelog

All notable changes to TaskbarMonitor are documented in this file.

## [1.0.5] - 2026-11-22

### Removed
- Dormant Codex/CPA code: `CodexUsage` provider, `AgentIds.Codex`, `CodexEnabled` flag, all Codex branches in `UsagePoller`, `TaskbarContentViewModel`, `LayoutManager`, `ConfigManager`, and config.json.
- Codex-specific tests and assertions; remaining agent tests use Antigravity/CommandCode/OpenCode/Claude for coverage.

### Changed
- Unified version to 1.0.5 across `Directory.Build.props`, `TaskbarMonitor.csproj`, `TaskbarMonitor.iss`, and `CHANGELOG.md`.

## [Unreleased]

### Added
- Zero-Overhead Memory Trimming (`WorkingSetTrimmer`): Combines Win32 `EmptyWorkingSet` and compacting Gen2/LOH GC on application idle and periodic 30s timer, reducing RAM from ~210MB to < 60MB.
- Dynamic Alert Colors: Threshold-driven alert colors (amber `#F59E0B` for warning, red `#EF4444` for danger) on CPU, GPU, RAM, and temperature pods.
- Edge Snapping: Automatically snaps floating capsule to screen working area edges when within 16px.
- Quick Actions: Middle mouse click and context menu action to launch Windows Task Manager (`taskmgr.exe`).
- Mini Game HUD: Configurable `GameOverlayMode` ("AutoHide" vs "MiniHud") to display ultra-compact FPS, Ping, and APU temperature during fullscreen gaming.
- Comprehensive unit tests: Added test suites for WorkingSetTrimmer, dynamic alert colors, and GameOverlayMode configuration (267/267 tests passing).

## [1.0.3] - 2026-08-22

### Fixed
- Unified the notification-area and taskbar right-click menus on one WPF menu implementation, including matching actions, check states, and Windows menu fonts.
- Added installed-font detection and fallback (`Cascadia Mono`, then `Segoe UI`) when the configured font is unavailable, and applied the resolved font consistently to system and agent widgets.
- Removed mojibake from user-visible menu labels and tooltips.
- Shared metric samplers can now restart cleanly after `Stop()` without retaining inactive subscriptions.
- `TaskbarMonitor.slnx` now contains all five test projects (`AgentUsageTests`, `ConfigTests`, `MetricsTests`, `LayoutTests`, `PaletteTests`); running tests via the new solution no longer silently skips suites.
- `UsagePoller` now performs a graceful shutdown: `DisposeAsync` cancels in-flight HTTP polls via a poller-scoped `CancellationTokenSource` and awaits them before disposing HTTP clients (no more fire-and-forget races at exit).
- `ConfigManager` writes `config.json` atomically (temp file + `File.Replace`, keeping a `.bak`) and falls back to the last good backup when the active file is corrupt, instead of resetting to defaults.

### Changed
- Taskbar-host windows now use the Win32 tool-window style so they stay out of Alt+Tab while remaining available through the tray icon.
- Removed redundant agent text formatting work from the UI refresh path.
- Unified test package versions via central package management (`Directory.Packages.props`): all test projects use `Microsoft.NET.Test.Sdk 18.0.1`, `xunit.v3 3.2.2`, and `xunit.runner.visualstudio 3.1.5` (no more v2/v3 mix).
- All test projects now target `net10.0-windows10.0.26100` and reference the real `TaskbarMonitor` assembly via `InternalsVisibleTo` instead of re-compiling `src` sources.
- CI now restores with `--locked-mode` (committed `packages.lock.json`), collects code coverage, verifies formatting (`dotnet format --verify-no-changes`), and fails if `.slnx` misses any project present in `.sln`.

## [1.0.1] - 2026-08-12

### Security
- Removed `LibreHardwareMonitorLib` and its low-level hardware-driver path after Microsoft Defender identified the bundled WinRing0 driver as `VulnerableDriver:WinNT/Winring0`.
- The release no longer ships or loads a `.sys` driver. GPU temperature is unavailable (`--°C`) unless a future user-mode Windows API provides it.
- Added explicit product/version metadata and a release installer signing requirement; an unsigned installer may still receive a SmartScreen reputation prompt until an Authenticode certificate is used.

### Changed
- Added a standard per-user, self-contained Inno Setup installer with shortcuts and uninstaller; runtime config now lives at `%LocalAppData%\TaskbarMonitor\Config\config.json` so it remains writable after installation.

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
