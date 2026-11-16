#include "Engine.h"

#include "Platform.h"
#include "livecore/core/FlatJson.h"
#include "livecore/core/Log.h"

#include <algorithm>
#include <format>
#include <thread>
#include <vector>

#ifndef LIVECORE_ENGINE_VERSION
#define LIVECORE_ENGINE_VERSION "0.0.0-dev"
#endif

namespace livecore::engine {
namespace {
constexpr char kComp[] = "engine";
}

std::string_view toString(EngineState s) {
    switch (s) {
        case EngineState::Starting: return "starting";
        case EngineState::Running: return "running";
        case EngineState::Stopping: return "stopping";
    }
    return "unknown";
}

Engine::Engine(EngineOptions options) : options_(std::move(options)), started_(std::chrono::steady_clock::now()) {}

Engine::~Engine() = default;

std::int64_t Engine::uptimeMs() const {
    return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now() - started_).count();
}

void Engine::requestStop() {
    stopRequested_.store(true);
    if (server_) server_->stop();
}

int Engine::run() {
    log::info(kComp, "LiveCore.MediaEngine {} starting (pid {}, platform {}, protocol v{})", LIVECORE_ENGINE_VERSION,
              platform::currentPid(), platform::name(), protocol::kVersion);

    server_ = ipc::PipeServer::create(options_.pipeName);
    if (!server_->listen()) return 2;

    // Master clock: the engine's single timing reference. Later phases schedule
    // capture, compositing and output against it; for now it proves the
    // real-time thread runs and is reported through Status.
    std::jthread clock([this](std::stop_token st) {
        using steady = std::chrono::steady_clock;
        const auto period = std::chrono::nanoseconds(1'000'000'000LL / std::max(1, options_.clockRateHz));
        auto next = steady::now();
        while (!st.stop_requested()) {
            next += period;
            std::this_thread::sleep_until(next);
            clockTicks_.fetch_add(1, std::memory_order_relaxed);
            const auto now = steady::now();
            if (now - next > period * 10) next = now;  // resync after a stall instead of bursting
        }
    });

    std::jthread watchdog;
    if (options_.parentPid) {
        const auto parent = *options_.parentPid;
        watchdog = std::jthread([this, parent](std::stop_token st) {
            if (platform::waitForProcessExit(parent, st)) {
                log::warn(kComp, "parent process {} exited — shutting down", parent);
                requestStop();
            }
        });
    }

    state_.store(EngineState::Running);
    log::info(kComp, "running; waiting for control connection");

    while (!stopRequested_.load()) {
        auto conn = server_->accept();
        if (!conn) break;
        connections_.fetch_add(1);
        log::info(kComp, "client connected");
        const SessionEnd end = serve(*conn);
        conn->close();
        if (end == SessionEnd::ShutdownRequested) {
            stopRequested_.store(true);
            break;
        }
        log::info(kComp, "client disconnected; waiting for reconnection");
    }

    state_.store(EngineState::Stopping);
    log::info(kComp, "stopping");
    clock.request_stop();
    watchdog.request_stop();
    clock.join();
    if (watchdog.joinable()) watchdog.join();
    server_.reset();
    log::info(kComp, "stopped cleanly after {} ms", uptimeMs());
    return 0;
}

bool Engine::send(ipc::PipeConnection& conn, protocol::MessageType type, std::uint32_t requestId,
                  const std::string& payload) {
    const std::string frame = protocol::encodeFrame(type, requestId, payload);
    log::debug(kComp, "-> {} #{} {}", protocol::toString(type), requestId, payload);
    return conn.writeAll(frame.data(), frame.size());
}

bool Engine::sendError(ipc::PipeConnection& conn, std::uint32_t requestId, std::string_view code,
                       std::string_view message) {
    return send(conn, protocol::MessageType::Error, requestId,
                json::Writer().add("code", code).add("message", message).str());
}

std::string Engine::statusPayload() const {
    return json::Writer()
        .add("state", toString(state()))
        .add("uptimeMs", uptimeMs())
        .add("pid", platform::currentPid())
        .add("clockTicks", static_cast<std::int64_t>(clockTicks()))
        .add("clockRateHz", options_.clockRateHz)
        .add("connections", connections_.load())
        .str();
}

Engine::SessionEnd Engine::serve(ipc::PipeConnection& conn) {
    using protocol::MessageType;
    bool handshaken = false;
    std::vector<std::uint8_t> headerBuf(protocol::kHeaderSize);
    std::string payload;

    for (;;) {
        if (!conn.readExact(headerBuf.data(), headerBuf.size())) return SessionEnd::ClientGone;
        protocol::Header h;
        if (const auto err = protocol::decodeHeader(headerBuf.data(), h); err != protocol::HeaderError::None) {
            log::warn(kComp, "invalid frame header (code {}); dropping connection", static_cast<int>(err));
            return SessionEnd::ClientGone;
        }
        payload.assign(h.payloadLength, '\0');
        if (h.payloadLength > 0 && !conn.readExact(payload.data(), payload.size())) return SessionEnd::ClientGone;
        log::debug(kComp, "<- {} #{} {}", protocol::toString(h.type), h.requestId, payload);

        const auto body = json::Object::parse(payload.empty() ? std::string_view("{}") : std::string_view(payload));
        if (!body) {
            if (!sendError(conn, h.requestId, "bad_payload", "payload is not a JSON object")) return SessionEnd::ClientGone;
            continue;
        }

        if (!handshaken && h.type != MessageType::Hello) {
            if (!sendError(conn, h.requestId, "handshake_required", "first message must be Hello"))
                return SessionEnd::ClientGone;
            continue;
        }

        bool ok = true;
        switch (h.type) {
            case MessageType::Hello: {
                const auto version = body->getInt("protocolVersion").value_or(-1);
                if (version != protocol::kVersion) {
                    sendError(conn, h.requestId, "protocol_mismatch",
                              std::format("engine speaks protocol v{}, client sent v{}", protocol::kVersion, version));
                    return SessionEnd::ClientGone;
                }
                handshaken = true;
                log::info(kComp, "handshake with '{}'", body->getString("client").value_or("unknown"));
                ok = send(conn, MessageType::HelloAck, h.requestId,
                          json::Writer()
                              .add("engine", "LiveCore.MediaEngine")
                              .add("engineVersion", LIVECORE_ENGINE_VERSION)
                              .add("protocolVersion", static_cast<int>(protocol::kVersion))
                              .add("pid", platform::currentPid())
                              .add("platform", platform::name())
                              .str());
                break;
            }
            case MessageType::Ping:
                ok = send(conn, MessageType::Pong, h.requestId,
                          json::Writer()
                              .add("seq", body->getInt("seq").value_or(0))
                              .add("clientTimeMs", body->getInt("clientTimeMs").value_or(0))
                              .add("engineUptimeMs", uptimeMs())
                              .str());
                break;
            case MessageType::GetStatus:
                ok = send(conn, MessageType::Status, h.requestId, statusPayload());
                break;
            case MessageType::Shutdown:
                log::info(kComp, "shutdown requested by client (reason: {})", body->getString("reason").value_or("none"));
                state_.store(EngineState::Stopping);
                send(conn, MessageType::ShutdownAck, h.requestId, "{}");
                return SessionEnd::ShutdownRequested;
            default:
                ok = sendError(conn, h.requestId, "unknown_message",
                               std::format("message type {} is not supported", static_cast<int>(h.type)));
                break;
        }
        if (!ok) return SessionEnd::ClientGone;
    }
}

}  // namespace livecore::engine
