# Live Core Studio — Windows Edition

An independently developed, all-in-one broadcast production and live-streaming workstation for Windows — built from scratch for weddings, corporate events, conferences and multi-camera studios.

> **Status: Phase 1 foundation (v0.1.0).** The application shell, the native media engine process, the versioned IPC protocol, and engine supervision (health monitoring + automatic restart) are implemented and tested. **No capture, switching, audio, streaming or recording features exist yet** — see [Roadmap](docs/ROADMAP.md).

## Architecture at a glance

```
┌──────────────────────────── LiveCoreStudio.exe (C# / .NET 10 / WPF) ────────────────────────────┐
│  App (composition root) → UI (MVVM views, theme) → Application (contracts) → Core (domain)     │
│                         → Infrastructure (settings, logs)                                      │
│                         → EngineClient (launch, pipe client, heartbeats, supervised restart)   │
└──────────────────────────────────────────┬───────────────────────────────────────────────────────┘
                                           │  Named pipe  \\.\pipe\LiveCore.Engine.<id>
                                           │  Protocol v1: 16-byte binary header + JSON control payloads
┌──────────────────────────────────────────┴──── LiveCore.MediaEngine.exe (C++20) ─────────────────┐
│  Engine host: lifecycle, master clock thread, command dispatch, parent-process watchdog         │
│  MediaCore: protocol framing, JSON, logging, Win32 overlapped named-pipe server                 │
│  (later) Video · Audio · Rendering (D3D11) · Devices · Streaming · Recording                    │
└──────────────────────────────────────────────────────────────────────────────────────────────────┘
```

The engine is a separate process so a driver crash or codec fault never takes down the operator's UI — the supervisor detects it within seconds and relaunches it. Details: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) · protocol spec: [shared/Protocol/PROTOCOL.md](shared/Protocol/PROTOCOL.md).

## Build on Windows

Requirements: Windows 10/11 x64, Visual Studio 2022 (Desktop C++ workload, Windows SDK), CMake ≥ 3.25, Ninja, .NET 10 SDK.

```powershell
# From a "Developer PowerShell for VS 2022"
./scripts/build.ps1                 # native engine + tests, .NET solution + tests
./out/managed/bin/LiveCoreStudio.App/Debug/net10.0-windows/LiveCoreStudio.exe
```

Or step by step: [docs/BUILD.md](docs/BUILD.md).

## Repository layout

| Path | Contents |
|---|---|
| `managed/` | C# projects: `Core`, `Application`, `Infrastructure`, `EngineClient`, `UI`, `App` |
| `native/` | C++20 projects: `LiveCore.MediaCore` (library), `LiveCore.MediaEngine` (executable) |
| `shared/Protocol/` | IPC protocol specification shared by both sides |
| `tests/` | `native/` (CTest) and `LiveCoreStudio.Tests` (unit + C#↔C++ integration) |
| `scripts/` | `build.ps1` (Windows), `build-ci-linux.sh` (portable CI) |
| `docs/` | Architecture, build guide, roadmap, phase reports |
| `third_party/licenses/` | Inventory and license texts of every third-party component |

Folders listed in the long-term plan (`LiveCore.Video`, `LiveCore.Audio`, `LiveCoreStudio.Modules`, `installer/`, …) are created when their first real code lands, not as empty placeholders.

## Originality

All application code, UI, icons and branding in this repository are original to Live Core Studio. No code is cloned, forked or copied from OBS Studio, vMix, Wirecast or any other application. Third-party libraries and SDKs are used only under their licenses and are inventoried in [`third_party/licenses`](third_party/licenses/README.md).

## License

Proprietary — see [LICENSE](LICENSE).
