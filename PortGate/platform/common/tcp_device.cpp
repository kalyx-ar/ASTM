// TCP devices: serial-over-TCP and Ethernet instruments that listen (Tcp), and Ethernet instruments that connect (TcpListen).
#include <atomic>
#include <mutex>

#include "portgate/platform.hpp"

namespace portgate::platform {

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

/// Ethernet instrument that acts as a TCP client: the agent listens and serves one instrument connection at a time.
/// When the instrument disconnects, the port stays open waiting for it to reconnect.
class TcpListenDevice final : public Device {
public:
    explicit TcpListenDevice(DeviceConfig config) : config_(std::move(config)) {}

    bool open(std::string& error) override { return listener_.open(config_.host.empty() ? "0.0.0.0" : config_.host, config_.port, error); }

    int read(std::uint8_t* buffer, std::size_t size, int timeout_ms) override {
        if (!connected_) {
            auto accepted = listener_.accept(timeout_ms);
            if (!accepted.valid()) return 0;
            std::lock_guard<std::mutex> lock(mutex_);
            client_ = std::move(accepted);
            connected_ = true;
            return 0;
        }
        const int n = client_.receive(buffer, size, timeout_ms);
        if (n == -2) return 0;
        if (n <= 0) {
            // The instrument closed its connection: keep listening for the next one.
            std::lock_guard<std::mutex> lock(mutex_);
            connected_ = false;
            client_.close();
            return 0;
        }
        return n;
    }

    int write(const std::uint8_t* data, std::size_t size) override {
        std::lock_guard<std::mutex> lock(mutex_);
        if (!connected_) return -1;
        return client_.send_all(data, size) ? static_cast<int>(size) : -1;
    }

    void close() override {
        std::lock_guard<std::mutex> lock(mutex_);
        connected_ = false;
        client_.shutdown();
        client_.close();
        listener_.close();
    }

    std::string describe() const override {
        return "tcp-listen://" + (config_.host.empty() ? std::string("0.0.0.0") : config_.host) + ":" + std::to_string(config_.port);
    }

private:
    DeviceConfig config_;
    Listener listener_;
    Socket client_;
    std::mutex mutex_;
    std::atomic<bool> connected_{false};
};

std::unique_ptr<Device> make_serial_device(const DeviceConfig& config);
std::unique_ptr<Device> make_test_serial_device(const DeviceConfig& config);

std::unique_ptr<Device> make_device(const DeviceConfig& config) {
    if (config.kind == DeviceConfig::Kind::Tcp) return std::make_unique<TcpDevice>(config);
    if (config.kind == DeviceConfig::Kind::TcpListen) return std::make_unique<TcpListenDevice>(config);
    if (config.path.rfind("test://", 0) == 0) return make_test_serial_device(config);
    return make_serial_device(config);
}

}  // namespace portgate::platform
