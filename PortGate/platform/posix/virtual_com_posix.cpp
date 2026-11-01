#include "portgate/virtual_com.hpp"
namespace portgate {
bool virtual_com_supported() { return false; }
std::unique_ptr<Transport> connect_virtual_com(const std::string&,std::string& error) {
    error="Windows virtual COM driver IPC is unavailable on this platform"; return nullptr;
}
std::unique_ptr<VirtualComListener> VirtualComListener::start(const std::string&,
    std::function<void(std::unique_ptr<Transport>)>, std::string& error) {
    error = "Windows virtual COM driver is unavailable on this platform";
    return nullptr;
}
}
