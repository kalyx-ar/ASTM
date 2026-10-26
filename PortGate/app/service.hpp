// Operating-system service integration: Windows service control manager (service_win32.cpp); on POSIX the agent
// runs under systemd (PortGate/packaging/portgate.service) and these entry points report that.
#pragma once

#include <atomic>
#include <functional>
#include <string>
#include <vector>

namespace portgate::service {

/// install | uninstall [--purge] | start | stop. `run_arguments` are the `run` options stored in the service command line.
int command(const std::string& verb, const std::vector<std::string>& run_arguments, const std::string& state_directory, bool purge);

/// Runs `body` as the service main (called by the service control manager); `body` returns when `stop` becomes true.
int dispatch(const std::function<int(std::atomic<bool>& stop)>& body);

}  // namespace portgate::service
