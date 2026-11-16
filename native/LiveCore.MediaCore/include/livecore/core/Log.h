// Live Core Studio — LiveCore.MediaCore
// Thread-safe minimal logger. Writes to stderr and optionally a file.
#pragma once

#include <cstdio>
#include <format>
#include <string>
#include <string_view>

namespace livecore::log {

enum class Level { Trace = 0, Debug, Info, Warn, Error, Off };

void setLevel(Level level);
Level level();
bool parseLevel(std::string_view text, Level& out);

/// Opens (appends) a log file in addition to stderr. Returns false on failure.
bool openFile(const std::string& path);
void closeFile();

void write(Level level, std::string_view component, std::string_view message);

template <class... Args>
void info(std::string_view component, std::format_string<Args...> fmt, Args&&... args) {
    if (level() <= Level::Info) write(Level::Info, component, std::format(fmt, std::forward<Args>(args)...));
}
template <class... Args>
void debug(std::string_view component, std::format_string<Args...> fmt, Args&&... args) {
    if (level() <= Level::Debug) write(Level::Debug, component, std::format(fmt, std::forward<Args>(args)...));
}
template <class... Args>
void warn(std::string_view component, std::format_string<Args...> fmt, Args&&... args) {
    if (level() <= Level::Warn) write(Level::Warn, component, std::format(fmt, std::forward<Args>(args)...));
}
template <class... Args>
void error(std::string_view component, std::format_string<Args...> fmt, Args&&... args) {
    if (level() <= Level::Error) write(Level::Error, component, std::format(fmt, std::forward<Args>(args)...));
}

}  // namespace livecore::log
