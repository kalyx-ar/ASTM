#include "atsm/link.hpp"

namespace atsm::link {

namespace {

void put16(Bytes& out, std::uint16_t v) {
    out.push_back(static_cast<std::uint8_t>(v >> 8));
    out.push_back(static_cast<std::uint8_t>(v));
}

void put32(Bytes& out, std::uint32_t v) {
    for (int shift = 24; shift >= 0; shift -= 8) out.push_back(static_cast<std::uint8_t>(v >> shift));
}

void put64(Bytes& out, std::uint64_t v) {
    for (int shift = 56; shift >= 0; shift -= 8) out.push_back(static_cast<std::uint8_t>(v >> shift));
}

std::uint64_t get(const std::uint8_t* p, int bytes) {
    std::uint64_t v = 0;
    for (int i = 0; i < bytes; ++i) v = (v << 8) | p[i];
    return v;
}

}  // namespace

Bytes encode(const Unit& unit) {
    Bytes out;
    out.reserve(kHeaderSize + unit.payload.size());
    out.push_back(kMagic0);
    out.push_back(kMagic1);
    out.push_back(kVersion);
    out.push_back(static_cast<std::uint8_t>(unit.type));
    put16(out, unit.flags);
    put16(out, unit.channel);
    put32(out, static_cast<std::uint32_t>(unit.payload.size()));
    out.insert(out.end(), unit.payload.begin(), unit.payload.end());
    return out;
}

Unit control(Type type, std::uint16_t channel, const cbor::Value& map, bool critical) {
    Unit unit;
    unit.type = type;
    unit.flags = static_cast<std::uint16_t>(kFlagCbor | (critical ? kFlagCritical : 0));
    unit.channel = channel;
    unit.payload = cbor::encode(map);
    return unit;
}

Unit serial_data(std::uint16_t channel, const SerialData& data) {
    Unit unit;
    unit.type = Type::SerialData;
    unit.channel = channel;
    unit.payload.reserve(kSerialDataHeader + data.data.size());
    put64(unit.payload, data.seq);
    put64(unit.payload, data.offset);
    put32(unit.payload, data.generation);
    unit.payload.insert(unit.payload.end(), data.data.begin(), data.data.end());
    return unit;
}

std::optional<SerialData> parse_serial_data(const Unit& unit) {
    if (unit.type != Type::SerialData || unit.is_cbor() || unit.payload.size() < kSerialDataHeader) return std::nullopt;
    const auto* p = unit.payload.data();
    SerialData data;
    data.seq = get(p, 8);
    data.offset = get(p + 8, 8);
    data.generation = static_cast<std::uint32_t>(get(p + 16, 4));
    data.data.assign(unit.payload.begin() + kSerialDataHeader, unit.payload.end());
    return data;
}

std::optional<cbor::Value> parse_map(const Unit& unit) {
    if (!unit.is_cbor()) return std::nullopt;
    auto value = cbor::decode(unit.payload.data(), unit.payload.size());
    if (!value || !value->is(cbor::Value::Kind::Map)) return std::nullopt;
    return value;
}

DecodeError Decoder::feed(const std::uint8_t* data, std::size_t size, std::vector<Unit>& out) {
    if (error_ != DecodeError::None) return error_;
    buffer_.insert(buffer_.end(), data, data + size);
    std::size_t pos = 0;
    while (buffer_.size() - pos >= kHeaderSize) {
        const auto* h = buffer_.data() + pos;
        if (h[0] != kMagic0 || h[1] != kMagic1) return error_ = DecodeError::BadMagic;
        if (h[2] != kVersion) return error_ = DecodeError::BadVersion;
        const auto length = static_cast<std::uint32_t>(get(h + 8, 4));
        if (length > max_) return error_ = DecodeError::TooLarge;
        if (buffer_.size() - pos < kHeaderSize + length) break;
        Unit unit;
        unit.type = static_cast<Type>(h[3]);
        unit.flags = static_cast<std::uint16_t>(get(h + 4, 2));
        unit.channel = static_cast<std::uint16_t>(get(h + 6, 2));
        unit.payload.assign(h + kHeaderSize, h + kHeaderSize + length);
        out.push_back(std::move(unit));
        pos += kHeaderSize + length;
    }
    buffer_.erase(buffer_.begin(), buffer_.begin() + static_cast<std::ptrdiff_t>(pos));
    return DecodeError::None;
}

}  // namespace atsm::link
