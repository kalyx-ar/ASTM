// Native tests without external frameworks: unit tests of the core and an end-to-end agent test over loopback TCP.
#include <atomic>
#include <chrono>
#include <cstdio>
#include <fstream>
#include <functional>
#include <iostream>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

#include "atsm/agent.hpp"
#include "atsm/arbiter.hpp"
#include "atsm/cbor.hpp"
#include "atsm/config.hpp"
#include "atsm/link.hpp"

using namespace atsm;
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
        {"denied", link::control(link::Type::Denied, 1, Value::map({{0, Value::uint(8)}, {1, Value::text("ATSM-AG-QUEUE-FULL")}}))},
        {"heartbeat", link::control(link::Type::Heartbeat, 0, Value::map({{0, Value::uint(4294967296ull)}}))},
        {"error-negative", link::control(link::Type::Error, 0, Value::map({{0, Value::text("ATSM-X")}, {1, Value::integer(-500)}}))},
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
    std::ifstream file(ATSM_VECTORS);
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
    CHECK(events.size() == 1 && events[0].code == "ATSM-AG-CONTROLLER-NOT-AUTHORIZED");
    events.clear();
    arbiter.acquire(5, "a", 5, 0, 20, events);
    CHECK(events.size() == 1 && events[0].code == "ATSM-AG-QUEUE-FULL");

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
    CHECK(events.size() == 1 && events[0].code == "ATSM-AG-QUEUE-TIMEOUT");
}

void test_config() {
    std::string error;
    const char* valid = R"({"schemaVersion":"atsm-agent/1","agentId":"a1","listen":{"host":"127.0.0.1","port":0},
        "ports":[{"portId":"p1","instrumentId":"i1","device":{"kind":"serial","path":"COM3","baudRate":9600,"parity":"None"},
        "ownership":{"leaseMs":15000,"controllers":[{"id":"edc-server-a","priority":10}]}}]})";
    auto config = parse_config(valid, error);
    CHECK(config && config->ports.size() == 1 && config->ports[0].controllers.at("edc-server-a") == 10);

    error.clear();
    CHECK(!parse_config(R"({"schemaVersion":"atsm-agent/1","agentId":"a1","listen":{"port":1},"ports":[{"portId":"p","instrumentId":"i","device":{"kind":"serial","path":"COM1","baudrate":9600}}]})", error));
    CHECK(error.find("baudrate") != std::string::npos);
    error.clear();
    CHECK(!parse_config(R"({"schemaVersion":"atsm-agent/1",)", error));
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

}  // namespace

int main(int argc, char** argv) {
    if (argc > 1 && std::string(argv[1]) == "--emit-vectors") {
        std::cout << "# ATSM Link v1 conformance vectors: <name> <hex of the complete unit>\n";
        for (const auto& [name, unit] : vectors()) std::cout << name << " " << hex(link::encode(unit)) << "\n";
        return 0;
    }
    test_cbor();
    test_link_decoder();
    test_vectors_file();
    test_arbiter();
    test_config();
    test_agent_end_to_end();
    std::cout << g_checks << " checks, " << g_failures << " failures\n";
    return g_failures == 0 ? 0 : 1;
}
