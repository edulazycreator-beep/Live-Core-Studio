# Roadmap

Status key: ✅ done and tested · 🟡 written, awaiting first Windows build · ⬜ not started

## Phase 1 — Foundation
- ✅ Repository, layered solution, CMake + presets, shared build settings
- ✅ C++20 engine executable, protocol v1, Win32 named-pipe server
- ✅ Documentation (architecture, build, protocol, third-party inventory)

## Phase 2 — Application shell
- 🟡 WPF shell: navigation rail, page host, status bar, dark title bar
- 🟡 Dashboard (engine control + live health), Diagnostics (live log), Settings
- ✅ MVVM view models (CommunityToolkit.Mvvm) — logic covered via the services they call
- ✅ Configuration management: JSON settings, validation, atomic save, corrupt-file recovery

## Phase 3 — Engine lifecycle
- ✅ Launch, handshake, protocol-version check
- ✅ Heartbeat health monitoring (RTT, uptime, master clock)
- ✅ Automatic restart with backoff and restart budget
- ✅ Parent-process watchdog, single-owner pipe, graceful shutdown

## Phase 4 — Video core (next)
- ⬜ D3D11 device + frame pool in `LiveCore.Rendering`
- ⬜ Device enumeration (Media Foundation) in `LiveCore.Devices`; protocol: `ListDevices`
- ⬜ USB/HDMI capture via Media Foundation source reader → D3D11 textures
- ⬜ Shared-texture preview into WPF (D3DImage / D3D11 → D3D9Ex interop)
- ⬜ Preview / Program buses, cut and mix transitions

## Later phases
- ⬜ Multiview, scenes & sources, compositing, graphics overlays
- ⬜ Audio: WASAPI capture, mixer, routing, EQ / compressor / limiter / gate, metering
- ⬜ Encoding (NVENC / Quick Sync / AMF via FFmpeg), RTMP / RTMPS / SRT streaming, recording + recovery
- ⬜ NDI and DeckLink integration
- ⬜ Media library, projects (SQLite), hardware diagnostics, installer
