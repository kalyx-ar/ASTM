#pragma once
#include <condition_variable>
#include <functional>
#include <mutex>
#include <thread>
#include "portgate/virtual_com.hpp"
#include "portgate/virtual_com_protocol.hpp"
namespace portgate::vcom {
/// User-mode worker of one legacy open. It owns only IPC to PortGate, never the physical COM.
class Worker {
public:
    Worker(std::string name, std::function<bool(const VirtualComEvent&)> sink);
    ~Worker();
    bool start();
    bool write(std::uint64_t id, const link::Bytes& bytes);

    void fault(const std::string& reason);
    void close();
private:
    bool flush();
    void run();
    std::string name_;
    VirtualComProtocol protocol_;
    std::function<bool(const VirtualComEvent&)> sink_;
    std::unique_ptr<Transport> transport_;
    std::mutex mutex_;
    std::condition_variable changed_;
    std::thread thread_;
    bool stopping_=false;
};
}
