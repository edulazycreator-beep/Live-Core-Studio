#include "livecore/rendering/D3D.h"

#include <d3dcompiler.h>
#include <dxgi.h>

#include <cstring>
#include <format>

namespace livecore::rendering {
namespace {

// Original shader set for Live Core Studio. A single full-screen triangle is generated
// from SV_VertexID, so no vertex buffers or input layouts are needed.
constexpr char kShaderSource[] = R"(
struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

VSOut VSMain(uint id : SV_VertexID)
{
    VSOut o;
    float2 uv = float2((id << 1) & 2, id & 2);
    o.uv = uv;
    o.pos = float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    return o;
}

Texture2D texA : register(t0);
Texture2D texB : register(t1);
SamplerState linearClamp : register(s0);

cbuffer Params : register(b0)
{
    float4 color;   // pattern colour
    float  amount;  // mix amount
    float  time;    // seconds
    float  mode;    // pattern mode
    float  pad;
};

float4 PSMix(VSOut i) : SV_Target
{
    float3 a = texA.Sample(linearClamp, i.uv).rgb;
    float3 b = texB.Sample(linearClamp, i.uv).rgb;
    return float4(lerp(a, b, amount), 1.0);
}

static const float3 kBars[7] =
{
    float3(0.75, 0.75, 0.75), float3(0.75, 0.75, 0.0), float3(0.0, 0.75, 0.75), float3(0.0, 0.75, 0.0),
    float3(0.75, 0.0, 0.75),  float3(0.75, 0.0, 0.0),  float3(0.0, 0.0, 0.75)
};

float4 PSPattern(VSOut i) : SV_Target
{
    float3 c = color.rgb;
    if (mode > 0.5)
    {
        c = kBars[min((int)(i.uv.x * 7.0), 6)];
    }
    // Moving marker in the bottom strip shows the source is live without touching the centre.
    float x = frac(time * 0.25);
    if (i.uv.y > 0.92 && abs(i.uv.x - x) < 0.015)
    {
        c = float3(1.0, 1.0, 1.0);
    }
    return float4(c, 1.0);
}
)";

struct Constants {
    float color[4];
    float amount;
    float time;
    float mode;
    float pad;
};
static_assert(sizeof(Constants) % 16 == 0);

bool compile(const char* entry, const char* profile, ComPtr<ID3DBlob>& out, std::string& error) {
    ComPtr<ID3DBlob> errors;
    const HRESULT hr = D3DCompile(kShaderSource, sizeof(kShaderSource) - 1, "livecore_shaders", nullptr, nullptr, entry,
                                  profile, D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &out, &errors);
    if (FAILED(hr)) {
        error = std::format("shader {} failed to compile (0x{:08X})", entry, static_cast<unsigned>(hr));
        if (errors) error += ": " + std::string(static_cast<const char*>(errors->GetBufferPointer()), errors->GetBufferSize());
        return false;
    }
    return true;
}

}  // namespace

std::string narrow(const wchar_t* text) {
    const int n = WideCharToMultiByte(CP_UTF8, 0, text, -1, nullptr, 0, nullptr, nullptr);
    if (n <= 1) return {};
    std::string out(static_cast<std::size_t>(n - 1), '\0');
    WideCharToMultiByte(CP_UTF8, 0, text, -1, out.data(), n, nullptr, nullptr);
    return out;
}

std::unique_ptr<D3DContext> D3DContext::create(bool forceWarp, std::string& error) {
    char env[8] = {};
    if (GetEnvironmentVariableA("LIVECORE_FORCE_WARP", env, sizeof env) > 0 && std::strcmp(env, "1") == 0) forceWarp = true;

    const D3D_FEATURE_LEVEL levels[] = {D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_1,
                                        D3D_FEATURE_LEVEL_10_0};
    auto ctx = std::make_unique<D3DContext>();
    HRESULT hr = E_FAIL;
    const UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
    if (!forceWarp) {
        hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags, levels, ARRAYSIZE(levels),
                               D3D11_SDK_VERSION, &ctx->device_, nullptr, &ctx->context_);
    }
    if (FAILED(hr)) {
        hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, flags, levels, ARRAYSIZE(levels),
                               D3D11_SDK_VERSION, &ctx->device_, nullptr, &ctx->context_);
        ctx->warp_ = true;
    }
    if (FAILED(hr)) {
        error = std::format("D3D11CreateDevice failed (0x{:08X})", static_cast<unsigned>(hr));
        return nullptr;
    }

    ComPtr<IDXGIDevice> dxgi;
    ComPtr<IDXGIAdapter> adapter;
    DXGI_ADAPTER_DESC desc{};
    if (SUCCEEDED(ctx->device_.As(&dxgi)) && SUCCEEDED(dxgi->GetAdapter(&adapter)) && SUCCEEDED(adapter->GetDesc(&desc)))
        ctx->adapter_ = narrow(desc.Description);
    if (ctx->warp_ && ctx->adapter_.empty()) ctx->adapter_ = "Microsoft Basic Render Driver (WARP)";
    return ctx;
}

bool RenderTexture::create(ID3D11Device* device, UINT w, UINT h) {
    D3D11_TEXTURE2D_DESC d{};
    d.Width = w;
    d.Height = h;
    d.MipLevels = 1;
    d.ArraySize = 1;
    d.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    d.SampleDesc.Count = 1;
    d.Usage = D3D11_USAGE_DEFAULT;
    d.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
    if (FAILED(device->CreateTexture2D(&d, nullptr, &texture))) return false;
    if (FAILED(device->CreateRenderTargetView(texture.Get(), nullptr, &rtv))) return false;
    if (FAILED(device->CreateShaderResourceView(texture.Get(), nullptr, &srv))) return false;
    width = w;
    height = h;
    return true;
}

bool Renderer::init(ID3D11Device* device, std::string& error) {
    ComPtr<ID3DBlob> vsBlob, mixBlob, patternBlob;
    if (!compile("VSMain", "vs_4_0", vsBlob, error) || !compile("PSMix", "ps_4_0", mixBlob, error) ||
        !compile("PSPattern", "ps_4_0", patternBlob, error))
        return false;
    if (FAILED(device->CreateVertexShader(vsBlob->GetBufferPointer(), vsBlob->GetBufferSize(), nullptr, &vs_)) ||
        FAILED(device->CreatePixelShader(mixBlob->GetBufferPointer(), mixBlob->GetBufferSize(), nullptr, &psMix_)) ||
        FAILED(device->CreatePixelShader(patternBlob->GetBufferPointer(), patternBlob->GetBufferSize(), nullptr, &psPattern_))) {
        error = "creating shaders failed";
        return false;
    }

    D3D11_SAMPLER_DESC sd{};
    sd.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
    sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
    sd.ComparisonFunc = D3D11_COMPARISON_NEVER;
    sd.MaxLOD = D3D11_FLOAT32_MAX;
    if (FAILED(device->CreateSamplerState(&sd, &sampler_))) { error = "sampler creation failed"; return false; }

    D3D11_BUFFER_DESC bd{};
    bd.ByteWidth = sizeof(Constants);
    bd.Usage = D3D11_USAGE_DYNAMIC;
    bd.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    bd.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
    if (FAILED(device->CreateBuffer(&bd, nullptr, &constants_))) { error = "constant buffer creation failed"; return false; }

    D3D11_RASTERIZER_DESC rd{};
    rd.FillMode = D3D11_FILL_SOLID;
    rd.CullMode = D3D11_CULL_NONE;
    rd.DepthClipEnable = TRUE;
    if (FAILED(device->CreateRasterizerState(&rd, &raster_))) { error = "rasterizer state creation failed"; return false; }

    // 1×1 opaque black, used when a bus has no source.
    D3D11_TEXTURE2D_DESC td{};
    td.Width = td.Height = 1;
    td.MipLevels = td.ArraySize = 1;
    td.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    td.SampleDesc.Count = 1;
    td.Usage = D3D11_USAGE_IMMUTABLE;
    td.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    const std::uint32_t pixel = 0xFF000000u;
    D3D11_SUBRESOURCE_DATA init{&pixel, 4, 4};
    ComPtr<ID3D11Texture2D> tex;
    if (FAILED(device->CreateTexture2D(&td, &init, &tex)) || FAILED(device->CreateShaderResourceView(tex.Get(), nullptr, &black_))) {
        error = "black texture creation failed";
        return false;
    }
    return true;
}

void Renderer::bindCommon(ID3D11DeviceContext* ctx, const RenderTexture& target) {
    ctx->IASetInputLayout(nullptr);
    ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    ctx->VSSetShader(vs_.Get(), nullptr, 0);
    ctx->RSSetState(raster_.Get());
    D3D11_VIEWPORT vp{0.0f, 0.0f, static_cast<float>(target.width), static_cast<float>(target.height), 0.0f, 1.0f};
    ctx->RSSetViewports(1, &vp);
    ID3D11RenderTargetView* rtv = target.rtv.Get();
    ctx->OMSetRenderTargets(1, &rtv, nullptr);
    ID3D11Buffer* cb = constants_.Get();
    ctx->PSSetConstantBuffers(0, 1, &cb);
    ID3D11SamplerState* s = sampler_.Get();
    ctx->PSSetSamplers(0, 1, &s);
}

void Renderer::drawMix(ID3D11DeviceContext* ctx, const RenderTexture& target, ID3D11ShaderResourceView* a,
                       ID3D11ShaderResourceView* b, float mix) {
    D3D11_MAPPED_SUBRESOURCE m{};
    if (SUCCEEDED(ctx->Map(constants_.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &m))) {
        Constants c{};
        c.amount = mix;
        std::memcpy(m.pData, &c, sizeof c);
        ctx->Unmap(constants_.Get(), 0);
    }
    bindCommon(ctx, target);
    ctx->PSSetShader(psMix_.Get(), nullptr, 0);
    ID3D11ShaderResourceView* views[2] = {a ? a : black_.Get(), b ? b : black_.Get()};
    ctx->PSSetShaderResources(0, 2, views);
    ctx->Draw(3, 0);
    ID3D11ShaderResourceView* none[2] = {nullptr, nullptr};
    ctx->PSSetShaderResources(0, 2, none);  // unbind so these textures can be render targets next
}

void Renderer::drawPattern(ID3D11DeviceContext* ctx, const RenderTexture& target, Pattern pattern, float r, float g,
                           float b, float timeSeconds) {
    D3D11_MAPPED_SUBRESOURCE m{};
    if (SUCCEEDED(ctx->Map(constants_.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &m))) {
        Constants c{{r, g, b, 1.0f}, 0.0f, timeSeconds, pattern == Pattern::Bars ? 1.0f : 0.0f, 0.0f};
        std::memcpy(m.pData, &c, sizeof c);
        ctx->Unmap(constants_.Get(), 0);
    }
    bindCommon(ctx, target);
    ctx->PSSetShader(psPattern_.Get(), nullptr, 0);
    ctx->Draw(3, 0);
}

bool Readback::create(ID3D11Device* device, UINT w, UINT h) {
    D3D11_TEXTURE2D_DESC d{};
    d.Width = w;
    d.Height = h;
    d.MipLevels = d.ArraySize = 1;
    d.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    d.SampleDesc.Count = 1;
    d.Usage = D3D11_USAGE_STAGING;
    d.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    for (auto& s : staging_)
        if (FAILED(device->CreateTexture2D(&d, nullptr, &s))) return false;
    width_ = w;
    height_ = h;
    return true;
}

void Readback::capture(ID3D11DeviceContext* ctx, ID3D11Texture2D* source, const Sink& sink) {
    ctx->CopyResource(staging_[index_].Get(), source);
    filled_[index_] = true;
    const int previous = index_ ^ 1;
    if (filled_[previous]) {
        D3D11_MAPPED_SUBRESOURCE m{};
        if (SUCCEEDED(ctx->Map(staging_[previous].Get(), 0, D3D11_MAP_READ, 0, &m))) {
            sink(static_cast<const std::uint8_t*>(m.pData), m.RowPitch, width_, height_);
            ctx->Unmap(staging_[previous].Get(), 0);
        }
    }
    index_ = previous;
}

}  // namespace livecore::rendering
