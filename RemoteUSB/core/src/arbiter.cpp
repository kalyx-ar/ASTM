#include "remoteusb/arbiter.hpp"

#include <algorithm>

namespace remoteusb {

Arbiter::Arbiter(std::uint32_t lease_ms, std::size_t max_queue, std::map<std::string, int> controllers, std::uint64_t seed, std::uint32_t max_hold_ms)
    : lease_ms_(lease_ms), max_queue_(max_queue), controllers_(std::move(controllers)), random_(seed), max_hold_ms_(max_hold_ms) {}

void Arbiter::acquire(ConnectionId connection, const std::string& controller_id, std::uint64_t request_id, std::uint64_t deadline_ms,
                      std::uint64_t now, std::vector<ArbiterEvent>& out) {
    int priority = 0;
    if (!controllers_.empty()) {
        auto it = controllers_.find(controller_id);
        if (it == controllers_.end()) {
            out.push_back({ArbiterEvent::Kind::Denied, connection, request_id, 0, {}, 0, "REMOTEUSB-CONTROLLER-NOT-AUTHORIZED"});
            return;
        }
        priority = it->second;
    }
    if (closed_) {
        out.push_back({ArbiterEvent::Kind::Denied, connection, request_id, 0, {}, 0, "REMOTEUSB-SECURE-CLOSED"});
        return;
    }
    if (owner_ && owner_->connection == connection) {
        // Re-acquiring an owned port behaves as a renewal of the current grant.
        renew(connection, request_id, owner_->generation, now, out);
        return;
    }
    const bool already_queued = std::any_of(queue_.begin(), queue_.end(), [&](const Request& r) { return r.connection == connection; });
    if (already_queued) {
        out.push_back({ArbiterEvent::Kind::Denied, connection, request_id, 0, {}, 0, "REMOTEUSB-ALREADY-QUEUED"});
        return;
    }
    Request request{connection, request_id, priority, order_++, deadline_ms == 0 ? UINT64_MAX : now + deadline_ms};
    if (!owner_ && now >= quarantine_until_) {
        grant(request, now, out);
        return;
    }
    if (queue_.size() >= max_queue_) {
        out.push_back({ArbiterEvent::Kind::Denied, connection, request_id, 0, {}, 0, "REMOTEUSB-QUEUE-FULL"});
        return;
    }
    // Priority, then FIFO. The priority never preempts the current owner.
    auto position = std::find_if(queue_.begin(), queue_.end(), [&](const Request& r) { return r.priority > priority; });
    position = queue_.insert(position, request);
    out.push_back({ArbiterEvent::Kind::Queued, connection, request_id, 0, {}, static_cast<std::uint32_t>(position - queue_.begin() + 1), {}});
}

void Arbiter::renew(ConnectionId connection, std::uint64_t request_id, std::uint32_t generation, std::uint64_t now, std::vector<ArbiterEvent>& out) {
    if (!owner_ || owner_->connection != connection || owner_->generation != generation) {
        out.push_back({ArbiterEvent::Kind::Denied, connection, request_id, generation, {}, 0, "REMOTEUSB-STALE-LEASE"});
        return;
    }
    if (max_hold_ms_ != 0 && !queue_.empty() && now - owner_->since >= max_hold_ms_) {
        end_tenure("REMOTEUSB-MAX-HOLD", now, out);
        return;
    }
    owner_->expires = now + lease_ms_;
    out.push_back({ArbiterEvent::Kind::Granted, connection, request_id, owner_->generation, owner_->transfer_id, 0, {}});
}

void Arbiter::release(ConnectionId connection, std::uint32_t generation, std::uint64_t now, std::vector<ArbiterEvent>& out) {
    if (!owner_ || owner_->connection != connection || owner_->generation != generation) return;
    end_tenure("REMOTEUSB-RELEASED", now, out);
}

void Arbiter::disconnect(ConnectionId connection, std::uint64_t now, std::vector<ArbiterEvent>& out) {
    queue_.erase(std::remove_if(queue_.begin(), queue_.end(), [&](const Request& r) { return r.connection == connection; }), queue_.end());
    if (owner_ && owner_->connection == connection) {
        owner_.reset();
        grant_next(now, out);
    }
}

bool Arbiter::confirm(ConnectionId connection, std::uint32_t generation, const Id128& transfer_id) {
    if (!owner_ || owner_->connection != connection || owner_->generation != generation || owner_->transfer_id != transfer_id) return false;
    owner_->admitted = true;
    return true;
}

void Arbiter::tick(std::uint64_t now, std::vector<ArbiterEvent>& out) {
    for (auto it = queue_.begin(); it != queue_.end();) {
        if (now >= it->deadline) {
            out.push_back({ArbiterEvent::Kind::Denied, it->connection, it->request_id, 0, {}, 0, "REMOTEUSB-QUEUE-TIMEOUT"});
            it = queue_.erase(it);
        } else {
            ++it;
        }
    }
    if (owner_ && now >= owner_->expires) {
        end_tenure("REMOTEUSB-LEASE-EXPIRED", now, out);
    } else if (owner_ && max_hold_ms_ != 0 && !queue_.empty() && now - owner_->since >= max_hold_ms_) {
        end_tenure("REMOTEUSB-MAX-HOLD", now, out);
    } else if (!owner_ && !closed_) {
        grant_next(now, out);  // a quarantine that just ended
    }
}

bool Arbiter::revoke(const std::string& code, std::uint64_t quarantine_ms, std::uint64_t now, std::vector<ArbiterEvent>& out) {
    if (!owner_) return false;
    if (quarantine_ms > 0) quarantine_until_ = now + quarantine_ms;
    end_tenure(code, now, out);
    return true;
}

void Arbiter::close(const std::string& code, std::vector<ArbiterEvent>& out) {
    closed_ = true;
    if (owner_) {
        out.push_back({ArbiterEvent::Kind::Revoked, owner_->connection, 0, owner_->generation, {}, 0, code});
        owner_.reset();
    }
    for (const auto& r : queue_) out.push_back({ArbiterEvent::Kind::Denied, r.connection, r.request_id, 0, {}, 0, code});
    queue_.clear();
}

void Arbiter::reopen(std::uint64_t now, std::vector<ArbiterEvent>& out) {
    closed_ = false;
    quarantine_until_ = 0;
    grant_next(now, out);
}

bool Arbiter::may_write(ConnectionId connection, std::uint32_t generation) const {
    return owner_ && owner_->connection == connection && owner_->generation == generation && owner_->admitted;
}

std::optional<ConnectionId> Arbiter::owner() const {
    return owner_ ? std::optional<ConnectionId>(owner_->connection) : std::nullopt;
}

bool Arbiter::is_queued(ConnectionId connection) const {
    return std::any_of(queue_.begin(), queue_.end(), [&](const Request& r) { return r.connection == connection; });
}

void Arbiter::end_tenure(const std::string& code, std::uint64_t now, std::vector<ArbiterEvent>& out) {
    out.push_back({ArbiterEvent::Kind::Revoked, owner_->connection, 0, owner_->generation, {}, 0, code});
    owner_.reset();
    grant_next(now, out);
}

void Arbiter::grant_next(std::uint64_t now, std::vector<ArbiterEvent>& out) {
    if (queue_.empty() || closed_ || now < quarantine_until_) return;
    const Request next = queue_.front();
    queue_.pop_front();
    grant(next, now, out);
}

void Arbiter::grant(const Request& request, std::uint64_t now, std::vector<ArbiterEvent>& out) {
    owner_ = Owner{request.connection, ++generation_, random_id(), now + lease_ms_, false, now};
    out.push_back({ArbiterEvent::Kind::Granted, request.connection, request.request_id, owner_->generation, owner_->transfer_id, 0, {}});
}

Id128 Arbiter::random_id() {
    Id128 id{};
    for (std::size_t i = 0; i < id.size(); i += 8) {
        const auto v = random_();
        for (std::size_t j = 0; j < 8; ++j) id[i + j] = static_cast<std::uint8_t>(v >> (j * 8));
    }
    return id;
}

}  // namespace remoteusb
