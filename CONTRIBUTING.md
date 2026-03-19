# Contributing to Rolling Update Manager

Thank you for your interest in contributing! All contributions are welcome — bug reports, feature ideas, documentation improvements, and pull requests.

---

## Ground rules

- Be respectful. See [GitHub Community Guidelines](https://docs.github.com/en/site-policy/github-terms/github-community-guidelines).
- One concern per pull request. Keep changes focused.
- Write or update tests for any logic change in `Services/`, `Infrastructure/`, or `Models/`.
- Keep the public API of `ServiceOrchestrator` stable — it is the central contract.

---

## Development setup

**Requirements**

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8)
- Windows 10/11 (WPF is Windows-only)
- A Java runtime if you want to test with real Spring Boot JARs

**Build**

```bash
git clone https://github.com/matt-salis/rolling-update-manager.git
cd rolling-update-manager
dotnet build RollingUpdateManager.sln
```

**Run tests**

```bash
dotnet test RollingUpdateManager.Tests/RollingUpdateManager.Tests.csproj
```

All 51 tests should pass. The test project links source files directly (no WPF runtime required in the headless runner).

---

## Project layout

```
RollingUpdateManager/          ← main WPF application
  Models/                      ← data model (no dependencies)
  Services/                    ← orchestration, ports, health checks
  Infrastructure/               ← handoff, process job object, Windows service
  Proxy/                       ← YARP-based embedded reverse proxy
  ViewModels/                  ← MVVM, ring-buffer log display
  Views/                       ← XAML windows and dialogs
  Converters/                  ← WPF value converters
RollingUpdateManager.Tests/    ← xUnit test project
```

---

## What to work on

Check the [Issues](../../issues) tab. Good first issues are tagged **`good first issue`**.

Areas that would benefit from contributions:

- Multi-instance health check strategies (e.g. custom readiness URL)
- Export / import service configuration
- Log search / filter within the log panel
- Dark/light theme toggle
- Linux support via Avalonia UI (medium effort, large impact)
- CI pipeline (GitHub Actions)

---

## Pull request checklist

- [ ] `dotnet build` passes with 0 errors and 0 warnings
- [ ] `dotnet test` passes (51/51 green)
- [ ] New public types/methods have XML doc comments
- [ ] `CHANGELOG.md` updated under `[Unreleased]`
- [ ] No hardcoded paths, credentials, or machine-specific data

---

## Commit style

Use [Conventional Commits](https://www.conventionalcommits.org/):

```
feat: add log search bar in log panel
fix: prevent duplicate ports when two services share a range
docs: expand health check section in README
test: add edge case for PortManager.AcquirePortExact
refactor: extract ProcessLifecycle from ServiceOrchestrator
```

---

## Reporting bugs

Open an [Issue](../../issues/new) with:

1. Rolling Update Manager version (shown in title bar Help → About)
2. Windows version
3. Steps to reproduce
4. Expected vs. actual behaviour
5. Paste any relevant log lines (mask JAR paths if needed)
