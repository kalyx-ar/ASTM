#include "atsm/json.hpp"

#include <cstdlib>

namespace atsm::json {

namespace {

class Parser {
public:
    Parser(const std::string& text, ParseError& error, std::size_t max_depth) : text_(text), error_(error), max_depth_(max_depth) {}

    std::optional<Value> document() {
        auto value = parse_value(0);
        if (!value) return std::nullopt;
        skip_ws();
        if (pos_ != text_.size()) return fail("unexpected content after the document");
        return value;
    }

private:
    std::nullopt_t fail(const char* message) {
        if (error_.message.empty()) {
            error_.offset = pos_;
            error_.message = message;
        }
        return std::nullopt;
    }

    void skip_ws() {
        while (pos_ < text_.size() && (text_[pos_] == ' ' || text_[pos_] == '\t' || text_[pos_] == '\n' || text_[pos_] == '\r')) ++pos_;
    }

    bool literal(const char* word) {
        std::size_t i = 0;
        while (word[i] != '\0') {
            if (pos_ + i >= text_.size() || text_[pos_ + i] != word[i]) return false;
            ++i;
        }
        pos_ += i;
        return true;
    }

    std::optional<Value> parse_value(std::size_t depth) {
        if (depth > max_depth_) return fail("document too deep");
        skip_ws();
        if (pos_ >= text_.size()) return fail("unexpected end");
        Value value;
        const char c = text_[pos_];
        if (c == '{') return parse_object(depth);
        if (c == '[') return parse_array(depth);
        if (c == '"') {
            auto s = parse_string();
            if (!s) return std::nullopt;
            value.kind = Value::Kind::String;
            value.string = std::move(*s);
            return value;
        }
        if (literal("true")) { value.kind = Value::Kind::Bool; value.boolean = true; return value; }
        if (literal("false")) { value.kind = Value::Kind::Bool; return value; }
        if (literal("null")) return value;
        if (c == '-' || (c >= '0' && c <= '9')) {
            const char* begin = text_.c_str() + pos_;
            char* end = nullptr;
            value.number = std::strtod(begin, &end);
            if (end == begin) return fail("invalid number");
            pos_ += static_cast<std::size_t>(end - begin);
            value.kind = Value::Kind::Number;
            return value;
        }
        return fail("unexpected character");
    }

    std::optional<Value> parse_object(std::size_t depth) {
        Value value;
        value.kind = Value::Kind::Object;
        ++pos_;
        skip_ws();
        if (pos_ < text_.size() && text_[pos_] == '}') { ++pos_; return value; }
        while (true) {
            skip_ws();
            if (pos_ >= text_.size() || text_[pos_] != '"') return fail("expected a key");
            auto key = parse_string();
            if (!key) return std::nullopt;
            skip_ws();
            if (pos_ >= text_.size() || text_[pos_] != ':') return fail("expected ':'");
            ++pos_;
            auto item = parse_value(depth + 1);
            if (!item) return std::nullopt;
            if (!value.object.emplace(std::move(*key), std::move(*item)).second) return fail("duplicate key");
            skip_ws();
            if (pos_ < text_.size() && text_[pos_] == ',') { ++pos_; continue; }
            if (pos_ < text_.size() && text_[pos_] == '}') { ++pos_; return value; }
            return fail("expected ',' or '}'");
        }
    }

    std::optional<Value> parse_array(std::size_t depth) {
        Value value;
        value.kind = Value::Kind::Array;
        ++pos_;
        skip_ws();
        if (pos_ < text_.size() && text_[pos_] == ']') { ++pos_; return value; }
        while (true) {
            auto item = parse_value(depth + 1);
            if (!item) return std::nullopt;
            value.array.push_back(std::move(*item));
            skip_ws();
            if (pos_ < text_.size() && text_[pos_] == ',') { ++pos_; continue; }
            if (pos_ < text_.size() && text_[pos_] == ']') { ++pos_; return value; }
            return fail("expected ',' or ']'");
        }
    }

    std::optional<std::string> parse_string() {
        ++pos_;
        std::string out;
        while (pos_ < text_.size()) {
            const char c = text_[pos_++];
            if (c == '"') return out;
            if (static_cast<unsigned char>(c) < 0x20) { fail("control character in string"); return std::nullopt; }
            if (c != '\\') { out.push_back(c); continue; }
            if (pos_ >= text_.size()) break;
            const char e = text_[pos_++];
            switch (e) {
                case '"': out.push_back('"'); break;
                case '\\': out.push_back('\\'); break;
                case '/': out.push_back('/'); break;
                case 'b': out.push_back('\b'); break;
                case 'f': out.push_back('\f'); break;
                case 'n': out.push_back('\n'); break;
                case 'r': out.push_back('\r'); break;
                case 't': out.push_back('\t'); break;
                case 'u': {
                    if (pos_ + 4 > text_.size()) { fail("short unicode escape"); return std::nullopt; }
                    const unsigned long code = std::strtoul(text_.substr(pos_, 4).c_str(), nullptr, 16);
                    pos_ += 4;
                    // Configuration values are ASCII paths and identifiers; encode BMP code points as UTF-8.
                    if (code < 0x80) {
                        out.push_back(static_cast<char>(code));
                    } else if (code < 0x800) {
                        out.push_back(static_cast<char>(0xC0 | (code >> 6)));
                        out.push_back(static_cast<char>(0x80 | (code & 0x3F)));
                    } else {
                        out.push_back(static_cast<char>(0xE0 | (code >> 12)));
                        out.push_back(static_cast<char>(0x80 | ((code >> 6) & 0x3F)));
                        out.push_back(static_cast<char>(0x80 | (code & 0x3F)));
                    }
                    break;
                }
                default: fail("invalid escape"); return std::nullopt;
            }
        }
        fail("unterminated string");
        return std::nullopt;
    }

    const std::string& text_;
    ParseError& error_;
    std::size_t max_depth_;
    std::size_t pos_ = 0;
};

}  // namespace

std::optional<Value> parse(const std::string& text, ParseError& error, std::size_t max_depth) {
    Parser parser(text, error, max_depth);
    return parser.document();
}

}  // namespace atsm::json
