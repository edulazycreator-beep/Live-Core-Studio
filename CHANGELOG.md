# Changelog

All notable changes to Live Core Studio are documented here. Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow [SemVer](https://semver.org/).

## [0.1.0] — 2026-11-16 — Phase 1 foundation

### Added
- Native media engine `LiveCore.MediaEngine` (C++20): CLI, lifecycle, master clock thread, parent-process watchdog, clean Ctrl+C / console-close handling.
- `LiveCore.MediaCore`: engine control protocol v1 (binary header + JSON), Win32 overlapped named-pipe server, Unix-socket transport for CI, logger, flat JSON reader/writer.
- Managed engine client: frame codec, correlated request/response connection, process launcher, `EngineSupervisor` with heartbeat monitoring, exponential-backoff restarts and a restart budget.
- Settings: JSON store with validation, atomic save and corrupt-file recovery; file + in-memory application log.
- WPF shell (not yet compiled on Windows): navigation rail, Dashboard with engine control and live health, Diagnostics log, Settings; original dark charcoal / orange theme and icons.
- Tests: native unit tests (CTest), managed unit tests and C#↔C++ integration tests.
- Build: CMake presets (MSVC, Visual Studio, Linux dev, MinGW cross-check), `scripts/build.ps1`, `scripts/build-ci-linux.sh`.
- Docs: architecture, build guide, protocol spec, roadmap, Phase 1 report, third-party inventory.
