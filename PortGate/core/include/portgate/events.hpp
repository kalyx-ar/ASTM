// Persistent event log of the agent: semaphore, security, devices and configuration changes (who, when, revision;
// never the configuration content). Records are sealed with platform::protect, size-bounded and rotated.
#pragma once

#include <cstdint>
#include <deque>
#include <mutex>
#include <optional>
#include <string>
#include <vector>

#include "portgate/cbor.hpp"

namespace portgate {

struct Event {
    std::uint64_t id = 0;
    std::uint64_t time_ms = 0;  // UTC, milliseconds since the Unix epoch
    std::string kind;           // granted, revoked, handover-*, kill-switch, contest, secure-close, device-*, config-applied...
    std::string port_id;
    std::string actor;   // controller or admin identity that caused it
    std::string target;  // controller affected
    std::uint32_t generation = 0;
    std::string reason;
    std::uint64_t until_ms = 0;  // UTC deadline (contest window, block) when relevant
    bool uncertain = false;      // bytes in flight or a write of unknown outcome at the moment of the event
    std::string detail;

    cbor::Value to_cbor() const;
    static std::optional<Event> from_cbor(const cbor::Value& value);
};

class EventLog {
public:
    explicit EventLog(std::string directory, std::uint64_t max_file_bytes = 1024 * 1024, std::size_t memory = 2000);

    /// Loads the retained records (current and rotated file). A damaged tail is ignored, never fatal.
    bool open(std::string& error);
    /// Assigns id and time, keeps it in memory and appends it to disk. Disk errors are reported but the event is kept.
    Event append(Event event, std::string* error = nullptr);
    std::vector<Event> read(std::uint64_t after_id, std::size_t max) const;
    std::optional<Event> find(std::uint64_t id) const;
    std::uint64_t last_id() const;

private:
    void load_file(const std::string& path);

    std::string directory_;
    std::uint64_t max_file_bytes_;
    std::size_t memory_;
    mutable std::mutex mutex_;
    std::deque<Event> recent_;
    std::uint64_t next_id_ = 1;
};

}  // namespace portgate
