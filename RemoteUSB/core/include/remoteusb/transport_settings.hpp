// Settings shared by the configuration and the TLS transport.
#pragma once

#include <string>

namespace remoteusb {

struct TlsServerSettings {
    std::string certificate_file;
    std::string private_key_file;
    std::string private_key_password;
    std::string client_ca_file;
    bool require_client_certificate = true;
    bool minimum_tls13 = false;
};

}  // namespace remoteusb
