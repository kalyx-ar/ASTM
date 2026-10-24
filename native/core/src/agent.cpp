#include "atsm/agent.hpp"

#include <chrono>
#include <random>

namespace atsm {

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

}  // namespace

Agent::Agent(AgentConfig config, Logger logger) : config_(std::move(config)), logger_(std::move(logger)) {
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
        port->arbiter = std::make_unique<Arbiter>(port_config.lease_ms, port_config.max_queued_requests, port_config.controllers, random());
        ports_.push_back(std::move(port));
    }
}

Agent::~Agent() { shutdown(); }

bool Agent::start(std::string& error) {
    if (config_.tls == TlsMode::Tls) {
        // Never fall back to plain text when TLS was requested.
        error = "TLS endpoints require the TLS backend, which is not built in this version (see spec/link-v1.md)";
        return false;
    }
    if (!listener_.open(config_.listen_host, config_.listen_port, error)) return false;
    log(LogLevel::Info, "listening on " + config_.listen_host + ":" + std::to_string(listener_.port()) + " (plain TCP: use only on trusted networks)");
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
        connection->socket.shutdown();
    }
    // Connection threads are detached; wait until every one has left its loop.
    while (active_connections_ > 0) std::this_thread::sleep_for(std::chrono::milliseconds(10));
    for (auto& port : ports_) {
        if (port->reader.joinable()) port->reader.join();
        port->device->close();
    }
}

void Agent::accept_loop() {
    while (!stopping_) {
        auto socket = listener_.accept(kPollMs * 2);
        if (!socket.valid()) continue;
        auto connection = std::make_shared<Connection>();
        connection->socket = std::move(socket);
        connection->last_rx_ms = now_ms();
        {
            std::lock_guard<std::mutex> lock(mutex_);
            connection->id = next_connection_++;
            connections_[connection->id] = connection;
        }
        log(LogLevel::Info, "connection " + std::to_string(connection->id) + " from " + connection->socket.peer());
        send_hello(connection);
        ++active_connections_;
        std::thread([this, connection] {
            connection_loop(connection);
            --active_connections_;
        }).detach();
    }
}

void Agent::connection_loop(std::shared_ptr<Connection> connection) {
    link::Decoder decoder(config_.max_unit_bytes);
    std::vector<std::uint8_t> buffer(16 * 1024);
    std::vector<link::Unit> units;
    while (connection->alive && !stopping_) {
        const int n = connection->socket.receive(buffer.data(), buffer.size(), kPollMs * 2);
        if (n == -2) continue;
        if (n <= 0) break;
        connection->last_rx_ms = now_ms();
        units.clear();
        if (decoder.feed(buffer.data(), static_cast<std::size_t>(n), units) != link::DecodeError::None) {
            send_error(connection, "ATSM-AG-BAD-UNIT", 0, "invalid header, version or length");
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
            send_error(connection, "ATSM-AG-BAD-CHANNEL", 0, "SerialData before Hello or for an unknown port");
            return;
        }
        handle_serial_data(connection, *port, unit);
        return;
    }

    auto map = link::parse_map(unit);
    if (!map) {
        const bool known = unit.type == link::Type::Hello || unit.type == link::Type::Acquire || unit.type == link::Type::Renew ||
                           unit.type == link::Type::Release || unit.type == link::Type::ConfirmTransfer ||
                           unit.type == link::Type::Heartbeat || unit.type == link::Type::Error;
        if (known || (unit.flags & link::kFlagCritical) != 0) {
            send_error(connection, "ATSM-AG-BAD-PAYLOAD", 0, "unit " + std::to_string(static_cast<int>(unit.type)) + " without a valid CBOR map");
            if ((unit.flags & link::kFlagCritical) != 0) connection->alive = false;
        }
        return;
    }

    if (unit.type == link::Type::Hello) {
        const Value* id = map->get(2);
        const std::string* text = id ? id->as_text() : nullptr;
        if (!text || text->empty() || uint_or(*map, 0, 0) != link::kVersion) {
            send_error(connection, "ATSM-AG-BAD-HELLO", 0, "Hello needs version 1 and a controller id");
            connection->alive = false;
            return;
        }
        std::lock_guard<std::mutex> lock(mutex_);
        connection->controller_id = *text;
        connection->hello = true;
        log(LogLevel::Info, "connection " + std::to_string(connection->id) + " identifies as " + *text + " (unauthenticated in plain mode)");
        return;
    }
    if (unit.type == link::Type::Heartbeat || unit.type == link::Type::Error) return;
    if (!connection->hello) {
        send_error(connection, "ATSM-AG-HELLO-REQUIRED", 0, "send Hello first");
        connection->alive = false;
        return;
    }

    Port* port = port_for(unit.channel);
    const std::uint64_t request_id = uint_or(*map, 0, 0);
    if (!port) {
        send_error(connection, "ATSM-AG-BAD-CHANNEL", request_id, "unknown port index " + std::to_string(unit.channel));
        return;
    }

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
                send(connection, link::control(link::Type::Error, port->index,
                                               Value::map({{0, Value::text("ATSM-AG-STALE-TRANSFER")}, {1, Value::uint(request_id)}, {2, Value::text("generation or transferId is not current")}})));
            }
            break;
        }
        default:
            if ((unit.flags & link::kFlagCritical) != 0) connection->alive = false;
            break;
    }
    dispatch(*port, events);
}

void Agent::handle_serial_data(const std::shared_ptr<Connection>& connection, Port& port, const link::Unit& unit) {
    auto data = link::parse_serial_data(unit);
    if (!data) {
        send_error(connection, "ATSM-AG-BAD-SERIAL-DATA", 0, "SerialData shorter than its header");
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
            status(0, link::WriteResult::Rejected, "ATSM-AG-NOT-ADMITTED");
            return;
        }
    }
    if (data->data.size() > port.config.max_write_bytes) {
        status(0, link::WriteResult::Rejected, "ATSM-AG-WRITE-TOO-LARGE");
        return;
    }
    if (!port.open) {
        status(0, link::WriteResult::Rejected, "ATSM-AG-PORT-CLOSED");
        return;
    }
    int written;
    {
        std::lock_guard<std::mutex> write_lock(port.write_mutex);
        written = port.device->write(data->data.data(), data->data.size());
        port.last_write_unknown = written < 0;
    }
    if (written < 0) status(0, link::WriteResult::Unknown, "ATSM-AG-WRITE-FAILED");
    else if (static_cast<std::size_t>(written) < data->data.size()) status(static_cast<std::uint64_t>(written), link::WriteResult::Partial, "ATSM-AG-WRITE-PARTIAL");
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
                    send(connection, link::control(link::Type::PortEvent, port.index, Value::map({{0, Value::uint(2)}, {1, Value::text("ATSM-AG-PORT-FAULTED")}, {2, Value::text(port.device->describe())}})));
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
                log(LogLevel::Info, "port " + port.config.port_id + " generation " + std::to_string(event.generation) + " granted to " + connection->controller_id);
                break;
            }
            case ArbiterEvent::Kind::Queued:
                send(connection, link::control(link::Type::Queued, port.index, Value::map({{0, Value::uint(event.request_id)}, {1, Value::uint(event.position)}})));
                break;
            case ArbiterEvent::Kind::Denied:
                send(connection, link::control(link::Type::Denied, port.index, Value::map({{0, Value::uint(event.request_id)}, {1, Value::text(event.code)}})));
                break;
            case ArbiterEvent::Kind::Revoked:
                send(connection, link::control(link::Type::Revoked, port.index, Value::map({{1, Value::uint(event.generation)}, {2, Value::text(event.code)}})));
                log(LogLevel::Info, "port " + port.config.port_id + " generation " + std::to_string(event.generation) + " revoked: " + event.code);
                break;
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
    connection->socket.shutdown();
    log(LogLevel::Info, "connection " + std::to_string(connection->id) + " dropped: " + reason);
}

void Agent::send(const std::shared_ptr<Connection>& connection, const link::Unit& unit) {
    const auto bytes = link::encode(unit);
    std::lock_guard<std::mutex> lock(connection->send_mutex);
    if (connection->alive && !connection->socket.send_all(bytes.data(), bytes.size())) connection->alive = false;
}

void Agent::send_error(const std::shared_ptr<Connection>& connection, const std::string& code, std::uint64_t request_id, const std::string& detail) {
    send(connection, link::control(link::Type::Error, 0, Value::map({{0, Value::text(code)}, {1, Value::uint(request_id)}, {2, Value::text(detail)}})));
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

std::shared_ptr<Agent::Connection> Agent::connection_by_id(ConnectionId id) {
    auto it = connections_.find(id);
    return it == connections_.end() ? nullptr : it->second;
}

}  // namespace atsm
