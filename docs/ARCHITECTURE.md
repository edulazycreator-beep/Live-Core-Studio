# Architecture

## Principles

1. **Two processes, one product.** The WPF application never touches capture devices, GPUs for compositing, encoders or the network for media. All of that lives in `LiveCore.MediaEngine`. A faulty driver or codec can crash the engine, never the operator's UI.
2. **The UI is a client.** Everything the UI knows about media comes through the versioned control protocol (and, in later phases, shared GPU textures for previews).
3. **Supervision by default.** The app launches, monitors and restarts the engine. The engine also watches the app and exits if the app dies, so no orphan keeps a camera locked.
4. **Honest features.** A feature appears in the UI only when it is implemented end to end and tested.

## Managed side (C# / .NET 10)

| Project | Target | Responsibility | Depends on |
|---|---|---|---|
| `LiveCoreStudio.Core` | `net10.0` | Domain types: engine state & health, settings model + validation, log contract | — |
| `LiveCoreStudio.Application` | `net10.0` | Use-case contracts the UI programs against (`IEngineController`, `ISettingsStore`) | Core |
| `LiveCoreStudio.Infrastructure` | `net10.0` | JSON settings store (atomic writes, corrupt-file recovery), file log, app paths | Application |
| `LiveCoreStudio.EngineClient` | `net10.0` | Protocol codec, pipe connection with request correlation, engine process launcher, `EngineSupervisor` | Application |
| `LiveCoreStudio.UI` | `net10.0-windows` (WPF) | Theme, original icons, MVVM view models (CommunityToolkit.Mvvm), views | Application, Infrastructure |
| `LiveCoreStudio.App` | `net10.0-windows` (WPF, exe) | Composition root, startup/shutdown, crash logging | everything |

Only `UI` and `App` are Windows-specific. Everything below them builds and is tested on any OS, which keeps the core logic fast to test in CI.

No DI container: the composition root in `App.xaml.cs` wires roughly a dozen objects by hand. Revisit when the object graph grows enough to justify one.

### Engine supervision (`EngineSupervisor`)

One background loop owns the lifecycle, so there is a single writer for engine state:

```
Stopped → Starting → Running ─┬─(Stop)──────────→ Stopping → Stopped
                              └─(crash / pipe lost / N missed heartbeats)
                                    → Faulted → Restarting (backoff) → Starting …
                                    → Failed   (restart budget exhausted, or never started)
```

* Heartbeat: `Ping` + `GetStatus` every `HeartbeatIntervalMs`; RTT, uptime and master-clock ticks are published as an immutable `EngineHealthSnapshot`.
* Backoff: 0.5 s, 1 s, 2 s … capped at 10 s; resets after a stable run of 10 s.
* Budget: at most `MaxRestartsPerWindow` restarts per `RestartWindowSeconds`, then `Failed` with an explanation — a crash loop never spins forever during a show.
* Each launch uses a fresh pipe name (`LiveCore.Engine.<appPid>.<random>`), so a stale engine can never be mistaken for the new one.

## Native side (C++20)

| Target | Kind | Responsibility |
|---|---|---|
| `LiveCore.MediaCore` | static lib | Logging, flat JSON, protocol framing, `PipeServer` (Win32 overlapped named pipe; Unix socket for dev/CI) |
| `LiveCore.MediaEngine` | exe | Engine host: CLI, lifecycle states, master clock thread, command dispatch, parent watchdog, Ctrl+C/console-close handling |

Future libraries (`LiveCore.Video`, `Audio`, `Rendering`, `Devices`, `Streaming`, `Recording`) will link into `MediaEngine` and register their command handlers with the dispatcher.

### Win32 pipe design

* `CreateNamedPipeW` with `FILE_FLAG_FIRST_PIPE_INSTANCE` (a second engine cannot hijack the name) and `PIPE_REJECT_REMOTE_CLIENTS` (local only).
* All I/O is overlapped and waits on `{ioEvent, stopEvent}`; `stop()` sets the stop event and outstanding I/O is cancelled with `CancelIoEx`. No thread is ever killed.
* One instance, reused after `DisconnectNamedPipe`: the app can reconnect without restarting the engine.

## Media data path (planned)

The control pipe carries control and telemetry only. Video will move as D3D11 shared textures (keyed mutex / NT handles) and audio through shared-memory ring buffers. Media never goes through the pipe.

## Cross-platform note

The product targets Windows only. The POSIX transport exists so the engine core and the C#↔C++ contract can be built and integration-tested on Linux CI machines. It deliberately uses the socket path .NET's `NamedPipeClientStream` expects on Unix, so the managed client code is identical on both.
