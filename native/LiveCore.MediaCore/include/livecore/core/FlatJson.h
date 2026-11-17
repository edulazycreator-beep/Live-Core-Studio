// Live Core Studio — LiveCore.MediaCore
// Minimal JSON support for flat control messages: one object whose values are
// strings, numbers, booleans or null. Nested values are skipped when reading.
// The control channel never needs more; media payloads do not use JSON.
#pragma once

#include <cstdint>
#include <map>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

namespace livecore::json {

struct Value {
    enum class Kind { Null, Bool, Number, String } kind = Kind::Null;
    bool boolean = false;
    double number = 0.0;
    std::string text;
};

class Object {
public:
    /// Parses a flat JSON object. Returns std::nullopt on malformed input.
    static std::optional<Object> parse(std::string_view input);

    bool has(const std::string& key) const { return values_.contains(key); }
    std::optional<std::string> getString(const std::string& key) const;
    std::optional<std::int64_t> getInt(const std::string& key) const;
    std::optional<bool> getBool(const std::string& key) const;

    const std::map<std::string, Value>& values() const { return values_; }

private:
    std::map<std::string, Value> values_;
};

/// Builds a flat JSON object in insertion order.
class Writer {
public:
    Writer& add(std::string_view key, std::string_view value);
    Writer& add(std::string_view key, const char* value) { return add(key, std::string_view(value)); }
    Writer& add(std::string_view key, std::int64_t value);
    Writer& add(std::string_view key, int value) { return add(key, static_cast<std::int64_t>(value)); }
    Writer& add(std::string_view key, bool value);
    Writer& add(std::string_view key, double value);
    /// Inserts already-serialized JSON (an array or object) verbatim.
    Writer& addRaw(std::string_view key, std::string_view json);
    std::string str() const { return body_.empty() ? std::string("{}") : "{" + body_ + "}"; }

private:
    void key(std::string_view k);
    std::string body_;
};

std::string escape(std::string_view s);

/// Joins already-serialized JSON values into an array: [a,b,c].
std::string array(const std::vector<std::string>& items);

}  // namespace livecore::json
