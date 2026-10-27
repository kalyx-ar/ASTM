// Native tests without external frameworks: unit tests of the core and an end-to-end agent test over loopback TCP.
#include <algorithm>
#include <atomic>
#include <filesystem>
#include <mutex>
#include <chrono>
#include <cstdio>
#include <fstream>
#include <functional>
#include <iostream>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

#include "portgate/agent.hpp"
#include "portgate/arbiter.hpp"
#include "portgate/cbor.hpp"
#include "portgate/capture.hpp"
#include "portgate/config.hpp"
#include "portgate/link.hpp"
#include "portgate/state.hpp"
#include "portgate/supervisor.hpp"
#include "portgate/transport.hpp"

extern "C" {
#include "mbedtls/ssl.h"
}

using namespace portgate;
using cbor::Value;

namespace {

int g_failures = 0;
int g_checks = 0;

#define CHECK(condition)                                                                   \
    do {                                                                                   \
        ++g_checks;                                                                        \
        if (!(condition)) {                                                                \
            ++g_failures;                                                                  \
            std::cerr << __FILE__ << ":" << __LINE__ << ": CHECK failed: " #condition "\n"; \
        }                                                                                  \
    } while (0)

std::string hex(const std::vector<std::uint8_t>& bytes) {
    static const char* digits = "0123456789abcdef";
    std::string out;
    for (auto b : bytes) {
        out.push_back(digits[b >> 4]);
        out.push_back(digits[b & 15]);
    }
    return out;
}

Id128 counting_id() {
    Id128 id{};
    for (std::size_t i = 0; i < id.size(); ++i) id[i] = static_cast<std::uint8_t>(i);
    return id;
}

/// Canonical units shared with the .NET implementation (conformance/link-v1/vectors.txt).
std::vector<std::pair<std::string, link::Unit>> vectors() {
    const auto id = counting_id();
    const Value transfer = Value::bytes(cbor::Bytes(id.begin(), id.end()));
    link::SerialData data;
    data.seq = 2;
    data.offset = 300;
    data.generation = 3;
    data.data = {0x05};
    return {
        {"hello-controller", link::control(link::Type::Hello, 0, Value::map({{0, Value::uint(1)}, {1, Value::text("controller")}, {2, Value::text("edc-server-a")}}))},
        {"acquire", link::control(link::Type::Acquire, 1, Value::map({{0, Value::uint(7)}, {1, Value::integer(10)}, {2, Value::uint(5000)}}))},
        {"granted", link::control(link::Type::Granted, 1, Value::map({{0, Value::uint(7)}, {1, Value::uint(3)}, {2, transfer}, {3, Value::uint(15000)}, {4, Value::uint(0)}, {5, Value::boolean(false)}, {6, Value::uint(12)}}))},
        {"confirm-transfer", link::control(link::Type::ConfirmTransfer, 1, Value::map({{1, Value::uint(3)}, {2, transfer}}))},
        {"serial-data", link::serial_data(1, data)},
        {"write-status", link::control(link::Type::WriteStatus, 1, Value::map({{0, Value::uint(2)}, {1, Value::uint(1)}, {2, Value::uint(1)}, {3, Value::uint(0)}}))},
        {"denied", link::control(link::Type::Denied, 1, Value::map({{0, Value::uint(8)}, {1, Value::text("PORTGATE-QUEUE-FULL")}}))},
        {"heartbeat", link::control(link::Type::Heartbeat, 0, Value::map({{0, Value::uint(4294967296ull)}}))},
        {"error-negative", link::control(link::Type::Error, 0, Value::map({{0, Value::text("PORTGATE-X")}, {1, Value::integer(-500)}}))},
    };
}

void test_cbor() {
    const Value original = Value::map({{0, Value::uint(23)}, {1, Value::uint(24)}, {2, Value::uint(65536)}, {3, Value::integer(-25)},
                                       {4, Value::text("ok")}, {5, Value::bytes({1, 2})}, {6, Value::array({Value::boolean(true), Value::null()})}});
    const auto bytes = cbor::encode(original);
    CHECK(hex(bytes) == "a70017011818021a000100000338180462" "6f6b05420102068" "2f5f6");
    auto decoded = cbor::decode(bytes.data(), bytes.size());
    CHECK(decoded && cbor::encode(*decoded) == bytes);
    CHECK(decoded && decoded->get(3)->as_int() == -25);

    const std::vector<std::uint8_t> indefinite = {0x9F, 0x01, 0xFF};
    CHECK(!cbor::decode(indefinite.data(), indefinite.size()));
    const std::vector<std::uint8_t> truncated = {0x62, 'o'};
    CHECK(!cbor::decode(truncated.data(), truncated.size()));
    const std::vector<std::uint8_t> huge_array = {0x9B, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF};
    CHECK(!cbor::decode(huge_array.data(), huge_array.size()));
    const std::vector<std::uint8_t> duplicate = {0xA2, 0x00, 0x01, 0x00, 0x02};
    CHECK(!cbor::decode(duplicate.data(), duplicate.size()));
}

void test_link_decoder() {
    const auto units = vectors();
    std::vector<std::uint8_t> stream;
    for (const auto& [name, unit] : units) {
        (void)name;
        const auto bytes = link::encode(unit);
        stream.insert(stream.end(), bytes.begin(), bytes.end());
    }
    // Every split of the stream must produce the same units.
    for (std::size_t split = 0; split <= stream.size(); split += 7) {
        link::Decoder decoder;
        std::vector<link::Unit> out;
        CHECK(decoder.feed(stream.data(), split, out) == link::DecodeError::None);
        CHECK(decoder.feed(stream.data() + split, stream.size() - split, out) == link::DecodeError::None);
        CHECK(out.size() == units.size());
        if (out.size() == units.size()) {
            for (std::size_t i = 0; i < out.size(); ++i) CHECK(link::encode(out[i]) == link::encode(units[i].second));
        }
    }
    auto serial = link::parse_serial_data(units[4].second);
    CHECK(serial && serial->seq == 2 && serial->offset == 300 && serial->generation == 3 && serial->data.size() == 1);

    link::Decoder small(16);
    std::vector<link::Unit> out;
    const std::vector<std::uint8_t> too_large = {0x41, 0x4C, 0x01, 0x20, 0, 0, 0, 1, 0, 0, 0, 17};
    CHECK(small.feed(too_large.data(), too_large.size(), out) == link::DecodeError::TooLarge);
    link::Decoder bad;
    const std::vector<std::uint8_t> bad_magic = {0x41, 0x41, 0x01, 0x20, 0, 0, 0, 1, 0, 0, 0, 0};
    CHECK(bad.feed(bad_magic.data(), bad_magic.size(), out) == link::DecodeError::BadMagic);
}

void test_vectors_file() {
    std::ifstream file(PORTGATE_VECTORS);
    CHECK(static_cast<bool>(file));
    std::map<std::string, std::string> expected;
    std::string line;
    while (std::getline(file, line)) {
        if (line.empty() || line[0] == '#') continue;
        const auto first = line.find(' ');
        if (first == std::string::npos) continue;
        expected[line.substr(0, first)] = line.substr(line.rfind(' ') + 1);
    }
    for (const auto& [name, unit] : vectors()) {
        const auto it = expected.find(name);
        CHECK(it != expected.end());
        if (it != expected.end() && it->second != hex(link::encode(unit))) {
            std::cerr << "vector " << name << " differs: " << hex(link::encode(unit)) << "\n";
            CHECK(false);
        }
    }
}

void test_arbiter() {
    Arbiter arbiter(1000, 2, {{"a", 10}, {"b", 20}, {"local", 100}}, 42);
    std::vector<ArbiterEvent> events;
    arbiter.acquire(1, "a", 1, 0, 0, events);
    CHECK(events.size() == 1 && events[0].kind == ArbiterEvent::Kind::Granted && events[0].generation == 1);
    const auto transfer = events[0].transfer_id;
    CHECK(!arbiter.may_write(1, 1));  // not admitted before ConfirmTransfer
    CHECK(!arbiter.confirm(1, 1, Id128{}));
    CHECK(arbiter.confirm(1, 1, transfer));
    CHECK(arbiter.may_write(1, 1));

    events.clear();
    arbiter.acquire(3, "local", 2, 0, 10, events);
    arbiter.acquire(2, "b", 3, 0, 20, events);
    CHECK(events.size() == 2 && events[0].kind == ArbiterEvent::Kind::Queued && events[1].kind == ArbiterEvent::Kind::Queued);
    CHECK(events[1].position == 1);  // b has higher priority than local, but never preempts a
    events.clear();
    arbiter.acquire(4, "unknown", 4, 0, 20, events);
    CHECK(events.size() == 1 && events[0].code == "PORTGATE-CONTROLLER-NOT-AUTHORIZED");
    events.clear();
    arbiter.acquire(5, "a", 5, 0, 20, events);
    CHECK(events.size() == 1 && events[0].code == "PORTGATE-QUEUE-FULL");

    events.clear();
    arbiter.renew(1, 6, 1, 500, events);
    CHECK(events.size() == 1 && events[0].kind == ArbiterEvent::Kind::Granted);
    events.clear();
    arbiter.tick(1499, events);
    CHECK(events.empty());
    arbiter.tick(1500, events);
    CHECK(events.size() == 2 && events[0].kind == ArbiterEvent::Kind::Revoked && events[1].kind == ArbiterEvent::Kind::Granted);
    CHECK(events[1].connection == 2 && events[1].generation == 2);
    CHECK(!arbiter.may_write(1, 1));  // late write of the expired owner

    events.clear();
    arbiter.disconnect(2, 1600, events);
    CHECK(events.size() == 1 && events[0].connection == 3 && events[0].generation == 3);

    Arbiter deadlines(1000, 4, {}, 1);
    events.clear();
    deadlines.acquire(1, "x", 1, 0, 0, events);
    deadlines.acquire(2, "y", 2, 50, 0, events);
    events.clear();
    deadlines.tick(49, events);
    CHECK(events.empty());
    deadlines.tick(50, events);
    CHECK(events.size() == 1 && events[0].code == "PORTGATE-QUEUE-TIMEOUT");
}

void test_tls_material() {
    AgentConfig config;
    config.agent_id = "tls";
    config.listen_host = "127.0.0.1";
    config.tls = TlsMode::Tls;
    std::string error;
    {
        Agent agent(config, [](LogLevel, const std::string&) {});
        CHECK(!agent.start(error) && error.find("certificate and key or a pre-shared key") != std::string::npos);
    }
    TlsMaterial broken;
    broken.certificate_pem = "-----BEGIN CERTIFICATE-----\nnot a certificate\n-----END CERTIFICATE-----\n";
    broken.private_key_pem = "not a key";
    error.clear();
    {
        Agent agent(config, [](LogLevel, const std::string&) {}, broken);
        CHECK(!agent.start(error) && !error.empty());
    }
    TlsMaterial generated;
    CHECK(tls::generate_self_signed("unit", generated, error));
    CHECK(tls::certificate_sha256(generated.certificate_pem).size() == 64);
    error.clear();
    {
        // Client certificates required but no CA to verify them: refused instead of accepting anybody.
        Agent agent(config, [](LogLevel, const std::string&) {}, generated);
        CHECK(!agent.start(error) && error.find("client CA") != std::string::npos);
    }
    config.tls_settings.require_client_certificate = false;
    error.clear();
    {
        Agent agent(config, [](LogLevel, const std::string&) {}, generated);
        CHECK(platform::network_startup(error));
        CHECK(agent.start(error));
        agent.shutdown();
        platform::network_cleanup();
    }
}

/// Fake instrument: a TCP server that the agent uses as its "serial" device.
struct FakeInstrument {
    platform::Listener listener;
    platform::Socket socket;

    bool start(std::string& error) { return listener.open("127.0.0.1", 0, error); }
    bool accept(int timeout_ms) {
        socket = listener.accept(timeout_ms);
        return socket.valid();
    }
};

struct Client {
    platform::Socket socket;
    link::Decoder decoder;
    std::vector<link::Unit> pending;

    bool send(const link::Unit& unit) {
        const auto bytes = link::encode(unit);
        return socket.send_all(bytes.data(), bytes.size());
    }

    std::optional<link::Unit> next(link::Type type, int timeout_ms = 3000) {
        const auto deadline = platform::monotonic_ms() + static_cast<std::uint64_t>(timeout_ms);
        while (platform::monotonic_ms() < deadline) {
            for (auto it = pending.begin(); it != pending.end(); ++it) {
                if (it->type == type) {
                    auto unit = std::move(*it);
                    pending.erase(it);
                    return unit;
                }
            }
            std::uint8_t buffer[4096];
            const int n = socket.receive(buffer, sizeof(buffer), 100);
            if (n == -2) continue;
            if (n <= 0) return std::nullopt;
            decoder.feed(buffer, static_cast<std::size_t>(n), pending);
        }
        return std::nullopt;
    }
};

void test_agent_end_to_end() {
    std::string error;
    CHECK(platform::network_startup(error));
    FakeInstrument instrument;
    CHECK(instrument.start(error));

    AgentConfig config;
    config.agent_id = "test-agent";
    config.platform_profile = "test";
    config.listen_host = "127.0.0.1";
    config.listen_port = 0;
    config.heartbeat_ms = 1000;
    PortConfig port;
    port.port_id = "p1";
    port.instrument_id = "i1";
    port.device.kind = DeviceConfig::Kind::Tcp;
    port.device.host = "127.0.0.1";
    port.device.port = instrument.listener.port();
    port.lease_ms = 5000;
    port.controllers = {{"server-a", 10}, {"server-b", 20}};
    config.ports.push_back(port);

    Agent agent(config, [](LogLevel, const std::string&) {});
    CHECK(agent.start(error));
    std::atomic<bool> stop{false};
    std::thread housekeeping([&] { agent.run(stop); });
    CHECK(instrument.accept(3000));

    auto connect = [&](const std::string& id) {
        Client client;
        auto socket = platform::Socket::connect("127.0.0.1", agent.listen_port(), 3000, error);
        if (socket) client.socket = std::move(*socket);
        client.send(link::control(link::Type::Hello, 0, Value::map({{0, Value::uint(1)}, {1, Value::text("controller")}, {2, Value::text(id)}})));
        return client;
    };

    Client a = connect("server-a");
    auto hello = a.next(link::Type::Hello);
    CHECK(hello && link::parse_map(*hello)->get(3)->as_bytes()->size() == 16);
    auto capabilities = a.next(link::Type::Capabilities);
    CHECK(capabilities && link::parse_map(*capabilities)->get(0)->as_array()->size() == 1);

    a.send(link::control(link::Type::Acquire, 1, Value::map({{0, Value::uint(1)}, {1, Value::integer(0)}, {2, Value::uint(0)}})));
    auto granted = a.next(link::Type::Granted);
    CHECK(granted.has_value());
    if (!granted) {
        stop = true;
        housekeeping.join();
        return;
    }
    auto grant = *link::parse_map(*granted);
    const auto generation = static_cast<std::uint32_t>(*grant.get(1)->as_uint());

    // Writing before ConfirmTransfer is rejected and nothing reaches the instrument.
    link::SerialData early;
    early.seq = 0;
    early.generation = generation;
    early.data = {0x05};
    a.send(link::serial_data(1, early));
    auto rejected = a.next(link::Type::WriteStatus);
    CHECK(rejected && *link::parse_map(*rejected)->get(3)->as_uint() == static_cast<std::uint64_t>(link::WriteResult::Rejected));

    a.send(link::control(link::Type::ConfirmTransfer, 1, Value::map({{1, Value::uint(generation)}, {2, *grant.get(2)}})));
    link::SerialData write;
    write.seq = 1;
    write.generation = generation;
    write.data = {0x05, 0x00, 0xFF, 0x0D};  // binary bytes pass through unchanged
    a.send(link::serial_data(1, write));
    auto completed = a.next(link::Type::WriteStatus);
    CHECK(completed && *link::parse_map(*completed)->get(3)->as_uint() == static_cast<std::uint64_t>(link::WriteResult::Completed));
    std::uint8_t received[16] = {};
    int total = 0;
    while (total < 4) {
        const int n = instrument.socket.receive(received + total, sizeof(received) - static_cast<std::size_t>(total), 2000);
        if (n <= 0) break;
        total += n;
    }
    CHECK(total == 4 && received[0] == 0x05 && received[1] == 0x00 && received[2] == 0xFF && received[3] == 0x0D);

    // Instrument → owner.
    const std::uint8_t reply[] = {0x06};
    instrument.socket.send_all(reply, sizeof(reply));
    auto data = a.next(link::Type::SerialData);
    auto parsed = data ? link::parse_serial_data(*data) : std::nullopt;
    CHECK(parsed && parsed->data.size() == 1 && parsed->data[0] == 0x06 && parsed->generation == generation);

    // A second controller queues; when the owner disconnects it gets a new generation and the old one is useless.
    Client b = connect("server-b");
    b.send(link::control(link::Type::Acquire, 1, Value::map({{0, Value::uint(9)}, {1, Value::integer(0)}, {2, Value::uint(0)}})));
    CHECK(b.next(link::Type::Queued).has_value());
    a.socket.shutdown();
    a.socket.close();
    auto granted_b = b.next(link::Type::Granted);
    CHECK(granted_b.has_value());
    if (granted_b) {
        auto grant_b = *link::parse_map(*granted_b);
        CHECK(*grant_b.get(1)->as_uint() == generation + 1);
        link::SerialData stale;
        stale.seq = 0;
        stale.generation = generation;
        stale.data = {0x15};
        b.send(link::serial_data(1, stale));
        auto status = b.next(link::Type::WriteStatus);
        CHECK(status && *link::parse_map(*status)->get(3)->as_uint() == static_cast<std::uint64_t>(link::WriteResult::Rejected));
    }

    stop = true;
    housekeeping.join();
    agent.shutdown();
    platform::network_cleanup();
}


std::string temporary_directory(const std::string& name) {
    const auto path = std::filesystem::temp_directory_path() / ("portgate-test-" + name + "-" + std::to_string(platform::random_seed() % 1000000007ull));
    std::error_code ignored;
    std::filesystem::remove_all(path, ignored);
    return path.string();
}

AgentConfig sample_config(std::uint16_t instrument_port) {
    AgentConfig config;
    config.agent_id = "agent-1";
    config.platform_profile = "test";
    config.listen_host = "127.0.0.1";
    config.listen_port = 0;
    config.heartbeat_ms = 1000;
    config.security.handover_interval_ms = 0;
    config.security.handover_timeout_ms = 500;
    PortConfig port;
    port.port_id = "p1";
    port.instrument_id = "i1";
    port.device.kind = DeviceConfig::Kind::Tcp;
    port.device.host = "127.0.0.1";
    port.device.port = instrument_port;
    port.lease_ms = 5000;
    port.controllers = {{"server-a", 10}, {"server-b", 20}, {"server-c", 30}};
    port.controller_rights = {{"server-b", RightKillSwitch}, {"server-c", RightContest}};
    config.ports.push_back(port);
    return config;
}

void test_config_cbor() {
    auto config = sample_config(4000);
    config.mode.restricted = false;
    config.security.kill_switch = true;
    config.ports[0].max_hold_ms = 60000;
    config.ports[0].controller_certificates["server-a"] = std::string(64, 'a');
    std::string error;
    auto decoded = decode_config(encode_config(config), error);
    CHECK(decoded.has_value());
    if (decoded) {
        CHECK(cbor::encode(encode_config(*decoded)) == cbor::encode(encode_config(config)));  // round trip is exact
        CHECK(!decoded->mode.restricted && decoded->security.kill_switch && decoded->ports[0].max_hold_ms == 60000);
        CHECK(decoded->ports[0].controller_rights.at("server-b") == RightKillSwitch);
    }
    // Unknown keys, a tenure limit shorter than the lease and an incomplete agent-initiated mode are rejected.
    auto map = *encode_config(config).as_map();
    map.emplace(99, Value::uint(1));
    error.clear();
    CHECK(!decode_config(Value::map(map), error) && error.find("unknown key") != std::string::npos);
    config.ports[0].max_hold_ms = 10;
    error.clear();
    CHECK(!decode_config(encode_config(config), error));
    config.ports[0].max_hold_ms = 0;
    config.mode.initiator = ModeConfig::Initiator::Agent;
    error.clear();
    CHECK(!decode_config(encode_config(config), error) && error.find("server host") != std::string::npos);
}

void test_state_and_events() {
    const auto directory = temporary_directory("state");
    StateStore store(directory);
    std::string error;
    CHECK(!store.load(error) && error.empty());  // fresh installation
    AgentState state;
    state.configured = true;
    state.config = sample_config(4100);
    state.revision = 7;
    state.admin.claimed = true;
    state.admin.restricted = true;
    state.admin.address = "10.0.0.5";
    state.link_tls.psk_identity = "edc";
    state.link_tls.psk = std::vector<std::uint8_t>(32, 0x42);
    CHECK(store.save(state, error));
    auto loaded = store.load(error);
    CHECK(loaded && loaded->revision == 7 && loaded->admin.address == "10.0.0.5" && loaded->link_tls.psk.size() == 32 && loaded->config.ports.size() == 1);

    // The file on disk is sealed: the agent id does not appear in clear (DPAPI on Windows).
    platform::Bytes raw;
    CHECK(platform::read_file(platform::join_path(directory, "state.bin"), raw));
#ifdef _WIN32
    const std::string text(raw.begin(), raw.end());
    CHECK(text.find("agent-1") == std::string::npos);
#endif
    // A damaged state is an error, never a silent reset to "not configured".
    raw.resize(raw.size() / 2);
    CHECK(platform::write_file_atomic(platform::join_path(directory, "state.bin"), raw, error));
    error.clear();
    CHECK(!store.load(error) && !error.empty());

    EventLog log(directory, 600, 100);
    CHECK(log.open(error));
    for (int i = 0; i < 20; ++i) {
        Event e;
        e.kind = "granted";
        e.port_id = "p1";
        e.generation = static_cast<std::uint32_t>(i + 1);
        log.append(e);
    }
    CHECK(log.last_id() == 20);
    EventLog reopened(directory, 600, 100);
    CHECK(reopened.open(error));
    const auto recent = reopened.read(0, 1000);
    CHECK(!recent.empty() && recent.back().id == 20 && recent.back().generation == 20);
    CHECK(!recent.empty() && recent.front().id > 1);  // rotation keeps a bounded size
    CHECK(platform::file_size(platform::join_path(directory, "events.log")) <= 600);
    Event next;
    next.kind = "contest";
    CHECK(reopened.append(next).id == 21);
    std::error_code ignored;
    std::filesystem::remove_all(directory, ignored);
}

void test_arbiter_semaphore() {
    std::vector<ArbiterEvent> events;
    Arbiter hold(1000, 4, {}, 7, 3000);
    hold.acquire(1, "a", 1, 0, 0, events);
    // Without waiters the owner may renew beyond maxHold.
    events.clear();
    hold.renew(1, 2, 1, 3500, events);
    CHECK(events.size() == 1 && events[0].kind == ArbiterEvent::Kind::Granted);
    hold.acquire(2, "b", 3, 0, 3600, events);
    events.clear();
    hold.renew(1, 4, 1, 3700, events);
    CHECK(events.size() == 2 && events[0].code == "PORTGATE-MAX-HOLD" && events[1].connection == 2);

    Arbiter kill(1000, 4, {}, 9);
    events.clear();
    kill.acquire(1, "a", 1, 0, 0, events);
    kill.acquire(2, "b", 2, 0, 0, events);
    events.clear();
    CHECK(kill.revoke("PORTGATE-KILLED", 500, 100, events));
    CHECK(events.size() == 1 && events[0].code == "PORTGATE-KILLED");  // quarantine: nobody granted yet
    events.clear();
    kill.acquire(3, "c", 3, 0, 200, events);
    CHECK(events.size() == 1 && events[0].kind == ArbiterEvent::Kind::Queued);
    events.clear();
    kill.tick(599, events);
    CHECK(events.empty() && !kill.owner());
    kill.tick(600, events);
    CHECK(events.size() == 1 && events[0].kind == ArbiterEvent::Kind::Granted && events[0].connection == 2);

    events.clear();
    kill.close("PORTGATE-SECURE-CLOSED", events);
    CHECK(events.size() == 2 && events[0].kind == ArbiterEvent::Kind::Revoked && events[1].kind == ArbiterEvent::Kind::Denied);
    events.clear();
    kill.acquire(4, "d", 4, 0, 700, events);
    CHECK(events.size() == 1 && events[0].code == "PORTGATE-SECURE-CLOSED" && kill.closed());
    events.clear();
    kill.reopen(800, events);
    kill.acquire(4, "d", 5, 0, 800, events);
    CHECK(events.size() == 1 && events[0].kind == ArbiterEvent::Kind::Granted);
}

Client hello_client(std::uint16_t port, const std::string& role, const std::string& id) {
    Client client;
    std::string error;
    auto socket = platform::Socket::connect("127.0.0.1", port, 3000, error);
    if (socket) client.socket = std::move(*socket);
    client.send(link::control(link::Type::Hello, 0, Value::map({{0, Value::uint(1)}, {1, Value::text(role)}, {2, Value::text(id)}})));
    return client;
}

std::optional<Value> admin_call(Client& client, link::Type type, std::map<std::uint64_t, Value> fields, std::string* code = nullptr) {
    static std::uint64_t next = 100;
    const auto id = next++;
    fields.emplace(0, Value::uint(id));
    client.send(link::control(type, 0, Value::map(std::move(fields))));
    for (int i = 0; i < 20; ++i) {
        auto unit = client.next(link::Type::AdminResult);
        if (!unit) return std::nullopt;
        auto map = link::parse_map(*unit);
        if (!map || map->get(0)->as_uint() != std::optional<std::uint64_t>(id)) continue;
        if (code) *code = map->get(2) && map->get(2)->as_text() ? *map->get(2)->as_text() : std::string();
        if (!*map->get(1)->as_bool()) return std::nullopt;
        return map->get(4) ? *map->get(4) : Value::map({});
    }
    return std::nullopt;
}

void test_supervisor_administration() {
    std::string error;
    CHECK(platform::network_startup(error));
    FakeInstrument instrument;
    CHECK(instrument.start(error));
    const auto directory = temporary_directory("supervisor");
    SupervisorOptions options;
    options.state_directory = directory;
    options.admin_host = "127.0.0.1";
    options.admin_port = 0;
    options.agent_name = "test";
    options.device_poll_ms = 100000;
    auto supervisor = std::make_unique<Supervisor>(options, [](LogLevel, const std::string&) {});
    CHECK(supervisor->start(error));
    CHECK(supervisor->admin_certificate_sha256().size() == 64);

    Client admin = hello_client(supervisor->admin_port(), "admin", "edc-a");
    CHECK(admin.next(link::Type::Hello).has_value());
    auto status = admin_call(admin, link::Type::GetStatus, {});
    CHECK(status && !*status->get(5)->as_bool() && !*status->get(6)->as_bool());  // not configured, not claimed
    CHECK(admin_call(admin, link::Type::ListDevices, {}).has_value());

    // Private keys and PSKs never travel over a plain channel.
    std::string code;
    CHECK(!admin_call(admin, link::Type::SetTls, {{1, Value::uint(0)}, {5, Value::text("edc")}, {6, Value::bytes(cbor::Bytes(32, 1))}}, &code));
    CHECK(code == "PORTGATE-INSECURE-CHANNEL");
    // Agent-initiated mode needs a way to authenticate the server when TLS is requested.
    auto initiated = sample_config(instrument.listener.port());
    initiated.mode.initiator = ModeConfig::Initiator::Agent;
    initiated.mode.server_host = "127.0.0.1";
    initiated.mode.server_port = 1;
    initiated.mode.outbound_tls = true;  // TLS toward the server without its fingerprint or a PSK is refused
    CHECK(!admin_call(admin, link::Type::SetConfig, {{1, Value::uint(1)}, {2, encode_config(initiated)}}, &code) && code == "PORTGATE-OUTBOUND-IDENTITY");

    auto applied = admin_call(admin, link::Type::SetConfig, {{1, Value::uint(5)}, {2, encode_config(sample_config(instrument.listener.port()))}}, &code);
    CHECK(applied.has_value());
    const auto link_port = applied ? static_cast<std::uint16_t>(*applied->get(1)->as_uint()) : std::uint16_t{0};
    CHECK(link_port != 0 && link_port == supervisor->link_port());
    CHECK(instrument.accept(3000));
    CHECK(admin.next(link::Type::AdminEvent).has_value());
    auto config = admin_call(admin, link::Type::GetConfig, {});
    CHECK(config && *config->get(0)->as_uint() == 5 && config->get(1));

    // The Link agent serves controllers with the provisioned configuration.
    Client a = hello_client(link_port, "controller", "server-a");
    a.send(link::control(link::Type::Acquire, 1, Value::map({{0, Value::uint(1)}, {1, Value::integer(0)}, {2, Value::uint(0)}})));
    CHECK(a.next(link::Type::Granted).has_value());
    status = admin_call(admin, link::Type::GetStatus, {});
    const auto* ports = status ? status->get(10)->as_array() : nullptr;
    CHECK(ports && ports->size() == 1 && *(*ports)[0].get(4)->as_text() == "server-a");
    CHECK(admin_call(admin, link::Type::RevokePort, {{1, Value::text("p1")}}).has_value());
    auto revoked = a.next(link::Type::Revoked);
    CHECK(revoked && *link::parse_map(*revoked)->get(2)->as_text() == "PORTGATE-ADMIN-REVOKED");
    auto events = admin_call(admin, link::Type::ReadEvents, {{1, Value::uint(0)}, {2, Value::uint(100)}});
    bool found_applied = false, found_revoke = false;
    if (events)
        for (const auto& e : *events->get(0)->as_array()) {
            found_applied |= *e.get(2)->as_text() == "config-applied";
            found_revoke |= *e.get(2)->as_text() == "admin-revoke";
        }
    CHECK(found_applied && found_revoke);
    a.socket.close();
    admin.socket.close();

    // A restart always resumes the last installed configuration, with or without the server.
    supervisor->shutdown();
    instrument.socket.close();
    supervisor = std::make_unique<Supervisor>(options, [](LogLevel, const std::string&) {});
    CHECK(supervisor->start(error));
    CHECK(supervisor->link_port() != 0);
    CHECK(instrument.accept(3000));
    Client again = hello_client(supervisor->admin_port(), "admin", "edc-a");
    status = admin_call(again, link::Type::GetStatus, {});
    CHECK(status && *status->get(5)->as_bool() && *status->get(4)->as_uint() == 5 && *status->get(7)->as_bool());
    again.socket.close();
    supervisor->shutdown();
    supervisor.reset();

    // Fixed at installation: a fresh agent bound to a server certificate refuses an unauthenticated admin.
    const auto fixed_directory = temporary_directory("fixed");
    options.state_directory = fixed_directory;
    options.install_server_certificate_sha256 = std::string(64, 'b');
    Supervisor fixed(options, [](LogLevel, const std::string&) {});
    CHECK(fixed.start(error));
    Client stranger = hello_client(fixed.admin_port(), "admin", "intruder");
    auto refused = stranger.next(link::Type::AdminResult);
    CHECK(refused && *link::parse_map(*refused)->get(2)->as_text() == "PORTGATE-ADMIN-NOT-AUTHORIZED");
    stranger.socket.close();
    fixed.shutdown();

    platform::network_cleanup();
    std::error_code ignored;
    std::filesystem::remove_all(directory, ignored);
    std::filesystem::remove_all(fixed_directory, ignored);
}

/// Minimal TLS-PSK client (Mbed TLS) for the administration endpoint; .NET SslStream has no PSK support.
struct PskClient {
    platform::Socket socket;
    mbedtls_ssl_config config;
    mbedtls_ssl_context ssl;
    link::Decoder decoder{1024 * 1024};
    std::vector<link::Unit> pending;

    PskClient() {
        mbedtls_ssl_config_init(&config);
        mbedtls_ssl_init(&ssl);
    }
    ~PskClient() {
        mbedtls_ssl_free(&ssl);
        mbedtls_ssl_config_free(&config);
    }

    static int bio_send(void* context, const unsigned char* data, std::size_t size) {
        return static_cast<PskClient*>(context)->socket.send_all(data, size) ? static_cast<int>(size) : -1;
    }
    static int bio_receive(void* context, unsigned char* buffer, std::size_t size, std::uint32_t timeout_ms) {
        const int n = static_cast<PskClient*>(context)->socket.receive(buffer, size, timeout_ms == 0 ? 1000 : static_cast<int>(timeout_ms));
        return n == -2 ? MBEDTLS_ERR_SSL_TIMEOUT : n < 0 ? -1 : n;
    }

    bool connect(std::uint16_t port, const std::string& identity, const std::vector<std::uint8_t>& psk) {
        std::string error;
        auto s = platform::Socket::connect("127.0.0.1", port, 3000, error);
        if (!s) return false;
        socket = std::move(*s);
        if (mbedtls_ssl_config_defaults(&config, MBEDTLS_SSL_IS_CLIENT, MBEDTLS_SSL_TRANSPORT_STREAM, MBEDTLS_SSL_PRESET_DEFAULT) != 0) return false;
        mbedtls_ssl_conf_authmode(&config, MBEDTLS_SSL_VERIFY_NONE);  // the PSK authenticates both ends
        mbedtls_ssl_conf_read_timeout(&config, 100);
        if (mbedtls_ssl_conf_psk(&config, psk.data(), psk.size(), reinterpret_cast<const unsigned char*>(identity.data()), identity.size()) != 0) return false;
        mbedtls_ssl_conf_tls13_key_exchange_modes(&config, MBEDTLS_SSL_TLS1_3_KEY_EXCHANGE_MODE_PSK_EPHEMERAL);
        if (mbedtls_ssl_setup(&ssl, &config) != 0) return false;
        mbedtls_ssl_set_bio(&ssl, this, bio_send, nullptr, bio_receive);
        const auto deadline = platform::monotonic_ms() + 5000;
        int ret;
        while ((ret = mbedtls_ssl_handshake(&ssl)) != 0) {
            if ((ret == MBEDTLS_ERR_SSL_TIMEOUT || ret == MBEDTLS_ERR_SSL_WANT_READ || ret == MBEDTLS_ERR_SSL_WANT_WRITE) && platform::monotonic_ms() < deadline) continue;
            return false;
        }
        return true;
    }

    bool send(const link::Unit& unit) {
        const auto bytes = link::encode(unit);
        return mbedtls_ssl_write(&ssl, bytes.data(), bytes.size()) == static_cast<int>(bytes.size());
    }

    std::optional<link::Unit> next(link::Type type) {
        const auto deadline = platform::monotonic_ms() + 5000;
        while (platform::monotonic_ms() < deadline) {
            for (auto it = pending.begin(); it != pending.end(); ++it)
                if (it->type == type) {
                    auto unit = std::move(*it);
                    pending.erase(it);
                    return unit;
                }
            std::uint8_t buffer[4096];
            const int n = mbedtls_ssl_read(&ssl, buffer, sizeof(buffer));
            if (n == MBEDTLS_ERR_SSL_TIMEOUT || n == MBEDTLS_ERR_SSL_WANT_READ || n == MBEDTLS_ERR_SSL_RECEIVED_NEW_SESSION_TICKET) continue;
            if (n <= 0) return std::nullopt;
            decoder.feed(buffer, static_cast<std::size_t>(n), pending);
        }
        return std::nullopt;
    }
};

void test_admin_psk_fixed_at_installation() {
    std::string error;
    CHECK(platform::network_startup(error));
    const auto directory = temporary_directory("psk");
    SupervisorOptions options;
    options.state_directory = directory;
    options.admin_host = "127.0.0.1";
    options.admin_port = 0;
    options.agent_name = "psk";
    options.device_poll_ms = 100000;
    options.install_psk_identity = "edc-central";
    options.install_psk = std::vector<std::uint8_t>(32, 0x5A);
    Supervisor supervisor(options, [](LogLevel, const std::string&) {});
    CHECK(supervisor.start(error));

    PskClient wrong;
    CHECK(!wrong.connect(supervisor.admin_port(), "edc-central", std::vector<std::uint8_t>(32, 0x00)));  // wrong key: no handshake

    PskClient client;
    CHECK(client.connect(supervisor.admin_port(), "edc-central", options.install_psk));
    CHECK(client.send(link::control(link::Type::Hello, 0, Value::map({{0, Value::uint(1)}, {1, Value::text("admin")}, {2, Value::text("edc-central")}}))));
    CHECK(client.next(link::Type::Hello).has_value());
    CHECK(client.send(link::control(link::Type::GetStatus, 0, Value::map({{0, Value::uint(1)}}))));
    auto status = client.next(link::Type::AdminResult);
    CHECK(status && *link::parse_map(*status)->get(1)->as_bool() && *link::parse_map(*status)->get(4)->get(6)->as_bool());

    // Over this channel TLS material is accepted; a plain client is not the fixed server.
    CHECK(client.send(link::control(link::Type::SetTls, 0, Value::map({{0, Value::uint(2)}, {1, Value::uint(0)}, {5, Value::text("edc")}, {6, Value::bytes(cbor::Bytes(32, 7))}}))));
    auto installed = client.next(link::Type::AdminResult);
    CHECK(installed && *link::parse_map(*installed)->get(1)->as_bool());
    Client plain = hello_client(supervisor.admin_port(), "admin", "edc-central");
    auto refused = plain.next(link::Type::AdminResult);
    CHECK(refused && *link::parse_map(*refused)->get(2)->as_text() == "PORTGATE-ADMIN-NOT-AUTHORIZED");
    plain.socket.close();
    mbedtls_ssl_close_notify(&client.ssl);
    supervisor.shutdown();
    platform::network_cleanup();
    std::error_code ignored;
    std::filesystem::remove_all(directory, ignored);
}

void test_ethernet_instrument_connects_to_the_agent() {
    std::string error;
    CHECK(platform::network_startup(error));
    std::uint16_t device_port = 0;
    {
        platform::Listener probe;  // a free local port for the instrument side
        CHECK(probe.open("127.0.0.1", 0, error));
        device_port = probe.port();
    }
    AgentConfig config = sample_config(1);
    config.ports[0].device = DeviceConfig{};
    config.ports[0].device.kind = DeviceConfig::Kind::TcpListen;
    config.ports[0].device.host = "127.0.0.1";
    config.ports[0].device.port = device_port;
    auto encoded = decode_config(encode_config(config), error);
    CHECK(encoded && encoded->ports[0].device.kind == DeviceConfig::Kind::TcpListen && encoded->ports[0].device.port == device_port);

    Agent agent(config, [](LogLevel, const std::string&) {});
    CHECK(agent.start(error));
    std::atomic<bool> stop{false};
    std::thread housekeeping([&] { agent.run(stop); });

    Client a = hello_client(agent.listen_port(), "controller", "server-a");
    auto capabilities = a.next(link::Type::Capabilities);
    const auto* device = capabilities ? (*link::parse_map(*capabilities)->get(0)->as_array())[0].get(7) : nullptr;
    CHECK(device && device->get(0)->as_uint() == std::optional<std::uint64_t>(2) && device->get(8)->as_uint() == std::optional<std::uint64_t>(device_port));
    a.send(link::control(link::Type::Acquire, 1, Value::map({{0, Value::uint(1)}, {1, Value::integer(0)}, {2, Value::uint(0)}})));
    auto granted = a.next(link::Type::Granted);
    CHECK(granted.has_value());
    if (granted) {
        auto grant = *link::parse_map(*granted);
        const auto generation = static_cast<std::uint32_t>(*grant.get(1)->as_uint());
        a.send(link::control(link::Type::ConfirmTransfer, 1, Value::map({{1, Value::uint(generation)}, {2, *grant.get(2)}})));

        // The instrument is the TCP client: it connects to the agent, sends, and receives what the owner writes.
        std::optional<platform::Socket> instrument;
        for (int attempt = 0; attempt < 20 && !instrument; ++attempt) {
            instrument = platform::Socket::connect("127.0.0.1", device_port, 500, error);
            if (!instrument) std::this_thread::sleep_for(std::chrono::milliseconds(100));
        }
        CHECK(instrument.has_value());
        if (instrument) {
            std::this_thread::sleep_for(std::chrono::milliseconds(300));  // accepted by the port thread
            const std::uint8_t enq[] = {0x05};
            instrument->send_all(enq, sizeof(enq));
            auto data = a.next(link::Type::SerialData);
            auto parsed = data ? link::parse_serial_data(*data) : std::nullopt;
            CHECK(parsed && parsed->data.size() == 1 && parsed->data[0] == 0x05);
            link::SerialData ack;
            ack.seq = 1;
            ack.generation = generation;
            ack.data = {0x06};
            a.send(link::serial_data(1, ack));
            auto status = a.next(link::Type::WriteStatus);
            CHECK(status && *link::parse_map(*status)->get(3)->as_uint() == static_cast<std::uint64_t>(link::WriteResult::Completed));
            std::uint8_t received = 0;
            CHECK(instrument->receive(&received, 1, 2000) == 1 && received == 0x06);
            instrument->close();
        }
    }
    a.socket.close();
    stop = true;
    housekeeping.join();
    agent.shutdown();
    platform::network_cleanup();
}

void test_hot_device_reconfiguration() {
    std::string error;
    CHECK(platform::network_startup(error));
    FakeInstrument first;
    FakeInstrument second;
    CHECK(first.start(error) && second.start(error));
    auto config = sample_config(first.listener.port());
    Agent agent(config, [](LogLevel, const std::string&) {});
    CHECK(agent.start(error));
    std::atomic<bool> stop{false};
    std::thread housekeeping([&] { agent.run(stop); });
    CHECK(first.accept(3000));
    const auto link_port = agent.listen_port();

    Client a = hello_client(link_port, "controller", "server-a");
    a.send(link::control(link::Type::Acquire, 1, Value::map({{0, Value::uint(1)}, {1, Value::integer(0)}, {2, Value::uint(0)}})));
    auto granted = a.next(link::Type::Granted);
    CHECK(granted.has_value());

    // A lease or controller change needs a restart; a device change does not.
    auto restart = config;
    restart.ports[0].lease_ms = 9000;
    CHECK(!agent.reconfigure(restart));
    auto hot = config;
    hot.ports[0].device.port = second.listener.port();
    CHECK(agent.reconfigure(hot));

    auto event = a.next(link::Type::PortEvent);
    CHECK(event && *link::parse_map(*event)->get(1)->as_text() == "PORTGATE-PORT-RECONFIGURED");
    CHECK(second.accept(3000));  // the port reopened on the new device
    CHECK(agent.listen_port() == link_port);
    if (granted) {
        auto grant = *link::parse_map(*granted);
        const auto generation = static_cast<std::uint32_t>(*grant.get(1)->as_uint());
        a.send(link::control(link::Type::ConfirmTransfer, 1, Value::map({{1, Value::uint(generation)}, {2, *grant.get(2)}})));
        link::SerialData write;
        write.seq = 1;
        write.generation = generation;
        write.data = {0x05};
        a.send(link::serial_data(1, write));
        auto status = a.next(link::Type::WriteStatus);
        CHECK(status && *link::parse_map(*status)->get(3)->as_uint() == static_cast<std::uint64_t>(link::WriteResult::Completed));
        std::uint8_t received = 0;
        CHECK(second.socket.receive(&received, 1, 2000) == 1 && received == 0x05);  // same tenure, new device
    }
    // New controllers see the settings in force.
    Client b = hello_client(link_port, "controller", "server-b");
    auto capabilities = b.next(link::Type::Capabilities);
    const auto* device = capabilities ? (*link::parse_map(*capabilities)->get(0)->as_array())[0].get(7) : nullptr;
    CHECK(device && device->get(8)->as_uint() == std::optional<std::uint64_t>(second.listener.port()));

    stop = true;
    housekeeping.join();
    agent.shutdown();
    platform::network_cleanup();
}

void test_agent_initiated_connections() {
    std::string error;
    CHECK(platform::network_startup(error));
    FakeInstrument instrument;
    CHECK(instrument.start(error));
    platform::Listener server;  // the server listens; the agent dials it
    CHECK(server.open("127.0.0.1", 0, error));
    const auto directory = temporary_directory("outbound");
    SupervisorOptions options;
    options.state_directory = directory;
    options.admin_host = "127.0.0.1";
    options.admin_port = 0;
    options.agent_name = "outbound";
    options.device_poll_ms = 100000;
    options.install_server_host = "127.0.0.1";
    options.install_server_port = server.port();
    Supervisor supervisor(options, [](LogLevel, const std::string&) {});
    CHECK(supervisor.start(error));
    std::atomic<bool> stop{false};
    std::thread runner([&] { supervisor.run(stop); });

    // Installed with --server: the agent dials and the server, speaking first, uses the connection to administer it.
    Client admin;
    admin.socket = server.accept(5000);
    CHECK(admin.socket.valid());
    admin.send(link::control(link::Type::Hello, 0, Value::map({{0, Value::uint(1)}, {1, Value::text("admin")}, {2, Value::text("edc-central")}})));
    CHECK(admin.next(link::Type::Hello).has_value());
    auto config = sample_config(instrument.listener.port());
    config.mode.initiator = ModeConfig::Initiator::Agent;
    config.mode.server_host = "127.0.0.1";
    config.mode.server_port = server.port();
    config.mode.connections = 2;
    std::string code;
    auto applied = admin_call(admin, link::Type::SetConfig, {{1, Value::uint(1)}, {2, encode_config(config)}}, &code);
    CHECK(applied && applied->get(1)->as_uint() == std::optional<std::uint64_t>(0));  // no inbound Link endpoint
    CHECK(instrument.accept(3000));

    // The second connection becomes a controller when the server says so.
    Client controller;
    controller.socket = server.accept(5000);
    CHECK(controller.socket.valid());
    controller.send(link::control(link::Type::Hello, 0, Value::map({{0, Value::uint(1)}, {1, Value::text("controller")}, {2, Value::text("server-a")}})));
    CHECK(controller.next(link::Type::Hello).has_value());
    CHECK(controller.next(link::Type::Capabilities).has_value());
    controller.send(link::control(link::Type::Acquire, 1, Value::map({{0, Value::uint(1)}, {1, Value::integer(0)}, {2, Value::uint(0)}})));
    CHECK(controller.next(link::Type::Granted).has_value());

    // A connection lost on the server side is dialed again.
    controller.socket.close();
    Client again;
    again.socket = server.accept(5000);
    CHECK(again.socket.valid());
    again.socket.close();
    admin.socket.close();

    stop = true;
    runner.join();
    supervisor.shutdown();
    platform::network_cleanup();
    std::error_code ignored;
    std::filesystem::remove_all(directory, ignored);
}

void test_sealed_capture_store() {
    const auto directory = temporary_directory("capture");
    std::string error;
    CHECK(platform::ensure_directory(directory, error));
    std::vector<std::uint8_t> key(32, 0x11);
    PortConfig port;
    port.port_id = "p1";
    port.capture.enabled = true;
    port.capture.max_bytes = 64 * 1024;
    {
        CaptureStore store(directory);
        store.configure("agent-1", {port});
        store.append("p1", Value::map({{2, Value::uint(0)}}));  // no key yet: a gap, never plain data
        store.set_key(7, key);
        for (int i = 0; i < 3; ++i) store.append("p1", Value::map({{2, Value::uint(0)}, {6, Value::bytes({0x05, static_cast<std::uint8_t>(i)})}}));
        auto page = store.read("p1", 0, 100);
        CHECK(page.records.size() == 3 && page.first == 2 && page.last == 4);
        CHECK(page.gaps.size() == 1 && page.gaps[0].reason == "no-key" && page.gaps[0].from == 1);
        // Each record authenticates only at its position: the server decrypts with its key and the agent/port/seq AAD.
        std::vector<std::uint8_t> plain;
        const auto& r = page.records[1];
        CHECK(tls::open(key, CaptureStore::aad("agent-1", "p1", r.seq, r.key_id), r.nonce, r.sealed, plain, error));
        auto decoded = cbor::decode(plain.data(), plain.size());
        CHECK(decoded && (*decoded->get(6)->as_bytes())[1] == 1);
        CHECK(!tls::open(key, CaptureStore::aad("agent-1", "p1", r.seq + 1, r.key_id), r.nonce, r.sealed, plain, error));
        // The file never holds the clear bytes.
        platform::Bytes raw;
        CHECK(platform::read_file(platform::join_path(directory, "capture-7031.bin"), raw));
        const std::vector<std::uint8_t> needle{0x42, 0x05, 0x01};
        CHECK(std::search(raw.begin(), raw.end(), needle.begin(), needle.end()) == raw.end());
        CHECK(store.ack("p1", 3, error));
        CHECK(!store.ack("p1", 99, error));
    }
    {
        // After a restart the sequence continues and acknowledged records stay deleted.
        CaptureStore store(directory);
        store.configure("agent-1", {port});
        store.set_key(7, key);
        store.append("p1", Value::map({{2, Value::uint(1)}}));
        auto page = store.read("p1", 0, 100);
        CHECK(page.records.size() == 2 && page.records.front().seq == 4 && page.records.back().seq == 5);
        // Quota: the oldest records are evicted and reported as a gap.
        std::vector<std::uint8_t> big(4096, 0x30);
        for (int i = 0; i < 40; ++i) store.append("p1", Value::map({{6, Value::bytes(big)}}));
        page = store.read("p1", 0, 1000);
        CHECK(!page.records.empty() && page.records.front().seq > 5);
        CHECK(std::any_of(page.gaps.begin(), page.gaps.end(), [](const CaptureGap& g) { return g.reason == "quota"; }));
        CHECK(platform::file_size(platform::join_path(directory, "capture-7031.bin")) <= port.capture.max_bytes);
    }
    std::error_code ignored;
    std::filesystem::remove_all(directory, ignored);
}

void test_observers_and_capture_tap() {
    std::string error;
    CHECK(platform::network_startup(error));
    FakeInstrument instrument;
    CHECK(instrument.start(error));
    auto config = sample_config(instrument.listener.port());
    config.ports[0].controller_rights["server-c"] = RightObserve;
    config.ports[0].capture.enabled = true;
    std::vector<Value> captured;
    std::mutex captured_mutex;
    Agent agent(config, [](LogLevel, const std::string&) {});
    agent.set_capture_sink([&](const std::string&, const Value& record) {
        std::lock_guard<std::mutex> lock(captured_mutex);
        captured.push_back(record);
    });
    CHECK(agent.start(error));
    std::atomic<bool> stop{false};
    std::thread housekeeping([&] { agent.run(stop); });
    CHECK(instrument.accept(3000));

    Client b = hello_client(agent.listen_port(), "controller", "server-b");
    b.send(link::control(link::Type::Observe, 1, Value::map({{0, Value::uint(1)}, {1, Value::boolean(true)}})));
    auto refused = b.next(link::Type::ObserveResult);
    CHECK(refused && *link::parse_map(*refused)->get(2)->as_text() == "PORTGATE-OBSERVE-NOT-ALLOWED");

    Client c = hello_client(agent.listen_port(), "controller", "server-c");
    auto capabilities = c.next(link::Type::Capabilities);
    CHECK(capabilities && (*link::parse_map(*capabilities)->get(0)->as_array())[0].get(3)->as_uint() == std::optional<std::uint64_t>(2));
    c.send(link::control(link::Type::Observe, 1, Value::map({{0, Value::uint(2)}, {1, Value::boolean(true)}})));
    auto accepted = c.next(link::Type::ObserveResult);
    CHECK(accepted && *link::parse_map(*accepted)->get(1)->as_bool());

    // Bytes of the instrument reach the observer and the capture even with no owner.
    const std::uint8_t enq[] = {0x05};
    instrument.socket.send_all(enq, sizeof(enq));
    auto rx = c.next(link::Type::ObservedData);
    auto rx_data = rx ? link::parse_observed_data(*rx) : std::nullopt;
    CHECK(rx_data && rx_data->direction == 0 && rx_data->data.size() == 1 && rx_data->data[0] == 0x05);

    // What the owner writes is observed too; the observer cannot write.
    Client a = hello_client(agent.listen_port(), "controller", "server-a");
    a.send(link::control(link::Type::Acquire, 1, Value::map({{0, Value::uint(3)}, {1, Value::integer(0)}, {2, Value::uint(0)}})));
    auto granted = a.next(link::Type::Granted);
    CHECK(granted.has_value());
    if (granted) {
        auto grant = *link::parse_map(*granted);
        const auto generation = static_cast<std::uint32_t>(*grant.get(1)->as_uint());
        a.send(link::control(link::Type::ConfirmTransfer, 1, Value::map({{1, Value::uint(generation)}, {2, *grant.get(2)}})));
        link::SerialData ack;
        ack.seq = 1;
        ack.generation = generation;
        ack.data = {0x06};
        a.send(link::serial_data(1, ack));
        auto tx = c.next(link::Type::ObservedData);
        auto tx_data = tx ? link::parse_observed_data(*tx) : std::nullopt;
        CHECK(tx_data && tx_data->direction == 1 && tx_data->data[0] == 0x06 && tx_data->generation == generation);
        c.send(link::serial_data(1, ack));
        auto denied = c.next(link::Type::WriteStatus);
        CHECK(denied && *link::parse_map(*denied)->get(3)->as_uint() == static_cast<std::uint64_t>(link::WriteResult::Rejected));
    }
    auto notice = b.next(link::Type::SecurityEvent);  // everyone on the port learned that server-c observes
    CHECK(notice && *link::parse_map(*notice)->get(1)->as_text() == "observe-start" && *link::parse_map(*notice)->get(3)->as_text() == "server-c");
    {
        std::lock_guard<std::mutex> lock(captured_mutex);
        CHECK(captured.size() >= 2);
        if (captured.size() >= 2) {
            CHECK(captured[0].get(2)->as_uint() == std::optional<std::uint64_t>(0) && !captured[0].get(7));  // received while unowned
            CHECK(captured[1].get(2)->as_uint() == std::optional<std::uint64_t>(1) && *captured[1].get(7)->as_text() == "server-a");
        }
    }
    stop = true;
    housekeeping.join();
    agent.shutdown();
    platform::network_cleanup();
}

void test_handover_and_plain_security() {
    std::string error;
    CHECK(platform::network_startup(error));
    FakeInstrument instrument;
    CHECK(instrument.start(error));
    auto config = sample_config(instrument.listener.port());
    config.security.kill_switch = true;
    config.security.contest = true;
    std::vector<Event> logged;
    std::mutex logged_mutex;
    std::uint64_t ids = 1;
    Agent agent(config, [](LogLevel, const std::string&) {}, {}, [&](Event e) {
        std::lock_guard<std::mutex> lock(logged_mutex);
        e.id = ids++;
        logged.push_back(e);
        return e;
    });
    CHECK(agent.start(error));
    std::atomic<bool> stop{false};
    std::thread housekeeping([&] { agent.run(stop); });
    CHECK(instrument.accept(3000));

    Client a = hello_client(agent.listen_port(), "controller", "server-a");
    Client b = hello_client(agent.listen_port(), "controller", "server-b");
    auto acquire = [](Client& c, std::uint64_t id) {
        c.send(link::control(link::Type::Acquire, 1, Value::map({{0, Value::uint(id)}, {1, Value::integer(0)}, {2, Value::uint(0)}})));
    };
    acquire(a, 1);
    CHECK(a.next(link::Type::Granted).has_value());
    // Not queued: the request is refused without bothering the owner.
    b.send(link::control(link::Type::RequestHandover, 1, Value::map({{0, Value::uint(5)}, {1, Value::text("maintenance")}})));
    auto result = b.next(link::Type::HandoverResult);
    CHECK(result && *link::parse_map(*result)->get(2)->as_text() == "PORTGATE-HANDOVER-NOT-QUEUED");
    acquire(b, 2);
    CHECK(b.next(link::Type::Queued).has_value());

    // The owner keeps the port.
    b.send(link::control(link::Type::RequestHandover, 1, Value::map({{0, Value::uint(6)}, {1, Value::text("maintenance")}})));
    auto asked = a.next(link::Type::HandoverAsked);
    CHECK(asked && *link::parse_map(*asked)->get(1)->as_text() == "server-b");
    if (asked) a.send(link::control(link::Type::HandoverAnswer, 1, Value::map({{0, *link::parse_map(*asked)->get(0)}, {1, Value::boolean(false)}})));
    result = b.next(link::Type::HandoverResult);
    CHECK(result && !*link::parse_map(*result)->get(1)->as_bool() && *link::parse_map(*result)->get(2)->as_text() == "PORTGATE-HANDOVER-KEPT");
    CHECK(b.next(link::Type::SecurityEvent).has_value());  // observers see the request

    // Silence keeps the port too.
    b.send(link::control(link::Type::RequestHandover, 1, Value::map({{0, Value::uint(7)}})));
    CHECK(a.next(link::Type::HandoverAsked).has_value());
    result = b.next(link::Type::HandoverResult, 3000);
    CHECK(result && *link::parse_map(*result)->get(2)->as_text() == "PORTGATE-HANDOVER-NO-ANSWER");

    // The owner yields: the queue grants the port.
    b.send(link::control(link::Type::RequestHandover, 1, Value::map({{0, Value::uint(9)}})));
    asked = a.next(link::Type::HandoverAsked);
    CHECK(asked.has_value());
    if (asked) a.send(link::control(link::Type::HandoverAnswer, 1, Value::map({{0, *link::parse_map(*asked)->get(0)}, {1, Value::boolean(true)}})));
    auto revoked = a.next(link::Type::Revoked);
    CHECK(revoked && *link::parse_map(*revoked)->get(2)->as_text() == "PORTGATE-HANDOVER");
    CHECK(b.next(link::Type::Granted).has_value());
    result = b.next(link::Type::HandoverResult);
    CHECK(result && *link::parse_map(*result)->get(1)->as_bool());

    // Kill switch and contest are refused on a plain endpoint even with the right and the feature enabled.
    b.send(link::control(link::Type::KillSwitch, 1, Value::map({{0, Value::uint(10)}, {1, Value::text("test")}})));
    auto refused = b.next(link::Type::Error);
    CHECK(refused && *link::parse_map(*refused)->get(0)->as_text() == "PORTGATE-KILL-NEEDS-TLS");
    Client c = hello_client(agent.listen_port(), "controller", "server-c");
    c.send(link::control(link::Type::Contest, 1, Value::map({{0, Value::uint(11)}, {1, Value::uint(1)}})));
    refused = c.next(link::Type::Error);
    CHECK(refused && *link::parse_map(*refused)->get(0)->as_text() == "PORTGATE-CONTEST-NEEDS-TLS");
    {
        std::lock_guard<std::mutex> lock(logged_mutex);
        CHECK(std::any_of(logged.begin(), logged.end(), [](const Event& e) { return e.kind == "kill-switch-denied"; }));
        CHECK(std::any_of(logged.begin(), logged.end(), [](const Event& e) { return e.kind == "handover-yielded"; }));
    }

    stop = true;
    housekeeping.join();
    agent.shutdown();
    platform::network_cleanup();
}
}  // namespace

int main(int argc, char** argv) {
    if (argc > 1 && std::string(argv[1]) == "--emit-vectors") {
        std::cout << "# PortGate Link v1 conformance vectors: <name> <hex of the complete unit>\n";
        for (const auto& [name, unit] : vectors()) std::cout << name << " " << hex(link::encode(unit)) << "\n";
        return 0;
    }
    test_cbor();
    test_link_decoder();
    test_vectors_file();
    test_arbiter();

    test_tls_material();
    test_agent_end_to_end();
    test_config_cbor();
    test_state_and_events();
    test_arbiter_semaphore();
    test_supervisor_administration();
    test_handover_and_plain_security();
    test_admin_psk_fixed_at_installation();
    test_ethernet_instrument_connects_to_the_agent();
    test_hot_device_reconfiguration();
    test_agent_initiated_connections();
    test_sealed_capture_store();
    test_observers_and_capture_tap();
    std::cout << g_checks << " checks, " << g_failures << " failures\n";
    return g_failures == 0 ? 0 : 1;
}
