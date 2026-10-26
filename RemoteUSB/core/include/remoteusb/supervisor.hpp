// Supervisor of a RemoteUSB installation: protected state, event log, administration endpoint (spec/admin-v1.md)
// and the Link agent built from the configuration in force. It always starts with the last installed configuration.
#pragma once

#include <atomic>
#include <map>
#include <memory>
#include <mutex>
#include <optional>
#include <string>
#include <thread>
#include <vector>

#include "remoteusb/agent.hpp"
#include "remoteusb/events.hpp"
#include "remoteusb/state.hpp"
#include "remoteusb/transport.hpp"

namespace remoteusb {

constexpr std::uint16_t kDefaultAdminPort = 28240;

struct SupervisorOptions {
    std::string state_directory;
    std::string admin_host = "0.0.0.0";
    std::uint16_t admin_port = kDefaultAdminPort;
    std::string platform_profile = "unspecified";
    std::string agent_name;  // common name of the self-signed admin certificate (host name by default)
    /// Fixes the administering server at installation (risk R1): its certificate fingerprint and/or a PSK.
    /// Applied only when there is no state yet.
    std::string install_server_certificate_sha256;
    std::string install_psk_identity;
    std::vector<std::uint8_t> install_psk;
    std::uint32_t device_poll_ms = 2000;
};

class Supervisor {
public:
    Supervisor(SupervisorOptions options, Logger logger);
    ~Supervisor();
    Supervisor(const Supervisor&) = delete;
    Supervisor& operator=(const Supervisor&) = delete;

    bool start(std::string& error);
    /// Housekeeping (device changes) until `stop` becomes true.
    void run(const std::atomic<bool>& stop);
    void shutdown();

    std::uint16_t admin_port() const { return listener_.port(); }
    std::uint16_t link_port();
    std::string admin_certificate_sha256() const;

private:
    struct AdminConnection {
        std::uint64_t id = 0;
        std::unique_ptr<Transport> transport;
        std::mutex send_mutex;
        std::string server_id;
        std::string identity;  // what restricts administration: cert fingerprint, psk identity or address
        bool hello = false;
        std::atomic<bool> alive{true};
    };

    void accept_loop();
    void serve(platform::Socket socket);
    void connection_loop(const std::shared_ptr<AdminConnection>& connection);
    void handle(const std::shared_ptr<AdminConnection>& connection, const link::Unit& unit);
    bool authorize(const AdminConnection& connection, std::string& reason);
    void reply(const std::shared_ptr<AdminConnection>& connection, std::uint64_t request_id, const cbor::Value& body);
    void refuse(const std::shared_ptr<AdminConnection>& connection, std::uint64_t request_id, const std::string& code, const std::string& detail);
    void send(const std::shared_ptr<AdminConnection>& connection, const link::Unit& unit);

    cbor::Value status_body();
    cbor::Value devices_body();
    void set_config(const std::shared_ptr<AdminConnection>& connection, std::uint64_t request_id, const cbor::Value& map);
    void set_tls(const std::shared_ptr<AdminConnection>& connection, std::uint64_t request_id, const cbor::Value& map);

    /// Replaces the running Link agent; on failure the previous one keeps running. Called with agent_mutex_ held.
    bool apply(const AgentConfig& config, const TlsMaterial& link_tls, std::string& error);
    void stop_agent();
    bool rebuild_admin_tls(std::string& error);

    Event record(Event event);
    void poll_devices();

    SupervisorOptions options_;
    Logger logger_;
    StateStore store_;
    EventLog events_;
    std::mutex state_mutex_;  // state_ and the admin TLS server
    AgentState state_;
    std::shared_ptr<TlsServer> admin_tls_;
    platform::Listener listener_;
    std::atomic<bool> stopping_{false};
    std::thread accept_thread_;
    std::atomic<int> active_connections_{0};

    std::mutex connections_mutex_;
    std::map<std::uint64_t, std::shared_ptr<AdminConnection>> connections_;
    std::uint64_t next_connection_ = 1;

    std::mutex agent_mutex_;
    std::unique_ptr<Agent> agent_;
    std::atomic<bool> agent_stop_{false};
    std::thread agent_thread_;
    std::string link_error_;

    std::vector<platform::SerialDeviceInfo> devices_;
    std::uint64_t next_device_poll_ = 0;
    bool devices_polled_ = false;
};

}  // namespace remoteusb
