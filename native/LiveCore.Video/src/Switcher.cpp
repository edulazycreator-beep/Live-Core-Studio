#include "livecore/video/Switcher.h"

#include <algorithm>

namespace livecore::video {

void Switcher::addInput(SourceId id) {
    std::lock_guard lock(mutex_);
    inputs_.insert(id);
}

void Switcher::removeInput(SourceId id) {
    std::lock_guard lock(mutex_);
    inputs_.erase(id);
    if (program_ == id || preview_ == id) transitioning_ = false;
    if (program_ == id) program_ = kNoSource;
    if (preview_ == id) preview_ = kNoSource;
}

bool Switcher::hasInput(SourceId id) const {
    std::lock_guard lock(mutex_);
    return inputs_.contains(id);
}

bool Switcher::setPreview(SourceId id) {
    std::lock_guard lock(mutex_);
    if (id != kNoSource && !inputs_.contains(id)) return false;
    // Changing preview mid-transition would change the picture being mixed in; finish it instantly.
    if (transitioning_) {
        std::swap(program_, preview_);
        transitioning_ = false;
    }
    preview_ = id;
    return true;
}

bool Switcher::setProgram(SourceId id) {
    std::lock_guard lock(mutex_);
    if (id != kNoSource && !inputs_.contains(id)) return false;
    transitioning_ = false;
    program_ = id;
    return true;
}

void Switcher::cut() {
    std::lock_guard lock(mutex_);
    transitioning_ = false;
    std::swap(program_, preview_);
}

bool Switcher::autoTransition(std::chrono::milliseconds duration, Clock::time_point now) {
    std::lock_guard lock(mutex_);
    if (transitioning_ || preview_ == kNoSource) return false;
    if (duration.count() <= 0) {
        std::swap(program_, preview_);
        return true;
    }
    transitioning_ = true;
    transitionStart_ = now;
    transitionDuration_ = duration;
    return true;
}

float Switcher::positionLocked(Clock::time_point now) const {
    if (!transitioning_) return 0.0f;
    const auto elapsed = std::chrono::duration<float, std::milli>(now - transitionStart_).count();
    const auto total = static_cast<float>(transitionDuration_.count());
    return std::clamp(elapsed / total, 0.0f, 1.0f);
}

Switcher::ProgramFrame Switcher::advance(Clock::time_point now) {
    std::lock_guard lock(mutex_);
    if (transitioning_ && positionLocked(now) >= 1.0f) {
        std::swap(program_, preview_);
        transitioning_ = false;
    }
    if (!transitioning_) return {program_, program_, 0.0f};
    return {program_, preview_, positionLocked(now)};
}

Switcher::Snapshot Switcher::snapshot(Clock::time_point now) const {
    std::lock_guard lock(mutex_);
    Snapshot s;
    s.program = program_;
    s.preview = preview_;
    s.transitioning = transitioning_;
    s.position = positionLocked(now);
    s.transitionMs = transitioning_ ? static_cast<int>(transitionDuration_.count()) : 0;
    return s;
}

}  // namespace livecore::video
