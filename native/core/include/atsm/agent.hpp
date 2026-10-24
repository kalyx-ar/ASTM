// The serial ↔ TCP agent: owns the ports, arbitrates controllers and moves bytes without interpreting them.
#pragma once

#include <atomic>
#include <functional>
#include <map>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include "atsm/arbiter.hpp"
#include "atsm/config.hpp"
#include "atsm/link.hpp"
#include "atsm/platform.hpp"
#include "atsm/transport.hpp"

namespace atsm {

enum class LogLevel { Debug, Info, Warning, Error };

using Logger = std::function<void(LogLevel, const std::string&)>;

class Agent {
public:
    Agent(AgentConfig config, Logger logger);
    ~Agent();
    Agent(const Agent&) = delete;
    Agent& operator=(const Agent&) = delete;

    /// Opens the listener and starts the port and connection threads.
    bool start(std::string& error);
    /// Housekeeping loop (leases, queue deadlines, heartbeats) until `stop` becomes true.
    void run(const std::atomic<bool>& stop);
    void shutdown();

    std::uint16_t listen_port() const { return listener_.port(); }
    const Id128& epoch() const { return epoch_; }

private:
    struct Connection {
        ConnectionId id = 0;
        std::unique_ptr<Transport> transport;
        std::mutex send_mutex;
        std::string controller_id;
        bool hello = false;
        std::atomic<bool> alive{true};
        std::atomic<std::uint64_t> last_rx_ms{0};
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
    };

    void accept_loop();
    void serve(platform::Socket socket);
    bool authorize(const std::shared_ptr<Connection>& connection, const std::string& controller_id, std::string& reason) const;
    void connection_loop(std::shared_ptr<Connection> connection);
    void port_loop(Port& port);
    void handle(const std::shared_ptr<Connection>& connection, const link::Unit& unit);
    void handle_serial_data(const std::shared_ptr<Connection>& connection, Port& port, const link::Unit& unit);
    void drop(const std::shared_ptr<Connection>& connection, const std::string& reason);

    /// Sends the arbiter decisions. Must be called with `mutex_` held.
    void dispatch(Port& port, const std::vector<ArbiterEvent>& events);

    void send(const std::shared_ptr<Connection>& connection, const link::Unit& unit);
    void send_error(const std::shared_ptr<Connection>& connection, const std::string& code, std::uint64_t request_id, const std::string& detail);
    void send_hello(const std::shared_ptr<Connection>& connection);
    Port* port_for(std::uint16_t channel);
    std::shared_ptr<Connection> connection_by_id(ConnectionId id);

    void log(LogLevel level, const std::string& message) const { if (logger_) logger_(level, message); }

    AgentConfig config_;
    Logger logger_;
    Id128 epoch_{};
    platform::Listener listener_;
    std::atomic<bool> stopping_{false};
    std::thread accept_thread_;
    std::mutex mutex_;  // connections_, port arbitration and port counters
    std::map<ConnectionId, std::shared_ptr<Connection>> connections_;
    std::vector<std::unique_ptr<Port>> ports_;
    ConnectionId next_connection_ = 1;
    std::atomic<int> active_connections_{0};
    std::unique_ptr<TlsServer> tls_;
};

}  // namespace atsm
