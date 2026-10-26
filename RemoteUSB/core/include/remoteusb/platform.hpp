// Platform services used by the agent core. Implemented per target under RemoteUSB/platform/.
#pragma once

#include <cstdint>
#include <memory>
#include <optional>
#include <string>
#include <vector>

#include "remoteusb/config.hpp"

namespace remoteusb::platform {

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
    /// Like receive() but leaves the bytes in the socket (used to tell TLS from plain on one port).
    int peek(std::uint8_t* buffer, std::size_t size, int timeout_ms);
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

using Bytes = std::vector<std::uint8_t>;

/// Milliseconds since the Unix epoch (UTC), for event timestamps.
std::uint64_t utc_ms();

/// Seals data for this machine: DPAPI with machine scope on Windows. On POSIX the data is returned unchanged and the
/// protection is the owner-only permission of the files written by write_file_atomic/append_file.
bool protect(const Bytes& plain, Bytes& sealed, std::string& error);
bool unprotect(const Bytes& sealed, Bytes& plain, std::string& error);

bool ensure_directory(const std::string& path, std::string& error);
bool read_file(const std::string& path, Bytes& out);
/// Writes a temporary file and renames it over `path`, so a crash never leaves a half-written state.
bool write_file_atomic(const std::string& path, const Bytes& data, std::string& error);
bool append_file(const std::string& path, const Bytes& data, std::string& error);
std::uint64_t file_size(const std::string& path);
bool replace_file(const std::string& from, const std::string& to, std::string& error);
std::string join_path(const std::string& directory, const std::string& name);

/// Serial port present on this machine, with the physical identity the server uses to recognize an instrument.
struct SerialDeviceInfo {
    std::string name;           // COM5, /dev/ttyUSB0
    std::string friendly_name;  // "USB Serial Port (COM5)"
    std::string usb_vid;        // upper-case hex, empty when not USB
    std::string usb_pid;
    std::string usb_serial;     // serial number of the adapter, empty when it has none
    std::string location;       // physical location (hub port path)
    std::string instance_id;    // platform device instance id
};

/// Enumerates the serial ports present now. Never opens them.
std::vector<SerialDeviceInfo> list_serial_devices();

}  // namespace remoteusb::platform
