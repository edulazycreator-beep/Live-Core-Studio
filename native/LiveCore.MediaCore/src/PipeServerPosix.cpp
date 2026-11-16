// POSIX development/CI transport: Unix domain socket at the path .NET's
// NamedPipeClientStream uses on Unix, so the managed client works unchanged.
#if !defined(_WIN32)

#include "livecore/core/Log.h"
#include "livecore/core/PipeServer.h"

#include <cerrno>
#include <cstdlib>
#include <cstring>
#include <fcntl.h>
#include <poll.h>
#include <sys/socket.h>
#include <sys/un.h>
#include <unistd.h>

#include <atomic>

namespace livecore::ipc {
namespace {

constexpr char kComp[] = "ipc";

std::string socketPath(const std::string& name) {
    std::string dir;
    if (const char* t = std::getenv("TMPDIR"); t && *t) dir = t;
    else dir = "/tmp/";
    if (dir.back() != '/') dir += '/';
    return dir + "CoreFxPipe_" + name;
}

// Waits until fd is ready for `events`, or the stop pipe fires. True if fd ready.
bool waitFd(int fd, short events, int stopFd) {
    for (;;) {
        pollfd fds[2] = {{fd, events, 0}, {stopFd, POLLIN, 0}};
        const int r = ::poll(fds, 2, -1);
        if (r < 0) {
            if (errno == EINTR) continue;
            return false;
        }
        if (fds[1].revents) return false;
        if (fds[0].revents & (events | POLLHUP | POLLERR)) return (fds[0].revents & events) != 0 || (fds[0].revents & POLLHUP);
    }
}

class PosixConnection final : public PipeConnection {
public:
    PosixConnection(int fd, int stopFd) : fd_(fd), stop_(stopFd) {}
    ~PosixConnection() override { close(); }

    bool readExact(void* buffer, std::size_t size) override {
        auto* p = static_cast<char*>(buffer);
        while (size > 0) {
            if (fd_ < 0 || !waitFd(fd_, POLLIN, stop_)) return false;
            const ssize_t n = ::recv(fd_, p, size, 0);
            if (n == 0) return false;  // peer closed
            if (n < 0) {
                if (errno == EINTR || errno == EAGAIN) continue;
                return false;
            }
            p += n;
            size -= static_cast<std::size_t>(n);
        }
        return true;
    }

    bool writeAll(const void* data, std::size_t size) override {
        const auto* p = static_cast<const char*>(data);
        while (size > 0) {
            if (fd_ < 0 || !waitFd(fd_, POLLOUT, stop_)) return false;
            const ssize_t n = ::send(fd_, p, size, MSG_NOSIGNAL);
            if (n < 0) {
                if (errno == EINTR || errno == EAGAIN) continue;
                return false;
            }
            p += n;
            size -= static_cast<std::size_t>(n);
        }
        return true;
    }

    void close() override {
        if (fd_ >= 0) ::close(fd_);
        fd_ = -1;
    }

private:
    int fd_;
    int stop_;
};

class PosixPipeServer final : public PipeServer {
public:
    explicit PosixPipeServer(const std::string& name) : path_(socketPath(name)) {
        int p[2];
        if (::pipe(p) == 0) {
            stopRead_ = p[0];
            stopWrite_ = p[1];
            ::fcntl(stopRead_, F_SETFD, FD_CLOEXEC);
            ::fcntl(stopWrite_, F_SETFD, FD_CLOEXEC);
        }
    }

    ~PosixPipeServer() override {
        stop();
        if (listenFd_ >= 0) {
            ::close(listenFd_);
            ::unlink(path_.c_str());
        }
        if (stopRead_ >= 0) ::close(stopRead_);
        if (stopWrite_ >= 0) ::close(stopWrite_);
    }

    bool listen() override {
        sockaddr_un addr{};
        addr.sun_family = AF_UNIX;
        if (path_.size() >= sizeof(addr.sun_path)) {
            log::error(kComp, "socket path too long: {}", path_);
            return false;
        }
        std::memcpy(addr.sun_path, path_.c_str(), path_.size() + 1);

        // A leftover socket file from a crashed engine is stale only if nobody answers.
        if (::access(path_.c_str(), F_OK) == 0) {
            const int probe = ::socket(AF_UNIX, SOCK_STREAM, 0);
            const bool alive = probe >= 0 && ::connect(probe, reinterpret_cast<sockaddr*>(&addr), sizeof addr) == 0;
            if (probe >= 0) ::close(probe);
            if (alive) {
                log::error(kComp, "endpoint {} is already owned by another process", path_);
                return false;
            }
            ::unlink(path_.c_str());
        }

        listenFd_ = ::socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC, 0);
        if (listenFd_ < 0 || ::bind(listenFd_, reinterpret_cast<sockaddr*>(&addr), sizeof addr) != 0 ||
            ::listen(listenFd_, 1) != 0) {
            log::error(kComp, "cannot listen on {}: {}", path_, std::strerror(errno));
            if (listenFd_ >= 0) ::close(listenFd_);
            listenFd_ = -1;
            return false;
        }
        log::info(kComp, "listening on {}", endpoint());
        return true;
    }

    std::unique_ptr<PipeConnection> accept() override {
        if (listenFd_ < 0) return nullptr;
        for (;;) {
            if (!waitFd(listenFd_, POLLIN, stopRead_)) return nullptr;
            const int fd = ::accept4(listenFd_, nullptr, nullptr, SOCK_CLOEXEC);
            if (fd >= 0) return std::make_unique<PosixConnection>(fd, stopRead_);
            if (errno != EINTR && errno != EAGAIN && errno != ECONNABORTED) {
                log::error(kComp, "accept failed: {}", std::strerror(errno));
                return nullptr;
            }
        }
    }

    void stop() override {
        bool expected = false;
        if (stopped_.compare_exchange_strong(expected, true) && stopWrite_ >= 0) {
            const char b = 1;
            [[maybe_unused]] const auto r = ::write(stopWrite_, &b, 1);  // stays readable: level-triggered stop
        }
    }

    std::string endpoint() const override { return "unix:" + path_; }

private:
    std::string path_;
    int listenFd_ = -1;
    int stopRead_ = -1;
    int stopWrite_ = -1;
    std::atomic<bool> stopped_{false};
};

}  // namespace

std::unique_ptr<PipeServer> PipeServer::create(const std::string& pipeName) {
    return std::make_unique<PosixPipeServer>(pipeName);
}

}  // namespace livecore::ipc

#endif  // !_WIN32
