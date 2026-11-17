// Live Core Studio — LiveCore.MediaEngine entry point.
//
// Usage:
//   LiveCore.MediaEngine --pipe <name> [--parent-pid <pid>] [--clock-rate <hz>]
//                        [--log-level trace|debug|info|warn|error|off] [--log-file <path>]
//   LiveCore.MediaEngine --version
#include "Engine.h"
#include "Platform.h"
#include "livecore/core/Log.h"

#include <charconv>
#include <cstdio>
#include <cstring>
#include <string>
#include <string_view>

#ifndef LIVECORE_ENGINE_VERSION
#define LIVECORE_ENGINE_VERSION "0.0.0-dev"
#endif

namespace {

void usage() {
    std::fputs(
        "LiveCore.MediaEngine " LIVECORE_ENGINE_VERSION "\n"
        "Usage: LiveCore.MediaEngine --pipe <name> [--parent-pid <pid>] [--clock-rate <hz>]\n"
        "                            [--no-video] [--video-size WxH] [--fps <n>]\n"
        "                            [--log-level trace|debug|info|warn|error|off] [--log-file <path>]\n",
        stderr);
}

template <class T>
bool parseInt(std::string_view s, T& out) {
    const auto r = std::from_chars(s.data(), s.data() + s.size(), out);
    return r.ec == std::errc{} && r.ptr == s.data() + s.size();
}

bool validPipeName(std::string_view n) {
    if (n.empty() || n.size() > 80) return false;
    for (const char c : n) {
        const bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' ||
                        c == '-' || c == '_';
        if (!ok) return false;
    }
    return true;
}

livecore::engine::Engine* g_engine = nullptr;

}  // namespace

int main(int argc, char** argv) {
    using namespace livecore;
    engine::EngineOptions opts;

    for (int i = 1; i < argc; ++i) {
        const std::string_view a = argv[i];
        auto next = [&]() -> std::string_view { return i + 1 < argc ? std::string_view(argv[++i]) : std::string_view(); };
        if (a == "--version") {
            std::printf("%s\n", LIVECORE_ENGINE_VERSION);
            return 0;
        } else if (a == "--help" || a == "-h") {
            usage();
            return 0;
        } else if (a == "--pipe") {
            opts.pipeName = std::string(next());
        } else if (a == "--parent-pid") {
            std::int64_t pid = 0;
            if (!parseInt(next(), pid) || pid <= 0) { std::fputs("invalid --parent-pid\n", stderr); return 1; }
            opts.parentPid = pid;
        } else if (a == "--clock-rate") {
            int hz = 0;
            if (!parseInt(next(), hz) || hz < 1 || hz > 1000) { std::fputs("invalid --clock-rate (1..1000)\n", stderr); return 1; }
            opts.clockRateHz = hz;
        } else if (a == "--no-video") {
            opts.videoEnabled = false;
        } else if (a == "--video-size") {
            const std::string_view v = next();
            const auto x = v.find('x');
            std::uint32_t w = 0, h = 0;
            if (x == std::string_view::npos || !parseInt(v.substr(0, x), w) || !parseInt(v.substr(x + 1), h) || w < 64 ||
                h < 64 || w > 7680 || h > 4320) {
                std::fputs("invalid --video-size (e.g. 1920x1080)\n", stderr);
                return 1;
            }
            opts.videoWidth = w;
            opts.videoHeight = h;
        } else if (a == "--fps") {
            int fps = 0;
            if (!parseInt(next(), fps) || fps < 1 || fps > 120) { std::fputs("invalid --fps (1..120)\n", stderr); return 1; }
            opts.videoFps = fps;
        } else if (a == "--log-level") {
            log::Level lvl;
            if (!log::parseLevel(next(), lvl)) { std::fputs("invalid --log-level\n", stderr); return 1; }
            log::setLevel(lvl);
        } else if (a == "--log-file") {
            const std::string path(next());
            if (path.empty() || !log::openFile(path)) { std::fputs("cannot open --log-file\n", stderr); return 1; }
        } else {
            std::fprintf(stderr, "unknown argument: %s\n", argv[i]);
            usage();
            return 1;
        }
    }

    if (!validPipeName(opts.pipeName)) {
        std::fputs("--pipe is required (1-80 chars: letters, digits, '.', '-', '_')\n", stderr);
        usage();
        return 1;
    }

    engine::Engine eng(std::move(opts));
    g_engine = &eng;
    platform::installStopHandler([] { if (g_engine) g_engine->requestStop(); });
    const int code = eng.run();
    g_engine = nullptr;
    log::closeFile();
    return code;
}
