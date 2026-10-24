// Small strict JSON reader for the agent configuration (RFC 8259; no comments, no trailing commas).
#pragma once

#include <map>
#include <memory>
#include <optional>
#include <string>
#include <vector>

namespace atsm::json {

class Value {
public:
    enum class Kind { Null, Bool, Number, String, Array, Object };

    Kind kind = Kind::Null;
    bool boolean = false;
    double number = 0;
    std::string string;
    std::vector<Value> array;
    std::map<std::string, Value> object;

    const Value* find(const std::string& key) const {
        auto it = object.find(key);
        return kind == Kind::Object && it != object.end() ? &it->second : nullptr;
    }
};

struct ParseError {
    std::size_t offset = 0;
    std::string message;
};

std::optional<Value> parse(const std::string& text, ParseError& error, std::size_t max_depth = 32);

}  // namespace atsm::json
