# Rolling Update Manager

> A lightweight Windows desktop application to manage multiple Spring Boot (or any JAR) services using a **Blue/Green** deployment strategy — zero-downtime rolling updates, no Docker, no Kubernetes.

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/download/dotnet/8)
[![Platform: Windows](https://img.shields.io/badge/Platform-Windows%2010%2F11-0078D4)](https://www.microsoft.com/windows)
[![Tests](https://img.shields.io/badge/Tests-51%20passing-brightgreen)](#testing)

---

## What it does

Rolling Update Manager runs as a Windows desktop app (or Windows Service) and acts as a **process supervisor + dynamic reverse proxy** for your JAR services.

```
Internet / LAN
      │
      ▼  :8080  (public, fixed)
┌─────────────────────────────────┐
│   YARP Reverse Proxy (Kestrel)  │  ← embedded, one per service
└────────────┬────────────────────┘
             │  dynamic target
    ┌────────┴────────┐
    │                 │
 java.exe          java.exe
  (BLUE)            (GREEN)
 :10001             :10002
    │
    └── only one receives traffic at a time
```

When you trigger a **rolling update**:

1. Start the new JAR on the standby slot (e.g. GREEN `:10002`)
2. Wait for health-check `GET /actuator/health` → `{"status":"UP"}`
3. Switch the proxy target atomically (zero downtime)
4. Drain 3 s (active connections finish naturally)
5. Kill the old process (BLUE `:10001` released)

If anything fails, the proxy stays on the old instance — **automatic rollback**.

---

## Features

| Feature                   | Details                                                       |
| ------------------------- | ------------------------------------------------------------- |
| Blue/Green rolling update | Zero-downtime swap via embedded YARP proxy                    |
| Auto-start                | Services start with the app, staggered 2 s apart              |
| Health checks             | `GET /actuator/health` (Spring Boot) + TCP port-open fallback |
| Process watchdog          | Detects externally killed processes and updates UI            |
| Self-update               | Update the manager EXE itself without stopping Java services  |
| Windows Service           | Run headless via `--install` / `--service` flags              |
| Live resource stats       | CPU % + RAM (MB) per service, sampled at 1 Hz                 |
| Proxy metrics             | Requests/s, average latency, error %                          |
| Per-slot log tabs         | All / Blue / Green with live streaming                        |
| Persistent handoff        | Close and reopen the app — Java processes keep running        |
| Deployment history        | Tracks past deploys per service                               |

---

## Requirements

- **Windows 10 or 11** (WPF is Windows-only)
- **.NET 8** — download from [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/8)  
  _Or use the self-contained single-file build which bundles the runtime._
- **Java** — any version your JARs require

---

## Quickstart

### Download a release

Grab the latest `RollingUpdateManager.exe` from [Releases](../../releases) and run it. No installer needed.

### Build from source

```bash
git clone https://github.com/matt-salis/rolling-update-manager.git
cd rolling-update-manager

# Run (framework-dependent, requires .NET 8 installed)
dotnet run --project RollingUpdateManager

# Or publish as a single self-contained EXE (~190 MB, no .NET install required)
dotnet publish RollingUpdateManager/RollingUpdateManager.csproj ^
    -c Release -r win-x64 --self-contained ^
    -p:PublishSingleFile=true ^
    -o publish/singlefile
```

---

## Adding a service

1. Click **`+`** in the toolbar
2. Fill in the dialog:

| Field          | Description                           | Example                   |
| -------------- | ------------------------------------- | ------------------------- |
| Name           | Display name in the UI                | `Payment API`             |
| JAR path       | Absolute path to the `.jar` file      | `C:\apps\payment.jar`     |
| Config file    | Optional `.properties` or `.yml`      | `C:\apps\application.yml` |
| Public port    | Fixed port exposed to clients         | `8080`                    |
| JVM args       | Extra JVM arguments                   | `-Xmx512m -Xms256m`       |
| Health path    | Spring Boot actuator path             | `/actuator/health`        |
| Health timeout | Seconds to wait for health-check      | `60`                      |
| Drain delay    | Ms before killing old instance        | `3000`                    |
| Auto-start     | Start this service when the app opens | ✓                         |

3. Click **Save**. The service appears in the left panel.

---

## Service card buttons

| Button         | Action                                                          | Downtime? |
| -------------- | --------------------------------------------------------------- | --------- |
| `▶`            | Start (initial boot)                                            | —         |
| `⏹`            | Stop                                                            | Yes       |
| `↺ Restart`    | Stop then start (hard restart)                                  | ~3 s      |
| `⬆ Redeploy`   | Rolling update with the **same JAR** (file was updated on disk) | None      |
| `⬆ Update JAR` | Rolling update with a **new JAR** file you select               | None      |
| `⊗ Kill port`  | Kill any process holding the public port                        | —         |

> **Restart vs Redeploy**: `Restart` stops the process then starts it again — there is a brief downtime window. `Redeploy` uses the Blue/Green mechanism: the old instance stays live until the new one is healthy.

---

## Updating the manager itself

The `⬆ Manager` button in the toolbar lets you update the `RollingUpdateManager.exe` binary while Java services keep running:

1. Build or download the new version of the EXE
2. Click **`⬆ Manager`** → select the new `.exe`
3. Confirm — a visible command window opens, waits for the current process to exit, swaps the files, and launches the new version
4. The new version reads `handoff.json` and re-attaches to the running Java processes

> **Tip**: Place the EXE in a user-writable folder (e.g. `C:\Users\you\AppData\Local\RollingUpdateManager\`) not in `Program Files`. The swap script needs write access to the folder containing the running EXE.

---

## Running as a Windows Service

```bash
# 1. Publish first
dotnet publish RollingUpdateManager/RollingUpdateManager.csproj ^
    -c Release -r win-x64 --self-contained -o C:\RollingUpdateManager\

# 2. Install (run as Administrator)
C:\RollingUpdateManager\RollingUpdateManager.exe --install

# 3. Uninstall
C:\RollingUpdateManager\RollingUpdateManager.exe --uninstall
```

Or with [NSSM](https://nssm.cc/) (recommended for more control):

```bash
nssm install RollingUpdateManager "C:\RollingUpdateManager\RollingUpdateManager.exe" "--service"
nssm set    RollingUpdateManager AppDirectory "C:\RollingUpdateManager\"
nssm start  RollingUpdateManager
```

In service mode the UI is not shown; the proxy and watchdog run headlessly.

---

## Data storage

All configuration is persisted in:

```
%APPDATA%\RollingUpdateManager\Data\services.json
```

Example:

```json
{
  "Services": [
    {
      "Id": "3fa85f64-...",
      "Name": "Payment API",
      "JarPath": "C:\\apps\\payment.jar",
      "PublicPort": 8080,
      "ActiveSlot": "Blue",
      "AutoStart": true,
      "HealthCheckPath": "/actuator/health",
      "HealthCheckTimeoutSeconds": 60,
      "DrainDelayMs": 3000
    }
  ],
  "PortRanges": { "RangeStart": 10000, "RangeEnd": 19999 }
}
```

The internal port range (`10000–19999` by default) is used to allocate Blue/Green ports automatically. Edit the JSON directly to change this range, then restart the app.

---

## Architecture

```
RollingUpdateManager/
├── Models/
│   ├── Models.cs               ServiceConfig, ServiceInstance, ServiceRuntimeState,
│   │                           ProxyMetrics, HandoffState, LogEntry, enums
│   └── LogRingBuffer.cs        O(1) push ring buffer for log display
├── Services/
│   ├── ServiceOrchestrator.cs  Core: Start/Stop/Restart/RollingUpdate + watchdog
│   ├── PersistenceService.cs   Atomic JSON persistence (write-temp → rename)
│   ├── PortManager.cs          Thread-safe dynamic port allocation
│   ├── ProcessLauncher.cs      Launches java.exe, pipes stdout/stderr
│   └── HealthCheckService.cs   HTTP /actuator/health + TCP fallback
├── Proxy/
│   └── ProxyManager.cs         Per-service Kestrel + YARP reverse proxy
├── Infrastructure/
│   ├── HandoffService.cs       Zero-downtime exe swap via handoff.json
│   ├── ProcessJobObject.cs     Win32 Job Object (KILL_ON_JOB_CLOSE)
│   └── WindowsServiceHost.cs   BackgroundService wrapper
├── ViewModels/
│   └── ViewModels.cs           MainViewModel, ServiceItemViewModel (MVVM)
├── Views/
│   ├── MainWindow.xaml/cs      Main window: service list + log panel
│   └── AddEditServiceDialog.xaml/cs  Add/edit service dialog
├── Converters/
│   └── Converters.cs           WPF value converters
└── App.xaml/cs                 DI container, startup modes
```

### Key design decisions

**Embedded reverse proxy** — YARP runs inside the same process; one Kestrel instance per service. No external Nginx/Caddy needed. The proxy target is switched atomically during rolling updates.

**Job Object (KILL_ON_JOB_CLOSE)** — Java processes are added to a Win32 Job Object. If the manager crashes, Windows kills the Java processes to avoid orphans. During a planned close or exe swap, `DetachAll()` is called first so they survive.

**Persistent handoff** — On normal close, `handoff.json` is written with all running PIDs. On next open, the manager re-attaches to those processes instead of restarting them. Services survive a manager restart with zero downtime.

**Log ring buffer** — Each service maintains three ring buffers (All, Blue, Green) of 300 entries each. Appending is O(1); rebuilding the TextBox on tab switch is O(k). A 50 ms flush timer batches all new log lines into a single `AppendText` per tick, preventing UI freeze under high-volume logging.

---

## Tech stack

| Component       | Library                                      | Version |
| --------------- | -------------------------------------------- | ------- |
| UI framework    | WPF (.NET 8)                                 | 8.0     |
| MVVM            | CommunityToolkit.Mvvm                        | 8.2.2   |
| Reverse proxy   | Yarp.ReverseProxy                            | 2.1.0   |
| Hosting         | Microsoft.Extensions.Hosting                 | 8.0.0   |
| Windows Service | Microsoft.Extensions.Hosting.WindowsServices | 8.0.0   |
| UI theme        | MaterialDesignThemes                         | 5.0.0   |
| Serialization   | System.Text.Json                             | 8.0.5   |
| Tests           | xUnit                                        | 2.7.0   |

---

## Testing

```bash
dotnet test RollingUpdateManager.Tests/RollingUpdateManager.Tests.csproj
```

| Test class                | Count  | Coverage                                                                   |
| ------------------------- | ------ | -------------------------------------------------------------------------- |
| `LogRingBufferTests`      | 18     | Construction, push, wrap-around, `BuildText`, `Clear`, `ToArray`, capacity |
| `PortManagerTests`        | 10     | Allocation, uniqueness, release/re-acquire, exhaustion, concurrency        |
| `HandoffServiceTests`     | 10     | Write/read cycle, deletion, 60 s expiry, persistent flag, corrupt JSON     |
| `PersistenceServiceTests` | 12     | Save/load round-trip, upsert, remove, atomic write, corrupt JSON recovery  |
| **Total**                 | **51** |                                                                            |

The test project links source files directly (no `ProjectReference` to the WPF project) so tests run headlessly without a display.

---

## Troubleshooting

**Services don't start**  
→ Check the log panel for the error. Common causes: wrong JAR path, port already in use (`⊗ Kill port`), missing Java on `PATH`.

**Health check always times out**  
→ Verify the Spring Boot app exposes `/actuator/health` (requires `spring-boot-starter-actuator`). Alternatively set Health path to `/` or any always-200 endpoint.

**Manager self-update fails**  
→ The EXE must be in a user-writable folder. Antivirus may lock the file; the update script retries 15× with 1 s intervals and shows a visible window so you can see errors.

**Services restart on manager reopen**  
→ On unexpected crash, `handoff.json` may not have been written. Services will AutoStart instead. This is expected — handoff is only written on graceful close.

---

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).

---

## License

[MIT](LICENSE) — © 2026 matt-salis

> This software is provided **"AS IS"**, without warranty of any kind. See the [LICENSE](LICENSE) file for the full text.
