// Agent configuration (schema "atsm-agent/1"). Unknown keys are rejected so typos never pass silently.
#pragma once

#include <cstdint>
#include <map>
#include <optional>
#include <string>
#include <vector>

#include "atsm/transport_settings.hpp"

namespace atsm {

enum class Parity { None, Odd, Even, Mark, Space };
enum class FlowControl { None, RtsCts, XonXoff };
enum class TlsMode { Plain, Tls };

struct DeviceConfig {
    enum class Kind { Serial, Tcp };
    Kind kind = Kind::Serial;
    // Serial
    std::string path;
    std::uint32_t baud_rate = 9600;
    std::uint8_t data_bits = 8;
    Parity parity = Parity::None;
    std::uint8_t stop_bits = 1;
    FlowControl flow_control = FlowControl::None;
    // Serial over TCP (terminal servers, simulators)
    std::string host;
    std::uint16_t port = 0;
};

struct PortConfig {
    std::string port_id;
    std::string instrument_id;
    DeviceConfig device;
    std::uint32_t lease_ms = 15000;
    std::size_t max_queued_requests = 16;
    std::map<std::string, int> controllers;
    /// Optional SHA-256 (lower-case hex, DER) of the client certificate that identifies a controller in TLS mode.
    std::map<std::string, std::string> controller_certificates;
    std::size_t max_write_bytes = 16 * 1024;
};

struct AgentConfig {
    std::string agent_id;
    std::string platform_profile;
    std::string listen_host = "0.0.0.0";
    std::uint16_t listen_port = 0;
    TlsMode tls = TlsMode::Plain;
    TlsServerSettings tls_settings;
    std::uint32_t max_unit_bytes = 64 * 1024;
    std::uint32_t heartbeat_ms = 5000;
    std::vector<PortConfig> ports;
};

std::optional<AgentConfig> parse_config(const std::string& text, std::string& error);

}  // namespace atsm
