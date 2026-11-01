#pragma once
#include <map>
#include <memory>
#include <deque>
#include "portgate/link.hpp"

namespace portgate {
/// Opt-in boundary proof supplied by a protocol profile. Silence is never a completion proof.
class OperationBoundary {
public:
    virtual ~OperationBoundary() = default;
    virtual void observe(bool from_instrument, const link::Bytes& bytes) = 0;
    virtual bool complete() const = 0;
};

struct VirtualComEvent {
    enum class Kind { Opened, Received, Written, Warning, Disconnected, RetirementCancelled, Discontinuity };
    Kind kind;
    std::uint64_t request = 0;
    std::size_t written = 0;
    link::Bytes data;
    std::string detail;
};

/// Driver-side Link state machine. The caller serializes entry. No OS driver or physical device is opened here.
/// It never fabricates instrument bytes; output contains only Link control/data, events contain legacy data/status.
class VirtualComProtocol {
public:
    explicit VirtualComProtocol(std::string name, std::unique_ptr<OperationBoundary> boundary = {});
    void begin();
    bool set_boundary(std::unique_ptr<OperationBoundary> boundary) {
        if (generation_) return false;
        boundary_ = std::move(boundary); return true;
    }
    void accept(const link::Unit& unit);
    bool write(std::uint64_t request, const link::Bytes& bytes);
    void renew();
    void close();
    /// For an aware legacy client only: explicitly declares that its task finished after the current warning.
    bool complete_task(std::uint64_t warning);
    void fault(const std::string& reason);
    bool active() const { return active_; }
    bool disconnected() const { return disconnected_; }
    std::uint32_t lease_ms() const { return lease_ms_; }
    std::uint64_t warning() const { return warning_; }
    std::vector<link::Unit> take_output();
    std::vector<VirtualComEvent> take_events();
private:
    struct Chunk { std::uint64_t request; std::size_t size; };
    struct Write { std::size_t remaining = 0, written = 0; };
    void finish_if_safe();
    void resume_deferred();
    void send(link::Type type, const cbor::Value& map);
    std::string name_;
    std::string retirement_reason_;
    std::unique_ptr<OperationBoundary> boundary_;
    std::uint16_t port_ = 0;
    std::uint32_t generation_ = 0, lease_ms_ = 15000;
    std::size_t max_write_ = 16384, pending_bytes_ = 0;
    std::uint64_t sequence_ = 0, offset_ = 0, rx_offset_ = 0, rx_sequence_ = 0, warning_ = 0;
    std::uint64_t tx_version_=0;
    std::size_t max_payload_=65536;
    bool hello_=false, committing_=false;
    bool active_ = false, retiring_ = false, disconnected_ = false, confirmed_ = false, explicit_complete_ = false, rx_started_ = false;
    std::map<std::uint64_t, Chunk> chunks_;
    std::map<std::uint64_t, Write> writes_;
    std::deque<std::pair<std::uint64_t,link::Bytes>> deferred_;
    std::size_t deferred_bytes_=0;
    std::vector<link::Unit> output_;
    std::vector<VirtualComEvent> events_;
};
}
