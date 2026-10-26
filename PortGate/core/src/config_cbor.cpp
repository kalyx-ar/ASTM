// CBOR configuration (spec/admin-v1.md § Configuration). Unknown keys are rejected so a server typo never passes silently.
#include <algorithm>
#include <cctype>
#include <set>

#include "portgate/config.hpp"

namespace portgate {

namespace {

using cbor::Value;

class Reader {
public:
    explicit Reader(std::string& error) : error_(error) {}

    bool ok() const { return error_.empty(); }

    bool fail(const std::string& path, const std::string& message) {
        if (error_.empty()) error_ = path + ": " + message;
        return false;
    }

    const std::map<std::uint64_t, Value>* map(const Value& value, const std::string& path, std::uint64_t max_key) {
        const auto* m = value.as_map();
        if (!m) {
            fail(path, "expected a map");
            return nullptr;
        }
        for (const auto& [key, unused] : *m) {
            (void)unused;
            if (key > max_key) {
                fail(path + "/" + std::to_string(key), "unknown key");
                return nullptr;
            }
        }
        return m;
    }

    std::string text(const std::map<std::uint64_t, Value>& m, const std::string& path, std::uint64_t key, bool required, const std::string& fallback = {}) {
        auto it = m.find(key);
        if (it == m.end()) {
            if (required) fail(path + "/" + std::to_string(key), "required");
            return fallback;
        }
        const auto* t = it->second.as_text();
        if (!t) {
            fail(path + "/" + std::to_string(key), "expected text");
            return fallback;
        }
        return *t;
    }

    std::uint64_t number(const std::map<std::uint64_t, Value>& m, const std::string& path, std::uint64_t key, std::uint64_t fallback,
                         std::uint64_t min, std::uint64_t max) {
        auto it = m.find(key);
        if (it == m.end()) return fallback;
        const auto n = it->second.as_uint();
        if (!n) {
            fail(path + "/" + std::to_string(key), "expected an unsigned integer");
            return fallback;
        }
        if (*n < min || *n > max) {
            fail(path + "/" + std::to_string(key), "out of range [" + std::to_string(min) + ", " + std::to_string(max) + "]");
            return fallback;
        }
        return *n;
    }

    bool flag(const std::map<std::uint64_t, Value>& m, const std::string& path, std::uint64_t key, bool fallback) {
        auto it = m.find(key);
        if (it == m.end()) return fallback;
        const auto b = it->second.as_bool();
        if (!b) {
            fail(path + "/" + std::to_string(key), "expected a boolean");
            return fallback;
        }
        return *b;
    }

private:
    std::string& error_;
};

bool read_device(Reader& r, const Value& v, const std::string& path, DeviceConfig& device) {
    const auto* m = r.map(v, path, 8);
    if (!m) return false;
    const auto kind = r.number(*m, path, 0, 0, 0, 1);
    if (kind == 0) {
        device.kind = DeviceConfig::Kind::Serial;
        device.path = r.text(*m, path, 1, true);
        device.baud_rate = static_cast<std::uint32_t>(r.number(*m, path, 2, 9600, 50, 4000000));
        device.data_bits = static_cast<std::uint8_t>(r.number(*m, path, 3, 8, 5, 8));
        device.parity = static_cast<Parity>(r.number(*m, path, 4, 0, 0, 4));
        device.stop_bits = static_cast<std::uint8_t>(r.number(*m, path, 5, 1, 1, 2));
        device.flow_control = static_cast<FlowControl>(r.number(*m, path, 6, 0, 0, 2));
        if (r.ok() && device.path.empty()) return r.fail(path + "/1", "required");
    } else {
        device.kind = DeviceConfig::Kind::Tcp;
        device.host = r.text(*m, path, 7, true);
        device.port = static_cast<std::uint16_t>(r.number(*m, path, 8, 0, 1, 65535));
        if (r.ok() && device.port == 0) return r.fail(path + "/8", "required");
    }
    return r.ok();
}

bool read_port(Reader& r, const Value& v, const std::string& path, PortConfig& port) {
    const auto* m = r.map(v, path, 7);
    if (!m) return false;
    port.port_id = r.text(*m, path, 0, true);
    port.instrument_id = r.text(*m, path, 1, false);
    auto device = m->find(2);
    if (device == m->end()) return r.fail(path + "/2", "required");
    if (!read_device(r, device->second, path + "/2", port.device)) return false;
    port.lease_ms = static_cast<std::uint32_t>(r.number(*m, path, 3, 15000, 1000, 3600000));
    port.max_queued_requests = static_cast<std::size_t>(r.number(*m, path, 4, 16, 0, 1024));
    port.max_hold_ms = static_cast<std::uint32_t>(r.number(*m, path, 5, 0, 0, 86400000));
    if (port.max_hold_ms != 0 && port.max_hold_ms < port.lease_ms) return r.fail(path + "/5", "must be 0 or at least the lease");
    port.max_write_bytes = static_cast<std::size_t>(r.number(*m, path, 7, 16 * 1024, 1, 1024 * 1024));
    auto controllers = m->find(6);
    if (controllers != m->end()) {
        const auto* list = controllers->second.as_array();
        if (!list) return r.fail(path + "/6", "expected an array");
        for (std::size_t i = 0; i < list->size(); ++i) {
            const auto cp = path + "/6[" + std::to_string(i) + "]";
            const auto* c = r.map((*list)[i], cp, 3);
            if (!c) return false;
            const auto id = r.text(*c, cp, 0, true);
            const auto priority = static_cast<int>(r.number(*c, cp, 1, 0, 0, 1000000));
            if (!r.ok()) return false;
            if (id.empty()) return r.fail(cp + "/0", "required");
            if (!port.controllers.emplace(id, priority).second) return r.fail(cp + "/0", "duplicate controller");
            auto fingerprint = r.text(*c, cp, 2, false);
            if (!fingerprint.empty()) {
                for (auto& ch : fingerprint) ch = static_cast<char>(std::tolower(static_cast<unsigned char>(ch)));
                fingerprint.erase(std::remove(fingerprint.begin(), fingerprint.end(), ':'), fingerprint.end());
                if (fingerprint.size() != 64 || fingerprint.find_first_not_of("0123456789abcdef") != std::string::npos)
                    return r.fail(cp + "/2", "expected 64 hex digits");
                port.controller_certificates[id] = fingerprint;
            }
            const auto rights = static_cast<std::uint32_t>(r.number(*c, cp, 3, 0, 0, RightKillSwitch | RightContest));
            if (rights != 0) port.controller_rights[id] = rights;
        }
    }
    return r.ok();
}

}  // namespace

Value encode_config(const AgentConfig& config) {
    std::vector<Value> ports;
    for (const auto& port : config.ports) {
        std::map<std::uint64_t, Value> device;
        device.emplace(0, Value::uint(port.device.kind == DeviceConfig::Kind::Serial ? 0 : 1));
        if (port.device.kind == DeviceConfig::Kind::Serial) {
            device.emplace(1, Value::text(port.device.path));
            device.emplace(2, Value::uint(port.device.baud_rate));
            device.emplace(3, Value::uint(port.device.data_bits));
            device.emplace(4, Value::uint(static_cast<std::uint64_t>(port.device.parity)));
            device.emplace(5, Value::uint(port.device.stop_bits));
            device.emplace(6, Value::uint(static_cast<std::uint64_t>(port.device.flow_control)));
        } else {
            device.emplace(7, Value::text(port.device.host));
            device.emplace(8, Value::uint(port.device.port));
        }
        std::vector<Value> controllers;
        for (const auto& [id, priority] : port.controllers) {
            std::map<std::uint64_t, Value> c{{0, Value::text(id)}, {1, Value::uint(static_cast<std::uint64_t>(priority))}};
            auto certificate = port.controller_certificates.find(id);
            if (certificate != port.controller_certificates.end()) c.emplace(2, Value::text(certificate->second));
            auto rights = port.controller_rights.find(id);
            if (rights != port.controller_rights.end()) c.emplace(3, Value::uint(rights->second));
            controllers.push_back(Value::map(std::move(c)));
        }
        ports.push_back(Value::map({{0, Value::text(port.port_id)},
                                    {1, Value::text(port.instrument_id)},
                                    {2, Value::map(std::move(device))},
                                    {3, Value::uint(port.lease_ms)},
                                    {4, Value::uint(port.max_queued_requests)},
                                    {5, Value::uint(port.max_hold_ms)},
                                    {6, Value::array(std::move(controllers))},
                                    {7, Value::uint(port.max_write_bytes)}}));
    }
    std::map<std::uint64_t, Value> mode{{0, Value::uint(config.mode.initiator == ModeConfig::Initiator::Server ? 0 : 1)},
                                        {3, Value::boolean(config.mode.restricted)},
                                        {4, Value::boolean(config.mode.admin_requires_tls)}};
    if (!config.mode.server_host.empty()) mode.emplace(1, Value::text(config.mode.server_host));
    if (config.mode.server_port != 0) mode.emplace(2, Value::uint(config.mode.server_port));
    const auto& s = config.security;
    return Value::map({{0, Value::text(config.agent_id)},
                       {1, Value::text(config.platform_profile)},
                       {2, Value::map(std::move(mode))},
                       {3, Value::map({{0, Value::text(config.listen_host)},
                                       {1, Value::uint(config.listen_port)},
                                       {2, Value::boolean(config.tls == TlsMode::Tls)},
                                       {3, Value::boolean(config.tls_settings.require_client_certificate)},
                                       {4, Value::boolean(config.tls_settings.minimum_tls13)}})},
                       {4, Value::uint(config.heartbeat_ms)},
                       {5, Value::uint(config.max_unit_bytes)},
                       {6, Value::map({{0, Value::boolean(s.kill_switch)},
                                       {1, Value::boolean(s.contest)},
                                       {2, Value::uint(s.quarantine_ms)},
                                       {3, Value::uint(s.kill_cooldown_ms)},
                                       {4, Value::uint(s.contest_block_ms)},
                                       {5, Value::uint(s.handover_interval_ms)},
                                       {6, Value::uint(s.handover_timeout_ms)}})},
                       {7, Value::array(std::move(ports))}});
}

std::optional<AgentConfig> decode_config(const Value& value, std::string& error) {
    Reader r(error);
    AgentConfig config;
    const auto* m = r.map(value, "config", 7);
    if (!m) return std::nullopt;
    config.agent_id = r.text(*m, "config", 0, true);
    if (r.ok() && config.agent_id.empty()) r.fail("config/0", "required");
    config.platform_profile = r.text(*m, "config", 1, false, "unspecified");
    config.heartbeat_ms = static_cast<std::uint32_t>(r.number(*m, "config", 4, 5000, 500, 600000));
    config.max_unit_bytes = static_cast<std::uint32_t>(r.number(*m, "config", 5, 64 * 1024, 1024, 16 * 1024 * 1024));

    if (auto mode = m->find(2); mode != m->end() && r.ok()) {
        if (const auto* mm = r.map(mode->second, "config/2", 4)) {
            config.mode.initiator = r.number(*mm, "config/2", 0, 0, 0, 1) == 0 ? ModeConfig::Initiator::Server : ModeConfig::Initiator::Agent;
            config.mode.server_host = r.text(*mm, "config/2", 1, false);
            config.mode.server_port = static_cast<std::uint16_t>(r.number(*mm, "config/2", 2, 0, 0, 65535));
            config.mode.restricted = r.flag(*mm, "config/2", 3, true);
            config.mode.admin_requires_tls = r.flag(*mm, "config/2", 4, false);
            if (r.ok() && config.mode.initiator == ModeConfig::Initiator::Agent && (config.mode.server_host.empty() || config.mode.server_port == 0))
                r.fail("config/2", "the agent-initiated mode needs the server host and port");
        }
    }
    if (auto link = m->find(3); link != m->end() && r.ok()) {
        if (const auto* lm = r.map(link->second, "config/3", 4)) {
            config.listen_host = r.text(*lm, "config/3", 0, false, "0.0.0.0");
            config.listen_port = static_cast<std::uint16_t>(r.number(*lm, "config/3", 1, 0, 0, 65535));
            config.tls = r.flag(*lm, "config/3", 2, false) ? TlsMode::Tls : TlsMode::Plain;
            config.tls_settings.require_client_certificate = r.flag(*lm, "config/3", 3, true);
            config.tls_settings.minimum_tls13 = r.flag(*lm, "config/3", 4, false);
        }
    }
    if (auto security = m->find(6); security != m->end() && r.ok()) {
        if (const auto* sm = r.map(security->second, "config/6", 6)) {
            auto& s = config.security;
            s.kill_switch = r.flag(*sm, "config/6", 0, false);
            s.contest = r.flag(*sm, "config/6", 1, false);
            s.quarantine_ms = static_cast<std::uint32_t>(r.number(*sm, "config/6", 2, 10000, 0, 3600000));
            s.kill_cooldown_ms = static_cast<std::uint32_t>(r.number(*sm, "config/6", 3, 60000, 1000, 86400000));
            s.contest_block_ms = static_cast<std::uint32_t>(r.number(*sm, "config/6", 4, 600000, 1000, 604800000));
            s.handover_interval_ms = static_cast<std::uint32_t>(r.number(*sm, "config/6", 5, 5000, 0, 3600000));
            s.handover_timeout_ms = static_cast<std::uint32_t>(r.number(*sm, "config/6", 6, 5000, 100, 600000));
        }
    }
    if (auto ports = m->find(7); ports != m->end() && r.ok()) {
        const auto* list = ports->second.as_array();
        if (!list) {
            r.fail("config/7", "expected an array");
        } else {
            std::set<std::string> ids;
            for (std::size_t i = 0; i < list->size() && r.ok(); ++i) {
                PortConfig port;
                const auto path = "config/7[" + std::to_string(i) + "]";
                if (!read_port(r, (*list)[i], path, port)) break;
                if (!ids.insert(port.port_id).second) r.fail(path + "/0", "duplicate port");
                config.ports.push_back(std::move(port));
            }
        }
    }
    if (!r.ok()) return std::nullopt;
    return config;
}

}  // namespace portgate
