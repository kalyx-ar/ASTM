// POSIX: the service manager is systemd (RemoteUSB/packaging/remoteusb.service runs `remoteusb run`).
#include <iostream>

#include "service.hpp"

namespace remoteusb::service {

int command(const std::string&, const std::vector<std::string>&, const std::string&, bool) {
    std::cerr << "on Linux install RemoteUSB/packaging/remoteusb.service and use systemctl\n";
    return 64;
}

int dispatch(const std::function<int(std::atomic<bool>&)>&) {
    std::cerr << "`service run` exists only on Windows; systemd runs `remoteusb run`\n";
    return 64;
}

}  // namespace remoteusb::service
