// Live Core Studio — LiveCore.MediaCore
// Engine control protocol v1. Spec: shared/Protocol/PROTOCOL.md
#pragma once

#include <array>
#include <cstdint>
#include <optional>
#include <string>
#include <string_view>

namespace livecore::protocol {

inline constexpr std::uint32_t kMagic = 0x5053434C;  // "LCSP" little-endian
inline constexpr std::uint16_t kVersion = 1;
inline constexpr std::size_t kHeaderSize = 16;
inline constexpr std::uint32_t kMaxPayload = 1u << 20;

enum class MessageType : std::uint16_t {
    Hello = 1,
    HelloAck = 2,
    Ping = 3,
    Pong = 4,
    GetStatus = 5,
    Status = 6,
    Shutdown = 7,
    ShutdownAck = 8,
    Ok = 9,
    // Video / production (Phase 4)
    ListVideoDevices = 16,
    VideoDevices = 17,
    AddSource = 18,
    SourceAdded = 19,
    RemoveSource = 20,
    SetPreview = 22,
    SetProgram = 23,
    Cut = 24,
    AutoTransition = 25,
    GetSwitcherState = 26,
    SwitcherState = 27,
    Error = 255,
};

std::string_view toString(MessageType t);

struct Header {
    std::uint16_t version = kVersion;
    MessageType type = MessageType::Error;
    std::uint32_t requestId = 0;
    std::uint32_t payloadLength = 0;
};

enum class HeaderError { None, BadMagic, BadVersion, PayloadTooLarge };

std::array<std::uint8_t, kHeaderSize> encodeHeader(const Header& h);
HeaderError decodeHeader(const std::uint8_t* bytes, Header& out);

/// Encodes header + payload into one buffer ready to write.
std::string encodeFrame(MessageType type, std::uint32_t requestId, std::string_view payload);

}  // namespace livecore::protocol
