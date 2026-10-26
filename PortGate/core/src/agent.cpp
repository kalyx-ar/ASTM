#include "portgate/agent.hpp"

#include <chrono>
#include <random>

namespace portgate {

namespace {

using cbor::Value;

constexpr int kPollMs = 100;
constexpr std::uint64_t kReopenDelayMs = 2000;

std::uint64_t now_ms() { return platform::monotonic_ms(); }

Value id_value(const Id128& id) { return Value::bytes(cbor::Bytes(id.begin(), id.end())); }

std::uint64_t uint_or(const Value& map, std::uint64_t key, std::uint64_t fallback) {
    const Value* v = map.get(key);
    return v && v->as_uint() ? *v->as_uint() : fallback;
}

std::string text_or(const Value& map, std::uint64_t key) {
    const Value* v = map.get(key);
    const auto* t = v ? v->as_text() : nullptr;
    return t ? *t : std::string();
}

bool bool_or(const Value& map, std::uint64_t key) {
    const Value* v = map.get(key);
    return v && v->as_bool() && *v->as_bool();
}

}  // namespace

Agent::Agent(AgentConfig config, Logger logger, TlsMaterial tls, EventSink sink)
    : config_(std::move(config)), logger_(std::move(logger)), tls_material_(std::move(tls)), sink_(std::move(sink)) {
    std::mt19937_64 random(platform::random_seed());
    for (std::size_t i = 0; i < epoch_.size(); i += 8) {
        const auto v = random();
        for (std::size_t j = 0; j < 8; ++j) epoch_[i + j] = static_cast<std::uint8_t>(v >> (j * 8));
    }
    std::uint16_t index = 1;
    for (const auto& port_config : config_.ports) {
        auto port = std::make_unique<Port>();
        port->index = index++;
        port->config = port_config;
        port->device = platform::make_device(port_config.device);
        port->arbiter = std::make_unique<Arbiter>(port_config.lease_ms, port_config.max_queued_requests, port_config.controllers, random(),
                                                  port_config.max_hold_ms);
        ports_.push_back(std::move(port));
    }
}

Agent::~Agent() { shutdown(); }

bool Agent::start(std::string& error) {
    if (config_.tls == TlsMode::Tls) {
        // Never fall back to plain text when TLS was requested.
        tls_ = TlsServer::create(config_.tls_settings, tls_material_, error);
        if (!tls_) return false;
    }
    if (!listener_.open(config_.listen_host, config_.listen_port, error)) return false;
    log(LogLevel::Info, "listening on " + config_.listen_host + ":" + std::to_string(listener_.port()) +
                            (tls_ ? (config_.tls_settings.require_client_certificate ? " (TLS, client certificates required)" : " (TLS)")
                                  : " (plain TCP: use only on trusted networks)"));
    for (auto& port : ports_) {
        Port* p = port.get();
        p->reader = std::thread([this, p] { port_loop(*p); });
    }
    accept_thread_ = std::thread([this] { accept_loop(); });
    return true;
}

void Agent::run(const std::atomic<bool>& stop) {
    std::uint64_t last_heartbeat = now_ms();
    while (!stop && !stopping_) {
        std::this_thread::sleep_for(std::chrono::milliseconds(kPollMs));
        const auto now = now_ms();
        std::vector<std::shared_ptr<Connection>> stale;
        {
            std::lock_guard<std::mutex> lock(mutex_);
            for (auto& port : ports_) {
                if (port->handover && (now >= port->handover->deadline || port->arbiter->owner() != std::optional<ConnectionId>(port->handover->owner)))
                    finish_handover(*port, false, now >= port->handover->deadline ? "PORTGATE-HANDOVER-NO-ANSWER" : "PORTGATE-HANDOVER-OWNER-CHANGED");
                std::vector<ArbiterEvent> events;
                port->arbiter->tick(now, events);
                dispatch(*port, events);
            }
            for (auto& [id, connection] : connections_) {
                (void)id;
                if (now - connection->last_rx_ms > 3ull * config_.heartbeat_ms) stale.push_back(connection);
            }
        }
        for (auto& connection : stale) drop(connection, "no traffic for three heartbeat intervals");
        if (now - last_heartbeat >= config_.heartbeat_ms) {
            last_heartbeat = now;
            std::vector<std::shared_ptr<Connection>> all;
            {
                std::lock_guard<std::mutex> lock(mutex_);
                for (auto& [id, connection] : connections_) {
                    (void)id;
                    all.push_back(connection);
                }
            }
            for (auto& connection : all) send(connection, link::control(link::Type::Heartbeat, 0, Value::map({{0, Value::uint(now)}})));
        }
    }
}

void Agent::shutdown() {
    if (stopping_.exchange(true)) return;
    listener_.close();
    if (accept_thread_.joinable()) accept_thread_.join();
    std::vector<std::shared_ptr<Connection>> all;
    {
        std::lock_guard<std::mutex> lock(mutex_);
        for (auto& [id, connection] : connections_) {
            (void)id;
            all.push_back(connection);
        }
    }
    for (auto& connection : all) {
        connection->alive = false;
        connection->transport->shutdown();
    }
    // Connection threads are detached; wait until every one has left its loop.
    while (active_connections_ > 0) std::this_thread::sleep_for(std::chrono::milliseconds(10));
    for (auto& port : ports_) {
        if (port->reader.joinable()) port->reader.join();
        port->device->close();
    }
}

std::vector<PortStatus> Agent::status() {
    std::lock_guard<std::mutex> lock(mutex_);
    const auto now = now_ms();
    std::vector<PortStatus> out;
    for (auto& port : ports_) {
        PortStatus s;
        s.port_id = port->config.port_id;
        s.instrument_id = port->config.instrument_id;
        s.device = port->device->describe();
        s.open = port->open;
        if (auto owner = port->arbiter->owner()) s.owner = controller_of(*owner);
        s.generation = port->arbiter->generation();
        s.queued = port->arbiter->queued();
        s.closed = port->arbiter->closed();
        s.quarantine_ms = port->arbiter->quarantine_until() > now ? port->arbiter->quarantine_until() - now : 0;
        out.push_back(std::move(s));
    }
    return out;
}

bool Agent::revoke_port(const std::string& port_id, const std::string& actor, std::string& error) {
    std::lock_guard<std::mutex> lock(mutex_);
    Port* port = port_by_id(port_id);
    if (!port) {
        error = "unknown port " + port_id;
        return false;
    }
    const auto owner = port->arbiter->owner();
    const std::string target = owner ? controller_of(*owner) : std::string();
    const auto generation = port->arbiter->generation();
    std::vector<ArbiterEvent> events;
    if (!port->arbiter->revoke("PORTGATE-ADMIN-REVOKED", 0, now_ms(), events)) {
        error = "port " + port_id + " has no owner";
        return false;
    }
    Event event;
    event.kind = "admin-revoke";
    event.port_id = port_id;
    event.actor = actor;
    event.target = target;
    event.generation = generation;
    broadcast(*port, emit(event));
    dispatch(*port, events);
    return true;
}

bool Agent::reopen_port(const std::string& port_id, const std::string& actor, std::string& error) {
    std::lock_guard<std::mutex> lock(mutex_);
    Port* port = port_by_id(port_id);
    if (!port) {
        error = "unknown port " + port_id;
        return false;
    }
    std::vector<ArbiterEvent> events;
    port->arbiter->reopen(now_ms(), events);
    Event event;
    event.kind = "reopen";
    event.port_id = port_id;
    event.actor = actor;
    broadcast(*port, emit(event));
    dispatch(*port, events);
    return true;
}

bool Agent::unblock(const std::string& controller_id, const std::string& actor) {
    std::lock_guard<std::mutex> lock(mutex_);
    if (blocked_.erase(controller_id) == 0) return false;
    Event event;
    event.kind = "unblock";
    event.actor = actor;
    event.target = controller_id;
    emit(event);
    return true;
}

void Agent::accept_loop() {
    while (!stopping_) {
        auto socket = listener_.accept(kPollMs * 2);
        if (!socket.valid()) continue;
        // The TLS handshake runs on the connection thread so a slow client never blocks new connections.
        ++active_connections_;
        std::thread([this](platform::Socket accepted) {
            serve(std::move(accepted));
            --active_connections_;
        }, std::move(socket)).detach();
    }
}

void Agent::serve(platform::Socket socket) {
    const std::string peer = socket.peer();
    auto connection = std::make_shared<Connection>();
    connection->peer = peer;
    if (tls_) {
        std::string error;
        connection->transport = tls_->accept(std::move(socket), 10000, error);
        if (!connection->transport) {
            log(LogLevel::Warning, "TLS connection from " + peer + " rejected: " + error);
            return;
        }
    } else {
        connection->transport = std::make_unique<PlainTransport>(std::move(socket));
    }
    connection->last_rx_ms = now_ms();
    {
        std::lock_guard<std::mutex> lock(mutex_);
        connection->id = next_connection_++;
        connections_[connection->id] = connection;
    }
    log(LogLevel::Info, "connection " + std::to_string(connection->id) + " from " + peer +
                            (connection->transport->authenticated() ? " certificate CN=" + connection->transport->peer_common_name() : std::string()));
    send_hello(connection);
    connection_loop(connection);
}

bool Agent::authorize(const std::shared_ptr<Connection>& connection, const std::string& controller_id, std::string& reason) const {
    if (!tls_) return true;  // plain mode: the id is a declaration, see spec/link-v1.md § Security
    const auto& transport = *connection->transport;
    if (transport.psk_identity().size() > 0) {
        if (transport.psk_identity() == controller_id) return true;
        reason = "PSK identity " + transport.psk_identity() + " does not identify controller " + controller_id;
        return false;
    }
    if (!transport.authenticated()) {
        if (!config_.tls_settings.require_client_certificate) return true;
        reason = "no verified client certificate";
        return false;
    }
    if (transport.peer_common_name() == controller_id) return true;
    for (const auto& port : config_.ports) {
        auto it = port.controller_certificates.find(controller_id);
        if (it != port.controller_certificates.end() && it->second == transport.peer_certificate_sha256()) return true;
    }
    reason = "certificate CN=" + transport.peer_common_name() + " does not identify controller " + controller_id;
    return false;
}

bool Agent::strongly_authenticated(const Connection& connection) const {
    // Kill switch and contest need TLS that identifies the peer: a verified client certificate or a PSK.
    return tls_ && (connection.transport->authenticated() || !connection.transport->psk_identity().empty());
}

void Agent::connection_loop(std::shared_ptr<Connection> connection) {
    link::Decoder decoder(config_.max_unit_bytes);
    std::vector<std::uint8_t> buffer(16 * 1024);
    std::vector<link::Unit> units;
    while (connection->alive && !stopping_) {
        const int n = connection->transport->receive(buffer.data(), buffer.size(), kPollMs * 2);
        if (n == -2) continue;
        if (n <= 0) break;
        connection->last_rx_ms = now_ms();
        units.clear();
        if (decoder.feed(buffer.data(), static_cast<std::size_t>(n), units) != link::DecodeError::None) {
            send_error(connection, "PORTGATE-BAD-UNIT", 0, "invalid header, version or length");
            break;
        }
        for (const auto& unit : units) {
            handle(connection, unit);
            if (!connection->alive) break;
        }
    }
    drop(connection, "connection closed");
}

void Agent::handle(const std::shared_ptr<Connection>& connection, const link::Unit& unit) {
    if (unit.type == link::Type::SerialData) {
        Port* port = port_for(unit.channel);
        if (!connection->hello || !port) {
            send_error(connection, "PORTGATE-BAD-CHANNEL", 0, "SerialData before Hello or for an unknown port");
            return;
        }
        handle_serial_data(connection, *port, unit);
        return;
    }

    auto map = link::parse_map(unit);
    if (!map) {
        const bool known = unit.type == link::Type::Hello || unit.type == link::Type::Acquire || unit.type == link::Type::Renew ||
                           unit.type == link::Type::Release || unit.type == link::Type::ConfirmTransfer || unit.type == link::Type::RequestHandover ||
                           unit.type == link::Type::HandoverAnswer || unit.type == link::Type::KillSwitch || unit.type == link::Type::Contest ||
                           unit.type == link::Type::Heartbeat || unit.type == link::Type::Error;
        if (known || (unit.flags & link::kFlagCritical) != 0) {
            send_error(connection, "PORTGATE-BAD-PAYLOAD", 0, "unit " + std::to_string(static_cast<int>(unit.type)) + " without a valid CBOR map");
            if ((unit.flags & link::kFlagCritical) != 0) connection->alive = false;
        }
        return;
    }

    if (unit.type == link::Type::Hello) {
        const Value* id = map->get(2);
        const std::string* text = id ? id->as_text() : nullptr;
        if (!text || text->empty() || uint_or(*map, 0, 0) != link::kVersion) {
            send_error(connection, "PORTGATE-BAD-HELLO", 0, "Hello needs version 1 and a controller id");
            connection->alive = false;
            return;
        }
        std::string reason;
        if (!authorize(connection, *text, reason)) {
            log(LogLevel::Warning, "connection " + std::to_string(connection->id) + " refused as " + *text + ": " + reason);
            send_error(connection, "PORTGATE-IDENTITY-MISMATCH", 0, reason);
            connection->alive = false;
            return;
        }
        std::lock_guard<std::mutex> lock(mutex_);
        auto blocked = blocked_.find(*text);
        if (blocked != blocked_.end() && now_ms() < blocked->second) {
            send_error(connection, "PORTGATE-CONTROLLER-BLOCKED", 0, "blocked after a contested kill switch");
            connection->alive = false;
            return;
        }
        connection->controller_id = *text;
        connection->hello = true;
        log(LogLevel::Info, "connection " + std::to_string(connection->id) + " identifies as " + *text + (tls_ ? " (verified)" : " (unauthenticated in plain mode)"));
        return;
    }
    if (unit.type == link::Type::Heartbeat || unit.type == link::Type::Error) return;
    if (!connection->hello) {
        send_error(connection, "PORTGATE-HELLO-REQUIRED", 0, "send Hello first");
        connection->alive = false;
        return;
    }

    Port* port = port_for(unit.channel);
    const std::uint64_t request_id = uint_or(*map, 0, 0);
    if (!port) {
        send_error(connection, "PORTGATE-BAD-CHANNEL", request_id, "unknown port index " + std::to_string(unit.channel));
        return;
    }

    std::vector<std::shared_ptr<Connection>> to_drop;
    {
        std::lock_guard<std::mutex> lock(mutex_);
        std::vector<ArbiterEvent> events;
        const auto now = now_ms();
        switch (unit.type) {
            case link::Type::Acquire:
                port->arbiter->acquire(connection->id, connection->controller_id, request_id, uint_or(*map, 2, 0), now, events);
                break;
            case link::Type::Renew:
                port->arbiter->renew(connection->id, request_id, static_cast<std::uint32_t>(uint_or(*map, 1, 0)), now, events);
                break;
            case link::Type::Release:
                port->arbiter->release(connection->id, static_cast<std::uint32_t>(uint_or(*map, 1, 0)), now, events);
                break;
            case link::Type::ConfirmTransfer: {
                Id128 transfer{};
                const Value* t = map->get(2);
                const auto* bytes = t ? t->as_bytes() : nullptr;
                if (bytes && bytes->size() == transfer.size()) std::copy(bytes->begin(), bytes->end(), transfer.begin());
                if (!bytes || bytes->size() != transfer.size() ||
                    !port->arbiter->confirm(connection->id, static_cast<std::uint32_t>(uint_or(*map, 1, 0)), transfer)) {
                    send_error(connection, "PORTGATE-STALE-TRANSFER", request_id, "generation or transferId is not current", port->index);
                }
                break;
            }
            case link::Type::RequestHandover:
                request_handover(connection, *port, *map, now);
                break;
            case link::Type::HandoverAnswer:
                answer_handover(connection, *port, *map, now);
                break;
            case link::Type::KillSwitch:
                kill_switch(connection, *port, *map, now);
                break;
            case link::Type::Contest:
                contest(connection, *port, *map, now, to_drop);
                break;
            default:
                if ((unit.flags & link::kFlagCritical) != 0) connection->alive = false;
                break;
        }
        dispatch(*port, events);
    }
    for (auto& victim : to_drop) drop(victim, "kill switch contested");
}

void Agent::request_handover(const std::shared_ptr<Connection>& connection, Port& port, const Value& map, std::uint64_t now) {
    const auto request_id = uint_or(map, 0, 0);
    auto result = [&](bool yielded, const std::string& code) {
        std::map<std::uint64_t, Value> fields{{0, Value::uint(request_id)}, {1, Value::boolean(yielded)}};
        if (!code.empty()) fields.emplace(2, Value::text(code));
        send(connection, link::control(link::Type::HandoverResult, port.index, Value::map(std::move(fields))));
    };
    const auto owner = port.arbiter->owner();
    if (!owner || owner == std::optional<ConnectionId>(connection->id)) return result(false, "PORTGATE-HANDOVER-NO-OWNER");
    if (!port.arbiter->is_queued(connection->id)) return result(false, "PORTGATE-HANDOVER-NOT-QUEUED");
    if (port.handover) return result(false, "PORTGATE-HANDOVER-PENDING");
    auto last = port.last_handover.find(connection->controller_id);
    if (last != port.last_handover.end() && now - last->second < config_.security.handover_interval_ms) return result(false, "PORTGATE-HANDOVER-RATE");
    port.last_handover[connection->controller_id] = now;

    Handover handover;
    handover.id = next_handover_++;
    handover.requester = connection->id;
    handover.requester_id = connection->controller_id;
    handover.request_id = request_id;
    handover.owner = *owner;
    handover.deadline = now + config_.security.handover_timeout_ms;
    port.handover = handover;
    const std::string reason = text_or(map, 1);
    if (auto target = connection_by_id(*owner))
        send(target, link::control(link::Type::HandoverAsked, port.index,
                                   Value::map({{0, Value::uint(handover.id)},
                                               {1, Value::text(connection->controller_id)},
                                               {2, Value::text(reason)},
                                               {3, Value::uint(config_.security.handover_timeout_ms)}})));
    Event event;
    event.kind = "handover-requested";
    event.port_id = port.config.port_id;
    event.actor = connection->controller_id;
    event.target = controller_of(*owner);
    event.generation = port.arbiter->generation();
    event.reason = reason;
    broadcast(port, emit(event));
}

void Agent::answer_handover(const std::shared_ptr<Connection>& connection, Port& port, const Value& map, std::uint64_t now) {
    if (!port.handover || port.handover->id != uint_or(map, 0, 0) || port.handover->owner != connection->id) {
        send_error(connection, "PORTGATE-HANDOVER-UNKNOWN", 0, "no pending handover with that id for this owner", port.index);
        return;
    }
    const bool yielded = bool_or(map, 1);
    if (yielded) {
        std::vector<ArbiterEvent> events;
        port.arbiter->revoke("PORTGATE-HANDOVER", 0, now, events);
        finish_handover(port, true, {});
        dispatch(port, events);
    } else {
        finish_handover(port, false, "PORTGATE-HANDOVER-KEPT");
    }
}

void Agent::finish_handover(Port& port, bool yielded, const std::string& code) {
    const Handover handover = *port.handover;
    port.handover.reset();
    if (auto requester = connection_by_id(handover.requester)) {
        std::map<std::uint64_t, Value> fields{{0, Value::uint(handover.request_id)}, {1, Value::boolean(yielded)}};
        if (!code.empty()) fields.emplace(2, Value::text(code));
        send(requester, link::control(link::Type::HandoverResult, port.index, Value::map(std::move(fields))));
    }
    Event event;
    event.kind = yielded ? "handover-yielded" : "handover-kept";
    event.port_id = port.config.port_id;
    event.actor = controller_of(handover.owner);
    event.target = handover.requester_id;
    event.reason = code;
    broadcast(port, emit(event));
}

bool Agent::has_right(const Port& port, const std::string& controller_id, std::uint32_t right) const {
    auto it = port.config.controller_rights.find(controller_id);
    return it != port.config.controller_rights.end() && (it->second & right) != 0;
}

void Agent::kill_switch(const std::shared_ptr<Connection>& connection, Port& port, const Value& map, std::uint64_t now) {
    const auto request_id = uint_or(map, 0, 0);
    auto deny = [&](const std::string& code, const std::string& detail) {
        send_error(connection, code, request_id, detail, port.index);
        Event event;
        event.kind = "kill-switch-denied";
        event.port_id = port.config.port_id;
        event.actor = connection->controller_id;
        event.reason = code;
        event.detail = connection->peer;
        emit(event);
    };
    if (!config_.security.kill_switch) return deny("PORTGATE-KILL-DISABLED", "the kill switch is not enabled on this agent");
    if (!strongly_authenticated(*connection)) return deny("PORTGATE-KILL-NEEDS-TLS", "the kill switch requires TLS with an authenticated peer");
    if (!has_right(port, connection->controller_id, RightKillSwitch)) return deny("PORTGATE-KILL-NOT-ALLOWED", "no kill-switch right on this port");
    auto last = last_kill_.find(connection->controller_id);
    if (last != last_kill_.end() && now - last->second < config_.security.kill_cooldown_ms) return deny("PORTGATE-KILL-COOLDOWN", "wait before another kill switch");
    const auto owner = port.arbiter->owner();
    if (!owner) return deny("PORTGATE-KILL-NO-OWNER", "the port has no owner");

    last_kill_[connection->controller_id] = now;
    const bool writing = !port.write_mutex.try_lock();
    if (!writing) port.write_mutex.unlock();
    Event event;
    event.kind = "kill-switch";
    event.port_id = port.config.port_id;
    event.actor = connection->controller_id;
    event.target = controller_of(*owner);
    event.generation = port.arbiter->generation();
    event.reason = text_or(map, 1);
    event.uncertain = writing || port.last_write_unknown;
    event.until_ms = platform::utc_ms() + config_.security.quarantine_ms;
    event.detail = connection->peer;
    std::vector<ArbiterEvent> events;
    port.arbiter->revoke("PORTGATE-KILLED", config_.security.quarantine_ms, now, events);
    if (port.handover) finish_handover(port, false, "PORTGATE-HANDOVER-OWNER-CHANGED");
    const Event logged = emit(event);
    kills_[logged.id] = KillRecord{connection->controller_id, port.index, logged.until_ms, false};
    log(LogLevel::Warning, "port " + port.config.port_id + ": kill switch by " + connection->controller_id + " (" + connection->peer + ") on " + logged.target);
    dispatch(port, events);
    broadcast(port, logged);
    send(connection, link::control(link::Type::SecurityResult, port.index, Value::map({{0, Value::uint(request_id)}, {1, Value::uint(logged.id)}})));
}

void Agent::contest(const std::shared_ptr<Connection>& connection, Port& port, const Value& map, std::uint64_t now,
                    std::vector<std::shared_ptr<Connection>>& to_drop) {
    const auto request_id = uint_or(map, 0, 0);
    auto deny = [&](const std::string& code, const std::string& detail) { send_error(connection, code, request_id, detail, port.index); };
    if (!config_.security.contest) return deny("PORTGATE-CONTEST-DISABLED", "contest is not enabled on this agent");
    if (!strongly_authenticated(*connection)) return deny("PORTGATE-CONTEST-NEEDS-TLS", "contest requires TLS with an authenticated peer");
    if (!has_right(port, connection->controller_id, RightContest)) return deny("PORTGATE-CONTEST-NOT-ALLOWED", "no contest right on this port");
    auto record = kills_.find(uint_or(map, 1, 0));
    if (record == kills_.end() || record->second.port != port.index) return deny("PORTGATE-CONTEST-UNKNOWN", "no kill switch with that event id on this port");
    if (record->second.contested) return deny("PORTGATE-CONTEST-DONE", "that kill switch was already contested");
    if (platform::utc_ms() > record->second.contest_until_utc) return deny("PORTGATE-CONTEST-LATE", "the contest window is over");
    if (record->second.controller == connection->controller_id) return deny("PORTGATE-CONTEST-SELF", "a controller cannot contest its own kill switch");

    record->second.contested = true;
    const std::string killer = record->second.controller;
    blocked_[killer] = now + config_.security.contest_block_ms;
    for (auto& [id, other] : connections_) {
        (void)id;
        if (other->controller_id == killer) to_drop.push_back(other);
    }
    // Dropping the killer's connections also removes its queued requests (drop() runs after this lock is released).
    Event event;
    event.kind = "contest";
    event.port_id = port.config.port_id;
    event.actor = connection->controller_id;
    event.target = killer;
    event.reason = text_or(map, 2);
    event.until_ms = platform::utc_ms() + config_.security.contest_block_ms;
    event.detail = "kill-switch event " + std::to_string(record->first);
    log(LogLevel::Warning, "port " + port.config.port_id + ": kill switch of " + killer + " contested by " + connection->controller_id);
    const Event contested = emit(event);
    broadcast(port, contested);
    send(connection, link::control(link::Type::SecurityResult, port.index, Value::map({{0, Value::uint(request_id)}, {1, Value::uint(contested.id)}})));
    if (bool_or(map, 3)) {
        std::vector<ArbiterEvent> events;
        port.arbiter->close("PORTGATE-SECURE-CLOSED", events);
        if (port.handover) finish_handover(port, false, "PORTGATE-SECURE-CLOSED");
        Event closed;
        closed.kind = "secure-close";
        closed.port_id = port.config.port_id;
        closed.actor = connection->controller_id;
        closed.reason = event.reason;
        broadcast(port, emit(closed));
        dispatch(port, events);
    }
}

Event Agent::emit(Event event) {
    if (sink_) return sink_(std::move(event));
    event.id = next_local_event_++;
    event.time_ms = platform::utc_ms();
    return event;
}

void Agent::broadcast(Port& port, const Event& event) {
    std::map<std::uint64_t, Value> fields{{0, Value::uint(event.id)}, {1, Value::text(event.kind)}, {2, Value::text(event.port_id)},
                                          {9, Value::uint(event.time_ms)}};
    if (!event.actor.empty()) fields.emplace(3, Value::text(event.actor));
    if (!event.target.empty()) fields.emplace(4, Value::text(event.target));
    if (event.generation != 0) fields.emplace(5, Value::uint(event.generation));
    if (!event.reason.empty()) fields.emplace(6, Value::text(event.reason));
    if (event.until_ms != 0) fields.emplace(7, Value::uint(event.until_ms));
    if (event.uncertain) fields.emplace(8, Value::boolean(true));
    const auto unit = link::control(link::Type::SecurityEvent, port.index, Value::map(std::move(fields)));
    for (auto& [id, connection] : connections_) {
        (void)id;
        if (!connection->hello) continue;
        if (!port.config.controllers.empty() && port.config.controllers.count(connection->controller_id) == 0) continue;
        send(connection, unit);
    }
}

void Agent::handle_serial_data(const std::shared_ptr<Connection>& connection, Port& port, const link::Unit& unit) {
    auto data = link::parse_serial_data(unit);
    if (!data) {
        send_error(connection, "PORTGATE-BAD-SERIAL-DATA", 0, "SerialData shorter than its header");
        return;
    }
    auto status = [&](std::uint64_t written, link::WriteResult result, const std::string& code) {
        std::map<std::uint64_t, Value> fields{{0, Value::uint(data->seq)},
                                              {1, Value::uint(result == link::WriteResult::Rejected ? 0 : data->data.size())},
                                              {2, Value::uint(written)},
                                              {3, Value::uint(static_cast<std::uint64_t>(result))}};
        if (!code.empty()) fields.emplace(4, Value::text(code));
        send(connection, link::control(link::Type::WriteStatus, port.index, Value::map(std::move(fields))));
    };

    {
        std::lock_guard<std::mutex> lock(mutex_);
        if (!port.arbiter->may_write(connection->id, data->generation)) {
            status(0, link::WriteResult::Rejected, "PORTGATE-NOT-ADMITTED");
            return;
        }
    }
    if (data->data.size() > port.config.max_write_bytes) {
        status(0, link::WriteResult::Rejected, "PORTGATE-WRITE-TOO-LARGE");
        return;
    }
    if (!port.open) {
        status(0, link::WriteResult::Rejected, "PORTGATE-PORT-CLOSED");
        return;
    }
    int written;
    {
        std::lock_guard<std::mutex> write_lock(port.write_mutex);
        written = port.device->write(data->data.data(), data->data.size());
        port.last_write_unknown = written < 0;
    }
    if (written < 0) status(0, link::WriteResult::Unknown, "PORTGATE-WRITE-FAILED");
    else if (static_cast<std::size_t>(written) < data->data.size()) status(static_cast<std::uint64_t>(written), link::WriteResult::Partial, "PORTGATE-WRITE-PARTIAL");
    else status(static_cast<std::uint64_t>(written), link::WriteResult::Completed, {});
}

void Agent::port_loop(Port& port) {
    std::vector<std::uint8_t> buffer(4096);
    std::uint64_t retry_at = 0;
    while (!stopping_) {
        if (!port.open) {
            if (now_ms() < retry_at) {
                std::this_thread::sleep_for(std::chrono::milliseconds(kPollMs));
                continue;
            }
            std::string error;
            if (port.device->open(error)) {
                port.open = true;
                log(LogLevel::Info, "port " + port.config.port_id + " opened: " + port.device->describe());
            } else {
                log(LogLevel::Warning, "port " + port.config.port_id + ": " + error);
                retry_at = now_ms() + kReopenDelayMs;
                continue;
            }
        }
        const int n = port.device->read(buffer.data(), buffer.size(), kPollMs);
        if (n == 0) continue;
        if (n < 0) {
            log(LogLevel::Warning, "port " + port.config.port_id + " read failed; reopening");
            port.device->close();
            port.open = false;
            retry_at = now_ms() + kReopenDelayMs;
            std::lock_guard<std::mutex> lock(mutex_);
            if (auto owner = port.arbiter->owner()) {
                if (auto connection = connection_by_id(*owner))
                    send(connection, link::control(link::Type::PortEvent, port.index, Value::map({{0, Value::uint(2)}, {1, Value::text("PORTGATE-PORT-FAULTED")}, {2, Value::text(port.device->describe())}})));
            }
            continue;
        }

        std::shared_ptr<Connection> target;
        link::SerialData data;
        {
            std::lock_guard<std::mutex> lock(mutex_);
            auto owner = port.arbiter->owner();
            if (owner && port.arbiter->may_write(*owner, port.arbiter->generation())) target = connection_by_id(*owner);
            if (!target) {
                port.unowned_rx += static_cast<std::uint64_t>(n);
                continue;
            }
            data.seq = port.rx_seq++;
            data.offset = port.rx_offset;
            data.generation = port.arbiter->generation();
            port.rx_offset += static_cast<std::uint64_t>(n);
            data.data.assign(buffer.begin(), buffer.begin() + n);
        }
        send(target, link::serial_data(port.index, data));
    }
}

void Agent::dispatch(Port& port, const std::vector<ArbiterEvent>& events) {
    for (const auto& event : events) {
        auto connection = connection_by_id(event.connection);
        if (!connection) continue;
        switch (event.kind) {
            case ArbiterEvent::Kind::Granted: {
                // A write of the previous owner still holding the port means its outcome is uncertain.
                const bool writing = !port.write_mutex.try_lock();
                if (!writing) port.write_mutex.unlock();
                send(connection, link::control(link::Type::Granted, port.index,
                                               Value::map({{0, Value::uint(event.request_id)},
                                                           {1, Value::uint(event.generation)},
                                                           {2, id_value(event.transfer_id)},
                                                           {3, Value::uint(port.arbiter->lease_ms())},
                                                           {4, Value::uint(0)},
                                                           {5, Value::boolean(writing || port.last_write_unknown)},
                                                           {6, Value::uint(port.unowned_rx)}})));
                port.unowned_rx = 0;
                // Renewals answer with Granted for the same generation; only a new generation is an event.
                if (event.generation != port.logged_generation) {
                    port.logged_generation = event.generation;
                    log(LogLevel::Info, "port " + port.config.port_id + " generation " + std::to_string(event.generation) + " granted to " + connection->controller_id);
                    Event logged;
                    logged.kind = "granted";
                    logged.port_id = port.config.port_id;
                    logged.target = connection->controller_id;
                    logged.generation = event.generation;
                    emit(logged);
                }
                break;
            }
            case ArbiterEvent::Kind::Queued:
                send(connection, link::control(link::Type::Queued, port.index, Value::map({{0, Value::uint(event.request_id)}, {1, Value::uint(event.position)}})));
                break;
            case ArbiterEvent::Kind::Denied:
                send(connection, link::control(link::Type::Denied, port.index, Value::map({{0, Value::uint(event.request_id)}, {1, Value::text(event.code)}})));
                break;
            case ArbiterEvent::Kind::Revoked: {
                send(connection, link::control(link::Type::Revoked, port.index, Value::map({{1, Value::uint(event.generation)}, {2, Value::text(event.code)}})));
                log(LogLevel::Info, "port " + port.config.port_id + " generation " + std::to_string(event.generation) + " revoked: " + event.code);
                Event logged;
                logged.kind = "revoked";
                logged.port_id = port.config.port_id;
                logged.target = connection->controller_id;
                logged.generation = event.generation;
                logged.reason = event.code;
                emit(logged);
                break;
            }
        }
    }
}

void Agent::drop(const std::shared_ptr<Connection>& connection, const std::string& reason) {
    {
        std::lock_guard<std::mutex> lock(mutex_);
        if (connections_.erase(connection->id) == 0) return;
        const auto now = now_ms();
        for (auto& port : ports_) {
            std::vector<ArbiterEvent> events;
            port->arbiter->disconnect(connection->id, now, events);
            dispatch(*port, events);
        }
    }
    connection->alive = false;
    connection->transport->shutdown();
    log(LogLevel::Info, "connection " + std::to_string(connection->id) + " dropped: " + reason);
}

void Agent::send(const std::shared_ptr<Connection>& connection, const link::Unit& unit) {
    const auto bytes = link::encode(unit);
    std::lock_guard<std::mutex> lock(connection->send_mutex);
    if (connection->alive && !connection->transport->send_all(bytes.data(), bytes.size())) connection->alive = false;
}

void Agent::send_error(const std::shared_ptr<Connection>& connection, const std::string& code, std::uint64_t request_id, const std::string& detail,
                       std::uint16_t channel) {
    send(connection, link::control(link::Type::Error, channel, Value::map({{0, Value::text(code)}, {1, Value::uint(request_id)}, {2, Value::text(detail)}})));
}

void Agent::send_hello(const std::shared_ptr<Connection>& connection) {
    send(connection, link::control(link::Type::Hello, 0,
                                   Value::map({{0, Value::uint(link::kVersion)},
                                               {1, Value::text("agent")},
                                               {2, Value::text(config_.agent_id)},
                                               {3, id_value(epoch_)},
                                               {4, Value::text(config_.platform_profile)},
                                               {5, Value::uint(config_.max_unit_bytes)}})));
    std::vector<Value> ports;
    for (const auto& port : ports_) {
        ports.push_back(Value::map({{0, Value::text(port->config.port_id)},
                                    {1, Value::text(port->config.instrument_id)},
                                    {2, Value::uint(2)},
                                    {3, Value::uint(0)},
                                    {4, Value::boolean(false)},
                                    {5, Value::uint(port->config.max_write_bytes)},
                                    {6, Value::boolean(port->config.max_queued_requests > 0)}}));
    }
    send(connection, link::control(link::Type::Capabilities, 0, Value::map({{0, Value::array(std::move(ports))}})));
}

Agent::Port* Agent::port_for(std::uint16_t channel) {
    if (channel == 0 || channel > ports_.size()) return nullptr;
    return ports_[channel - 1].get();
}

Agent::Port* Agent::port_by_id(const std::string& port_id) {
    for (auto& port : ports_)
        if (port->config.port_id == port_id) return port.get();
    return nullptr;
}

std::shared_ptr<Agent::Connection> Agent::connection_by_id(ConnectionId id) {
    auto it = connections_.find(id);
    return it == connections_.end() ? nullptr : it->second;
}

std::string Agent::controller_of(ConnectionId id) {
    auto connection = connection_by_id(id);
    return connection ? connection->controller_id : std::string();
}

}  // namespace portgate
