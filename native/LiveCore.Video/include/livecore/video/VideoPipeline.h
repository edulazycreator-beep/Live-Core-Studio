// Live Core Studio — LiveCore.Video (Windows)
// Real-time video pipeline: sources → switcher → Program/Preview buses → monitor feeds.
#pragma once

#include "livecore/video/Switcher.h"

#include <atomic>
#include <cstdint>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <thread>

namespace livecore::rendering {
class D3DContext;
}

namespace livecore::video {

class VideoSource;
class SharedFrameWriter;

struct VideoConfig {
    std::uint32_t width = 1280;
    std::uint32_t height = 720;
    int fps = 30;
    std::uint32_t monitorWidth = 640;
    std::uint32_t monitorHeight = 360;
    /// Shared-memory name prefix for monitor feeds, e.g. "Local\\LiveCore.<pipe>".
    std::string monitorPrefix;
    bool forceWarp = false;
};

struct AddSourceRequest {
    SourceId id = kNoSource;  ///< Client-chosen id (stable across engine restarts); 0 = engine assigns.
    std::string kind;         ///< "testpattern" | "device"
    std::string name;
    std::string pattern = "bars";  ///< testpattern: "bars" | "solid"
    float r = 0.0f, g = 0.0f, b = 0.0f;
    std::string deviceId;  ///< device: Media Foundation symbolic link
};

class VideoPipeline {
public:
    explicit VideoPipeline(VideoConfig config);
    ~VideoPipeline();
    VideoPipeline(const VideoPipeline&) = delete;
    VideoPipeline& operator=(const VideoPipeline&) = delete;

    /// Creates the GPU device on the render thread and starts rendering.
    bool start(std::string& error);
    void stop();

    bool addSource(const AddSourceRequest& request, SourceId& assigned, std::string& error);
    bool removeSource(SourceId id);

    Switcher& switcher() { return switcher_; }

    /// Full switcher + pipeline state as a JSON object.
    std::string stateJson() const;

    /// Video capture devices present on this machine, as a JSON array.
    static std::string listDevicesJson(std::string& error);

private:
    void renderLoop(std::stop_token st);

    VideoConfig config_;
    Switcher switcher_;
    std::unique_ptr<rendering::D3DContext> d3d_;
    std::jthread thread_;

    mutable std::mutex sourcesMutex_;
    std::map<SourceId, std::shared_ptr<VideoSource>> sources_;
    SourceId nextId_ = 1000;

    std::unique_ptr<SharedFrameWriter> programMonitor_;
    std::unique_ptr<SharedFrameWriter> previewMonitor_;
    std::atomic<std::uint64_t> framesRendered_{0};
    std::atomic<std::uint64_t> framesDropped_{0};
    std::atomic<bool> running_{false};
};

}  // namespace livecore::video
