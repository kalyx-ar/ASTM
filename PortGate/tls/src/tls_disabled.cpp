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
