# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),  
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [Unreleased]

---

## [1.1.0] — 2026-03-18

### Added

- **CPU and RAM stats per service card** — live `Process.TotalProcessorTime` delta and `WorkingSet64` sampling at 1 Hz; displayed as `CPU 2.3%  RAM 312 MB`
- **`LogRingBuffer` class** (`Models/LogRingBuffer.cs`) — proper ring buffer replacing inline array fields in `ServiceItemViewModel`. O(1) push, O(k) `BuildText`
- **Full xUnit test suite** (`RollingUpdateManager.Tests/`) — 51 tests covering `LogRingBuffer`, `PortManager`, `HandoffService`, `PersistenceService`
- **`IsPersistent` flag on `HandoffState`** — normal app close keeps Java processes alive; persistent handoff has no 60 s expiry
- `ClearLog` button in log panel header — clears visible TextBox without discarding in-memory ring buffer

### Changed

- **UI redesign** — compact service cards with shared button styles, cleaner toolbar with DockPanel layout, `⊗ Kill port` button now visibly styled; `↺ Restart` and `⬆ Redeploy` labelled clearly so they are distinguishable
- **Self-update script** — `.bat` now runs in a **visible window** (errors are obvious), retries `move` up to 15× for AV-locked files, includes 2 s grace period, auto-restores old exe if new move fails
- Log tab switch clears TextBox **immediately** before loading new slot content (no ghost lines from previous slot)
- `MaxTextBoxLines` reduced from 500 → 300 (matches ring buffer size)
- `LogBufferMaxSize` reduced from 2 000 → 300 per slot
- Removed unused `Hardcodet.NotifyIcon.Wpf` NuGet dependency
- Startup stagger between AutoStart services: 2 s offset per JAR

### Fixed

- `TryReadAndDelete_Expired_NonPersistent` test — `WriteAsync` always overwrites `WrittenAt`; test now writes JSON directly with backdated timestamp
- Redundant `BeginInvoke` per log line removed; replaced with 50 ms flush timer (`_logFlushTimer`)

---

## [1.0.0] — 2026-03-01

### Added

- Initial release
- Blue/Green rolling update for Spring Boot JARs with zero downtime
- Embedded YARP reverse proxy per service (Kestrel on public port, dynamic target)
- `ServiceOrchestrator` — Start, Stop, Restart, Rolling Update, Watchdog loop
- `HealthCheckService` — HTTP `/actuator/health` + TCP port-open fallback
- `PortManager` — dynamic allocation from configurable range
- `PersistenceService` — atomic JSON write (temp → rename)
- `HandoffService` — zero-downtime exe swap via `handoff.json` + `ProcessJobObject`
- AutoStart with per-service stagger
- Windows Service mode (`--install` / `--uninstall` / `--service`)
- WPF dark UI with per-slot log tabs, proxy metrics (req/s, latency, error %)
- Deployment history tracking per service

[Unreleased]: https://github.com/matt-salis/rolling-update-manager/compare/v1.1.0...HEAD
[1.1.0]: https://github.com/matt-salis/rolling-update-manager/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/matt-salis/rolling-update-manager/releases/tag/v1.0.0
