// Durable capture of the port traffic (spec/admin-v1.md § Capture). Each record is sealed with AES-256-GCM using the
// key the server provisioned; the agent never decrypts it. Records are bounded by quota and retention and deleted when
// the server confirms custody. Lost ranges are kept as explicit gaps.
#pragma once

#include <condition_variable>
#include <cstdint>
#include <deque>
#include <map>
#include <mutex>
#include <string>
#include <thread>
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
    std::string reason;  // quota, retention, write-failed, seal-failed, no-key, backlog
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
    /// Seals and writes what is still queued, then stops the writer thread.
    ~CaptureStore();
    CaptureStore(const CaptureStore&) = delete;
    CaptureStore& operator=(const CaptureStore&) = delete;

    void set_key(std::uint32_t key_id, std::vector<std::uint8_t> key);
    bool has_key() const;
    void configure(const std::string& agent_id, const std::vector<PortConfig>& ports);

    /// Seals and appends one record of `port_id`; enforces quota and retention. Failures become gaps, never plain data.
    void append(const std::string& port_id, const cbor::Value& record);
    /// Queues the record for the writer thread and returns at once (called by the port thread): sealing and disk never
    /// delay the owner. Past kQueueLimit the record is not kept and becomes a `backlog` gap.
    void submit(const std::string& port_id, const cbor::Value& record);
    /// Waits (bounded) until the queued records are written. read() calls it so a page includes what was submitted.
    void flush(int wait_ms);
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
    void append_encoded(const std::string& port_id, std::vector<std::uint8_t>& plain);
    void writer_loop();

    std::string directory_;
    std::mutex mutex_;
    std::string agent_id_;
    std::uint32_t key_id_ = 0;
    std::vector<std::uint8_t> key_;
    std::map<std::string, Port> ports_;

    struct Queued {
        std::string port_id;
        std::vector<std::uint8_t> plain;  // encoded record, wiped once sealed
        std::uint64_t skipped = 0;        // > 0: records not kept because the queue was full
    };
    static constexpr std::size_t kQueueLimit = 4 * 1024 * 1024;
    std::mutex queue_mutex_;
    std::condition_variable queue_changed_;
    std::deque<Queued> queue_;
    std::size_t queue_bytes_ = 0;
    bool busy_ = false;
    bool stopping_ = false;
    std::thread writer_;
};

}  // namespace portgate
