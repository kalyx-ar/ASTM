// Exclusive, renewable write permission for one port (spec/link-v1.md, rules 2-6; spec/admin-v1.md § Semaphore).
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

namespace remoteusb {

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
    /// An empty map admits any controller with priority 0. `max_hold_ms` (0 = no limit) bounds a continuous tenure
    /// while other controllers wait.
    Arbiter(std::uint32_t lease_ms, std::size_t max_queue, std::map<std::string, int> controllers, std::uint64_t seed, std::uint32_t max_hold_ms = 0);

    void acquire(ConnectionId connection, const std::string& controller_id, std::uint64_t request_id, std::uint64_t deadline_ms,
                 std::uint64_t now, std::vector<ArbiterEvent>& out);
    void renew(ConnectionId connection, std::uint64_t request_id, std::uint32_t generation, std::uint64_t now, std::vector<ArbiterEvent>& out);
    void release(ConnectionId connection, std::uint32_t generation, std::uint64_t now, std::vector<ArbiterEvent>& out);
    void disconnect(ConnectionId connection, std::uint64_t now, std::vector<ArbiterEvent>& out);
    /// Opens write admission for the current owner. Returns false for a stale generation or transfer id.
    bool confirm(ConnectionId connection, std::uint32_t generation, const Id128& transfer_id);
    /// Expires leases, tenures over `max_hold_ms` with waiters, queued requests past their deadline and quarantines.
    void tick(std::uint64_t now, std::vector<ArbiterEvent>& out);

    /// Ends the current tenure with `code`. With `quarantine_ms` > 0 nobody is granted until it passes (kill switch);
    /// otherwise the next queued request is granted. Returns false when there was no owner.
    bool revoke(const std::string& code, std::uint64_t quarantine_ms, std::uint64_t now, std::vector<ArbiterEvent>& out);
    /// Secure close: revokes the owner, denies every queued request and refuses new ones until reopen().
    void close(const std::string& code, std::vector<ArbiterEvent>& out);
    void reopen(std::uint64_t now, std::vector<ArbiterEvent>& out);

    bool may_write(ConnectionId connection, std::uint32_t generation) const;
    std::optional<ConnectionId> owner() const;
    bool is_queued(ConnectionId connection) const;
    std::uint32_t generation() const { return generation_; }
    std::uint32_t lease_ms() const { return lease_ms_; }
    std::size_t queued() const { return queue_.size(); }
    bool closed() const { return closed_; }
    std::uint64_t quarantine_until() const { return quarantine_until_; }

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
        std::uint64_t since;
    };

    void grant_next(std::uint64_t now, std::vector<ArbiterEvent>& out);
    void grant(const Request& request, std::uint64_t now, std::vector<ArbiterEvent>& out);
    void end_tenure(const std::string& code, std::uint64_t now, std::vector<ArbiterEvent>& out);
    Id128 random_id();

    std::uint32_t lease_ms_;
    std::size_t max_queue_;
    std::map<std::string, int> controllers_;
    std::mt19937_64 random_;
    std::uint32_t max_hold_ms_;
    std::optional<Owner> owner_;
    std::deque<Request> queue_;
    std::uint32_t generation_ = 0;
    std::uint64_t order_ = 0;
    std::uint64_t quarantine_until_ = 0;
    bool closed_ = false;
};

}  // namespace remoteusb
