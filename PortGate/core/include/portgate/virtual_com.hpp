// OS frontend only: the COM driver enters the same Link protocol and arbiter as every other controller.
#pragma once
#include <functional>
#include <memory>
#include <string>
#include "portgate/transport.hpp"

namespace portgate {
bool virtual_com_supported();
std::unique_ptr<Transport> connect_virtual_com(const std::string& name, std::string& error);
class VirtualComListener {
public:
    virtual ~VirtualComListener() = default;
    virtual void stop() = 0;
    static std::unique_ptr<VirtualComListener> start(const std::string& name,
        std::function<void(std::unique_ptr<Transport>)> serve, std::string& error);
};
}
