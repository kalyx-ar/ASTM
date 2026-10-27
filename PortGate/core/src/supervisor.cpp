#include "portgate/supervisor.hpp"

#include <algorithm>
#include <cctype>
#include <chrono>

#ifndef PORTGATE_VERSION
#define PORTGATE_VERSION "dev"
#endif

namespace portgate {

namespace {

using cbor::Value;

constexpr int kPollMs = 100;
constexpr std::uint32_t kAdminMaxUnitBytes = 1024 * 1024;
constexpr std::uint8_t kTlsHandshakeRecord = 0x16;

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

std::string host_of(const std::string& peer) {
    const auto colon = peer.rfind(':');
    return colon == std::string::npos ? peer : peer.substr(0, colon);
}

bool same_device(const std::string& a, const std::string& b) {
    if (a.size() != b.size()) return false;
    for (std::size_t i = 0; i < a.size(); ++i)
        if (std::tolower(static_cast<unsigned char>(a[i])) != std::tolower(static_cast<unsigned char>(b[i]))) return false;
    return true;
}

}  // namespace

Supervisor::Supervisor(SupervisorOptions options, Logger logger)
    : options_(std::move(options)), logger_(std::move(logger)), store_(options_.state_directory), events_(options_.state_directory),
      capture_(options_.state_directory) {}

Supervisor::~Supervisor() { shutdown(); }

bool Supervisor::start(std::string& error) {
    if (!events_.open(error)) return false;
    auto loaded = store_.load(error);
    if (!loaded && !error.empty()) {
        // Never fall back to an unconfigured agent: a damaged state is a fault to repair by reinstalling.
        error = "cannot read the protected state: " + error;
        return false;
    }
    bool dirty = false;
    if (loaded) {
        state_ = std::move(*loaded);
    } else if (!options_.install_server_certificate_sha256.empty() || !options_.install_psk.empty()) {
        // Server fixed at installation (R1): nobody else can take the agent before it is configured.
        state_.admin.claimed = true;
        state_.admin.restricted = true;
        state_.admin.certificate_sha256 = options_.install_server_certificate_sha256;
        state_.admin_tls.psk_identity = options_.install_psk_identity;
        state_.admin_tls.psk = options_.install_psk;
        dirty = true;
    }
    if (!loaded && !options_.install_server_host.empty() && options_.install_server_port != 0) {
        // Agent-initiated from the start: the agent dials the server, which configures it (no inbound port needed).
        state_.admin.server_host = options_.install_server_host;
        state_.admin.server_port = options_.install_server_port;
        dirty = true;
    }
    if (!state_.admin_tls.has_certificate() && TlsServer::available()) {
        const std::string name = options_.agent_name.empty() ? "portgate" : options_.agent_name;
        if (!tls::generate_self_signed(name, state_.admin_tls, error)) return false;
        dirty = true;
    }
    if (dirty && !store_.save(state_, error)) return false;
    if (!state_.capture_key.empty()) capture_.set_key(state_.capture_key_id, state_.capture_key);
    if (!rebuild_admin_tls(error)) return false;
    if (!listener_.open(options_.admin_host, options_.admin_port, error)) return false;
    logger_(LogLevel::Info, "administration on " + options_.admin_host + ":" + std::to_string(listener_.port()) +
                                (admin_tls_ ? " (TLS certificate sha256 " + admin_certificate_sha256() + "; plain text also accepted unless restricted)" : " (plain)"));
    if (state_.configured) {
        std::lock_guard<std::mutex> lock(agent_mutex_);
        std::string link_error;
        if (!apply(state_.config, state_.link_tls, link_error))
            logger_(LogLevel::Error, "the installed configuration cannot start: " + link_error + " (administration stays available)");
    } else {
        logger_(LogLevel::Warning, state_.admin.claimed ? "waiting for the configuration of the server fixed at installation"
                                                        : "not configured: the first server that configures this agent can administer it");
    }
    Event started;
    started.kind = "agent-start";
    started.detail = std::string("version ") + PORTGATE_VERSION + ", revision " + std::to_string(state_.revision);
    record(started);
    accept_thread_ = std::thread([this] { accept_loop(); });
    return true;
}

void Supervisor::run(const std::atomic<bool>& stop) {
    while (!stop && !stopping_) {
        std::this_thread::sleep_for(std::chrono::milliseconds(kPollMs));
        const auto now = platform::monotonic_ms();
        if (now >= next_device_poll_) {
            next_device_poll_ = now + options_.device_poll_ms;
            poll_devices();
        }
        OutboundTarget target;
        if (outbound_target(target))
            while (outbound_threads_.size() < target.connections) {
                const auto index = outbound_threads_.size();
                outbound_threads_.emplace_back([this, index] { outbound_slot(index); });
            }
    }
}

void Supervisor::shutdown() {
    if (stopping_.exchange(true)) return;
    listener_.close();
    if (accept_thread_.joinable()) accept_thread_.join();
    std::vector<std::shared_ptr<AdminConnection>> all;
    {
        std::lock_guard<std::mutex> lock(connections_mutex_);
        for (auto& [id, connection] : connections_) {
            (void)id;
            all.push_back(connection);
        }
    }
    for (auto& connection : all) {
        connection->alive = false;
        connection->transport->shutdown();
    }
    while (active_connections_ > 0) std::this_thread::sleep_for(std::chrono::milliseconds(10));
    {
        std::lock_guard<std::mutex> lock(agent_mutex_);
        stop_agent();
    }
    for (auto& thread : outbound_threads_)
        if (thread.joinable()) thread.join();
}

std::uint16_t Supervisor::link_port() {
    std::lock_guard<std::mutex> lock(agent_mutex_);
    return agent_ ? agent_->listen_port() : 0;
}

std::string Supervisor::admin_certificate_sha256() const { return tls::certificate_sha256(state_.admin_tls.certificate_pem); }

bool Supervisor::rebuild_admin_tls(std::string& error) {
    if (!TlsServer::available() || (!state_.admin_tls.has_certificate() && !state_.admin_tls.has_psk())) {
        admin_tls_.reset();
        return true;
    }
    TlsServerSettings settings;
    settings.require_client_certificate = false;
    TlsMaterial material = state_.admin_tls;
    // The admin client may present any certificate (it is pinned by fingerprint, not chained to a CA). Using the
    // agent's own certificate as the CA list keeps Mbed TLS asking for it without trusting anything else.
    if (material.ca_pem.empty()) material.ca_pem = material.certificate_pem;
    auto server = TlsServer::create(settings, material, error);
    if (!server) return false;
    admin_tls_ = std::move(server);
    return true;
}

bool Supervisor::apply(const AgentConfig& config, const TlsMaterial& link_tls, std::string& error) {
    stop_agent();
    auto agent = std::make_shared<Agent>(config, logger_, link_tls, [this](Event event) { return record(std::move(event)); });
    capture_.configure(config.agent_id, config.ports);
    agent->set_capture_sink([this](const std::string& port_id, const Value& captured) { capture_.submit(port_id, captured); });
    if (!agent->start(error)) {
        link_error_ = error;
        return false;
    }
    link_error_.clear();
    agent_ = std::move(agent);
    agent_stop_ = false;
    Agent* running = agent_.get();
    agent_thread_ = std::thread([this, running] { running->run(agent_stop_); });
    return true;
}

void Supervisor::stop_agent() {
    if (!agent_) return;
    agent_stop_ = true;
    if (agent_thread_.joinable()) agent_thread_.join();
    agent_->shutdown();
    agent_.reset();
    agent_stop_ = false;
}

void Supervisor::accept_loop() {
    while (!stopping_) {
        auto socket = listener_.accept(kPollMs * 2);
        if (!socket.valid()) continue;
        ++active_connections_;
        std::thread([this](platform::Socket accepted) {
            serve(std::move(accepted));
            --active_connections_;
        }, std::move(socket)).detach();
    }
}

std::string Supervisor::identity_of(const Transport& transport, const std::string& peer) {
    if (!transport.psk_identity().empty()) return "psk:" + transport.psk_identity();
    if (!transport.peer_certificate_sha256().empty()) return "sha256:" + transport.peer_certificate_sha256();
    return "address:" + host_of(peer);
}

void Supervisor::serve(platform::Socket socket) {
    const std::string peer = socket.peer();
    std::uint8_t first = 0;
    // TLS and plain text share the port: a TLS client starts with a handshake record, a Link client with "AL".
    if (socket.peek(&first, 1, 10000) != 1) return;
    std::unique_ptr<Transport> transport;
    if (first == kTlsHandshakeRecord) {
        std::shared_ptr<TlsServer> server;
        {
            std::lock_guard<std::mutex> lock(state_mutex_);
            server = admin_tls_;
        }
        if (!server) return;
        std::string error;
        transport = server->accept(std::move(socket), 10000, error);
        if (!transport) {
            logger_(LogLevel::Warning, "admin TLS connection from " + peer + " rejected: " + error);
            return;
        }
    } else {
        transport = std::make_unique<PlainTransport>(std::move(socket));
    }
    const auto identity = identity_of(*transport, peer);
    serve_admin(std::move(transport), identity, {}, link::Decoder(kAdminMaxUnitBytes));
}

void Supervisor::serve_admin(std::unique_ptr<Transport> transport, const std::string& identity, std::vector<link::Unit> pending, link::Decoder decoder) {
    auto connection = std::make_shared<AdminConnection>();
    connection->transport = std::move(transport);
    connection->identity = identity;
    {
        std::lock_guard<std::mutex> lock(connections_mutex_);
        connection->id = next_connection_++;
        connections_[connection->id] = connection;
    }
    std::string agent_id;
    {
        std::lock_guard<std::mutex> lock(state_mutex_);
        agent_id = state_.configured ? state_.config.agent_id : (options_.agent_name.empty() ? "portgate" : options_.agent_name);
    }
    send(connection, link::control(link::Type::Hello, 0,
                                   Value::map({{0, Value::uint(link::kVersion)},
                                               {1, Value::text("agent")},
                                               {2, Value::text(agent_id)},
                                               {4, Value::text(options_.platform_profile)},
                                               {5, Value::uint(kAdminMaxUnitBytes)}})));
    connection_loop(connection, std::move(decoder), std::move(pending));
    {
        std::lock_guard<std::mutex> lock(connections_mutex_);
        connections_.erase(connection->id);
    }
    connection->transport->shutdown();
}

void Supervisor::connection_loop(const std::shared_ptr<AdminConnection>& connection, link::Decoder decoder, std::vector<link::Unit> pending) {
    for (const auto& unit : pending) {
        handle(connection, unit);
        if (!connection->alive) break;
    }
    std::vector<std::uint8_t> buffer(16 * 1024);
    std::vector<link::Unit> units;
    while (connection->alive && !stopping_) {
        const int n = connection->transport->receive(buffer.data(), buffer.size(), kPollMs * 2);
        if (n == -2) continue;
        if (n <= 0) break;
        units.clear();
        if (decoder.feed(buffer.data(), static_cast<std::size_t>(n), units) != link::DecodeError::None) break;
        for (const auto& unit : units) {
            handle(connection, unit);
            if (!connection->alive) break;
        }
    }
    // The agent ended the connection (refusal): read what the server already sent for a moment, so closing with unread
    // data does not reset the connection and discard the refusal before the server reads it.
    const auto until = platform::monotonic_ms() + 250;
    while (!connection->alive && !stopping_ && platform::monotonic_ms() < until) {
        const int n = connection->transport->receive(buffer.data(), buffer.size(), 50);
        if (n != -2 && n <= 0) break;
    }
}

bool Supervisor::outbound_target(OutboundTarget& target) {
    std::lock_guard<std::mutex> lock(state_mutex_);
    target.material = state_.admin_tls;
    if (state_.configured && state_.config.mode.initiator == ModeConfig::Initiator::Agent) {
        const auto& mode = state_.config.mode;
        target.host = mode.server_host;
        target.port = mode.server_port;
        target.pin = mode.server_certificate_sha256;
        target.tls = mode.outbound_tls;
        target.connections = mode.connections;
        return true;
    }
    if (!state_.configured && !state_.admin.server_host.empty() && state_.admin.server_port != 0) {
        // Fixed at installation (R1): one connection for the server to configure the agent.
        target.host = state_.admin.server_host;
        target.port = state_.admin.server_port;
        target.pin = state_.admin.certificate_sha256;
        target.tls = !target.pin.empty() || state_.admin_tls.has_psk();
        target.connections = 1;
        return true;
    }
    return false;
}

void Supervisor::outbound_slot(std::size_t index) {
    std::uint64_t backoff = 1000;
    std::string last_error;
    auto pause = [this](std::uint64_t ms) {
        const auto until = platform::monotonic_ms() + ms;
        while (!stopping_ && platform::monotonic_ms() < until) std::this_thread::sleep_for(std::chrono::milliseconds(kPollMs));
    };
    while (!stopping_) {
        OutboundTarget target;
        if (!outbound_target(target) || index >= target.connections) {
            pause(500);
            continue;
        }
        std::string error;
        std::unique_ptr<Transport> transport;
        if (auto socket = platform::Socket::connect(target.host, target.port, 5000, error)) {
            if (target.tls) transport = tls::connect_client(std::move(*socket), target.host, target.material, target.pin, 10000, error);
            else transport = std::make_unique<PlainTransport>(std::move(*socket));
        }
        if (!transport) {
            if (error != last_error) logger_(LogLevel::Warning, "outbound connection to " + target.host + ":" + std::to_string(target.port) + ": " + error);
            last_error = error;
            pause(backoff);
            backoff = std::min<std::uint64_t>(backoff * 2, 30000);
            continue;
        }
        backoff = 1000;
        last_error.clear();
        // The server speaks first: its Hello gives this connection a role.
        link::Decoder decoder(kAdminMaxUnitBytes);
        std::vector<link::Unit> units;
        std::vector<std::uint8_t> buffer(4096);
        while (!stopping_ && units.empty()) {
            const int n = transport->receive(buffer.data(), buffer.size(), kPollMs * 2);
            if (n == -2) continue;
            if (n <= 0 || decoder.feed(buffer.data(), static_cast<std::size_t>(n), units) != link::DecodeError::None) break;
        }
        const auto hello = !units.empty() && units.front().type == link::Type::Hello ? link::parse_map(units.front()) : std::nullopt;
        const auto role = hello ? text_or(*hello, 1) : std::string();
        if (role == "admin") {
            const auto identity = identity_of(*transport, transport->peer());
            serve_admin(std::move(transport), identity, std::move(units), std::move(decoder));
        } else if (role == "controller") {
            std::shared_ptr<Agent> agent;
            {
                std::lock_guard<std::mutex> lock(agent_mutex_);
                agent = agent_;
            }
            if (agent) {
                agent->adopt(std::move(transport), std::move(units), std::move(decoder));
            } else {
                const auto refusal = link::encode(link::control(link::Type::Error, 0, Value::map({{0, Value::text("PORTGATE-NOT-CONFIGURED")}, {1, Value::uint(0)},
                                                                                                  {2, Value::text("the agent has no Link configuration yet")}})));
                transport->send_all(refusal.data(), refusal.size());
                transport->shutdown();
                pause(backoff);
            }
        } else {
            transport->shutdown();
        }
    }
}

bool Supervisor::authorize(const AdminConnection& connection, std::string& reason) {
    std::lock_guard<std::mutex> lock(state_mutex_);
    if (state_.configured && state_.config.mode.admin_requires_tls && !connection.transport->encrypted()) {
        reason = "this agent requires TLS for administration";
        return false;
    }
    if (!state_.admin.claimed || !state_.admin.restricted) return true;
    const auto& admin = state_.admin;
    if (!admin.certificate_sha256.empty() && connection.identity == "sha256:" + admin.certificate_sha256) return true;
    if (state_.admin_tls.has_psk() && connection.identity == "psk:" + state_.admin_tls.psk_identity) return true;
    if (admin.certificate_sha256.empty() && !state_.admin_tls.has_psk() && !admin.address.empty() && connection.identity == "address:" + admin.address)
        return true;
    reason = "this agent is restricted to the server that configured it";
    return false;
}

void Supervisor::handle(const std::shared_ptr<AdminConnection>& connection, const link::Unit& unit) {
    auto map = link::parse_map(unit);
    if (!map) {
        refuse(connection, 0, "PORTGATE-BAD-PAYLOAD", "administration units are CBOR maps");
        return;
    }
    const auto request_id = uint_or(*map, 0, 0);
    if (unit.type == link::Type::Hello) {
        const std::string id = text_or(*map, 2);
        if (id.empty() || uint_or(*map, 0, 0) != link::kVersion || text_or(*map, 1) != "admin") {
            refuse(connection, 0, "PORTGATE-BAD-HELLO", "Hello needs version 1, role admin and a server id");
            connection->alive = false;
            return;
        }
        std::string reason;
        if (!authorize(*connection, reason)) {
            Event denied;
            denied.kind = "admin-denied";
            denied.actor = id;
            denied.detail = connection->identity;
            denied.reason = reason;
            record(denied);
            logger_(LogLevel::Warning, "administration refused to " + id + " (" + connection->identity + "): " + reason);
            refuse(connection, 0, "PORTGATE-ADMIN-NOT-AUTHORIZED", reason);
            connection->alive = false;
            return;
        }
        connection->server_id = id;
        connection->hello = true;
        return;
    }
    if (unit.type == link::Type::Heartbeat) return;
    if (!connection->hello) {
        refuse(connection, request_id, "PORTGATE-HELLO-REQUIRED", "send Hello first");
        connection->alive = false;
        return;
    }
    switch (unit.type) {
        case link::Type::GetStatus:
            reply(connection, request_id, status_body());
            break;
        case link::Type::GetConfig: {
            std::lock_guard<std::mutex> lock(state_mutex_);
            std::map<std::uint64_t, Value> body{{0, Value::uint(state_.revision)}};
            if (state_.configured) body.emplace(1, encode_config(state_.config));
            reply(connection, request_id, Value::map(std::move(body)));
            break;
        }
        case link::Type::SetConfig:
            set_config(connection, request_id, *map);
            break;
        case link::Type::SetTls:
            set_tls(connection, request_id, *map);
            break;
        case link::Type::ListDevices:
            reply(connection, request_id, devices_body());
            break;
        case link::Type::RevokePort:
        case link::Type::ReopenPort: {
            std::lock_guard<std::mutex> lock(agent_mutex_);
            std::string error = "the agent is not running";
            const bool ok = agent_ && (unit.type == link::Type::RevokePort ? agent_->revoke_port(text_or(*map, 1), connection->server_id, error)
                                                                            : agent_->reopen_port(text_or(*map, 1), connection->server_id, error));
            if (ok) reply(connection, request_id, Value::map({}));
            else refuse(connection, request_id, "PORTGATE-PORT", error);
            break;
        }
        case link::Type::SetCaptureKey:
        case link::Type::ReadCapture:
        case link::Type::AckCapture:
            capture_request(connection, unit.type, request_id, *map);
            break;
        case link::Type::UnblockController: {
            std::lock_guard<std::mutex> lock(agent_mutex_);
            if (agent_ && agent_->unblock(text_or(*map, 1), connection->server_id)) reply(connection, request_id, Value::map({}));
            else refuse(connection, request_id, "PORTGATE-NOT-BLOCKED", "that controller is not blocked");
            break;
        }
        case link::Type::ReadEvents: {
            const auto max = std::min<std::uint64_t>(uint_or(*map, 2, 200), 1000);
            std::vector<Value> list;
            for (const auto& event : events_.read(uint_or(*map, 1, 0), static_cast<std::size_t>(max))) list.push_back(event.to_cbor());
            reply(connection, request_id, Value::map({{0, Value::array(std::move(list))}, {1, Value::uint(events_.last_id())}}));
            break;
        }
        case link::Type::Restart: {
            AgentConfig config;
            TlsMaterial tls;
            bool configured;
            {
                std::lock_guard<std::mutex> lock(state_mutex_);
                configured = state_.configured;
                config = state_.config;
                tls = state_.link_tls;
            }
            std::string error = "not configured";
            std::lock_guard<std::mutex> lock(agent_mutex_);
            if (configured && apply(config, tls, error)) {
                Event restarted;
                restarted.kind = "restart";
                restarted.actor = connection->server_id;
                record(restarted);
                reply(connection, request_id, Value::map({{1, Value::uint(agent_->listen_port())}}));
            } else {
                refuse(connection, request_id, "PORTGATE-RESTART", error);
            }
            break;
        }
        default:
            refuse(connection, request_id, "PORTGATE-UNKNOWN-REQUEST", "unit " + std::to_string(static_cast<int>(unit.type)) + " is not an administration request");
            break;
    }
}

void Supervisor::set_config(const std::shared_ptr<AdminConnection>& connection, std::uint64_t request_id, const Value& map) {
    const Value* encoded = map.get(2);
    std::string error;
    auto config = encoded ? decode_config(*encoded, error) : std::nullopt;
    if (!config) return refuse(connection, request_id, "PORTGATE-INVALID-CONFIG", encoded ? error : "config/2 required");
    if (config->mode.admin_requires_tls && !connection->transport->encrypted())
        return refuse(connection, request_id, "PORTGATE-INSECURE-CHANNEL", "requiring TLS for administration must be set over TLS");
    TlsMaterial link_tls;
    AgentState previous;
    {
        std::lock_guard<std::mutex> lock(state_mutex_);
        link_tls = state_.link_tls;
        previous = state_;
    }
    const bool outbound = config->mode.initiator == ModeConfig::Initiator::Agent;
    // Outbound TLS authenticates the server by its pinned certificate or by the administration PSK: never blindly.
    if (outbound && config->mode.outbound_tls && config->mode.server_certificate_sha256.empty() && !previous.admin_tls.has_psk())
        return refuse(connection, request_id, "PORTGATE-OUTBOUND-IDENTITY", "outbound TLS needs the server certificate fingerprint or an administration PSK");
    if (std::any_of(config->ports.begin(), config->ports.end(), [](const PortConfig& p) { return p.capture.enabled; }) && previous.capture_key.empty())
        return refuse(connection, request_id, "PORTGATE-CAPTURE-KEY-MISSING", "send SetCaptureKey before enabling the durable capture");
    // Inbound Link TLS material is irrelevant when the agent opens the connections (it uses its own certificate).
    if (!outbound && config->tls == TlsMode::Tls && !link_tls.has_certificate() && !link_tls.has_psk())
        return refuse(connection, request_id, "PORTGATE-TLS-MATERIAL-MISSING", "send SetTls for the Link endpoint before enabling TLS");

    std::uint16_t port = 0;
    bool hot = false;
    {
        std::lock_guard<std::mutex> lock(agent_mutex_);
        // Only devices changed: reopen those ports without restarting Link or dropping any controller.
        hot = previous.configured && agent_ && agent_->reconfigure(*config);
        if (!hot && !apply(*config, link_tls, error)) {
            std::string ignored;
            if (previous.configured) apply(previous.config, previous.link_tls, ignored);
            return refuse(connection, request_id, "PORTGATE-APPLY-FAILED", error);
        }
        port = agent_->listen_port();
    }
    const auto revision = uint_or(map, 1, previous.revision + 1);
    {
        std::lock_guard<std::mutex> lock(state_mutex_);
        state_.configured = true;
        state_.config = *config;
        state_.revision = revision;
        state_.admin.claimed = true;
        state_.admin.restricted = config->mode.restricted;
        state_.admin.server_id = connection->server_id;
        if (connection->identity.rfind("sha256:", 0) == 0) state_.admin.certificate_sha256 = connection->identity.substr(7);
        else if (connection->identity.rfind("address:", 0) == 0 && state_.admin.certificate_sha256.empty()) state_.admin.address = connection->identity.substr(8);
        if (!store_.save(state_, error)) {
            logger_(LogLevel::Error, "configuration applied but not saved: " + error);
            return refuse(connection, request_id, "PORTGATE-STATE-NOT-SAVED", error);
        }
    }
    Event applied;
    applied.kind = "config-applied";
    applied.actor = connection->server_id;
    applied.detail = "revision " + std::to_string(revision) + ", " + std::to_string(config->ports.size()) + " port(s), " + connection->identity;
    if (hot) applied.detail += ", devices applied without restarting Link";
    record(applied);
    reply(connection, request_id, Value::map({{0, Value::uint(revision)}, {1, Value::uint(port)}, {2, Value::boolean(hot)}}));
}

void Supervisor::set_tls(const std::shared_ptr<AdminConnection>& connection, std::uint64_t request_id, const Value& map) {
    const auto target = uint_or(map, 1, 0);
    if (target > 1) return refuse(connection, request_id, "PORTGATE-BAD-TARGET", "target 0 is Link, 1 is administration");
    TlsMaterial material;
    material.certificate_pem = text_or(map, 2);
    material.private_key_pem = text_or(map, 3);
    material.ca_pem = text_or(map, 4);
    material.psk_identity = text_or(map, 5);
    if (const Value* psk = map.get(6); psk && psk->as_bytes()) material.psk = *psk->as_bytes();
    const bool clear = bool_or(map, 7);
    if ((!material.private_key_pem.empty() || !material.psk.empty()) && !connection->transport->encrypted())
        return refuse(connection, request_id, "PORTGATE-INSECURE-CHANNEL", "private keys and PSKs are accepted only over TLS");
    if (!material.psk.empty() && material.psk.size() < 16)
        return refuse(connection, request_id, "PORTGATE-WEAK-PSK", "a PSK needs at least 128 random bits");
    if (!clear && !material.has_certificate() && !material.has_psk())
        return refuse(connection, request_id, "PORTGATE-TLS-MATERIAL-MISSING", "send certificate and key, a PSK, or clear");
    if (target == 1 && clear) return refuse(connection, request_id, "PORTGATE-BAD-TARGET", "the administration certificate can be replaced, not cleared");

    std::string error;
    if (!clear) {
        TlsServerSettings probe;
        probe.require_client_certificate = false;
        if (!TlsServer::create(probe, material, error)) return refuse(connection, request_id, "PORTGATE-TLS-INVALID", error);
    }
    if (target == 0) {
        AgentState snapshot;
        {
            std::lock_guard<std::mutex> lock(state_mutex_);
            snapshot = state_;
        }
        if (snapshot.configured && snapshot.config.tls == TlsMode::Tls) {
            if (clear) return refuse(connection, request_id, "PORTGATE-TLS-IN-USE", "disable TLS on the Link endpoint before clearing its material");
            std::lock_guard<std::mutex> lock(agent_mutex_);
            if (!apply(snapshot.config, material, error)) {
                std::string ignored;
                apply(snapshot.config, snapshot.link_tls, ignored);
                return refuse(connection, request_id, "PORTGATE-APPLY-FAILED", error);
            }
        }
    }
    std::string fingerprint;
    {
        std::lock_guard<std::mutex> lock(state_mutex_);
        auto& slot = target == 0 ? state_.link_tls : state_.admin_tls;
        slot = clear ? TlsMaterial{} : material;
        if (target == 1 && !rebuild_admin_tls(error)) return refuse(connection, request_id, "PORTGATE-TLS-INVALID", error);
        if (!store_.save(state_, error)) return refuse(connection, request_id, "PORTGATE-STATE-NOT-SAVED", error);
        fingerprint = tls::certificate_sha256(slot.certificate_pem);
    }
    Event updated;
    updated.kind = clear ? "tls-cleared" : "tls-updated";
    updated.actor = connection->server_id;
    updated.detail = std::string(target == 0 ? "link" : "admin") + (fingerprint.empty() ? "" : ", certificate sha256 " + fingerprint) +
                     (material.has_psk() ? ", psk identity " + material.psk_identity : "");
    record(updated);
    reply(connection, request_id, Value::map({{0, Value::text(fingerprint)}}));
}

void Supervisor::capture_request(const std::shared_ptr<AdminConnection>& connection, link::Type type, std::uint64_t request_id, const Value& map) {
    if (type == link::Type::SetCaptureKey) {
        const Value* key = map.get(2);
        const auto key_id = static_cast<std::uint32_t>(uint_or(map, 1, 0));
        if (!connection->transport->encrypted())
            return refuse(connection, request_id, "PORTGATE-INSECURE-CHANNEL", "the capture key is accepted only over TLS");
        if (key_id == 0 || !key || !key->as_bytes() || key->as_bytes()->size() != 32)
            return refuse(connection, request_id, "PORTGATE-BAD-CAPTURE-KEY", "send a key id > 0 and a 32-byte AES-256 key");
        std::string error;
        {
            std::lock_guard<std::mutex> lock(state_mutex_);
            state_.capture_key_id = key_id;
            state_.capture_key = *key->as_bytes();
            if (!store_.save(state_, error)) return refuse(connection, request_id, "PORTGATE-STATE-NOT-SAVED", error);
        }
        capture_.set_key(key_id, *key->as_bytes());
        Event event;
        event.kind = "capture-key";
        event.actor = connection->server_id;
        event.detail = "key id " + std::to_string(key_id);  // never the key
        record(event);
        return reply(connection, request_id, Value::map({}));
    }
    const auto port_id = text_or(map, 1);
    bool known;
    {
        std::lock_guard<std::mutex> lock(state_mutex_);
        known = state_.configured && std::any_of(state_.config.ports.begin(), state_.config.ports.end(), [&](const PortConfig& p) { return p.port_id == port_id; });
    }
    if (!known) return refuse(connection, request_id, "PORTGATE-PORT", "unknown port " + port_id);
    if (type == link::Type::AckCapture) {
        std::string error;
        if (!capture_.ack(port_id, uint_or(map, 2, 0), error)) return refuse(connection, request_id, "PORTGATE-CAPTURE-ACK", error);
        Event event;
        event.kind = "capture-ack";
        event.port_id = port_id;
        event.actor = connection->server_id;
        event.detail = "up to " + std::to_string(uint_or(map, 2, 0));
        record(event);
        return reply(connection, request_id, Value::map({}));
    }
    // Records leave sealed: the agent never decrypts its capture, the server holds the key.
    const auto page = capture_.read(port_id, uint_or(map, 2, 0), static_cast<std::size_t>(std::min<std::uint64_t>(uint_or(map, 3, 200), 1000)));
    std::vector<Value> records;
    for (const auto& r : page.records)
        records.push_back(Value::map({{0, Value::uint(r.seq)}, {1, Value::uint(r.key_id)}, {2, Value::uint(r.time_ms)}, {3, Value::bytes(r.nonce)}, {4, Value::bytes(r.sealed)}}));
    std::vector<Value> gaps;
    for (const auto& g : page.gaps) gaps.push_back(Value::array({Value::uint(g.from), Value::uint(g.to), Value::text(g.reason)}));
    std::string agent_id;
    {
        std::lock_guard<std::mutex> lock(state_mutex_);
        agent_id = state_.config.agent_id;
    }
    reply(connection, request_id, Value::map({{0, Value::array(std::move(records))},
                                              {1, Value::uint(page.first)},
                                              {2, Value::uint(page.last)},
                                              {3, Value::array(std::move(gaps))},
                                              {4, Value::text(agent_id)}}));
}

Value Supervisor::status_body() {
    std::vector<Value> ports;
    std::uint16_t link = 0;
    std::string link_error;
    {
        std::lock_guard<std::mutex> lock(agent_mutex_);
        link_error = link_error_;
        if (agent_) {
            link = agent_->listen_port();
            for (const auto& p : agent_->status()) {
                ports.push_back(Value::map({{0, Value::text(p.port_id)},
                                            {1, Value::text(p.instrument_id)},
                                            {2, Value::text(p.device)},
                                            {3, Value::boolean(p.open)},
                                            {4, Value::text(p.owner)},
                                            {5, Value::uint(p.generation)},
                                            {6, Value::uint(p.queued)},
                                            {7, Value::boolean(p.closed)},
                                            {8, Value::uint(p.quarantine_ms)}}));
            }
        }
    }
    std::lock_guard<std::mutex> lock(state_mutex_);
    return Value::map({{0, Value::text(state_.configured ? state_.config.agent_id : std::string())},
                       {1, Value::text(options_.platform_profile)},
                       {2, Value::text(PORTGATE_VERSION)},
                       {4, Value::uint(state_.revision)},
                       {5, Value::boolean(state_.configured)},
                       {6, Value::boolean(state_.admin.claimed)},
                       {7, Value::boolean(state_.admin.restricted)},
                       {8, Value::uint(link)},
                       {9, Value::text(tls::certificate_sha256(state_.admin_tls.certificate_pem))},
                       {10, Value::array(std::move(ports))},
                       {11, Value::boolean(state_.configured && state_.config.tls == TlsMode::Tls)},
                       {12, Value::text(link_error)}});
}

Value Supervisor::devices_body() {
    const auto devices = platform::list_serial_devices();
    std::vector<std::string> used;
    {
        std::lock_guard<std::mutex> lock(state_mutex_);
        if (state_.configured)
            for (const auto& port : state_.config.ports)
                if (port.device.kind == DeviceConfig::Kind::Serial) used.push_back(port.device.path);
    }
    std::vector<Value> list;
    for (const auto& d : devices) {
        const bool in_use = std::any_of(used.begin(), used.end(), [&](const std::string& path) { return same_device(path, d.name); });
        list.push_back(Value::map({{0, Value::text(d.name)},
                                   {1, Value::text(d.friendly_name)},
                                   {2, Value::text(d.usb_vid)},
                                   {3, Value::text(d.usb_pid)},
                                   {4, Value::text(d.usb_serial)},
                                   {5, Value::text(d.location)},
                                   {6, Value::text(d.instance_id)},
                                   {7, Value::boolean(in_use)}}));
    }
    return Value::array(std::move(list));
}

void Supervisor::poll_devices() {
    auto current = platform::list_serial_devices();
    auto key = [](const platform::SerialDeviceInfo& d) { return d.name + "|" + d.instance_id; };
    auto describe = [](const platform::SerialDeviceInfo& d) {
        return d.name + " " + d.friendly_name + (d.usb_vid.empty() ? "" : " USB " + d.usb_vid + ":" + d.usb_pid) +
               (d.usb_serial.empty() ? "" : " serial " + d.usb_serial) + (d.location.empty() ? "" : " at " + d.location);
    };
    if (!devices_polled_) {
        // The first enumeration is the baseline, not a change.
        devices_polled_ = true;
        devices_ = std::move(current);
        return;
    }
    for (const auto& d : current)
        if (std::none_of(devices_.begin(), devices_.end(), [&](const platform::SerialDeviceInfo& o) { return key(o) == key(d); })) {
            Event event;
            event.kind = "device-arrived";
            event.detail = describe(d);
            record(event);
        }
    for (const auto& d : devices_)
        if (std::none_of(current.begin(), current.end(), [&](const platform::SerialDeviceInfo& o) { return key(o) == key(d); })) {
            Event event;
            event.kind = "device-removed";
            event.detail = describe(d);
            record(event);
        }
    devices_ = std::move(current);
}

Event Supervisor::record(Event event) {
    std::string error;
    const Event logged = events_.append(std::move(event), &error);
    if (!error.empty()) logger_(LogLevel::Warning, "event log: " + error);
    std::vector<std::shared_ptr<AdminConnection>> targets;
    {
        std::lock_guard<std::mutex> lock(connections_mutex_);
        for (auto& [id, connection] : connections_) {
            (void)id;
            if (connection->hello) targets.push_back(connection);
        }
    }
    const auto unit = link::control(link::Type::AdminEvent, 0, logged.to_cbor());
    for (auto& connection : targets) send(connection, unit);
    return logged;
}

void Supervisor::reply(const std::shared_ptr<AdminConnection>& connection, std::uint64_t request_id, const Value& body) {
    send(connection, link::control(link::Type::AdminResult, 0, Value::map({{0, Value::uint(request_id)}, {1, Value::boolean(true)}, {4, body}})));
}

void Supervisor::refuse(const std::shared_ptr<AdminConnection>& connection, std::uint64_t request_id, const std::string& code, const std::string& detail) {
    send(connection, link::control(link::Type::AdminResult, 0,
                                   Value::map({{0, Value::uint(request_id)}, {1, Value::boolean(false)}, {2, Value::text(code)}, {3, Value::text(detail)}})));
}

void Supervisor::send(const std::shared_ptr<AdminConnection>& connection, const link::Unit& unit) {
    const auto bytes = link::encode(unit);
    std::lock_guard<std::mutex> lock(connection->send_mutex);
    if (connection->alive && !connection->transport->send_all(bytes.data(), bytes.size())) connection->alive = false;
}

}  // namespace portgate
