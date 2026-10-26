// Build without TLS (REMOTEUSB_ENABLE_TLS=OFF): Tls endpoints refuse to start instead of falling back to plain.
#include "remoteusb/transport.hpp"

namespace remoteusb {

std::unique_ptr<TlsServer> TlsServer::create(const TlsServerSettings&, const TlsMaterial&, std::string& error) {
    error = "this agent was built without TLS (REMOTEUSB_ENABLE_TLS=OFF)";
    return nullptr;
}

bool TlsServer::available() { return false; }

namespace tls {

bool generate_self_signed(const std::string&, TlsMaterial&, std::string& error) {
    error = "this agent was built without TLS (REMOTEUSB_ENABLE_TLS=OFF)";
    return false;
}

std::string certificate_sha256(const std::string&) { return {}; }

}  // namespace tls

}  // namespace remoteusb
