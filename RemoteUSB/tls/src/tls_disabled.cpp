// Build without TLS (REMOTEUSB_ENABLE_TLS=OFF): Tls endpoints refuse to start instead of falling back to plain.
#include "remoteusb/transport.hpp"

namespace remoteusb {

std::unique_ptr<TlsServer> TlsServer::create(const TlsServerSettings&, std::string& error) {
    error = "this agent was built without TLS (REMOTEUSB_ENABLE_TLS=OFF)";
    return nullptr;
}

bool TlsServer::available() { return false; }

}  // namespace remoteusb
