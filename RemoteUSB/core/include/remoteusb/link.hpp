// RemoteUSB Link v1 unit framing (spec/link-v1.md).
#pragma once

#include <array>
#include <cstdint>
#include <optional>
#include <string>
#include <vector>

#include "remoteusb/cbor.hpp"

namespace remoteusb::link {

using Bytes = std::vector<std::uint8_t>;

constexpr std::uint8_t kMagic0 = 0x41;
constexpr std::uint8_t kMagic1 = 0x4C;
constexpr std::uint8_t kVersion = 1;
constexpr std::size_t kHeaderSize = 12;
constexpr std::uint32_t kDefaultMaxUnitBytes = 64 * 1024;

constexpr std::uint16_t kFlagCbor = 0x0001;
constexpr std::uint16_t kFlagCritical = 0x8000;

enum class Type : std::uint8_t {
    Hello = 0x01,
    Capabilities = 0x02,
    Acquire = 0x10,
    Queued = 0x11,
    Granted = 0x12,
    Denied = 0x13,
    Renew = 0x14,
    Release = 0x15,
    Revoked = 0x16,
    RequestHandover = 0x17,
    ConfirmTransfer = 0x18,
    HandoverAsked = 0x19,
    HandoverAnswer = 0x1A,
    HandoverResult = 0x1B,
    KillSwitch = 0x1C,
    Contest = 0x1D,
    SerialData = 0x20,
    WriteStatus = 0x21,
    PortEvent = 0x22,
    SecurityEvent = 0x23,
    // Administration endpoint (spec/admin-v1.md)
    GetStatus = 0x40,
    GetConfig = 0x41,
    SetConfig = 0x42,
    SetTls = 0x43,
    ListDevices = 0x44,
    RevokePort = 0x45,
    ReopenPort = 0x46,
    ReadEvents = 0x47,
    Restart = 0x48,
    UnblockController = 0x49,
    AdminEvent = 0x4E,
    AdminResult = 0x4F,
    Heartbeat = 0x7E,
    Error = 0x7F,
};

enum class WriteResult : std::uint8_t { Completed = 0, Partial = 1, Unknown = 2, Rejected = 3 };

struct Unit {
    Type type{};
    std::uint16_t flags = 0;
    std::uint16_t channel = 0;
    Bytes payload;

    bool is_cbor() const { return (flags & kFlagCbor) != 0; }
};

/// Binary header of SerialData followed by the raw serial bytes.
struct SerialData {
    std::uint64_t seq = 0;
    std::uint64_t offset = 0;
    std::uint32_t generation = 0;
    Bytes data;
};

constexpr std::size_t kSerialDataHeader = 20;

Bytes encode(const Unit& unit);
Unit control(Type type, std::uint16_t channel, const cbor::Value& map, bool critical = false);
Unit serial_data(std::uint16_t channel, const SerialData& data);
std::optional<SerialData> parse_serial_data(const Unit& unit);
std::optional<cbor::Value> parse_map(const Unit& unit);

enum class DecodeError { None, BadMagic, BadVersion, TooLarge };

/// Incremental decoder: accepts any split of the TCP stream and validates the length before buffering a payload.
class Decoder {
public:
    explicit Decoder(std::uint32_t max_unit_bytes = kDefaultMaxUnitBytes) : max_(max_unit_bytes) {}

    /// Appends bytes and moves complete units to `out`. After an error the decoder stays failed.
    DecodeError feed(const std::uint8_t* data, std::size_t size, std::vector<Unit>& out);

    DecodeError error() const { return error_; }

private:
    std::uint32_t max_;
    Bytes buffer_;
    DecodeError error_ = DecodeError::None;
};

}  // namespace remoteusb::link
