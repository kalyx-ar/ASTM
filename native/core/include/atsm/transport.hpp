// Byte transport of one controller connection: plain TCP or TLS. The agent core only sees this interface.
#pragma once

#include <cstdint>
#include <memory>
#include <string>

#include "atsm/platform.hpp"
#include "atsm/transport_settings.hpp"

namespace atsm {

class Transport {
public:
    virtual ~Transport() = default;
    virtual bool send_all(const std::uint8_t* data, std::size_t size) = 0;
    /// Returns bytes read, 0 on orderly close, -1 on failure, -2 on timeout.
    virtual int receive(std::uint8_t* buffer, std::size_t size, int timeout_ms) = 0;
    virtual void shutdown() = 0;
    virtual std::string peer() const = 0;
    virtual bool authenticated() const { return false; }
    /// Subject common name of the verified client certificate (TLS with client authentication), else empty.
    virtual std::string peer_common_name() const { return {}; }
    /// Lower-case hex SHA-256 of the verified client certificate (DER), else empty.
    virtual std::string peer_certificate_sha256() const { return {}; }
};

class PlainTransport final : public Transport {
public:
    explicit PlainTransport(platform::Socket socket) : socket_(std::move(socket)) {}
    bool send_all(const std::uint8_t* data, std::size_t size) override { return socket_.send_all(data, size); }
    int receive(std::uint8_t* buffer, std::size_t size, int timeout_ms) override { return socket_.receive(buffer, size, timeout_ms); }
    void shutdown() override { socket_.shutdown(); }
    std::string peer() const override { return socket_.peer(); }

private:
    platform::Socket socket_;
};

/// Accepts TLS connections. Implemented with Mbed TLS (native/tls); absent when the agent is built without TLS.
class TlsServer {
public:
    virtual ~TlsServer() = default;
    /// Runs the handshake on an accepted socket. Returns nullptr and an error on failure (never falls back to plain).
    virtual std::unique_ptr<Transport> accept(platform::Socket socket, int timeout_ms, std::string& error) = 0;

    static std::unique_ptr<TlsServer> create(const TlsServerSettings& settings, std::string& error);
    static bool available();
};

}  // namespace atsm
