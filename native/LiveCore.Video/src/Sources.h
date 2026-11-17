// Live Core Studio — LiveCore.Video (Windows): video sources and the monitor feed writer.
#pragma once

#include "livecore/rendering/D3D.h"
#include "livecore/video/Switcher.h"

#include <mfidl.h>
#include <mfreadwrite.h>

#include <atomic>
#include <chrono>
#include <cstdint>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

namespace livecore::video {

using rendering::ComPtr;

class VideoSource {
public:
    VideoSource(SourceId id, std::string name) : id_(id), name_(std::move(name)) {}
    virtual ~VideoSource() = default;

    SourceId id() const { return id_; }
    const std::string& name() const { return name_; }
    virtual const char* kind() const = 0;
    virtual std::string status() const = 0;
    virtual std::uint32_t width() const = 0;
    virtual std::uint32_t height() const = 0;

    /// Render thread: bring the source texture up to date for this frame.
    virtual void render(rendering::Renderer& renderer, ID3D11DeviceContext* ctx, double timeSeconds) = 0;
    /// Render thread: the current picture, or null when nothing is available yet.
    virtual ID3D11ShaderResourceView* view() const = 0;

private:
    SourceId id_;
    std::string name_;
};

/// GPU-generated test signal (colour bars or a solid colour) with a moving liveness marker.
class TestPatternSource final : public VideoSource {
public:
    TestPatternSource(SourceId id, std::string name, rendering::Renderer::Pattern pattern, float r, float g, float b);
    bool init(ID3D11Device* device, std::uint32_t w, std::uint32_t h);

    const char* kind() const override { return "testpattern"; }
    std::string status() const override { return "live"; }
    std::uint32_t width() const override { return target_.width; }
    std::uint32_t height() const override { return target_.height; }
    void render(rendering::Renderer& renderer, ID3D11DeviceContext* ctx, double timeSeconds) override;
    ID3D11ShaderResourceView* view() const override { return target_.srv.Get(); }

private:
    rendering::Renderer::Pattern pattern_;
    float r_, g_, b_;
    rendering::RenderTexture target_;
};

/// USB / HDMI capture device through the Media Foundation source reader.
/// A dedicated thread reads frames (RGB32) into a CPU buffer; the render thread uploads the newest one.
class CaptureSource final : public VideoSource {
public:
    CaptureSource(SourceId id, std::string name, std::wstring symbolicLink);
    ~CaptureSource() override;
    void start();

    const char* kind() const override { return "device"; }
    std::string status() const override;
    std::uint32_t width() const override { return width_.load(); }
    std::uint32_t height() const override { return height_.load(); }
    void render(rendering::Renderer& renderer, ID3D11DeviceContext* ctx, double timeSeconds) override;
    ID3D11ShaderResourceView* view() const override { return srv_.Get(); }

private:
    void captureLoop(std::stop_token st);
    void setStatus(std::string s);

    std::wstring link_;
    std::jthread thread_;
    mutable std::mutex mutex_;
    std::string status_ = "starting";
    std::vector<std::uint8_t> frame_;  // tightly packed BGRA, top-down
    bool newFrame_ = false;
    std::chrono::steady_clock::time_point lastFrame_{};
    std::atomic<std::uint32_t> width_{0};
    std::atomic<std::uint32_t> height_{0};
    ComPtr<IMFMediaSource> mediaSource_;  // guarded by mutex_; shut down to unblock ReadSample

    // Render-thread GPU state.
    ComPtr<ID3D11Texture2D> texture_;
    ComPtr<ID3D11ShaderResourceView> srv_;
    std::uint32_t texWidth_ = 0;
    std::uint32_t texHeight_ = 0;
};

/// Single-writer shared-memory frame (seqlock). Layout: shared/Protocol/PROTOCOL.md "Monitor feeds".
class SharedFrameWriter {
public:
    ~SharedFrameWriter();
    bool create(const std::string& name, std::uint32_t width, std::uint32_t height);
    void write(const std::uint8_t* pixels, std::uint32_t rowPitch, std::uint64_t frameNumber);
    const std::string& name() const { return name_; }

private:
    std::string name_;
    HANDLE mapping_ = nullptr;
    std::uint8_t* view_ = nullptr;
    std::uint32_t width_ = 0;
    std::uint32_t height_ = 0;
};

std::wstring widen(const std::string& s);

}  // namespace livecore::video
