# Live Core Engine IPC Protocol — v1

Transport between **Live Core Studio** (C#/WPF) and **LiveCore.MediaEngine** (C++20).

## Transport

| Platform | Endpoint |
|---|---|
| Windows (production) | Named pipe `\\.\pipe\<PipeName>` — byte mode, duplex, local clients only (`PIPE_REJECT_REMOTE_CLIENTS`) |
| Linux/macOS (dev & CI only) | Unix domain socket at `$TMPDIR/CoreFxPipe_<PipeName>` (the path .NET `NamedPipeClientStream` uses on Unix) |

Default pipe name: `LiveCore.Engine.<sessionId>` — the app generates a unique session id per launch so two app instances never share an engine.

One client connection at a time. When the client disconnects, the engine keeps running and waits for a new connection.

## Framing

Every message is a 16-byte header followed by an optional payload. All integers are **little-endian**.

| Offset | Size | Field | Notes |
|---|---|---|---|
| 0 | 4 | `magic` | `0x5053434C` (bytes `L C S P`) |
| 4 | 2 | `version` | Protocol major version. Currently `1`. |
| 6 | 2 | `type` | `MessageType` (below) |
| 8 | 4 | `requestId` | Set by the client; echoed in the response. `0` for unsolicited engine events. |
| 12 | 4 | `payloadLength` | Bytes of payload. Max `1 048 576`. |

Payload is a UTF-8 JSON object with camelCase keys. Video/audio frames never travel over this pipe — media will move via D3D11 shared textures and shared-memory ring buffers (later phases). This channel is for control and telemetry.

A receiver that sees a wrong `magic`, an unsupported `version`, or an oversized payload must close the connection.

## Message types

| Id | Name | Direction | Payload |
|---|---|---|---|
| 1 | `Hello` | C → E | `{ "client": string, "protocolVersion": int }` |
| 2 | `HelloAck` | E → C | `{ "engine": string, "engineVersion": string, "protocolVersion": int, "pid": int, "platform": string }` |
| 3 | `Ping` | C → E | `{ "seq": int, "clientTimeMs": int }` |
| 4 | `Pong` | E → C | `{ "seq": int, "clientTimeMs": int, "engineUptimeMs": int }` |
| 5 | `GetStatus` | C → E | `{}` |
| 6 | `Status` | E → C | `{ "state": string, "uptimeMs": int, "pid": int, "clockTicks": int, "clockRateHz": int, "connections": int }` |
| 7 | `Shutdown` | C → E | `{ "reason": string }` |
| 8 | `ShutdownAck` | E → C | `{}` — engine exits after sending |
| 9 | `Ok` | E → C | `{}` — generic success reply |
| 16 | `ListVideoDevices` | C → E | `{}` |
| 17 | `VideoDevices` | E → C | `{ "devices": [ { "name": string, "id": string } ] }` — `id` is the Media Foundation symbolic link |
| 18 | `AddSource` | C → E | `{ "sourceId": int, "kind": "testpattern"\|"device", "name": string, "pattern": "bars"\|"solid", "color": "#RRGGBB", "deviceId": string }` — `sourceId` is chosen by the client (stable across engine restarts); `0` lets the engine pick |
| 19 | `SourceAdded` | E → C | `{ "sourceId": int }` |
| 20 | `RemoveSource` | C → E | `{ "sourceId": int }` → `Ok` |
| 22 | `SetPreview` | C → E | `{ "sourceId": int }` → `Ok` (`0` clears the bus) |
| 23 | `SetProgram` | C → E | `{ "sourceId": int }` → `Ok` — hot cut, aborts a running transition |
| 24 | `Cut` | C → E | `{}` → `Ok` — swaps Program and Preview |
| 25 | `AutoTransition` | C → E | `{ "durationMs": int }` → `Ok` — mix Program → Preview (0–10000 ms); `Error{transition_rejected}` if one is running or Preview is empty |
| 26 | `GetSwitcherState` | C → E | `{}` |
| 27 | `SwitcherState` | E → C | `{ "program", "preview", "transitioning", "position", "transitionMs", "outputWidth", "outputHeight", "fps", "adapter", "softwareRendering", "framesRendered", "framesDropped", "programMonitor", "previewMonitor", "monitorWidth", "monitorHeight", "sources": [ { "id", "name", "kind", "status", "width", "height" } ] }` |
| 255 | `Error` | E → C | `{ "code": string, "message": string }` |

Video messages return `Error{code:"video_unavailable"}` when the engine runs without the video pipeline (non-Windows builds, `--no-video`, or no usable Direct3D device).

### Rules

1. The first message on a connection must be `Hello`. Anything else gets `Error{code:"handshake_required"}`.
2. If `Hello.protocolVersion` differs from the engine's, the engine replies `Error{code:"protocol_mismatch"}` and closes.
3. Unknown message types get `Error{code:"unknown_message"}`; the connection stays open.

## Monitor feeds (shared memory)

The engine publishes downscaled Program and Preview pictures for the operator UI as named file mappings (`SwitcherState.programMonitor` / `previewMonitor`, e.g. `Local\LiveCore.<pipe>.Program`). Full-resolution media never travels this way; it stays on the GPU.

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | magic `0x464D434C` (`LCMF`) |
| 4 | 4 | version `1` |
| 8 | 4 | width |
| 12 | 4 | height |
| 16 | 4 | stride (bytes per row) |
| 20 | 4 | format (`1` = BGRA8, top-down) |
| 24 | 8 | sequence — odd while a frame is being written |
| 32 | 8 | frame number |
| 40 | 8 | timestamp (µs, engine steady clock) |
| 64 | stride × height | pixels |

Readers copy the pixels and accept the copy only if `sequence` was even and unchanged before and after (sequence lock).

## Health monitoring (client side)

* The client sends `Ping` every `HeartbeatIntervalMs` (default 1000 ms).
* A `Pong` not received within `HeartbeatTimeoutMs` (default 3000 ms) counts as a missed heartbeat.
* `MaxMissedHeartbeats` (default 3) consecutive misses, a broken pipe, or the engine process exiting marks the engine **Faulted**, which triggers supervised restart.

## Engine lifecycle (client side)

`Stopped → Starting → Running → (Faulted → Restarting → Starting …) → Stopping → Stopped`

Restart uses exponential backoff (500 ms, 1 s, 2 s, 4 s … capped at 10 s). If more than `MaxRestartsPerWindow` (default 5) restarts happen within `RestartWindowSeconds` (default 60), the supervisor stops trying and reports `Failed` so the operator can act.

The engine also watches its parent: started with `--parent-pid <pid>`, it exits by itself when the parent process dies, so a crashed UI never leaves an orphaned engine holding capture devices.

## Versioning

* Adding optional JSON fields: no version change. Readers ignore unknown fields.
* Adding a message type: no version change (old peers answer `unknown_message`).
* Changing the header or the meaning of an existing field: bump `version`.
