#include "Platform.h"

#include <chrono>
#include <thread>

#if defined(_WIN32)
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#else
#include <cerrno>
#include <csignal>
#include <signal.h>
#include <unistd.h>
#endif

namespace livecore::platform {
namespace {
std::function<void()> g_onStop;
}

#if defined(_WIN32)

std::int64_t currentPid() { return static_cast<std::int64_t>(GetCurrentProcessId()); }
const char* name() { return "windows"; }

bool waitForProcessExit(std::int64_t pid, std::stop_token st) {
    HANDLE h = OpenProcess(SYNCHRONIZE, FALSE, static_cast<DWORD>(pid));
    if (!h) return true;  // already gone (or inaccessible — treat as gone to avoid orphaning)
    bool exited = false;
    while (!st.stop_requested()) {
        if (WaitForSingleObject(h, 250) == WAIT_OBJECT_0) { exited = true; break; }
    }
    CloseHandle(h);
    return exited;
}

static BOOL WINAPI consoleHandler(DWORD type) {
    switch (type) {
        case CTRL_C_EVENT:
        case CTRL_BREAK_EVENT:
        case CTRL_CLOSE_EVENT:
        case CTRL_SHUTDOWN_EVENT:
            if (g_onStop) g_onStop();
            return TRUE;
        default:
            return FALSE;
    }
}

void installStopHandler(std::function<void()> onStop) {
    g_onStop = std::move(onStop);
    SetConsoleCtrlHandler(consoleHandler, TRUE);
}

#else

std::int64_t currentPid() { return static_cast<std::int64_t>(::getpid()); }
const char* name() {
#if defined(__APPLE__)
    return "macos";
#else
    return "linux";
#endif
}

bool waitForProcessExit(std::int64_t pid, std::stop_token st) {
    while (!st.stop_requested()) {
        if (::kill(static_cast<pid_t>(pid), 0) != 0 && errno == ESRCH) return true;
        std::this_thread::sleep_for(std::chrono::milliseconds(250));
    }
    return false;
}

static void signalHandler(int) {
    if (g_onStop) g_onStop();
}

void installStopHandler(std::function<void()> onStop) {
    g_onStop = std::move(onStop);
    struct sigaction sa{};
    sa.sa_handler = signalHandler;
    sigemptyset(&sa.sa_mask);
    sigaction(SIGINT, &sa, nullptr);
    sigaction(SIGTERM, &sa, nullptr);
    std::signal(SIGPIPE, SIG_IGN);
}

#endif

}  // namespace livecore::platform
