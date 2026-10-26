// Settings shared by the configuration and the TLS transport.
#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace portgate {

struct TlsServerSettings {
    bool require_client_certificate = true;
    bool minimum_tls13 = false;
};

/// TLS material provisioned by the server, in PEM form, plus an optional pre-shared key. Kept only in the protected
/// state, never in GetConfig.
struct TlsMaterial {
    std::string certificate_pem;
    std::string private_key_pem;
    std::string ca_pem;
    std::string psk_identity;
    std::vector<std::uint8_t> psk;

    bool has_certificate() const { return !certificate_pem.empty() && !private_key_pem.empty(); }
    bool has_psk() const { return !psk_identity.empty() && !psk.empty(); }
};

}  // namespace portgate
