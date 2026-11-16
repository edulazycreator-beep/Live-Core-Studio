// Native unit tests for LiveCore.MediaCore. Dependency-free on purpose:
// the build must work on a clean machine without a package manager.
#include "livecore/core/FlatJson.h"
#include "livecore/core/PipeServer.h"
#include "livecore/core/Protocol.h"

#include <cstdio>
#include <chrono>
#include <cstring>
#include <functional>
#include <string>
#include <thread>
#include <vector>

#if defined(_WIN32)
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#else
#include <cstdlib>
#include <sys/socket.h>
#include <sys/un.h>
#include <unistd.h>
#endif

namespace {

int g_failures = 0;
int g_checks = 0;

#define CHECK(expr)                                                                   \
    do {                                                                              \
        ++g_checks;                                                                   \
        if (!(expr)) {                                                                \
            ++g_failures;                                                             \
            std::fprintf(stderr, "  FAIL %s:%d: %s\n", __FILE__, __LINE__, #expr);   \
        }                                                                             \
    } while (0)

struct TestCase {
    const char* name;
    std::function<void()> fn;
};

using namespace livecore;

void headerRoundTrip() {
    protocol::Header h;
    h.type = protocol::MessageType::Status;
    h.requestId = 0xA1B2C3D4;
    h.payloadLength = 42;
    const auto bytes = protocol::encodeHeader(h);
    CHECK(bytes[0] == 'L' && bytes[1] == 'C' && bytes[2] == 'S' && bytes[3] == 'P');
    protocol::Header back;
    CHECK(protocol::decodeHeader(bytes.data(), back) == protocol::HeaderError::None);
    CHECK(back.type == protocol::MessageType::Status);
    CHECK(back.requestId == 0xA1B2C3D4);
    CHECK(back.payloadLength == 42);
}

void headerRejectsBadInput() {
    protocol::Header h;
    auto bytes = protocol::encodeHeader(h);
    bytes[0] = 'X';
    protocol::Header out;
    CHECK(protocol::decodeHeader(bytes.data(), out) == protocol::HeaderError::BadMagic);

    bytes = protocol::encodeHeader(h);
    bytes[4] = 9;  // version 9
    CHECK(protocol::decodeHeader(bytes.data(), out) == protocol::HeaderError::BadVersion);

    h.payloadLength = protocol::kMaxPayload + 1;
    bytes = protocol::encodeHeader(h);
    CHECK(protocol::decodeHeader(bytes.data(), out) == protocol::HeaderError::PayloadTooLarge);
}

void frameLayout() {
    const std::string f = protocol::encodeFrame(protocol::MessageType::Ping, 7, "{\"seq\":1}");
    CHECK(f.size() == protocol::kHeaderSize + 9);
    CHECK(f.substr(protocol::kHeaderSize) == "{\"seq\":1}");
}

void jsonParseFlat() {
    auto o = json::Object::parse(R"({"client":"LiveCoreStudio","protocolVersion":1,"ok":true,"x":null,"neg":-12})");
    CHECK(o.has_value());
    if (!o) return;
    CHECK(o->getString("client") == std::optional<std::string>("LiveCoreStudio"));
    CHECK(o->getInt("protocolVersion") == std::optional<std::int64_t>(1));
    CHECK(o->getBool("ok") == std::optional<bool>(true));
    CHECK(o->getInt("neg") == std::optional<std::int64_t>(-12));
    CHECK(!o->getString("protocolVersion").has_value());
    CHECK(!o->getInt("missing").has_value());
}

void jsonEscapesAndUnicode() {
    auto o = json::Object::parse(R"({"s":"a\"b\\c\né🎥"})");
    CHECK(o.has_value());
    if (!o) return;
    CHECK(o->getString("s") == std::optional<std::string>("a\"b\\c\n\xC3\xA9\xF0\x9F\x8E\xA5"));
}

void jsonSkipsNested() {
    auto o = json::Object::parse(R"({"a":1,"nested":{"b":[1,2,{"c":"}"}]},"z":"end"})");
    CHECK(o.has_value());
    if (!o) return;
    CHECK(!o->has("nested"));
    CHECK(o->getString("z") == std::optional<std::string>("end"));
}

void jsonRejectsMalformed() {
    CHECK(!json::Object::parse("").has_value());
    CHECK(!json::Object::parse("{").has_value());
    CHECK(!json::Object::parse(R"({"a":})").has_value());
    CHECK(!json::Object::parse(R"({"a":1,})").has_value());
    CHECK(!json::Object::parse(R"({"a":1} trailing)").has_value());
    CHECK(!json::Object::parse(R"([1,2])").has_value());
    CHECK(json::Object::parse("{}").has_value());
}

void jsonWriterRoundTrip() {
    const std::string s = json::Writer().add("name", "Cam \"A\"\n").add("n", std::int64_t{1234567890123}).add("on", false).str();
    auto o = json::Object::parse(s);
    CHECK(o.has_value());
    if (!o) return;
    CHECK(o->getString("name") == std::optional<std::string>("Cam \"A\"\n"));
    CHECK(o->getInt("n") == std::optional<std::int64_t>(1234567890123));
    CHECK(o->getBool("on") == std::optional<bool>(false));
    CHECK(json::Writer().str() == "{}");
}

void pipeServerStopUnblocksAccept() {
    auto server = ipc::PipeServer::create("LiveCore.Test.Stop." + std::to_string(std::hash<std::thread::id>{}(std::this_thread::get_id()) % 100000));
    CHECK(server->listen());
    std::thread stopper([&] {
        std::this_thread::sleep_for(std::chrono::milliseconds(100));
        server->stop();
    });
    auto conn = server->accept();
    CHECK(conn == nullptr);
    stopper.join();
}

// Minimal blocking client for the round-trip test (Win32 pipe or Unix socket).
class TestClient {
public:
    bool connect(const std::string& name) {
#if defined(_WIN32)
        const std::string path = "\\\\.\\pipe\\" + name;
        for (int i = 0; i < 50; ++i) {
            h_ = CreateFileA(path.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
            if (h_ != INVALID_HANDLE_VALUE) return true;
            Sleep(20);
        }
        return false;
#else
        const char* t = std::getenv("TMPDIR");
        std::string dir = (t && *t) ? t : "/tmp/";
        if (dir.back() != '/') dir += '/';
        const std::string path = dir + "CoreFxPipe_" + name;
        sockaddr_un addr{};
        addr.sun_family = AF_UNIX;
        std::memcpy(addr.sun_path, path.c_str(), path.size() + 1);
        for (int i = 0; i < 50; ++i) {
            fd_ = ::socket(AF_UNIX, SOCK_STREAM, 0);
            if (::connect(fd_, reinterpret_cast<sockaddr*>(&addr), sizeof addr) == 0) return true;
            ::close(fd_);
            fd_ = -1;
            std::this_thread::sleep_for(std::chrono::milliseconds(20));
        }
        return false;
#endif
    }
    bool write(const std::string& data) {
#if defined(_WIN32)
        DWORD n = 0;
        return WriteFile(h_, data.data(), static_cast<DWORD>(data.size()), &n, nullptr) && n == data.size();
#else
        return ::send(fd_, data.data(), data.size(), MSG_NOSIGNAL) == static_cast<ssize_t>(data.size());
#endif
    }
    bool readExact(void* buf, std::size_t size) {
        auto* p = static_cast<char*>(buf);
        while (size > 0) {
#if defined(_WIN32)
            DWORD n = 0;
            if (!ReadFile(h_, p, static_cast<DWORD>(size), &n, nullptr) || n == 0) return false;
#else
            const ssize_t n = ::recv(fd_, p, size, 0);
            if (n <= 0) return false;
#endif
            p += n;
            size -= static_cast<std::size_t>(n);
        }
        return true;
    }
    ~TestClient() {
#if defined(_WIN32)
        if (h_ != INVALID_HANDLE_VALUE) CloseHandle(h_);
#else
        if (fd_ >= 0) ::close(fd_);
#endif
    }

private:
#if defined(_WIN32)
    HANDLE h_ = INVALID_HANDLE_VALUE;
#else
    int fd_ = -1;
#endif
};

// Server echoes a frame back with type Pong; client verifies, for two
// consecutive connections (exercises disconnect + instance reuse).
void pipeRoundTrip() {
    const std::string name = "LiveCore.Test.RT." + std::to_string(std::chrono::steady_clock::now().time_since_epoch().count() % 1000000);
    auto server = ipc::PipeServer::create(name);
    CHECK(server->listen());

    std::thread srv([&] {
        for (int round = 0; round < 2; ++round) {
            auto conn = server->accept();
            if (!conn) return;
            std::uint8_t hb[protocol::kHeaderSize];
            protocol::Header h;
            if (!conn->readExact(hb, sizeof hb) || protocol::decodeHeader(hb, h) != protocol::HeaderError::None) return;
            std::string payload(h.payloadLength, '\0');
            if (!conn->readExact(payload.data(), payload.size())) return;
            const auto reply = protocol::encodeFrame(protocol::MessageType::Pong, h.requestId, payload);
            conn->writeAll(reply.data(), reply.size());
            std::uint8_t extra;
            conn->readExact(&extra, 1);  // returns false when the client hangs up
            conn->close();
        }
    });

    for (int round = 0; round < 2; ++round) {
        TestClient client;
        CHECK(client.connect(name));
        const std::string body = "{\"round\":" + std::to_string(round) + "}";
        CHECK(client.write(protocol::encodeFrame(protocol::MessageType::Ping, 40u + static_cast<std::uint32_t>(round), body)));
        std::uint8_t hb[protocol::kHeaderSize];
        protocol::Header h;
        CHECK(client.readExact(hb, sizeof hb));
        CHECK(protocol::decodeHeader(hb, h) == protocol::HeaderError::None);
        CHECK(h.type == protocol::MessageType::Pong);
        CHECK(h.requestId == 40u + static_cast<std::uint32_t>(round));
        std::string back(h.payloadLength, '\0');
        CHECK(client.readExact(back.data(), back.size()));
        CHECK(back == body);
    }
    srv.join();
    server->stop();
}

}  // namespace

int main() {
    const std::vector<TestCase> tests = {
        {"protocol: header round trip", headerRoundTrip},
        {"protocol: rejects bad header", headerRejectsBadInput},
        {"protocol: frame layout", frameLayout},
        {"json: parse flat object", jsonParseFlat},
        {"json: escapes and unicode", jsonEscapesAndUnicode},
        {"json: skips nested values", jsonSkipsNested},
        {"json: rejects malformed", jsonRejectsMalformed},
        {"json: writer round trip", jsonWriterRoundTrip},
        {"ipc: stop() unblocks accept()", pipeServerStopUnblocksAccept},
        {"ipc: round trip over two connections", pipeRoundTrip},
    };
    for (const auto& t : tests) {
        const int before = g_failures;
        t.fn();
        std::printf("[%s] %s\n", g_failures == before ? " OK " : "FAIL", t.name);
    }
    std::printf("\n%d checks, %d failures\n", g_checks, g_failures);
    return g_failures == 0 ? 0 : 1;
}
