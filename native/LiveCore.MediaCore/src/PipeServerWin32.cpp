// Windows named pipe server. Overlapped I/O so that stop() can interrupt any
// blocking wait without killing threads.
#if defined(_WIN32)

#include "livecore/core/Log.h"
#include "livecore/core/PipeServer.h"

#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include <algorithm>
#include <cstdint>

namespace livecore::ipc {
namespace {

constexpr char kComp[] = "ipc";
constexpr DWORD kBufferSize = 64 * 1024;
constexpr DWORD kLingerMs = 1000;

std::wstring widen(const std::string& s) {
    if (s.empty()) return {};
    const int n = MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), nullptr, 0);
    std::wstring w(static_cast<std::size_t>(n), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), w.data(), n);
    return w;
}

class Event {
public:
    Event() : h_(CreateEventW(nullptr, TRUE, FALSE, nullptr)) {}
    ~Event() { if (h_) CloseHandle(h_); }
    Event(const Event&) = delete;
    Event& operator=(const Event&) = delete;
    HANDLE get() const { return h_; }
private:
    HANDLE h_;
};

// Waits for an overlapped operation or the stop event. Returns bytes transferred,
// or -1 on error / stop / disconnect.
long long waitOverlapped(HANDLE pipe, OVERLAPPED& ov, HANDLE stopEvent) {
    const HANDLE handles[2] = {ov.hEvent, stopEvent};
    const DWORD w = WaitForMultipleObjects(2, handles, FALSE, INFINITE);
    DWORD transferred = 0;
    if (w != WAIT_OBJECT_0) {
        CancelIoEx(pipe, &ov);
        GetOverlappedResult(pipe, &ov, &transferred, TRUE);  // drain the cancelled op
        return -1;
    }
    if (!GetOverlappedResult(pipe, &ov, &transferred, FALSE)) return -1;
    return static_cast<long long>(transferred);
}

class Win32Connection final : public PipeConnection {
public:
    Win32Connection(HANDLE pipe, HANDLE stopEvent) : pipe_(pipe), stop_(stopEvent) {}
    ~Win32Connection() override { close(); }

    bool readExact(void* buffer, std::size_t size) override {
        auto* p = static_cast<std::uint8_t*>(buffer);
        while (size > 0 && open_) {
            OVERLAPPED ov{};
            ov.hEvent = io_.get();
            ResetEvent(io_.get());
            const DWORD chunk = static_cast<DWORD>(std::min<std::size_t>(size, kBufferSize));
            DWORD n = 0;
            long long got;
            if (ReadFile(pipe_, p, chunk, &n, &ov)) {
                got = n;
            } else if (GetLastError() == ERROR_IO_PENDING) {
                got = waitOverlapped(pipe_, ov, stop_);
            } else {
                got = -1;
            }
            if (got <= 0) return false;
            p += got;
            size -= static_cast<std::size_t>(got);
        }
        return size == 0;
    }

    bool writeAll(const void* data, std::size_t size) override {
        const auto* p = static_cast<const std::uint8_t*>(data);
        while (size > 0 && open_) {
            OVERLAPPED ov{};
            ov.hEvent = io_.get();
            ResetEvent(io_.get());
            const DWORD chunk = static_cast<DWORD>(std::min<std::size_t>(size, kBufferSize));
            DWORD n = 0;
            long long put;
            if (WriteFile(pipe_, p, chunk, &n, &ov)) {
                put = n;
            } else if (GetLastError() == ERROR_IO_PENDING) {
                put = waitOverlapped(pipe_, ov, stop_);
            } else {
                put = -1;
            }
            if (put <= 0) return false;
            p += put;
            size -= static_cast<std::size_t>(put);
        }
        return size == 0;
    }

    void close() override {
        if (!open_) return;
        open_ = false;
        // DisconnectNamedPipe discards data the client has not read yet, which would
        // drop a final reply (ShutdownAck, Error). Linger up to 1 s for the client to
        // read it and hang up; a pending 1-byte read completes when it does.
        OVERLAPPED ov{};
        ov.hEvent = io_.get();
        ResetEvent(io_.get());
        std::uint8_t byte = 0;
        DWORD n = 0;
        if (!ReadFile(pipe_, &byte, 1, &n, &ov) && GetLastError() == ERROR_IO_PENDING) {
            const HANDLE handles[2] = {io_.get(), stop_};
            if (WaitForMultipleObjects(2, handles, FALSE, kLingerMs) != WAIT_OBJECT_0) CancelIoEx(pipe_, &ov);
            GetOverlappedResult(pipe_, &ov, &n, TRUE);
        }
        DisconnectNamedPipe(pipe_);  // pipe instance is reused for the next client
    }

private:
    HANDLE pipe_;
    HANDLE stop_;
    Event io_;
    bool open_ = true;
};

class Win32PipeServer final : public PipeServer {
public:
    explicit Win32PipeServer(std::string name) : name_(std::move(name)), path_(L"\\\\.\\pipe\\" + widen(name_)) {}

    ~Win32PipeServer() override {
        stop();
        if (pipe_ != INVALID_HANDLE_VALUE) CloseHandle(pipe_);
    }

    bool listen() override {
        pipe_ = CreateNamedPipeW(path_.c_str(),
                                 PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED | FILE_FLAG_FIRST_PIPE_INSTANCE,
                                 PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
                                 1, kBufferSize, kBufferSize, 0, nullptr);
        if (pipe_ == INVALID_HANDLE_VALUE) {
            const DWORD err = GetLastError();
            if (err == ERROR_ACCESS_DENIED || err == ERROR_PIPE_BUSY)
                log::error(kComp, "pipe '{}' is already owned by another process (error {})", name_, err);
            else
                log::error(kComp, "CreateNamedPipe failed for '{}' (error {})", name_, err);
            return false;
        }
        log::info(kComp, "listening on {}", endpoint());
        return true;
    }

    std::unique_ptr<PipeConnection> accept() override {
        if (pipe_ == INVALID_HANDLE_VALUE) return nullptr;
        Event connectEvent;
        OVERLAPPED ov{};
        ov.hEvent = connectEvent.get();
        if (!ConnectNamedPipe(pipe_, &ov)) {
            const DWORD err = GetLastError();
            if (err == ERROR_IO_PENDING) {
                if (waitOverlapped(pipe_, ov, stop_.get()) < 0) return nullptr;
            } else if (err != ERROR_PIPE_CONNECTED) {
                log::error(kComp, "ConnectNamedPipe failed (error {})", err);
                return nullptr;
            }
        }
        if (WaitForSingleObject(stop_.get(), 0) == WAIT_OBJECT_0) {
            DisconnectNamedPipe(pipe_);
            return nullptr;
        }
        return std::make_unique<Win32Connection>(pipe_, stop_.get());
    }

    void stop() override { SetEvent(stop_.get()); }

    std::string endpoint() const override { return "\\\\.\\pipe\\" + name_; }

private:
    std::string name_;
    std::wstring path_;
    HANDLE pipe_ = INVALID_HANDLE_VALUE;
    Event stop_;
};

}  // namespace

std::unique_ptr<PipeServer> PipeServer::create(const std::string& pipeName) {
    return std::make_unique<Win32PipeServer>(pipeName);
}

}  // namespace livecore::ipc

#endif  // _WIN32
