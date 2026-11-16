# Phase 1 Report — Foundation (2026-11-16)

## 1. Development environment inspected

The initial build ran on a **Linux** cloud workspace, not a Windows machine.

| Item | Found |
|---|---|
| OS | Ubuntu 24.04.5 LTS, x86_64 |
| .NET SDK | Not present → installed 10.0.115 from the Ubuntu archive |
| Visual Studio / MSVC / Windows SDK | **Not available** (Linux host) |
| CMake | 3.28.3 |
| C++ compilers | GCC 13.3, Clang 18.1; MinGW-w64 GCC (posix) installed for Win32 cross-compile |
| Git | 2.43.0 |
| FFmpeg | CLI 6.1.1 only; **no development libraries** |
| NDI / DeckLink SDKs | Not available |
| Wine | Installed (wine64) to execute the Windows build of the native tests |
| Network | NuGet (`api.nuget.org`) and `dot.net` **blocked** by the workspace egress policy |

## 2. What was built

* New repository `live-core-studio-windows` created with `git init`. Nothing cloned or forked.
* Native: `LiveCore.MediaCore` (static lib) + `LiveCore.MediaEngine` (exe), C++20, `-Wall -Wextra -Wpedantic -Wshadow -Wconversion -Werror` (MSVC: `/W4 /WX /permissive-`).
* Managed: `Core`, `Application`, `Infrastructure`, `EngineClient` (net10.0) and `UI`, `App` (net10.0-windows, WPF, CommunityToolkit.Mvvm).
* IPC: protocol v1 over Windows named pipes (Unix socket on dev/CI), spec in `shared/Protocol/PROTOCOL.md`.
* Supervision: heartbeat monitoring, crash detection, backoff restarts, restart budget, parent watchdog.

## 3. Verification results

| Check | Result |
|---|---|
| Native build, Linux (GCC, warnings as errors) | ✅ Pass, 0 warnings |
| Native unit tests, Linux (CTest) | ✅ 10 tests / 53 checks pass |
| Native build, **Windows target** (MinGW-w64 cross-compile, warnings as errors) | ✅ Pass, PE32+ executables |
| Native unit tests, **Windows build under Wine** — includes Win32 named-pipe round trip over two connections, overlapped I/O cancellation | ✅ 53/53 checks pass |
| `LiveCore.MediaEngine.exe` under Wine | ✅ Starts, listens on `\\.\pipe\…` |
| Managed core libraries build (warnings as errors, `latest-recommended` analyzers) | ✅ Pass, 0 warnings |
| Managed tests: 7 unit + 8 C#↔C++ integration | ✅ 15/15 pass, stable over 6 consecutive runs |
| Full pipeline from a clean `out/` (`scripts/build-ci-linux.sh`) | ✅ Exit 0 |
| **WPF projects (`UI`, `App`) build** | ⚠️ **Not compiled yet** — requires Windows + NuGet |
| MSVC build of the native engine | ⚠️ Not run (no MSVC here); MinGW + Wine cover the Win32 API usage |

### Integration tests (real engine process, real pipe)

1. Handshake → Ping → Status (master clock advances at the configured rate) → 20 concurrent requests correctly correlated → Shutdown → exit code 0.
2. Engine rejects messages before `Hello` (`handshake_required`) and wrong protocol versions (`protocol_mismatch`), then still serves a new, correct client.
3. A second engine cannot take a pipe name already in use (exit code 2); the first is unaffected.
4. Engine exits cleanly within 3 s when its parent process dies.
5. Supervisor: start → heartbeat telemetry (RTT < 100 ms) → graceful stop → process gone.
6. Supervisor: engine killed → `Faulted → Restarting → Running` with a new PID, restart count 1.
7. Supervisor: restart budget exhausted → `Failed` with explanation; operator can start again.
8. Supervisor: missing engine executable → clear error, state `Failed`, no restart loop.

## 4. Known gaps and first actions on a Windows machine

1. Run `scripts/build.ps1`. Expect possible small compile fixes in the **WPF projects**, since they were written without a compiler. Then set `TreatWarningsAsErrors` back to `true` in `LiveCoreStudio.UI.csproj` and `LiveCoreStudio.App.csproj`.
2. Confirm the MSVC build of the engine (`/W4 /WX`).
3. Visually review the shell (Dashboard, Diagnostics, Settings). The `ComboBox` in Settings still uses the default WPF style and needs a dark template.
4. On Linux only, an engine killed with SIGKILL leaves its socket file in `/tmp`. Windows pipes are kernel objects and disappear with the process.

## 5. Not implemented (by design, Phase 1)

Capture, Preview/Program, switching, multiview, compositing, audio, streaming, recording, NDI, DeckLink, media library, projects/SQLite, hardware diagnostics, installer. None of these appear in the UI.
