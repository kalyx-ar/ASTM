#include "remoteusb/cbor.hpp"

#include <limits>

namespace remoteusb::cbor {

Value Value::integer(std::int64_t v) {
    if (v >= 0) return uint(static_cast<std::uint64_t>(v));
    Value x;
    x.kind_ = Kind::Negative;
    x.uint_ = static_cast<std::uint64_t>(-1 - v);
    return x;
}

std::optional<std::int64_t> Value::as_int() const {
    if (kind_ == Kind::Unsigned && uint_ <= static_cast<std::uint64_t>(std::numeric_limits<std::int64_t>::max()))
        return static_cast<std::int64_t>(uint_);
    if (kind_ == Kind::Negative && uint_ <= static_cast<std::uint64_t>(std::numeric_limits<std::int64_t>::max()))
        return -1 - static_cast<std::int64_t>(uint_);
    return std::nullopt;
}

const Value* Value::get(std::uint64_t key) const {
    if (kind_ != Kind::Map) return nullptr;
    auto it = map_.find(key);
    return it == map_.end() ? nullptr : &it->second;
}

namespace {

void head(std::uint8_t major, std::uint64_t argument, Bytes& out) {
    const auto m = static_cast<std::uint8_t>(major << 5);
    if (argument < 24) {
        out.push_back(static_cast<std::uint8_t>(m | argument));
    } else if (argument <= 0xFF) {
        out.push_back(m | 24);
        out.push_back(static_cast<std::uint8_t>(argument));
    } else if (argument <= 0xFFFF) {
        out.push_back(m | 25);
        for (int shift = 8; shift >= 0; shift -= 8) out.push_back(static_cast<std::uint8_t>(argument >> shift));
    } else if (argument <= 0xFFFFFFFFull) {
        out.push_back(m | 26);
        for (int shift = 24; shift >= 0; shift -= 8) out.push_back(static_cast<std::uint8_t>(argument >> shift));
    } else {
        out.push_back(m | 27);
        for (int shift = 56; shift >= 0; shift -= 8) out.push_back(static_cast<std::uint8_t>(argument >> shift));
    }
}

class Reader {
public:
    Reader(const std::uint8_t* data, std::size_t size, DecodeLimits limits) : data_(data), size_(size), limits_(limits) {}

    std::optional<Value> item(std::size_t depth) {
        if (depth > limits_.max_depth || ++items_ > limits_.max_items || pos_ >= size_) return std::nullopt;
        const std::uint8_t initial = data_[pos_++];
        const std::uint8_t major = initial >> 5;
        const std::uint8_t info = initial & 0x1F;
        if (major == 7) {
            switch (info) {
                case 20: return Value::boolean(false);
                case 21: return Value::boolean(true);
                case 22: return Value::null();
                default: return std::nullopt;  // floats, undefined and simple values are not used by Link v1
            }
        }
        auto argument = read_argument(info);
        if (!argument) return std::nullopt;
        switch (major) {
            case 0: return Value::uint(*argument);
            case 1: {
                if (*argument > static_cast<std::uint64_t>(std::numeric_limits<std::int64_t>::max())) return std::nullopt;
                return Value::integer(-1 - static_cast<std::int64_t>(*argument));
            }
            case 2:
            case 3: {
                if (*argument > size_ - pos_) return std::nullopt;
                const auto begin = data_ + pos_;
                pos_ += static_cast<std::size_t>(*argument);
                if (major == 2) return Value::bytes(Bytes(begin, begin + *argument));
                return Value::text(std::string(reinterpret_cast<const char*>(begin), static_cast<std::size_t>(*argument)));
            }
            case 4: {
                if (*argument > limits_.max_items) return std::nullopt;
                std::vector<Value> values;
                values.reserve(static_cast<std::size_t>(*argument));
                for (std::uint64_t i = 0; i < *argument; ++i) {
                    auto v = item(depth + 1);
                    if (!v) return std::nullopt;
                    values.push_back(std::move(*v));
                }
                return Value::array(std::move(values));
            }
            case 5: {
                if (*argument > limits_.max_items) return std::nullopt;
                std::map<std::uint64_t, Value> values;
                for (std::uint64_t i = 0; i < *argument; ++i) {
                    auto key = item(depth + 1);
                    if (!key || !key->as_uint()) return std::nullopt;  // Link v1 maps use unsigned keys only
                    auto v = item(depth + 1);
                    if (!v) return std::nullopt;
                    if (!values.emplace(*key->as_uint(), std::move(*v)).second) return std::nullopt;  // duplicate key
                }
                return Value::map(std::move(values));
            }
            default: return std::nullopt;  // tags are not used
        }
    }

    bool at_end() const { return pos_ == size_; }

private:
    std::optional<std::uint64_t> read_argument(std::uint8_t info) {
        if (info < 24) return info;
        std::size_t length;
        switch (info) {
            case 24: length = 1; break;
            case 25: length = 2; break;
            case 26: length = 4; break;
            case 27: length = 8; break;
            default: return std::nullopt;  // indefinite lengths are not deterministic
        }
        if (length > size_ - pos_) return std::nullopt;
        std::uint64_t value = 0;
        for (std::size_t i = 0; i < length; ++i) value = (value << 8) | data_[pos_++];
        return value;
    }

    const std::uint8_t* data_;
    std::size_t size_;
    std::size_t pos_ = 0;
    std::size_t items_ = 0;
    DecodeLimits limits_;
};

}  // namespace

void encode(const Value& value, Bytes& out) {
    switch (value.kind()) {
        case Value::Kind::Null: out.push_back(0xF6); break;
        case Value::Kind::Bool: out.push_back(*value.as_bool() ? 0xF5 : 0xF4); break;
        case Value::Kind::Unsigned: head(0, *value.as_uint(), out); break;
        case Value::Kind::Negative: head(1, value.raw_uint(), out); break;
        case Value::Kind::ByteString: {
            const auto& b = *value.as_bytes();
            head(2, b.size(), out);
            out.insert(out.end(), b.begin(), b.end());
            break;
        }
        case Value::Kind::TextString: {
            const auto& t = *value.as_text();
            head(3, t.size(), out);
            out.insert(out.end(), t.begin(), t.end());
            break;
        }
        case Value::Kind::Array: {
            const auto& a = *value.as_array();
            head(4, a.size(), out);
            for (const auto& v : a) encode(v, out);
            break;
        }
        case Value::Kind::Map: {
            const auto& m = *value.as_map();
            head(5, m.size(), out);
            for (const auto& [key, v] : m) {
                head(0, key, out);
                encode(v, out);
            }
            break;
        }
    }
}

Bytes encode(const Value& value) {
    Bytes out;
    encode(value, out);
    return out;
}

std::optional<Value> decode(const std::uint8_t* data, std::size_t size, DecodeLimits limits) {
    Reader reader(data, size, limits);
    auto value = reader.item(0);
    if (!value || !reader.at_end()) return std::nullopt;
    return value;
}

}  // namespace remoteusb::cbor
