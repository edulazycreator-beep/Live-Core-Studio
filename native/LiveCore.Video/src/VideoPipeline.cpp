#include "livecore/video/VideoPipeline.h"

#include "Sources.h"
#include "livecore/core/FlatJson.h"
#include "livecore/core/Log.h"
#include "livecore/rendering/D3D.h"

#include <mfapi.h>
#include <timeapi.h>

#include <algorithm>
#include <format>
#include <future>
#include <vector>

namespace livecore::video {
namespace {
constexpr char kComp[] = "video";
}

VideoPipeline::VideoPipeline(VideoConfig config) : config_(std::move(config)) {}

VideoPipeline::~VideoPipeline() { stop(); }

bool VideoPipeline::start(std::string& error) {
    if (running_) return true;
    const HRESULT mf = MFStartup(MF_VERSION, MFSTARTUP_NOSOCKET);
    if (FAILED(mf)) log::warn(kComp, "MFStartup failed (0x{:08X}); capture devices unavailable", static_cast<unsigned>(mf));

    programMonitor_ = std::make_unique<SharedFrameWriter>();
    previewMonitor_ = std::make_unique<SharedFrameWriter>();
    if (!programMonitor_->create(config_.monitorPrefix + ".Program", config_.monitorWidth, config_.monitorHeight) ||
        !previewMonitor_->create(config_.monitorPrefix + ".Preview", config_.monitorWidth, config_.monitorHeight)) {
        error = "cannot create monitor shared memory";
        return false;
    }

    std::promise<std::string> ready;
    auto readyFuture = ready.get_future();
    thread_ = std::jthread([this, &ready](std::stop_token st) {
        std::string err;
        d3d_ = rendering::D3DContext::create(config_.forceWarp, err);
        if (!d3d_) {
            ready.set_value(err);
            return;
        }
        ready.set_value({});
        renderLoop(st);
    });
    error = readyFuture.get();
    if (!error.empty()) {
        thread_.join();
        return false;
    }
    running_ = true;
    log::info(kComp, "pipeline running {}x{} @ {} fps on '{}'{}", config_.width, config_.height, config_.fps,
              d3d_->adapterName(), d3d_->isWarp() ? " (software)" : "");
    return true;
}

void VideoPipeline::stop() {
    if (thread_.joinable()) {
        thread_.request_stop();
        thread_.join();
    }
    {
        std::lock_guard lock(sourcesMutex_);
        sources_.clear();
    }
    if (running_) MFShutdown();
    running_ = false;
}

bool VideoPipeline::addSource(const AddSourceRequest& req, SourceId& assigned, std::string& error) {
    if (!running_ || !d3d_) { error = "video pipeline is not running"; return false; }
    std::shared_ptr<VideoSource> source;
    SourceId id = req.id;
    {
        std::lock_guard lock(sourcesMutex_);
        if (id == kNoSource) {
            while (sources_.contains(nextId_)) ++nextId_;
            id = nextId_++;
        } else if (sources_.contains(id)) {
            error = std::format("source id {} already exists", id);
            return false;
        }
    }
    const std::string name = req.name.empty() ? std::format("Source {}", id) : req.name;

    if (req.kind == "testpattern") {
        const auto pattern = req.pattern == "solid" ? rendering::Renderer::Pattern::Solid : rendering::Renderer::Pattern::Bars;
        auto tp = std::make_shared<TestPatternSource>(id, name, pattern, req.r, req.g, req.b);
        if (!tp->init(d3d_->device(), config_.width, config_.height)) { error = "cannot allocate source texture"; return false; }
        source = tp;
    } else if (req.kind == "device") {
        if (req.deviceId.empty()) { error = "deviceId is required"; return false; }
        auto cap = std::make_shared<CaptureSource>(id, name, widen(req.deviceId));
        cap->start();
        source = cap;
    } else {
        error = std::format("unknown source kind '{}'", req.kind);
        return false;
    }

    {
        std::lock_guard lock(sourcesMutex_);
        sources_[id] = source;
    }
    switcher_.addInput(id);
    assigned = id;
    log::info(kComp, "source {} added: '{}' ({})", id, name, source->kind());
    return true;
}

bool VideoPipeline::removeSource(SourceId id) {
    std::shared_ptr<VideoSource> removed;
    {
        std::lock_guard lock(sourcesMutex_);
        auto it = sources_.find(id);
        if (it == sources_.end()) return false;
        removed = std::move(it->second);
        sources_.erase(it);
    }
    switcher_.removeInput(id);
    log::info(kComp, "source {} removed", id);
    return true;  // `removed` is released here or by the render thread's snapshot
}

void VideoPipeline::renderLoop(std::stop_token st) {
    const HRESULT co = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    timeBeginPeriod(1);  // 1 ms sleep granularity for frame pacing

    std::string err;
    rendering::Renderer renderer;
    rendering::RenderTexture program, preview, programMon, previewMon;
    rendering::Readback programRb, previewRb;
    ID3D11Device* dev = d3d_->device();
    ID3D11DeviceContext* ctx = d3d_->context();
    if (!renderer.init(dev, err) || !program.create(dev, config_.width, config_.height) ||
        !preview.create(dev, config_.width, config_.height) ||
        !programMon.create(dev, config_.monitorWidth, config_.monitorHeight) ||
        !previewMon.create(dev, config_.monitorWidth, config_.monitorHeight) ||
        !programRb.create(dev, config_.monitorWidth, config_.monitorHeight) ||
        !previewRb.create(dev, config_.monitorWidth, config_.monitorHeight)) {
        log::error(kComp, "render setup failed: {}", err.empty() ? "resource allocation" : err);
        timeEndPeriod(1);
        if (SUCCEEDED(co)) CoUninitialize();
        return;
    }

    using clock = std::chrono::steady_clock;
    const auto period = std::chrono::nanoseconds(1'000'000'000LL / std::max(1, config_.fps));
    const auto t0 = clock::now();
    auto next = t0;
    std::vector<std::shared_ptr<VideoSource>> snapshot;
    std::map<SourceId, ID3D11ShaderResourceView*> views;

    while (!st.stop_requested()) {
        next += period;
        std::this_thread::sleep_until(next);
        const auto now = clock::now();
        if (now - next > period) {  // fell behind: count it and resync instead of bursting
            framesDropped_ += static_cast<std::uint64_t>((now - next) / period);
            next = now;
        }
        const double t = std::chrono::duration<double>(now - t0).count();

        snapshot.clear();
        {
            std::lock_guard lock(sourcesMutex_);
            for (auto& [id, s] : sources_) snapshot.push_back(s);
        }
        views.clear();
        for (auto& s : snapshot) {
            s->render(renderer, ctx, t);
            views[s->id()] = s->view();
        }
        auto lookup = [&](SourceId id) -> ID3D11ShaderResourceView* {
            auto it = views.find(id);
            return it == views.end() ? nullptr : it->second;
        };

        const auto frame = switcher_.advance(now);
        renderer.drawMix(ctx, program, lookup(frame.from), lookup(frame.to), frame.mix);
        auto* pv = lookup(switcher_.snapshot(now).preview);
        renderer.drawMix(ctx, preview, pv, pv, 0.0f);

        // Downscaled monitor feeds for the operator UI.
        const std::uint64_t n = framesRendered_.load() + 1;
        renderer.drawMix(ctx, programMon, program.srv.Get(), program.srv.Get(), 0.0f);
        renderer.drawMix(ctx, previewMon, preview.srv.Get(), preview.srv.Get(), 0.0f);
        programRb.capture(ctx, programMon.texture.Get(),
                          [&](const std::uint8_t* px, UINT pitch, UINT, UINT) { programMonitor_->write(px, pitch, n); });
        previewRb.capture(ctx, previewMon.texture.Get(),
                          [&](const std::uint8_t* px, UINT pitch, UINT, UINT) { previewMonitor_->write(px, pitch, n); });
        framesRendered_ = n;
    }

    snapshot.clear();
    timeEndPeriod(1);
    if (SUCCEEDED(co)) CoUninitialize();
}

std::string VideoPipeline::stateJson() const {
    const auto s = switcher_.snapshot(std::chrono::steady_clock::now());
    std::vector<std::string> items;
    {
        std::lock_guard lock(sourcesMutex_);
        for (const auto& [id, src] : sources_) {
            items.push_back(json::Writer()
                                .add("id", static_cast<std::int64_t>(id))
                                .add("name", src->name())
                                .add("kind", src->kind())
                                .add("status", src->status())
                                .add("width", static_cast<std::int64_t>(src->width()))
                                .add("height", static_cast<std::int64_t>(src->height()))
                                .str());
        }
    }
    return json::Writer()
        .add("program", static_cast<std::int64_t>(s.program))
        .add("preview", static_cast<std::int64_t>(s.preview))
        .add("transitioning", s.transitioning)
        .add("position", static_cast<double>(s.position))
        .add("transitionMs", s.transitionMs)
        .add("outputWidth", static_cast<std::int64_t>(config_.width))
        .add("outputHeight", static_cast<std::int64_t>(config_.height))
        .add("fps", config_.fps)
        .add("adapter", d3d_ ? d3d_->adapterName() : std::string())
        .add("softwareRendering", d3d_ && d3d_->isWarp())
        .add("framesRendered", static_cast<std::int64_t>(framesRendered_.load()))
        .add("framesDropped", static_cast<std::int64_t>(framesDropped_.load()))
        .add("programMonitor", programMonitor_ ? programMonitor_->name() : std::string())
        .add("previewMonitor", previewMonitor_ ? previewMonitor_->name() : std::string())
        .add("monitorWidth", static_cast<std::int64_t>(config_.monitorWidth))
        .add("monitorHeight", static_cast<std::int64_t>(config_.monitorHeight))
        .addRaw("sources", json::array(items))
        .str();
}

std::string VideoPipeline::listDevicesJson(std::string& error) {
    const HRESULT co = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    std::vector<std::string> items;
    ComPtr<IMFAttributes> attrs;
    IMFActivate** devices = nullptr;
    UINT32 count = 0;
    HRESULT hr = MFCreateAttributes(&attrs, 1);
    if (SUCCEEDED(hr)) hr = attrs->SetGUID(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
    if (SUCCEEDED(hr)) hr = MFEnumDeviceSources(attrs.Get(), &devices, &count);
    if (FAILED(hr)) {
        error = std::format("MFEnumDeviceSources failed (0x{:08X})", static_cast<unsigned>(hr));
    } else {
        for (UINT32 i = 0; i < count; ++i) {
            WCHAR* name = nullptr;
            WCHAR* link = nullptr;
            UINT32 len = 0;
            if (SUCCEEDED(devices[i]->GetAllocatedString(MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME, &name, &len)) &&
                SUCCEEDED(devices[i]->GetAllocatedString(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK, &link, &len))) {
                items.push_back(json::Writer().add("name", rendering::narrow(name)).add("id", rendering::narrow(link)).str());
            }
            CoTaskMemFree(name);
            CoTaskMemFree(link);
            devices[i]->Release();
        }
        CoTaskMemFree(devices);
    }
    if (SUCCEEDED(co)) CoUninitialize();
    return json::array(items);
}

}  // namespace livecore::video
