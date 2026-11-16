#include "livecore/core/Log.h"

#include <atomic>
#include <chrono>
#include <ctime>
#include <mutex>

namespace livecore::log {
namespace {

std::atomic<Level> g_level{Level::Info};
std::mutex g_mutex;
std::FILE* g_file = nullptr;

const char* levelName(Level l) {
    switch (l) {
        case Level::Trace: return "TRACE";
        case Level::Debug: return "DEBUG";
        case Level::Info:  return "INFO ";
        case Level::Warn:  return "WARN ";
        case Level::Error: return "ERROR";
        default:           return "?    ";
    }
}

std::string timestamp() {
    using namespace std::chrono;
    const auto now = system_clock::now();
    const auto ms = duration_cast<milliseconds>(now.time_since_epoch()) % 1000;
    const std::time_t t = system_clock::to_time_t(now);
    std::tm tm{};
#if defined(_WIN32)
    localtime_s(&tm, &t);
#else
    localtime_r(&t, &tm);
#endif
    char buf[32];
    std::strftime(buf, sizeof buf, "%Y-%m-%d %H:%M:%S", &tm);
    return std::format("{}.{:03}", buf, static_cast<int>(ms.count()));
}

}  // namespace

void setLevel(Level l) { g_level.store(l); }
Level level() { return g_level.load(); }

bool parseLevel(std::string_view text, Level& out) {
    if (text == "trace") out = Level::Trace;
    else if (text == "debug") out = Level::Debug;
    else if (text == "info") out = Level::Info;
    else if (text == "warn") out = Level::Warn;
    else if (text == "error") out = Level::Error;
    else if (text == "off") out = Level::Off;
    else return false;
    return true;
}

bool openFile(const std::string& path) {
    std::lock_guard lock(g_mutex);
    if (g_file) std::fclose(g_file);
#if defined(_WIN32)
    if (fopen_s(&g_file, path.c_str(), "a") != 0) g_file = nullptr;
#else
    g_file = std::fopen(path.c_str(), "a");
#endif
    return g_file != nullptr;
}

void closeFile() {
    std::lock_guard lock(g_mutex);
    if (g_file) std::fclose(g_file);
    g_file = nullptr;
}

void write(Level l, std::string_view component, std::string_view message) {
    if (l < g_level.load()) return;
    const std::string line = std::format("{} [{}] {}: {}\n", timestamp(), levelName(l), component, message);
    std::lock_guard lock(g_mutex);
    std::fputs(line.c_str(), stderr);
    std::fflush(stderr);
    if (g_file) {
        std::fputs(line.c_str(), g_file);
        std::fflush(g_file);
    }
}

}  // namespace livecore::log
