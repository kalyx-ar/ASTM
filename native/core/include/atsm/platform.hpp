// Platform services used by the agent core. Implemented per target under native/platform/.
#pragma once

#include <cstdint>
#include <memory>
#include <optional>
#include <string>

#include "atsm/config.hpp"

namespace atsm::platform {

std::uint64_t monotonic_ms();

/// Cryptographically unsuitable but unique-per-start seed (time and address entropy) for epochs and transfer ids.
std::uint64_t random_seed();

/// Serial port or serial-over-TCP endpoint.
class Device {
public:
    virtual ~Device() = default;
    virtual bool open(std::string& error) = 0;
    /// Returns bytes read, 0 on timeout, -1 on failure.
    virtual int read(std::uint8_t* buffer, std::size_t size, int timeout_ms) = 0;
    /// Returns bytes written (all of them on success), -1 on failure.
    virtual int write(const std::uint8_t* data, std::size_t size) = 0;
    virtual void close() = 0;
    virtual std::string describe() const = 0;
};

std::unique_ptr<Device> make_device(const DeviceConfig& config);

class Socket {
public:
    Socket() = default;
    explicit Socket(std::intptr_t handle) : handle_(handle) {}
    Socket(const Socket&) = delete;
    Socket& operator=(const Socket&) = delete;
    Socket(Socket&& other) noexcept : handle_(other.handle_) { other.handle_ = -1; }
    Socket& operator=(Socket&& other) noexcept;
    ~Socket() { close(); }

    static std::optional<Socket> connect(const std::string& host, std::uint16_t port, int timeout_ms, std::string& error);

    bool valid() const { return handle_ != -1; }
    bool send_all(const std::uint8_t* data, std::size_t size);
    /// Returns bytes read, 0 on orderly close, -1 on failure, -2 on timeout.
    int receive(std::uint8_t* buffer, std::size_t size, int timeout_ms);
    /// True when data (or a close) is ready to read within timeout_ms.
    bool wait_readable(int timeout_ms) const;
    void shutdown();
    void close();
    std::string peer() const;

private:
    std::intptr_t handle_ = -1;
};

class Listener {
public:
    Listener() = default;
    ~Listener() { close(); }
    Listener(const Listener&) = delete;
    Listener& operator=(const Listener&) = delete;

    bool open(const std::string& host, std::uint16_t port, std::string& error);
    /// Waits up to timeout_ms; returns an invalid socket on timeout.
    Socket accept(int timeout_ms);
    std::uint16_t port() const { return port_; }
    void close();

private:
    std::intptr_t handle_ = -1;
    std::uint16_t port_ = 0;
};

bool network_startup(std::string& error);
void network_cleanup();

}  // namespace atsm::platform
