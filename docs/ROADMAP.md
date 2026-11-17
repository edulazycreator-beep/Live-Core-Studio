# Roadmap

Status key: ✅ done and tested · 🟡 builds on Windows CI, not yet reviewed on screen · ⬜ not started

## Phase 1 — Foundation
- ✅ Repository, layered solution, CMake + presets, shared build settings
- ✅ C++20 engine executable, protocol v1, Win32 named-pipe server
- ✅ Documentation (architecture, build, protocol, third-party inventory)

## Phase 2 — Application shell
- ✅ WPF shell: navigation rail, page host, status bar, dark title bar (screenshot-verified in CI)
- ✅ Dashboard (engine control + live health), Diagnostics (live log), Settings
- ✅ MVVM view models (CommunityToolkit.Mvvm) — logic covered via the services they call
- ✅ Configuration management: JSON settings, validation, atomic save, corrupt-file recovery

## Phase 3 — Engine lifecycle
- ✅ Launch, handshake, protocol-version check
- ✅ Heartbeat health monitoring (RTT, uptime, master clock)
- ✅ Automatic restart with backoff and restart budget
- ✅ Parent-process watchdog, single-owner pipe, graceful shutdown

## Phase 4 — Video core
- ✅ D3D11 device (GPU, WARP fallback), runtime-compiled shaders, render targets, GPU readback (`LiveCore.Rendering`)
- ✅ Program / Preview buses, CUT, AUTO mix, hot cut — portable switcher state machine with unit tests
- ✅ GPU test-pattern sources (bars / solid colour) with liveness marker
- ✅ Live Program / Preview monitors in the UI via shared memory (pixel-verified in CI at 30 fps)
- ✅ Operator setup restored automatically after an engine crash (verified in CI)
- ✅ Production page: monitors with tally, Program/Preview bus rows, CUT (Space) / AUTO (Enter), source management
- 🟡 USB / HDMI capture via Media Foundation (device listing + capture implemented; **not yet tested with a real device** — CI machines have none)
- ⬜ Full-resolution GPU shared textures to the UI (current monitors are 640×360 CPU copies — enough for operator monitoring)

## Phase 5 — Audio (next)
- ⬜ WASAPI capture/render, per-channel gain, mute, routing
- ⬜ DSP: EQ, compressor, limiter, noise gate; peak/RMS metering
- ⬜ Audio-follow-video, A/V sync offset per source

## Later phases
- ⬜ Multiview, scenes & sources, compositing, graphics overlays
- ⬜ Encoding (NVENC / Quick Sync / AMF via FFmpeg), RTMP / RTMPS / SRT streaming, recording + recovery
- ⬜ NDI and DeckLink integration
- ⬜ Media library, projects (SQLite), hardware diagnostics, installer
