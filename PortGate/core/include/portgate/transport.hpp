// Byte transport of one controller connection: plain TCP or TLS. The agent core only sees this interface.
#pragma once

#include <cstdint>
#include <memory>
#include <string>

#include "portgate/platform.hpp"
#include "portgate/transport_settings.hpp"

namespace portgate {

class Transport {
public:
    virtual ~Transport() = default;
    virtual bool send_all(const std::uint8_t* data, std::size_t size) = 0;
    /// Returns bytes read, 0 on orderly close, -1 on failure, -2 on timeout.
    virtual int receive(std::uint8_t* buffer, std::size_t size, int timeout_ms) = 0;
    virtual void shutdown() = 0;
    virtual std::string peer() const = 0;
    /// True when the channel is TLS (any authentication).
    virtual bool encrypted() const { return false; }
    /// True when a client certificate was verified against the configured CA.
    virtual bool authenticated() const { return false; }
    /// Subject common name of the verified client certificate (TLS with client authentication), else empty.
    virtual std::string peer_common_name() const { return {}; }
    /// Lower-case hex SHA-256 of the client certificate (DER), else empty. Present also when it was not verified
    /// against a CA, so a pinned fingerprint can identify a self-signed client.
    virtual std::string peer_certificate_sha256() const { return {}; }
    /// PSK identity of the handshake, when the peer authenticated with the pre-shared key.
    virtual std::string psk_identity() const { return {}; }
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

/// Accepts TLS connections. Implemented with Mbed TLS (PortGate/tls); absent when the agent is built without TLS.
class TlsServer {
public:
    virtual ~TlsServer() = default;
    /// Runs the handshake on an accepted socket. Returns nullptr and an error on failure (never falls back to plain).
    virtual std::unique_ptr<Transport> accept(platform::Socket socket, int timeout_ms, std::string& error) = 0;

    /// Certificate, key and client CA from `material` (PEM, provisioned through SetTls).
    /// A PSK in `material` enables (EC)DHE-PSK key exchanges; plain PSK without forward secrecy is never offered.
    static std::unique_ptr<TlsServer> create(const TlsServerSettings& settings, const TlsMaterial& material, std::string& error);
    static bool available();
};

namespace tls {

/// Generates an ECDSA P-256 key and a self-signed certificate for `common_name` (agent identity on the admin endpoint).
bool generate_self_signed(const std::string& common_name, TlsMaterial& out, std::string& error);
/// Lower-case hex SHA-256 of the first certificate (DER) of a PEM chain; empty when it does not parse.
std::string certificate_sha256(const std::string& pem);

}  // namespace tls

}  // namespace portgate
