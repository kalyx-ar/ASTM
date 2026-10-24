#include "atsm/config.hpp"

#include <algorithm>
#include <cctype>
#include <cmath>
#include <set>

#include "atsm/json.hpp"

namespace atsm {

namespace {

using json::Value;

class Reader {
public:
    explicit Reader(std::string& error) : error_(error) {}

    bool ok() const { return error_.empty(); }

    bool fail(const std::string& path, const std::string& message) {
        if (error_.empty()) error_ = path + ": " + message;
        return false;
    }

    bool only(const Value& object, const std::string& path, std::set<std::string> allowed) {
        if (object.kind != Value::Kind::Object) return fail(path, "expected an object");
        for (const auto& [key, unused] : object.object) {
            (void)unused;
            if (allowed.count(key) == 0) return fail(path + "." + key, "unknown key");
        }
        return true;
    }

    std::string text(const Value& object, const std::string& path, const std::string& key, bool required, const std::string& fallback = {}) {
        const Value* v = object.find(key);
        if (!v) {
            if (required) fail(path + "." + key, "required");
            return fallback;
        }
        if (v->kind != Value::Kind::String) {
            fail(path + "." + key, "expected a string");
            return fallback;
        }
        return v->string;
    }

    std::uint64_t number(const Value& object, const std::string& path, const std::string& key, std::uint64_t fallback, std::uint64_t min, std::uint64_t max) {
        const Value* v = object.find(key);
        if (!v) return fallback;
        if (v->kind != Value::Kind::Number || v->number < 0 || std::floor(v->number) != v->number) {
            fail(path + "." + key, "expected a non-negative integer");
            return fallback;
        }
        const auto n = static_cast<std::uint64_t>(v->number);
        if (n < min || n > max) {
            fail(path + "." + key, "out of range");
            return fallback;
        }
        return n;
    }

private:
    std::string& error_;
};

bool read_device(Reader& r, const Value& v, const std::string& path, DeviceConfig& device) {
    if (!r.only(v, path, {"kind", "path", "baudRate", "dataBits", "parity", "stopBits", "flowControl", "host", "port"})) return false;
    const auto kind = r.text(v, path, "kind", true);
    if (kind == "serial") {
        device.kind = DeviceConfig::Kind::Serial;
        device.path = r.text(v, path, "path", true);
        device.baud_rate = static_cast<std::uint32_t>(r.number(v, path, "baudRate", 9600, 50, 4000000));
        device.data_bits = static_cast<std::uint8_t>(r.number(v, path, "dataBits", 8, 5, 8));
        device.stop_bits = static_cast<std::uint8_t>(r.number(v, path, "stopBits", 1, 1, 2));
        const auto parity = r.text(v, path, "parity", false, "None");
        if (parity == "None") device.parity = Parity::None;
        else if (parity == "Odd") device.parity = Parity::Odd;
        else if (parity == "Even") device.parity = Parity::Even;
        else if (parity == "Mark") device.parity = Parity::Mark;
        else if (parity == "Space") device.parity = Parity::Space;
        else return r.fail(path + ".parity", "expected None, Odd, Even, Mark or Space");
        const auto flow = r.text(v, path, "flowControl", false, "None");
        if (flow == "None") device.flow_control = FlowControl::None;
        else if (flow == "RtsCts") device.flow_control = FlowControl::RtsCts;
        else if (flow == "XonXoff") device.flow_control = FlowControl::XonXoff;
        else return r.fail(path + ".flowControl", "expected None, RtsCts or XonXoff");
    } else if (kind == "tcp") {
        device.kind = DeviceConfig::Kind::Tcp;
        device.host = r.text(v, path, "host", true);
        device.port = static_cast<std::uint16_t>(r.number(v, path, "port", 0, 1, 65535));
        if (device.port == 0) return r.fail(path + ".port", "required");
    } else {
        return r.fail(path + ".kind", "expected serial or tcp");
    }
    return r.ok();
}

bool read_port(Reader& r, const Value& v, const std::string& path, PortConfig& port) {
    if (!r.only(v, path, {"portId", "instrumentId", "device", "ownership", "buffers"})) return false;
    port.port_id = r.text(v, path, "portId", true);
    port.instrument_id = r.text(v, path, "instrumentId", true);
    const Value* device = v.find("device");
    if (!device) return r.fail(path + ".device", "required");
    if (!read_device(r, *device, path + ".device", port.device)) return false;
    if (const Value* ownership = v.find("ownership")) {
        const auto p = path + ".ownership";
        if (!r.only(*ownership, p, {"leaseMs", "maxQueuedRequests", "controllers"})) return false;
        port.lease_ms = static_cast<std::uint32_t>(r.number(*ownership, p, "leaseMs", 15000, 1000, 3600000));
        port.max_queued_requests = static_cast<std::size_t>(r.number(*ownership, p, "maxQueuedRequests", 16, 0, 1024));
        if (const Value* controllers = ownership->find("controllers")) {
            if (controllers->kind != Value::Kind::Array) return r.fail(p + ".controllers", "expected an array");
            for (std::size_t i = 0; i < controllers->array.size(); ++i) {
                const auto cp = p + ".controllers[" + std::to_string(i) + "]";
                const Value& c = controllers->array[i];
                if (!r.only(c, cp, {"id", "priority", "certificateSha256"})) return false;
                const auto id = r.text(c, cp, "id", true);
                const auto priority = static_cast<int>(r.number(c, cp, "priority", 0, 0, 1000000));
                if (!port.controllers.emplace(id, priority).second) return r.fail(cp + ".id", "duplicate controller");
                if (c.find("certificateSha256")) {
                    auto fingerprint = r.text(c, cp, "certificateSha256", true);
                    for (auto& ch : fingerprint) ch = static_cast<char>(std::tolower(static_cast<unsigned char>(ch)));
                    fingerprint.erase(std::remove(fingerprint.begin(), fingerprint.end(), ':'), fingerprint.end());
                    if (fingerprint.size() != 64) return r.fail(cp + ".certificateSha256", "expected 64 hex digits");
                    port.controller_certificates[id] = fingerprint;
                }
            }
        }
    }
    if (const Value* buffers = v.find("buffers")) {
        const auto p = path + ".buffers";
        if (!r.only(*buffers, p, {"maxWriteBytes"})) return false;
        port.max_write_bytes = static_cast<std::size_t>(r.number(*buffers, p, "maxWriteBytes", 16 * 1024, 1, 1024 * 1024));
    }
    return r.ok();
}

}  // namespace

std::optional<AgentConfig> parse_config(const std::string& text, std::string& error) {
    json::ParseError parse_error;
    auto document = json::parse(text, parse_error);
    if (!document) {
        error = "invalid JSON at offset " + std::to_string(parse_error.offset) + ": " + parse_error.message;
        return std::nullopt;
    }
    Reader r(error);
    AgentConfig config;
    if (!r.only(*document, "$", {"schemaVersion", "agentId", "platformProfile", "listen", "maxUnitBytes", "heartbeatMs", "ports"})) return std::nullopt;
    if (r.text(*document, "$", "schemaVersion", true) != "atsm-agent/1") {
        r.fail("$.schemaVersion", "expected atsm-agent/1");
        return std::nullopt;
    }
    config.agent_id = r.text(*document, "$", "agentId", true);
    config.platform_profile = r.text(*document, "$", "platformProfile", false, "unspecified");
    config.max_unit_bytes = static_cast<std::uint32_t>(r.number(*document, "$", "maxUnitBytes", 64 * 1024, 1024, 16 * 1024 * 1024));
    config.heartbeat_ms = static_cast<std::uint32_t>(r.number(*document, "$", "heartbeatMs", 5000, 500, 600000));

    const Value* listen = document->find("listen");
    if (!listen) {
        r.fail("$.listen", "required");
        return std::nullopt;
    }
    if (!r.only(*listen, "$.listen", {"host", "port", "tls"})) return std::nullopt;
    config.listen_host = r.text(*listen, "$.listen", "host", false, "0.0.0.0");
    if (!listen->find("port")) r.fail("$.listen.port", "required (0 selects an ephemeral port)");
    config.listen_port = static_cast<std::uint16_t>(r.number(*listen, "$.listen", "port", 0, 0, 65535));
    const Value* tls = listen->find("tls");
    if (!tls || (tls->kind == Value::Kind::String && tls->string == "Plain")) {
        config.tls = TlsMode::Plain;
    } else if (tls->kind == Value::Kind::Object) {
        const std::string p = "$.listen.tls";
        if (!r.only(*tls, p, {"mode", "certificate", "privateKey", "privateKeyPassword", "clientCa", "requireClientCertificate", "minimumVersion"})) return std::nullopt;
        if (r.text(*tls, p, "mode", true) != "Tls") r.fail(p + ".mode", "expected Tls (use \"tls\": \"Plain\" for plain text)");
        config.tls = TlsMode::Tls;
        auto& s = config.tls_settings;
        s.certificate_file = r.text(*tls, p, "certificate", true);
        s.private_key_file = r.text(*tls, p, "privateKey", true);
        s.private_key_password = r.text(*tls, p, "privateKeyPassword", false);
        s.client_ca_file = r.text(*tls, p, "clientCa", false);
        if (const Value* require = tls->find("requireClientCertificate")) {
            if (require->kind != Value::Kind::Bool) r.fail(p + ".requireClientCertificate", "expected a boolean");
            s.require_client_certificate = require->boolean;
        }
        const auto minimum = r.text(*tls, p, "minimumVersion", false, "1.2");
        if (minimum == "1.3") s.minimum_tls13 = true;
        else if (minimum != "1.2") r.fail(p + ".minimumVersion", "expected 1.2 or 1.3");
        if (s.require_client_certificate && s.client_ca_file.empty()) r.fail(p + ".clientCa", "required when requireClientCertificate is true");
    } else {
        r.fail("$.listen.tls", "expected \"Plain\" or a TLS object");
    }

    const Value* ports = document->find("ports");
    if (!ports || ports->kind != Value::Kind::Array || ports->array.empty()) {
        r.fail("$.ports", "expected a non-empty array");
        return std::nullopt;
    }
    std::set<std::string> ids;
    for (std::size_t i = 0; i < ports->array.size() && r.ok(); ++i) {
        PortConfig port;
        const auto path = "$.ports[" + std::to_string(i) + "]";
        if (!read_port(r, ports->array[i], path, port)) break;
        if (!ids.insert(port.port_id).second) r.fail(path + ".portId", "duplicate port");
        config.ports.push_back(std::move(port));
    }
    if (!r.ok()) return std::nullopt;
    return config;
}

}  // namespace atsm
