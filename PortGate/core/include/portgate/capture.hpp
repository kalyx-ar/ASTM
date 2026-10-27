// Durable capture of the port traffic (spec/admin-v1.md § Capture). Each record is sealed with AES-256-GCM using the
// key the server provisioned; the agent never decrypts it. Records are bounded by quota and retention and deleted when
// the server confirms custody. Lost ranges are kept as explicit gaps.
#pragma once

#include <cstdint>
#include <map>
#include <mutex>
#include <string>
#include <vector>

#include "portgate/cbor.hpp"
#include "portgate/config.hpp"

namespace portgate {

struct CaptureRecord {
    std::uint64_t seq = 0;
    std::uint32_t key_id = 0;
    std::uint64_t time_ms = 0;  // UTC, in clear for retention
    std::vector<std::uint8_t> nonce;
    std::vector<std::uint8_t> sealed;
};

struct CaptureGap {
    std::uint64_t from = 0;
    std::uint64_t to = 0;
    std::string reason;  // quota, retention, write-failed, no-key
};

struct CapturePage {
    std::vector<CaptureRecord> records;
    std::uint64_t first = 0;  // oldest retained sequence (0 when empty)
    std::uint64_t last = 0;   // newest sequence ever written for the port
    std::vector<CaptureGap> gaps;
};

class CaptureStore {
public:
    explicit CaptureStore(std::string directory) : directory_(std::move(directory)) {}

    void set_key(std::uint32_t key_id, std::vector<std::uint8_t> key);
    bool has_key() const;
    void configure(const std::string& agent_id, const std::vector<PortConfig>& ports);

    /// Seals and appends one record of `port_id`; enforces quota and retention. Failures become gaps, never plain data.
    void append(const std::string& port_id, const cbor::Value& record);
    CapturePage read(const std::string& port_id, std::uint64_t after, std::size_t max);
    /// The server took custody up to `upto`: those records are deleted.
    bool ack(const std::string& port_id, std::uint64_t upto, std::string& error);

    /// Additional data authenticated with each record: binds it to agent, port, position and key.
    static std::vector<std::uint8_t> aad(const std::string& agent_id, const std::string& port_id, std::uint64_t seq, std::uint32_t key_id);

private:
    struct Port {
        bool loaded = false;
        std::string path;
        std::string gaps_path;
        PortConfig::Capture limits;
        std::vector<CaptureRecord> records;
        std::uint64_t file_bytes = 0;
        std::uint64_t next_seq = 1;
        std::vector<CaptureGap> gaps;
    };

    Port& port(const std::string& port_id);
    void load(Port& port);
    void rewrite(Port& port);
    void add_gap(Port& port, std::uint64_t from, std::uint64_t to, const std::string& reason);
    void enforce(Port& port);

    std::string directory_;
    std::mutex mutex_;
    std::string agent_id_;
    std::uint32_t key_id_ = 0;
    std::vector<std::uint8_t> key_;
    std::map<std::string, Port> ports_;
};

}  // namespace portgate
