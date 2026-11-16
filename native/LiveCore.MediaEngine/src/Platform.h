// Live Core Studio — LiveCore.MediaEngine
// Small OS helpers used by the engine host.
#pragma once

#include <cstdint>
#include <functional>
#include <stop_token>

namespace livecore::platform {

std::int64_t currentPid();
const char* name();

/// Blocks until process `pid` exits (true) or `st` is stopped (false).
/// Returns true immediately if the process does not exist.
bool waitForProcessExit(std::int64_t pid, std::stop_token st);

/// Installs Ctrl+C / SIGTERM handling that calls `onStop` once.
void installStopHandler(std::function<void()> onStop);

}  // namespace livecore::platform
