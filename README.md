# TaskbarMonitor

A compact **Windows 11 taskbar widget** for local system metrics and optional AI coding-agent usage. It is a WPF/.NET 10 application embedded as a real child of the Windows taskbar through [`Deskband11Lib.Wpf`](https://www.nuget.org/packages/Deskband11Lib.Wpf/), not a floating imitation window.

> **Windows 11 target · .NET 10 · text-only UI · privacy-first defaults**

## What it shows

- CPU and RAM utilization
- GPU utilization and best-effort CPU/GPU temperature
- One compact network cell: `↑512K ↓1.5M`
- Optional local usage summaries for CommandCode and OpenCode
- Optional quota usage for ChatGPT/Codex, Antigravity, and Claude Code

Unavailable data is always rendered as `--`; the application never substitutes a fabricated reading or a false `0%` value.

## Stable grid layout

The default `Grid` layout uses a fixed 3 × 2 matrix. Values are clipped or ellipsized inside their own cell, so changing values cannot move neighbouring pods.

```text
CPU | GPU         | GPT
RAM | ↑ upload ↓ download | provider
```

The widget is vertically centred inside the live taskbar height. Other layouts (`Compact`, `Minimal`, `TwoLine`, and `AgentCentric`) are also available from the tray menu.

## Controls

Taskbar-hosted applications do not have a conventional main window. Use either entry point:

1. **Tray icon (primary)** — left- or right-click the `TaskbarMonitor` notification-area icon. It opens a native Windows menu attached to the tray icon with Layout, Position, refresh interval, Metrics, Agents, display preferences, configuration, and Exit.
2. **Widget menu** — right-click the embedded widget for the WPF context menu.

The tray's **Advanced settings palette…** is an optional searchable fallback. It closes with **Esc**, **Ctrl+W**, **Alt+F4**, or the visible **Đóng** button.

## Privacy, accounts, and network access

### Default behavior

The sample configuration is privacy-first:

- `autostart` is `false`.
- Network-backed quota providers — ChatGPT/Codex, Antigravity, and Claude Code — are **disabled by default**.
- The application does not listen on TCP or UDP ports.
- Local usage readers open their SQLite sources read-only and do not read provider API-key tables.

### Optional provider usage

Enabling a provider in the tray menu or `Config/config.json` authorizes its reader to use credentials already managed by that provider's CLI on the local computer:

| Provider | Local credential source | Network destination | Credential handling |
|---|---|---|---|
| ChatGPT / Codex | `~/.codex/auth.json` | `https://chatgpt.com/backend-api/wham/usage` | Access token and account id are held in memory only for the request; never stored, logged, or refreshed by this app. |
| Antigravity | Windows Credential Manager entry `gemini:antigravity` | Google Cloud Code Assist endpoints | Uses only a still-valid access token. The app does **not** embed an OAuth client secret or exchange a refresh token. |
| Claude Code | Windows Credential Manager or `~/.claude/.credentials.json` | `https://api.anthropic.com/api/oauth/usage` | Optional and disabled by default; best-effort because the usage endpoint can rate-limit. |

These usage endpoints are provider-owned services and may change independently of this project. A failed response leaves the last known good result intact and displays unavailable data where necessary.

**Never put tokens, passwords, keys, or account identifiers in `Config/config.json`.** The committed example contains only feature toggles and colours.

## Configuration

At runtime the configuration lives beside the executable:

```text
Config/config.json
```

Example (safe defaults):

```json
{
  "metrics": {
    "cpu": true,
    "ram": true,
    "network": true,
    "disk": false,
    "gpu": true,
    "temperature": true
  },
  "agents": {
    "commandcode": true,
    "opencode": true,
    "codex": false,
    "antigravity": false,
    "claude": false
  },
  "updateIntervalSeconds": 1,
  "position": "Center",
  "layout": "Grid",
  "density": "Compact",
  "autostart": false
}
```

`position` accepts `Left`, `Center`, and `Right`. `Center` requests Deskband's automatic placement; Windows does not expose an exact arbitrary horizontal taskbar slot for hosted child controls.

## Performance and size

TaskbarMonitor is compact in UI surface, but it is not a tiny native Win32 utility: it uses WPF/.NET 10 and optional hardware-monitor libraries.

Measured on the development Windows 11 machine with the default Grid layout and network-backed providers disabled:

| Measure | Observed result |
|---|---:|
| Idle CPU | ~4.92% of one logical CPU over 60 seconds |
| Working set | ~192 MB |
| Private memory | ~103 MB |
| Threads / handles | 27 / 1,885 |
| Listener ports | 0 TCP, 0 UDP |
| Clean framework-dependent `win-x64` publish | ~32 MB |

CPU/RAM/network are sampled every 2 seconds. GPU utilization is cached for 5 seconds; disk and temperature readers are cached for 10 seconds to avoid continuous WMI/hardware-sensor work. These are machine-specific observations, not resource guarantees.

## Build, test, and run

### Requirements

- Windows 11 (the taskbar embedding target)
- .NET 10 SDK

### Build

```powershell
dotnet build TaskbarMonitor.sln --configuration Release --warnaserror
```

### Test

```powershell
dotnet test TaskbarMonitor.sln --configuration Release
```

### Run

```powershell
.\src\bin\Release\net10.0-windows10.0.26100\TaskbarMonitor.exe
```

Exit a running tray instance before rebuilding: Windows locks the executable while it is active.

### Publish a framework-dependent build

```powershell
dotnet publish src\TaskbarMonitor.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained false `
  --output dist\win-x64
```

## Architecture

```text
src/
  App.xaml.cs                    Single-instance lifecycle, autostart, Explorer recovery
  MainWindow.xaml.cs             Deskband host configuration
  TaskbarEmbedder.cs             Native taskbar child HWND embedding
  TrayIconService.cs             Native notification-area menu
  Metrics/                       OS counters, network, GPU, and temperature readers
  AgentUsage/                    Read-only local usage readers and optional quota clients
  UI/                            Fixed-grid widget, layouts, context menu, palette fallback
  Config/                        Local JSON configuration and hot reload

tests/                           Metrics, usage, layout, configuration, palette, recovery tests
```

## Security review gates

Before every public release, run:

```powershell
dotnet build TaskbarMonitor.sln --configuration Release --warnaserror
dotnet test TaskbarMonitor.sln --configuration Release --no-build
dotnet list TaskbarMonitor.sln package --vulnerable --include-transitive
git diff --check
```

The repository intentionally excludes build output, local plans/jobs, environment files, certificates, keys, and local secrets. See [SECURITY.md](SECURITY.md) for disclosure guidance.

## License

MIT. See [LICENSE](LICENSE).
