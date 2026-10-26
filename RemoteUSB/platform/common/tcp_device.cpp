// Serial-over-TCP device: terminal servers (raw TCP mode) and instrument simulators.
#include "remoteusb/platform.hpp"

namespace remoteusb::platform {

class TcpDevice final : public Device {
public:
    explicit TcpDevice(DeviceConfig config) : config_(std::move(config)) {}

    bool open(std::string& error) override {
        auto socket = Socket::connect(config_.host, config_.port, 5000, error);
        if (!socket) return false;
        socket_ = std::move(*socket);
        return true;
    }

    int read(std::uint8_t* buffer, std::size_t size, int timeout_ms) override {
        const int n = socket_.receive(buffer, size, timeout_ms);
        if (n == -2) return 0;
        if (n <= 0) return -1;
        return n;
    }

    int write(const std::uint8_t* data, std::size_t size) override {
        return socket_.send_all(data, size) ? static_cast<int>(size) : -1;
    }

    void close() override {
        socket_.shutdown();
        socket_.close();
    }

    std::string describe() const override { return "tcp://" + config_.host + ":" + std::to_string(config_.port); }

private:
    DeviceConfig config_;
    Socket socket_;
};

std::unique_ptr<Device> make_serial_device(const DeviceConfig& config);

std::unique_ptr<Device> make_device(const DeviceConfig& config) {
    if (config.kind == DeviceConfig::Kind::Tcp) return std::make_unique<TcpDevice>(config);
    return make_serial_device(config);
}

}  // namespace remoteusb::platform
