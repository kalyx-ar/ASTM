// Minimal deterministic CBOR (RFC 8949) subset used by PortGate Link v1:
// unsigned/negative integers, byte and text strings, arrays, maps, booleans and null.
#pragma once

#include <cstdint>
#include <map>
#include <memory>
#include <optional>
#include <string>
#include <vector>

namespace portgate::cbor {

using Bytes = std::vector<std::uint8_t>;

class Value {
public:
    enum class Kind { Null, Bool, Unsigned, Negative, ByteString, TextString, Array, Map };

    Value() = default;
    static Value null() { return Value(); }
    static Value boolean(bool v) { Value x; x.kind_ = Kind::Bool; x.bool_ = v; return x; }
    static Value uint(std::uint64_t v) { Value x; x.kind_ = Kind::Unsigned; x.uint_ = v; return x; }
    static Value integer(std::int64_t v);
    static Value bytes(Bytes v) { Value x; x.kind_ = Kind::ByteString; x.bytes_ = std::move(v); return x; }
    static Value text(std::string v) { Value x; x.kind_ = Kind::TextString; x.text_ = std::move(v); return x; }
    static Value array(std::vector<Value> v) { Value x; x.kind_ = Kind::Array; x.array_ = std::move(v); return x; }
    /// Map with small unsigned integer keys; std::map keeps them in ascending (deterministic) order.
    static Value map(std::map<std::uint64_t, Value> v) { Value x; x.kind_ = Kind::Map; x.map_ = std::move(v); return x; }

    Kind kind() const { return kind_; }
    bool is(Kind k) const { return kind_ == k; }

    std::optional<std::uint64_t> as_uint() const { return kind_ == Kind::Unsigned ? std::optional<std::uint64_t>(uint_) : std::nullopt; }
    std::optional<std::int64_t> as_int() const;
    std::optional<bool> as_bool() const { return kind_ == Kind::Bool ? std::optional<bool>(bool_) : std::nullopt; }
    const std::string* as_text() const { return kind_ == Kind::TextString ? &text_ : nullptr; }
    const Bytes* as_bytes() const { return kind_ == Kind::ByteString ? &bytes_ : nullptr; }
    const std::vector<Value>* as_array() const { return kind_ == Kind::Array ? &array_ : nullptr; }
    const std::map<std::uint64_t, Value>* as_map() const { return kind_ == Kind::Map ? &map_ : nullptr; }

    /// Map lookup; returns nullptr when this is not a map or the key is absent.
    const Value* get(std::uint64_t key) const;

    std::uint64_t raw_uint() const { return uint_; }

private:
    Kind kind_ = Kind::Null;
    bool bool_ = false;
    std::uint64_t uint_ = 0;  // for Negative: encoded argument n, value = -1 - n
    Bytes bytes_;
    std::string text_;
    std::vector<Value> array_;
    std::map<std::uint64_t, Value> map_;
};

void encode(const Value& value, Bytes& out);
Bytes encode(const Value& value);

struct DecodeLimits {
    std::size_t max_depth = 8;
    std::size_t max_items = 4096;
};

/// Decodes exactly one item spanning the whole input. Returns nullopt on malformed, unsupported or oversized input.
std::optional<Value> decode(const std::uint8_t* data, std::size_t size, DecodeLimits limits = {});

}  // namespace portgate::cbor
