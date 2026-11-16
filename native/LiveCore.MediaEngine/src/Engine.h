// Live Core Studio — LiveCore.MediaEngine
// Engine host: owns the control endpoint, the master clock and lifecycle state.
#pragma once

#include "livecore/core/PipeServer.h"
#include "livecore/core/Protocol.h"

#include <atomic>
#include <chrono>
#include <cstdint>
#include <memory>
#include <optional>
#include <string>
#include <string_view>

namespace livecore::engine {

enum class EngineState { Starting, Running, Stopping };
std::string_view toString(EngineState s);

struct EngineOptions {
    std::string pipeName;
    std::optional<std::int64_t> parentPid;
    int clockRateHz = 60;
};

class Engine {
public:
    explicit Engine(EngineOptions options);
    ~Engine();

    /// Runs until Shutdown is received, requestStop() is called or the parent dies.
    /// Returns the process exit code.
    int run();

    /// Thread-safe and async-signal-safe enough for console/signal handlers.
    void requestStop();

    EngineState state() const { return state_.load(); }
    std::uint64_t clockTicks() const { return clockTicks_.load(); }
    std::int64_t uptimeMs() const;

private:
    enum class SessionEnd { ClientGone, ShutdownRequested };

    SessionEnd serve(ipc::PipeConnection& conn);
    bool send(ipc::PipeConnection& conn, protocol::MessageType type, std::uint32_t requestId, const std::string& payload);
    bool sendError(ipc::PipeConnection& conn, std::uint32_t requestId, std::string_view code, std::string_view message);
    std::string statusPayload() const;

    EngineOptions options_;
    std::unique_ptr<ipc::PipeServer> server_;
    std::atomic<EngineState> state_{EngineState::Starting};
    std::atomic<bool> stopRequested_{false};
    std::atomic<std::uint64_t> clockTicks_{0};
    std::atomic<int> connections_{0};
    std::chrono::steady_clock::time_point started_;
};

}  // namespace livecore::engine
