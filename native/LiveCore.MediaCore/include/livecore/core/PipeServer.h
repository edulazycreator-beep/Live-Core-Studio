// Live Core Studio — LiveCore.MediaCore
// Local IPC server endpoint.
//   Windows: \\.\pipe\<name> (overlapped I/O, local clients only)
//   POSIX:   Unix domain socket at $TMPDIR/CoreFxPipe_<name>, matching .NET's
//            NamedPipeClientStream on Unix. Used for development and CI.
#pragma once

#include <cstddef>
#include <memory>
#include <string>

namespace livecore::ipc {

class PipeConnection {
public:
    virtual ~PipeConnection() = default;
    /// Blocks until `size` bytes are read. False on disconnect, error or server stop.
    virtual bool readExact(void* buffer, std::size_t size) = 0;
    /// Blocks until all bytes are written. False on disconnect, error or server stop.
    virtual bool writeAll(const void* data, std::size_t size) = 0;
    virtual void close() = 0;
};

class PipeServer {
public:
    virtual ~PipeServer() = default;

    /// Creates the endpoint. False (with a logged reason) if it cannot be created,
    /// e.g. the name is already owned by another engine instance.
    virtual bool listen() = 0;

    /// Waits for one client. Returns nullptr when stop() was called or on fatal error.
    /// Only one connection is active at a time; call again after it closes.
    virtual std::unique_ptr<PipeConnection> accept() = 0;

    /// Thread-safe. Unblocks accept() and any blocking read/write on connections.
    virtual void stop() = 0;

    virtual std::string endpoint() const = 0;

    static std::unique_ptr<PipeServer> create(const std::string& pipeName);
};

}  // namespace livecore::ipc
