#include "livecore/core/FlatJson.h"

#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <format>

namespace livecore::json {
namespace {

class Parser {
public:
    explicit Parser(std::string_view s) : s_(s) {}

    std::optional<Object> run(std::map<std::string, Value>& out) {
        ws();
        if (!eat('{')) return std::nullopt;
        ws();
        if (eat('}')) return finish();
        for (;;) {
            ws();
            std::string k;
            if (!str(k)) return std::nullopt;
            ws();
            if (!eat(':')) return std::nullopt;
            ws();
            Value v;
            bool keep = true;
            if (!value(v, keep)) return std::nullopt;
            if (keep) out[k] = std::move(v);
            ws();
            if (eat(',')) continue;
            if (eat('}')) return finish();
            return std::nullopt;
        }
    }

private:
    std::optional<Object> finish() {
        ws();
        if (i_ != s_.size()) return std::nullopt;
        return Object{};
    }

    void ws() {
        while (i_ < s_.size() && (s_[i_] == ' ' || s_[i_] == '\t' || s_[i_] == '\n' || s_[i_] == '\r')) ++i_;
    }
    bool eat(char c) {
        if (i_ < s_.size() && s_[i_] == c) { ++i_; return true; }
        return false;
    }
    bool lit(std::string_view w) {
        if (s_.substr(i_, w.size()) == w) { i_ += w.size(); return true; }
        return false;
    }

    static void appendUtf8(std::string& out, std::uint32_t cp) {
        if (cp < 0x80) out += static_cast<char>(cp);
        else if (cp < 0x800) { out += static_cast<char>(0xC0 | (cp >> 6)); out += static_cast<char>(0x80 | (cp & 0x3F)); }
        else if (cp < 0x10000) {
            out += static_cast<char>(0xE0 | (cp >> 12));
            out += static_cast<char>(0x80 | ((cp >> 6) & 0x3F));
            out += static_cast<char>(0x80 | (cp & 0x3F));
        } else {
            out += static_cast<char>(0xF0 | (cp >> 18));
            out += static_cast<char>(0x80 | ((cp >> 12) & 0x3F));
            out += static_cast<char>(0x80 | ((cp >> 6) & 0x3F));
            out += static_cast<char>(0x80 | (cp & 0x3F));
        }
    }

    bool hex4(std::uint32_t& cp) {
        if (i_ + 4 > s_.size()) return false;
        cp = 0;
        for (int n = 0; n < 4; ++n) {
            const char c = s_[i_++];
            cp <<= 4;
            if (c >= '0' && c <= '9') cp |= c - '0';
            else if (c >= 'a' && c <= 'f') cp |= c - 'a' + 10;
            else if (c >= 'A' && c <= 'F') cp |= c - 'A' + 10;
            else return false;
        }
        return true;
    }

    bool str(std::string& out) {
        if (!eat('"')) return false;
        while (i_ < s_.size()) {
            const char c = s_[i_++];
            if (c == '"') return true;
            if (static_cast<unsigned char>(c) < 0x20) return false;
            if (c != '\\') { out += c; continue; }
            if (i_ >= s_.size()) return false;
            const char e = s_[i_++];
            switch (e) {
                case '"': out += '"'; break;
                case '\\': out += '\\'; break;
                case '/': out += '/'; break;
                case 'b': out += '\b'; break;
                case 'f': out += '\f'; break;
                case 'n': out += '\n'; break;
                case 'r': out += '\r'; break;
                case 't': out += '\t'; break;
                case 'u': {
                    std::uint32_t cp;
                    if (!hex4(cp)) return false;
                    if (cp >= 0xD800 && cp <= 0xDBFF) {  // surrogate pair
                        std::uint32_t lo;
                        if (!lit("\\u") || !hex4(lo) || lo < 0xDC00 || lo > 0xDFFF) return false;
                        cp = 0x10000 + ((cp - 0xD800) << 10) + (lo - 0xDC00);
                    }
                    appendUtf8(out, cp);
                    break;
                }
                default: return false;
            }
        }
        return false;
    }

    bool number(double& out) {
        const std::size_t start = i_;
        if (i_ < s_.size() && s_[i_] == '-') ++i_;
        while (i_ < s_.size() && ((s_[i_] >= '0' && s_[i_] <= '9') || s_[i_] == '.' || s_[i_] == 'e' ||
                                  s_[i_] == 'E' || s_[i_] == '+' || s_[i_] == '-'))
            ++i_;
        if (i_ == start) return false;
        const std::string tmp(s_.substr(start, i_ - start));
        char* end = nullptr;
        out = std::strtod(tmp.c_str(), &end);
        return end == tmp.c_str() + tmp.size() && std::isfinite(out);
    }

    // Skips a nested array/object (kept out of the flat map).
    bool skipNested() {
        int depth = 0;
        while (i_ < s_.size()) {
            const char c = s_[i_];
            if (c == '"') { std::string dummy; if (!str(dummy)) return false; continue; }
            ++i_;
            if (c == '{' || c == '[') ++depth;
            else if (c == '}' || c == ']') { if (--depth == 0) return true; }
        }
        return false;
    }

    bool value(Value& v, bool& keep) {
        if (i_ >= s_.size()) return false;
        const char c = s_[i_];
        if (c == '"') { v.kind = Value::Kind::String; return str(v.text); }
        if (c == '{' || c == '[') { keep = false; return skipNested(); }
        if (lit("true")) { v.kind = Value::Kind::Bool; v.boolean = true; return true; }
        if (lit("false")) { v.kind = Value::Kind::Bool; v.boolean = false; return true; }
        if (lit("null")) { v.kind = Value::Kind::Null; return true; }
        v.kind = Value::Kind::Number;
        return number(v.number);
    }

    std::string_view s_;
    std::size_t i_ = 0;
};

}  // namespace

std::optional<Object> Object::parse(std::string_view input) {
    std::map<std::string, Value> values;
    Parser p(input);
    auto result = p.run(values);
    if (!result) return std::nullopt;
    result->values_ = std::move(values);
    return result;
}

std::optional<std::string> Object::getString(const std::string& key) const {
    auto it = values_.find(key);
    if (it == values_.end() || it->second.kind != Value::Kind::String) return std::nullopt;
    return it->second.text;
}

std::optional<std::int64_t> Object::getInt(const std::string& key) const {
    auto it = values_.find(key);
    if (it == values_.end() || it->second.kind != Value::Kind::Number) return std::nullopt;
    const double d = it->second.number;
    if (d != std::floor(d) || std::fabs(d) > 9.007199254740992e15) return std::nullopt;
    return static_cast<std::int64_t>(d);
}

std::optional<bool> Object::getBool(const std::string& key) const {
    auto it = values_.find(key);
    if (it == values_.end() || it->second.kind != Value::Kind::Bool) return std::nullopt;
    return it->second.boolean;
}

std::string escape(std::string_view s) {
    std::string out;
    out.reserve(s.size() + 2);
    for (const char c : s) {
        switch (c) {
            case '"': out += "\\\""; break;
            case '\\': out += "\\\\"; break;
            case '\n': out += "\\n"; break;
            case '\r': out += "\\r"; break;
            case '\t': out += "\\t"; break;
            default:
                if (static_cast<unsigned char>(c) < 0x20) out += std::format("\\u{:04x}", static_cast<unsigned>(c));
                else out += c;
        }
    }
    return out;
}

void Writer::key(std::string_view k) {
    if (!body_.empty()) body_ += ',';
    body_ += '"';
    body_ += escape(k);
    body_ += "\":";
}

Writer& Writer::add(std::string_view k, std::string_view value) {
    key(k);
    body_ += '"';
    body_ += escape(value);
    body_ += '"';
    return *this;
}

Writer& Writer::add(std::string_view k, std::int64_t value) {
    key(k);
    body_ += std::to_string(value);
    return *this;
}

Writer& Writer::add(std::string_view k, bool value) {
    key(k);
    body_ += value ? "true" : "false";
    return *this;
}

}  // namespace livecore::json
