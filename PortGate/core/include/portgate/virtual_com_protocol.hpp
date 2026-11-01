#pragma once
#include <map>
#include "portgate/link.hpp"
namespace portgate {
struct VirtualComEvent {
    enum class Kind { Opened, Received, Written, Disconnected, Discontinuity };
    Kind kind;
    std::uint64_t request=0;
    std::size_t written=0;
    link::Bytes data;
    std::string detail;
};
// Byte transport only: no instrument protocol, no proprietary legacy-client API.
class VirtualComProtocol {
public:
    explicit VirtualComProtocol(std::string name);
    void begin();
    void accept(const link::Unit& unit);
    bool write(std::uint64_t request,const link::Bytes& bytes);
    void renew();
    void close();
    void fault(const std::string& reason);
    bool active() const { return active_; }
    bool disconnected() const { return disconnected_; }
    std::uint32_t lease_ms() const { return lease_ms_; }
    std::vector<link::Unit> take_output();
    std::vector<VirtualComEvent> take_events();
private:
    struct Chunk { std::uint64_t request; std::size_t size; };
    struct Write { std::size_t remaining=0,written=0; };
    void interrupt_writes(const std::string& reason);
    void acquire();
    void send(link::Type type,const cbor::Value& map);
    std::string name_;
    std::uint16_t port_=0;
    std::uint32_t generation_=0,lease_ms_=15000;
    std::size_t max_write_=16384,pending_bytes_=0,max_payload_=65536;
    std::uint64_t sequence_=0,offset_=0,rx_offset_=0,rx_sequence_=0,pause_request_=0;
    bool hello_=false,acquiring_=false,suspended_=false;
    bool active_=false,disconnected_=false,confirmed_=false,rx_started_=false;
    std::map<std::uint64_t,Chunk> chunks_;
    std::map<std::uint64_t,Write> writes_;
    std::vector<link::Unit> output_;
    std::vector<VirtualComEvent> events_;
};
}