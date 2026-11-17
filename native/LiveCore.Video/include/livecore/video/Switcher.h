// Live Core Studio — LiveCore.Video
// Program/Preview switcher state machine. Platform-independent and thread-safe:
// commands arrive on the IPC thread, advance() runs on the render thread.
#pragma once

#include <chrono>
#include <cstdint>
#include <mutex>
#include <set>

namespace livecore::video {

using SourceId = std::uint32_t;
inline constexpr SourceId kNoSource = 0;

class Switcher {
public:
    using Clock = std::chrono::steady_clock;

    /// What the program bus must show this frame: source `from`, mixed toward `to` by `mix` (0..1).
    struct ProgramFrame {
        SourceId from = kNoSource;
        SourceId to = kNoSource;
        float mix = 0.0f;
    };

    struct Snapshot {
        SourceId program = kNoSource;
        SourceId preview = kNoSource;
        bool transitioning = false;
        float position = 0.0f;  // 0..1 progress of the running transition
        int transitionMs = 0;
    };

    void addInput(SourceId id);
    /// Removing a source that is on a bus clears that bus (and aborts a transition using it).
    void removeInput(SourceId id);
    bool hasInput(SourceId id) const;

    /// False if the id is unknown. kNoSource clears the bus.
    bool setPreview(SourceId id);
    /// Hot cut straight to program; aborts a running transition.
    bool setProgram(SourceId id);
    /// Swaps program and preview immediately; aborts a running transition.
    void cut();
    /// Starts a mix from program to preview. False if a transition is running or preview is empty.
    bool autoTransition(std::chrono::milliseconds duration, Clock::time_point now);

    /// Called once per rendered frame. Completes a finished transition (program ← preview,
    /// preview ← old program) and returns what to render.
    ProgramFrame advance(Clock::time_point now);

    Snapshot snapshot(Clock::time_point now) const;

private:
    float positionLocked(Clock::time_point now) const;

    mutable std::mutex mutex_;
    std::set<SourceId> inputs_;
    SourceId program_ = kNoSource;
    SourceId preview_ = kNoSource;
    bool transitioning_ = false;
    Clock::time_point transitionStart_{};
    std::chrono::milliseconds transitionDuration_{0};
};

}  // namespace livecore::video
