// Build without TLS (PORTGATE_ENABLE_TLS=OFF): Tls endpoints refuse to start instead of falling back to plain.
#include "portgate/transport.hpp"

namespace portgate {

std::unique_ptr<TlsServer> TlsServer::create(const TlsServerSettings&, const TlsMaterial&, std::string& error) {
    error = "this agent was built without TLS (PORTGATE_ENABLE_TLS=OFF)";
    return nullptr;
}

bool TlsServer::available() { return false; }

namespace tls {

bool generate_self_signed(const std::string&, TlsMaterial&, std::string& error) {
    error = "this agent was built without TLS (PORTGATE_ENABLE_TLS=OFF)";
    return false;
}

std::string certificate_sha256(const std::string&) { return {}; }

}  // namespace tls

}  // namespace portgate

namespace portgate::tls {

std::unique_ptr<Transport> connect_client(platform::Socket, const std::string&, const TlsMaterial&, const std::string&, int, std::string& error) {
    error = "this agent was built without TLS (PORTGATE_ENABLE_TLS=OFF)";
    return nullptr;
}

}  // namespace portgate::tls

namespace portgate::tls {

bool seal(const std::vector<std::uint8_t>&, const std::vector<std::uint8_t>&, const std::vector<std::uint8_t>&, std::vector<std::uint8_t>&, std::vector<std::uint8_t>&,
          std::string& error) {
    error = "this agent was built without cryptography (PORTGATE_ENABLE_TLS=OFF): capture is unavailable";
    return false;
}

bool open(const std::vector<std::uint8_t>&, const std::vector<std::uint8_t>&, const std::vector<std::uint8_t>&, const std::vector<std::uint8_t>&,
          std::vector<std::uint8_t>&, std::string& error) {
    error = "this agent was built without cryptography (PORTGATE_ENABLE_TLS=OFF)";
    return false;
}

}  // namespace portgate::tls
