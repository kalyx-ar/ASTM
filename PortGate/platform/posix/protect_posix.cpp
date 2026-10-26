// POSIX: no machine-bound sealing in the base system; the state and event files are owner-only (0600) under a 0700
// directory owned by the service user.
#include "portgate/platform.hpp"

namespace portgate::platform {

bool protect(const Bytes& plain, Bytes& sealed, std::string&) {
    sealed = plain;
    return true;
}

bool unprotect(const Bytes& sealed, Bytes& plain, std::string&) {
    plain = sealed;
    return true;
}

}  // namespace portgate::platform
