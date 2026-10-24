// Build without TLS (ATSM_ENABLE_TLS=OFF): Tls endpoints refuse to start instead of falling back to plain.
#include "atsm/transport.hpp"

namespace atsm {

std::unique_ptr<TlsServer> TlsServer::create(const TlsServerSettings&, std::string& error) {
    error = "this agent was built without TLS (ATSM_ENABLE_TLS=OFF)";
    return nullptr;
}

bool TlsServer::available() { return false; }

}  // namespace atsm
