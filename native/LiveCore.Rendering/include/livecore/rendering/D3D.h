// Live Core Studio — LiveCore.Rendering (Windows, Direct3D 11)
// Device creation, GPU render targets, the shared shader set and CPU readback.
#pragma once

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <d3d11.h>
#include <wrl/client.h>

#include <cstdint>
#include <functional>
#include <memory>
#include <string>

namespace livecore::rendering {

using Microsoft::WRL::ComPtr;

/// D3D11 device + immediate context. Uses the hardware GPU when available and falls
/// back to WARP (software rasterizer) on machines without one (servers, CI).
class D3DContext {
public:
    /// forceWarp: always use WARP (also enabled by env LIVECORE_FORCE_WARP=1).
    static std::unique_ptr<D3DContext> create(bool forceWarp, std::string& error);

    ID3D11Device* device() const { return device_.Get(); }
    ID3D11DeviceContext* context() const { return context_.Get(); }
    const std::string& adapterName() const { return adapter_; }
    bool isWarp() const { return warp_; }

private:
    ComPtr<ID3D11Device> device_;
    ComPtr<ID3D11DeviceContext> context_;
    std::string adapter_;
    bool warp_ = false;
};

/// A BGRA texture usable both as render target and shader input.
struct RenderTexture {
    ComPtr<ID3D11Texture2D> texture;
    ComPtr<ID3D11RenderTargetView> rtv;
    ComPtr<ID3D11ShaderResourceView> srv;
    UINT width = 0;
    UINT height = 0;

    bool create(ID3D11Device* device, UINT w, UINT h);
};

/// Shaders and fixed pipeline state shared by every draw.
class Renderer {
public:
    bool init(ID3D11Device* device, std::string& error);

    /// Draws lerp(a, b, mix) scaled to fill `target`. Null inputs render black.
    void drawMix(ID3D11DeviceContext* ctx, const RenderTexture& target, ID3D11ShaderResourceView* a,
                 ID3D11ShaderResourceView* b, float mix);

    enum class Pattern { Solid = 0, Bars = 1 };
    /// Generates a test signal: solid colour or colour bars, plus a moving marker in the bottom strip.
    void drawPattern(ID3D11DeviceContext* ctx, const RenderTexture& target, Pattern pattern, float r, float g, float b,
                     float timeSeconds);

    ID3D11ShaderResourceView* black() const { return black_.Get(); }

private:
    void bindCommon(ID3D11DeviceContext* ctx, const RenderTexture& target);

    ComPtr<ID3D11VertexShader> vs_;
    ComPtr<ID3D11PixelShader> psMix_;
    ComPtr<ID3D11PixelShader> psPattern_;
    ComPtr<ID3D11SamplerState> sampler_;
    ComPtr<ID3D11Buffer> constants_;
    ComPtr<ID3D11RasterizerState> raster_;
    ComPtr<ID3D11ShaderResourceView> black_;
};

/// Copies a GPU texture to CPU memory through a two-deep staging ring, so the copy for
/// frame N is read while frame N+1 is being produced (no pipeline stall).
class Readback {
public:
    bool create(ID3D11Device* device, UINT w, UINT h);

    using Sink = std::function<void(const std::uint8_t* pixels, UINT rowPitch, UINT width, UINT height)>;
    /// Queues `source` for readback and delivers the previously queued frame to `sink`.
    void capture(ID3D11DeviceContext* ctx, ID3D11Texture2D* source, const Sink& sink);

private:
    ComPtr<ID3D11Texture2D> staging_[2];
    bool filled_[2] = {false, false};
    int index_ = 0;
    UINT width_ = 0;
    UINT height_ = 0;
};

std::string narrow(const wchar_t* text);

}  // namespace livecore::rendering
