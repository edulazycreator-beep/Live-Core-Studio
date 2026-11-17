# Changelog

All notable changes to Live Core Studio are documented here. Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow [SemVer](https://semver.org/).

## [Unreleased]

### Added
- **Phase 4 — video core.** Direct3D 11 rendering with WARP fallback; GPU test-pattern sources; Media Foundation USB/HDMI capture sources and device listing; Program/Preview switcher with CUT, AUTO (mix) and hot cut; Program/Preview monitor feeds over shared memory; protocol messages 9 and 16–27.
- Production page: live Program/Preview monitors with tally, bus rows, CUT (Space) / AUTO (Enter), transition duration, add/remove sources.
- Automatic restore of sources and bus selection after an engine restart.
- Tests: switcher unit tests; Windows integration tests that read real rendered pixels; restore-after-crash test.
- CI: UI smoke test runs the real app and screenshots every page.
- Dark scrollbar and ComboBox templates.
- GitHub Actions CI: Windows (MSVC + WPF, all tests, app artifact) and Linux jobs.

### Fixed
- Windows: the engine's last reply (`ShutdownAck`, protocol `Error`) could be lost because `DisconnectNamedPipe` discards unread data; the engine now lingers up to 1 s for the client to read and hang up.
- MSVC C4459 (clock alias shadowing `::clock_t`); missing `System.IO` imports in WPF view models.

### Changed
- WPF projects build with warnings as errors (now verified on Windows CI).

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
