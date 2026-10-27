// Agent configuration. It is provisioned by the administering server (spec/admin-v1.md) and kept as CBOR in the
// protected state of the agent; there is no configuration file to edit by hand.
#pragma once

#include <cstdint>
#include <map>
#include <optional>
#include <string>
#include <vector>

#include "portgate/cbor.hpp"
#include "portgate/transport_settings.hpp"

namespace portgate {

enum class Parity { None, Odd, Even, Mark, Space };
enum class FlowControl { None, RtsCts, XonXoff };
enum class TlsMode { Plain, Tls };

/// Rights a controller may hold on a port besides using it (bit mask).
enum ControllerRight : std::uint32_t { RightKillSwitch = 1, RightContest = 2 };

struct DeviceConfig {
    /// Serial: local port. Tcp: the agent connects to an instrument (or terminal server) that listens. TcpListen: the agent
    /// listens on host:port and the instrument connects to it (Ethernet instruments that act as TCP clients).
    enum class Kind { Serial, Tcp, TcpListen };
    Kind kind = Kind::Serial;
    // Serial
    std::string path;
    std::uint32_t baud_rate = 9600;
    std::uint8_t data_bits = 8;
    Parity parity = Parity::None;
    std::uint8_t stop_bits = 1;
    FlowControl flow_control = FlowControl::None;
    /// Modem lines asserted while the port is open (RTS is driven by the driver with RTS/CTS flow control).
    bool dtr = true;
    bool rts = true;
    // Tcp: remote endpoint; TcpListen: local address and port
    std::string host;
    std::uint16_t port = 0;
};

struct PortConfig {
    std::string port_id;
    std::string instrument_id;
    DeviceConfig device;
    std::uint32_t lease_ms = 15000;
    std::size_t max_queued_requests = 16;
    /// Maximum continuous tenure while other controllers wait; 0 disables the limit.
    std::uint32_t max_hold_ms = 0;
    std::map<std::string, int> controllers;
    /// Optional SHA-256 (lower-case hex, DER) of the client certificate that identifies a controller in TLS mode.
    std::map<std::string, std::string> controller_certificates;
    std::map<std::string, std::uint32_t> controller_rights;
    std::size_t max_write_bytes = 16 * 1024;
};

/// Who opens the connection and who may administer the agent (decided by the server).
struct ModeConfig {
    enum class Initiator { Server, Agent };
    Initiator initiator = Initiator::Server;
    /// Server address used when the agent initiates.
    std::string server_host;
    std::uint16_t server_port = 0;
    /// Only the server that configured the agent may administer it.
    bool restricted = true;
    /// Refuse plain-text administration (TLS with the agent certificate or the PSK is then required).
    bool admin_requires_tls = false;
};

/// Optional security features of the port semaphore (ADMINISTRACION-REMOTA, kill switch and contest).
struct SecurityConfig {
    bool kill_switch = false;
    bool contest = false;
    std::uint32_t quarantine_ms = 10000;
    std::uint32_t kill_cooldown_ms = 60000;
    std::uint32_t contest_block_ms = 600000;
    std::uint32_t handover_interval_ms = 5000;
    std::uint32_t handover_timeout_ms = 5000;
};

struct AgentConfig {
    std::string agent_id;
    std::string platform_profile;
    ModeConfig mode;
    std::string listen_host = "0.0.0.0";
    std::uint16_t listen_port = 0;
    TlsMode tls = TlsMode::Plain;
    TlsServerSettings tls_settings;
    std::uint32_t max_unit_bytes = 64 * 1024;
    std::uint32_t heartbeat_ms = 5000;
    SecurityConfig security;
    std::vector<PortConfig> ports;
};

/// CBOR form used by SetConfig/GetConfig and by the protected state. Private keys and PSKs are never encoded here:
/// TLS material travels only through SetTls.
cbor::Value encode_config(const AgentConfig& config);
/// Device map of a port (key 2 of a port), also announced in Capabilities so a controller can compare settings.
cbor::Value encode_device(const DeviceConfig& device);
/// Validates the whole configuration; on error nothing is returned and `error` names the field.
std::optional<AgentConfig> decode_config(const cbor::Value& value, std::string& error);

}  // namespace portgate
