// POSIX: the service manager is systemd (PortGate/packaging/portgate.service runs `portgate run`).
#include <iostream>

#include "service.hpp"

namespace portgate::service {

int command(const std::string&, const std::vector<std::string>&, const std::string&, bool) {
    std::cerr << "on Linux install PortGate/packaging/portgate.service and use systemctl\n";
    return 64;
}

int dispatch(const std::function<int(std::atomic<bool>&)>&) {
    std::cerr << "`service run` exists only on Windows; systemd runs `portgate run`\n";
    return 64;
}

}  // namespace portgate::service
