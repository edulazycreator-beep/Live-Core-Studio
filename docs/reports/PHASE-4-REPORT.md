# Phase 4 Report — Video core (2026-11-17)

## Delivered

| Area | What |
|---|---|
| `LiveCore.Rendering` | D3D11 device (hardware GPU, WARP fallback; `LIVECORE_FORCE_WARP=1`), shaders compiled at start-up with `D3DCompile`, render targets, two-deep GPU→CPU readback |
| `LiveCore.Video` | Portable `Switcher` (Program/Preview, CUT, AUTO mix, hot cut, source removal), GPU test patterns, Media Foundation capture source + device listing, 30 fps render loop with drop counting, shared-memory monitor feeds (seqlock) |
| Engine | Protocol messages `Ok`, 16–27; `--no-video`, `--video-size`, `--fps` |
| Managed | `IProductionService` / `EngineProductionService` (restores the operator's setup after every engine restart), `MonitorFrameReader` |
| UI | Production page (monitors with tally, buses, CUT/AUTO with keyboard shortcuts, source management); dark scrollbars and ComboBox |

## Verification (GitHub Actions, `windows-latest`, no GPU → WARP)

| Check | Result |
|---|---|
| MSVC `/W4 /WX` build incl. D3D11 + Media Foundation code | ✅ |
| Native tests incl. switcher state machine (Windows + Linux + Wine) | ✅ |
| Program/Preview **pixels** correct after SetProgram/SetPreview, CUT, AUTO (blend observed mid-mix), source removal → black | ✅ |
| Render rate | ✅ 30 fps of 30, 0 dropped (software renderer, 1280×720) |
| Engine killed → restarted → same 3 sources, same Program/Preview, correct pixels | ✅ |
| Real app launched, every page rendered and screenshotted, no UI exceptions | ✅ |
| Managed tests total | ✅ 17 passed (+1 Linux-only) |

## Not verified / open

* **Real capture hardware.** Device enumeration and the Media Foundation capture path are implemented and compile, but CI runners have no cameras. First test on a machine with a USB webcam or HDMI capture card: Production → Add camera → Refresh → Add.
* Monitors are 640×360 CPU copies. Full-resolution GPU shared textures come with multiview/outputs.
* Capture formats are converted to RGB32 by Media Foundation's video processor; NV12/YUY2 GPU conversion for lower CPU use is planned.
