#include "Sources.h"

#include "livecore/core/Log.h"

#include <mfapi.h>
#include <mferror.h>

#include <algorithm>
#include <cstring>
#include <format>

namespace livecore::video {
namespace {
constexpr char kComp[] = "video";

// Shared monitor header. Offsets are part of the protocol (see PROTOCOL.md).
struct MonitorHeader {
    std::uint32_t magic;        // 0  'LCMF'
    std::uint32_t version;      // 4  1
    std::uint32_t width;        // 8
    std::uint32_t height;       // 12
    std::uint32_t stride;       // 16 bytes per row (width * 4)
    std::uint32_t format;       // 20 1 = BGRA8
    std::uint64_t sequence;     // 24 odd while writing
    std::uint64_t frameNumber;  // 32
    std::uint64_t timestampUs;  // 40 steady clock
    std::uint8_t reserved[16];  // 48..63
};
static_assert(sizeof(MonitorHeader) == 64);
constexpr std::uint32_t kMonitorMagic = 0x464D434C;  // "LCMF"
}  // namespace

std::wstring widen(const std::string& s) {
    if (s.empty()) return {};
    const int n = MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), nullptr, 0);
    std::wstring w(static_cast<std::size_t>(n), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), w.data(), n);
    return w;
}

// ---------------------------------------------------------------- TestPatternSource

TestPatternSource::TestPatternSource(SourceId id, std::string name, rendering::Renderer::Pattern pattern, float r,
                                     float g, float b)
    : VideoSource(id, std::move(name)), pattern_(pattern), r_(r), g_(g), b_(b) {}

bool TestPatternSource::init(ID3D11Device* device, std::uint32_t w, std::uint32_t h) {
    return target_.create(device, w, h);
}

void TestPatternSource::render(rendering::Renderer& renderer, ID3D11DeviceContext* ctx, double timeSeconds) {
    renderer.drawPattern(ctx, target_, pattern_, r_, g_, b_, static_cast<float>(timeSeconds));
}

// ---------------------------------------------------------------- CaptureSource

CaptureSource::CaptureSource(SourceId id, std::string name, std::wstring symbolicLink)
    : VideoSource(id, std::move(name)), link_(std::move(symbolicLink)) {}

CaptureSource::~CaptureSource() {
    thread_.request_stop();
    {
        std::lock_guard lock(mutex_);
        if (mediaSource_) mediaSource_->Shutdown();  // makes a blocking ReadSample return
    }
    if (thread_.joinable()) thread_.join();
}

void CaptureSource::start() {
    thread_ = std::jthread([this](std::stop_token st) { captureLoop(st); });
}

void CaptureSource::setStatus(std::string s) {
    std::lock_guard lock(mutex_);
    status_ = std::move(s);
}

std::string CaptureSource::status() const {
    std::lock_guard lock(mutex_);
    if (status_ == "live" && std::chrono::steady_clock::now() - lastFrame_ > std::chrono::seconds(2)) return "no signal";
    return status_;
}

void CaptureSource::captureLoop(std::stop_token st) {
    const HRESULT co = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    auto fail = [&](const char* what, HRESULT hr) {
        log::error(kComp, "capture '{}': {} failed (0x{:08X})", name(), what, static_cast<unsigned>(hr));
        setStatus(std::format("error: {} (0x{:08X})", what, static_cast<unsigned>(hr)));
    };

    ComPtr<IMFAttributes> attrs;
    ComPtr<IMFMediaSource> source;
    ComPtr<IMFAttributes> readerAttrs;
    ComPtr<IMFSourceReader> reader;
    HRESULT hr = MFCreateAttributes(&attrs, 2);
    if (SUCCEEDED(hr)) hr = attrs->SetGUID(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
    if (SUCCEEDED(hr)) hr = attrs->SetString(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK, link_.c_str());
    if (SUCCEEDED(hr)) hr = MFCreateDeviceSource(attrs.Get(), &source);
    if (FAILED(hr)) {
        fail("opening device", hr);
        if (SUCCEEDED(co)) CoUninitialize();
        return;
    }
    {
        std::lock_guard lock(mutex_);
        mediaSource_ = source;
    }

    hr = MFCreateAttributes(&readerAttrs, 1);
    if (SUCCEEDED(hr)) hr = readerAttrs->SetUINT32(MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING, TRUE);
    if (SUCCEEDED(hr)) hr = MFCreateSourceReaderFromMediaSource(source.Get(), readerAttrs.Get(), &reader);
    ComPtr<IMFMediaType> type;
    if (SUCCEEDED(hr)) hr = MFCreateMediaType(&type);
    if (SUCCEEDED(hr)) hr = type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
    if (SUCCEEDED(hr)) hr = type->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_RGB32);
    if (SUCCEEDED(hr)) hr = reader->SetCurrentMediaType(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), nullptr, type.Get());

    auto readSize = [&]() {
        ComPtr<IMFMediaType> current;
        UINT32 w = 0, h = 0;
        if (SUCCEEDED(reader->GetCurrentMediaType(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), &current)) &&
            SUCCEEDED(MFGetAttributeSize(current.Get(), MF_MT_FRAME_SIZE, &w, &h))) {
            width_ = w;
            height_ = h;
        }
    };
    if (SUCCEEDED(hr)) readSize();
    if (FAILED(hr) || width_ == 0 || height_ == 0) {
        fail("configuring RGB32 output", FAILED(hr) ? hr : E_UNEXPECTED);
    } else {
        log::info(kComp, "capture '{}' running at {}x{}", name(), width_.load(), height_.load());
        setStatus("live");
        while (!st.stop_requested()) {
            DWORD stream = 0, flags = 0;
            LONGLONG ts = 0;
            ComPtr<IMFSample> sample;
            hr = reader->ReadSample(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), 0, &stream, &flags, &ts, &sample);
            if (st.stop_requested()) break;
            if (FAILED(hr) || (flags & MF_SOURCE_READERF_ERROR)) {
                fail("reading frame (device disconnected?)", FAILED(hr) ? hr : E_FAIL);
                break;
            }
            if (flags & MF_SOURCE_READERF_ENDOFSTREAM) {
                setStatus("ended");
                break;
            }
            if (flags & MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED) readSize();
            if (!sample) continue;

            ComPtr<IMFMediaBuffer> buffer;
            if (FAILED(sample->ConvertToContiguousBuffer(&buffer))) continue;
            const std::uint32_t w = width_, h = height_;
            const std::size_t row = static_cast<std::size_t>(w) * 4;
            BYTE* scan0 = nullptr;
            LONG pitch = 0;
            ComPtr<IMF2DBuffer> buffer2d;
            bool locked2d = SUCCEEDED(buffer.As(&buffer2d)) && SUCCEEDED(buffer2d->Lock2D(&scan0, &pitch));
            DWORD length = 0;
            if (!locked2d) {
                if (FAILED(buffer->Lock(&scan0, nullptr, &length))) continue;
                pitch = static_cast<LONG>(row);
                if (length < row * h) { buffer->Unlock(); continue; }
            }
            {
                std::lock_guard lock(mutex_);
                frame_.resize(row * h);
                for (std::uint32_t y = 0; y < h; ++y)  // pitch may be negative (bottom-up); Lock2D handles orientation
                    std::memcpy(frame_.data() + y * row, scan0 + static_cast<std::ptrdiff_t>(y) * pitch, row);
                newFrame_ = true;
                lastFrame_ = std::chrono::steady_clock::now();
            }
            if (locked2d) buffer2d->Unlock2D();
            else buffer->Unlock();
        }
    }

    {
        std::lock_guard lock(mutex_);
        if (mediaSource_) mediaSource_->Shutdown();
        mediaSource_.Reset();
    }
    reader.Reset();
    if (SUCCEEDED(co)) CoUninitialize();
}

void CaptureSource::render(rendering::Renderer&, ID3D11DeviceContext* ctx, double) {
    std::lock_guard lock(mutex_);
    if (!newFrame_) return;
    newFrame_ = false;
    const std::uint32_t w = width_, h = height_;
    if (frame_.size() < static_cast<std::size_t>(w) * h * 4 || w == 0 || h == 0) return;

    if (!texture_ || texWidth_ != w || texHeight_ != h) {
        ComPtr<ID3D11Device> device;
        ctx->GetDevice(&device);
        D3D11_TEXTURE2D_DESC d{};
        d.Width = w;
        d.Height = h;
        d.MipLevels = d.ArraySize = 1;
        d.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        d.SampleDesc.Count = 1;
        d.Usage = D3D11_USAGE_DYNAMIC;
        d.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        d.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        texture_.Reset();
        srv_.Reset();
        if (FAILED(device->CreateTexture2D(&d, nullptr, &texture_)) ||
            FAILED(device->CreateShaderResourceView(texture_.Get(), nullptr, &srv_)))
            return;
        texWidth_ = w;
        texHeight_ = h;
    }
    D3D11_MAPPED_SUBRESOURCE m{};
    if (FAILED(ctx->Map(texture_.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &m))) return;
    const std::size_t row = static_cast<std::size_t>(w) * 4;
    for (std::uint32_t y = 0; y < h; ++y)
        std::memcpy(static_cast<std::uint8_t*>(m.pData) + static_cast<std::size_t>(y) * m.RowPitch, frame_.data() + y * row, row);
    ctx->Unmap(texture_.Get(), 0);
}

// ---------------------------------------------------------------- SharedFrameWriter

SharedFrameWriter::~SharedFrameWriter() {
    if (view_) UnmapViewOfFile(view_);
    if (mapping_) CloseHandle(mapping_);
}

bool SharedFrameWriter::create(const std::string& name, std::uint32_t width, std::uint32_t height) {
    const std::uint64_t size = sizeof(MonitorHeader) + static_cast<std::uint64_t>(width) * height * 4;
    mapping_ = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, static_cast<DWORD>(size >> 32),
                                  static_cast<DWORD>(size & 0xFFFFFFFFu), widen(name).c_str());
    if (!mapping_) {
        log::error(kComp, "CreateFileMapping '{}' failed ({})", name, GetLastError());
        return false;
    }
    view_ = static_cast<std::uint8_t*>(MapViewOfFile(mapping_, FILE_MAP_ALL_ACCESS, 0, 0, static_cast<SIZE_T>(size)));
    if (!view_) {
        log::error(kComp, "MapViewOfFile '{}' failed ({})", name, GetLastError());
        return false;
    }
    auto* h = reinterpret_cast<MonitorHeader*>(view_);
    std::memset(h, 0, sizeof *h);
    h->magic = kMonitorMagic;
    h->version = 1;
    h->width = width;
    h->height = height;
    h->stride = width * 4;
    h->format = 1;
    name_ = name;
    width_ = width;
    height_ = height;
    return true;
}

void SharedFrameWriter::write(const std::uint8_t* pixels, std::uint32_t rowPitch, std::uint64_t frameNumber) {
    if (!view_) return;
    auto* h = reinterpret_cast<MonitorHeader*>(view_);
    std::atomic_ref<std::uint64_t> seq(h->sequence);
    const std::uint64_t s = seq.load(std::memory_order_relaxed);
    seq.store(s + 1, std::memory_order_relaxed);  // odd: write in progress
    std::atomic_thread_fence(std::memory_order_release);
    std::uint8_t* dst = view_ + sizeof(MonitorHeader);
    const std::size_t row = static_cast<std::size_t>(width_) * 4;
    for (std::uint32_t y = 0; y < height_; ++y) std::memcpy(dst + y * row, pixels + static_cast<std::size_t>(y) * rowPitch, row);
    h->frameNumber = frameNumber;
    h->timestampUs = static_cast<std::uint64_t>(
        std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now().time_since_epoch()).count());
    seq.store(s + 2, std::memory_order_release);  // even: frame complete
}

}  // namespace livecore::video
