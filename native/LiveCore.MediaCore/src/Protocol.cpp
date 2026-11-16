#include "livecore/core/Protocol.h"

#include <stdexcept>

namespace livecore::protocol {
namespace {

void put16(std::uint8_t* p, std::uint16_t v) {
    p[0] = static_cast<std::uint8_t>(v);
    p[1] = static_cast<std::uint8_t>(v >> 8);
}
void put32(std::uint8_t* p, std::uint32_t v) {
    for (int i = 0; i < 4; ++i) p[i] = static_cast<std::uint8_t>(v >> (8 * i));
}
std::uint16_t get16(const std::uint8_t* p) { return static_cast<std::uint16_t>(p[0] | (p[1] << 8)); }
std::uint32_t get32(const std::uint8_t* p) {
    return static_cast<std::uint32_t>(p[0]) | (static_cast<std::uint32_t>(p[1]) << 8) |
           (static_cast<std::uint32_t>(p[2]) << 16) | (static_cast<std::uint32_t>(p[3]) << 24);
}

}  // namespace

std::string_view toString(MessageType t) {
    switch (t) {
        case MessageType::Hello: return "Hello";
        case MessageType::HelloAck: return "HelloAck";
        case MessageType::Ping: return "Ping";
        case MessageType::Pong: return "Pong";
        case MessageType::GetStatus: return "GetStatus";
        case MessageType::Status: return "Status";
        case MessageType::Shutdown: return "Shutdown";
        case MessageType::ShutdownAck: return "ShutdownAck";
        case MessageType::Error: return "Error";
    }
    return "Unknown";
}

std::array<std::uint8_t, kHeaderSize> encodeHeader(const Header& h) {
    std::array<std::uint8_t, kHeaderSize> b{};
    put32(b.data() + 0, kMagic);
    put16(b.data() + 4, h.version);
    put16(b.data() + 6, static_cast<std::uint16_t>(h.type));
    put32(b.data() + 8, h.requestId);
    put32(b.data() + 12, h.payloadLength);
    return b;
}

HeaderError decodeHeader(const std::uint8_t* bytes, Header& out) {
    if (get32(bytes) != kMagic) return HeaderError::BadMagic;
    out.version = get16(bytes + 4);
    if (out.version != kVersion) return HeaderError::BadVersion;
    out.type = static_cast<MessageType>(get16(bytes + 6));
    out.requestId = get32(bytes + 8);
    out.payloadLength = get32(bytes + 12);
    if (out.payloadLength > kMaxPayload) return HeaderError::PayloadTooLarge;
    return HeaderError::None;
}

std::string encodeFrame(MessageType type, std::uint32_t requestId, std::string_view payload) {
    if (payload.size() > kMaxPayload) throw std::length_error("payload exceeds protocol maximum");
    Header h;
    h.type = type;
    h.requestId = requestId;
    h.payloadLength = static_cast<std::uint32_t>(payload.size());
    const auto hb = encodeHeader(h);
    std::string frame;
    frame.reserve(kHeaderSize + payload.size());
    frame.append(reinterpret_cast<const char*>(hb.data()), hb.size());
    frame.append(payload);
    return frame;
}

}  // namespace livecore::protocol
