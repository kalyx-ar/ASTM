// Exclusive, renewable write permission for one port (spec/link-v1.md, rules 2-6).
// Pure logic: time is injected as monotonic milliseconds, so it can be tested deterministically.
#pragma once

#include <array>
#include <cstdint>
#include <deque>
#include <map>
#include <optional>
#include <random>
#include <string>
#include <vector>

namespace atsm {

using ConnectionId = std::uint64_t;
using Id128 = std::array<std::uint8_t, 16>;

struct ArbiterEvent {
    enum class Kind { Granted, Queued, Denied, Revoked };
    Kind kind{};
    ConnectionId connection = 0;
    std::uint64_t request_id = 0;
    std::uint32_t generation = 0;
    Id128 transfer_id{};
    std::uint32_t position = 0;
    std::string code;
};

class Arbiter {
public:
    /// `controllers` maps allowed controller ids to their configured priority (lower value wins).
    /// An empty map admits any controller with priority 0.
    Arbiter(std::uint32_t lease_ms, std::size_t max_queue, std::map<std::string, int> controllers, std::uint64_t seed);

    void acquire(ConnectionId connection, const std::string& controller_id, std::uint64_t request_id, std::uint64_t deadline_ms,
                 std::uint64_t now, std::vector<ArbiterEvent>& out);
    void renew(ConnectionId connection, std::uint64_t request_id, std::uint32_t generation, std::uint64_t now, std::vector<ArbiterEvent>& out);
    void release(ConnectionId connection, std::uint32_t generation, std::uint64_t now, std::vector<ArbiterEvent>& out);
    void disconnect(ConnectionId connection, std::uint64_t now, std::vector<ArbiterEvent>& out);
    /// Opens write admission for the current owner. Returns false for a stale generation or transfer id.
    bool confirm(ConnectionId connection, std::uint32_t generation, const Id128& transfer_id);
    /// Expires leases and queued requests whose deadline passed.
    void tick(std::uint64_t now, std::vector<ArbiterEvent>& out);

    bool may_write(ConnectionId connection, std::uint32_t generation) const;
    std::optional<ConnectionId> owner() const;
    std::uint32_t generation() const { return generation_; }
    std::uint32_t lease_ms() const { return lease_ms_; }
    std::size_t queued() const { return queue_.size(); }

private:
    struct Request {
        ConnectionId connection;
        std::uint64_t request_id;
        int priority;
        std::uint64_t order;
        std::uint64_t deadline;
    };

    struct Owner {
        ConnectionId connection;
        std::uint32_t generation;
        Id128 transfer_id;
        std::uint64_t expires;
        bool admitted;
    };

    void grant_next(std::uint64_t now, std::vector<ArbiterEvent>& out);
    void grant(const Request& request, std::uint64_t now, std::vector<ArbiterEvent>& out);
    Id128 random_id();

    std::uint32_t lease_ms_;
    std::size_t max_queue_;
    std::map<std::string, int> controllers_;
    std::mt19937_64 random_;
    std::optional<Owner> owner_;
    std::deque<Request> queue_;
    std::uint32_t generation_ = 0;
    std::uint64_t order_ = 0;
};

}  // namespace atsm
