// The serial ↔ TCP agent: owns the ports, arbitrates controllers and moves bytes without interpreting them.
#pragma once

#include <atomic>
#include <functional>
#include <map>
#include <memory>
#include <mutex>
#include <optional>
#include <string>
#include <thread>
#include <vector>

#include "portgate/arbiter.hpp"
#include "portgate/config.hpp"
#include "portgate/events.hpp"
#include "portgate/link.hpp"
#include "portgate/platform.hpp"
#include "portgate/state.hpp"
#include "portgate/transport.hpp"

namespace portgate {

enum class LogLevel { Debug, Info, Warning, Error };

using Logger = std::function<void(LogLevel, const std::string&)>;
/// Receives every semaphore and security event; returns it with the id and time assigned by the event log.
using EventSink = std::function<Event(Event)>;

struct PortStatus {
    std::string port_id;
    std::string instrument_id;
    std::string device;
    bool open = false;
    std::string owner;
    std::uint32_t generation = 0;
    std::size_t queued = 0;
    bool closed = false;
    std::uint64_t quarantine_ms = 0;  // remaining
};

class Agent {
public:
    Agent(AgentConfig config, Logger logger, TlsMaterial tls = {}, EventSink sink = {});
    ~Agent();
    Agent(const Agent&) = delete;
    Agent& operator=(const Agent&) = delete;

    /// Opens the listener and starts the port and connection threads.
    bool start(std::string& error);
    /// Housekeeping loop (leases, queue deadlines, handovers, heartbeats) until `stop` becomes true.
    void run(const std::atomic<bool>& stop);
    void shutdown();

    std::uint16_t listen_port() const { return listener_.port(); }
    const Id128& epoch() const { return epoch_; }

    // Administration (called by the supervisor).
    std::vector<PortStatus> status();
    bool revoke_port(const std::string& port_id, const std::string& actor, std::string& error);
    bool reopen_port(const std::string& port_id, const std::string& actor, std::string& error);
    bool unblock(const std::string& controller_id, const std::string& actor);

private:
    struct Connection {
        ConnectionId id = 0;
        std::unique_ptr<Transport> transport;
        std::mutex send_mutex;
        std::string controller_id;
        std::string peer;
        bool hello = false;
        std::atomic<bool> alive{true};
        std::atomic<std::uint64_t> last_rx_ms{0};
    };

    struct Handover {
        std::uint64_t id = 0;
        ConnectionId requester = 0;
        std::string requester_id;
        std::uint64_t request_id = 0;
        ConnectionId owner = 0;
        std::uint64_t deadline = 0;
    };

    struct Port {
        std::uint16_t index = 0;
        PortConfig config;
        std::unique_ptr<platform::Device> device;
        std::unique_ptr<Arbiter> arbiter;
        std::mutex write_mutex;
        std::atomic<bool> open{false};
        std::uint64_t rx_seq = 0;
        std::uint64_t rx_offset = 0;
        std::uint64_t unowned_rx = 0;
        bool last_write_unknown = false;
        std::thread reader;
        std::optional<Handover> handover;
        std::map<std::string, std::uint64_t> last_handover;  // controller -> monotonic ms
        std::uint32_t logged_generation = 0;
    };

    struct KillRecord {
        std::string controller;
        std::uint16_t port = 0;
        std::uint64_t contest_until_utc = 0;
        bool contested = false;
    };

    void accept_loop();
    void serve(platform::Socket socket);
    bool authorize(const std::shared_ptr<Connection>& connection, const std::string& controller_id, std::string& reason) const;
    void connection_loop(std::shared_ptr<Connection> connection);
    void port_loop(Port& port);
    void handle(const std::shared_ptr<Connection>& connection, const link::Unit& unit);
    void handle_serial_data(const std::shared_ptr<Connection>& connection, Port& port, const link::Unit& unit);
    void drop(const std::shared_ptr<Connection>& connection, const std::string& reason);

    // Semaphore extensions; all called with `mutex_` held.
    void request_handover(const std::shared_ptr<Connection>& connection, Port& port, const cbor::Value& map, std::uint64_t now);
    void answer_handover(const std::shared_ptr<Connection>& connection, Port& port, const cbor::Value& map, std::uint64_t now);
    void kill_switch(const std::shared_ptr<Connection>& connection, Port& port, const cbor::Value& map, std::uint64_t now);
    void contest(const std::shared_ptr<Connection>& connection, Port& port, const cbor::Value& map, std::uint64_t now,
                 std::vector<std::shared_ptr<Connection>>& to_drop);
    void finish_handover(Port& port, bool yielded, const std::string& code);
    bool has_right(const Port& port, const std::string& controller_id, std::uint32_t right) const;
    bool strongly_authenticated(const Connection& connection) const;
    Event emit(Event event);
    void broadcast(Port& port, const Event& event);

    /// Sends the arbiter decisions. Must be called with `mutex_` held.
    void dispatch(Port& port, const std::vector<ArbiterEvent>& events);

    void send(const std::shared_ptr<Connection>& connection, const link::Unit& unit);
    void send_error(const std::shared_ptr<Connection>& connection, const std::string& code, std::uint64_t request_id, const std::string& detail,
                    std::uint16_t channel = 0);
    void send_hello(const std::shared_ptr<Connection>& connection);
    Port* port_for(std::uint16_t channel);
    Port* port_by_id(const std::string& port_id);
    std::shared_ptr<Connection> connection_by_id(ConnectionId id);
    std::string controller_of(ConnectionId id);

    void log(LogLevel level, const std::string& message) const { if (logger_) logger_(level, message); }

    AgentConfig config_;
    Logger logger_;
    TlsMaterial tls_material_;
    EventSink sink_;
    Id128 epoch_{};
    platform::Listener listener_;
    std::atomic<bool> stopping_{false};
    std::thread accept_thread_;
    std::mutex mutex_;  // connections_, port arbitration, port counters and security state
    std::map<ConnectionId, std::shared_ptr<Connection>> connections_;
    std::vector<std::unique_ptr<Port>> ports_;
    ConnectionId next_connection_ = 1;
    std::atomic<int> active_connections_{0};
    std::unique_ptr<TlsServer> tls_;
    std::uint64_t next_local_event_ = 1;
    std::uint64_t next_handover_ = 1;
    std::map<std::string, std::uint64_t> blocked_;    // controller -> monotonic ms until which Hello is refused
    std::map<std::string, std::uint64_t> last_kill_;  // controller -> monotonic ms of its last kill switch
    std::map<std::uint64_t, KillRecord> kills_;       // kill-switch event id -> record
};

}  // namespace portgate
