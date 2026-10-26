// Protected state of the agent: the configuration in force, who administers it and the TLS material provisioned by
// the server. Only the current values are kept; versions and history of the configuration belong to the server.
#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <vector>

#include "remoteusb/config.hpp"

namespace remoteusb {

/// Identity of the administering server recorded when it first configured the agent.
struct AdminAccess {
    bool claimed = false;
    /// Only this server may administer (fixed at installation, then as decided by SetConfig mode.restricted).
    bool restricted = false;
    std::string server_id;           // id announced in Hello
    std::string certificate_sha256;  // verified client certificate, when the admin channel used TLS
    std::string address;             // peer address (host, no port), the only identity on a plain channel
};


struct AgentState {
    bool configured = false;
    AgentConfig config;
    std::uint64_t revision = 0;  // assigned by the server; the agent only echoes it
    AdminAccess admin;
    TlsMaterial link_tls;
    TlsMaterial admin_tls;
};

class StateStore {
public:
    explicit StateStore(std::string directory) : directory_(std::move(directory)) {}

    /// Returns an empty optional with an empty error when no state exists yet (fresh installation).
    std::optional<AgentState> load(std::string& error) const;
    bool save(const AgentState& state, std::string& error) const;
    const std::string& directory() const { return directory_; }

private:
    std::string directory_;
};

}  // namespace remoteusb
