// Test serial line, backed by a loopback peer of the bench. No OS COM driver or hardware is involved.
#include <algorithm>
#include <atomic>
#include <charconv>
#include <chrono>
#include <condition_variable>
#include <mutex>
#include <vector>

#include "portgate/platform.hpp"

namespace portgate::platform {

class TestSerial final : public Device {
public:
    explicit TestSerial(DeviceConfig config) : config_(std::move(config)) {}
    bool open(std::string& error) override {
        const std::string prefix = "test://127.0.0.1:";
        if (config_.path.rfind(prefix, 0) != 0 || config_.flow_control != FlowControl::None) {
            error = "test serial requires a loopback endpoint and no hardware/software flow control";
            return false;
        }
        unsigned port = 0;
        const auto digits = config_.path.substr(prefix.size());
        const auto parsed = std::from_chars(digits.data(), digits.data() + digits.size(), port);
        if (parsed.ec != std::errc{} || parsed.ptr != digits.data() + digits.size() || port == 0 || port > 65535) {
            error = "invalid test serial peer port";
            return false;
        }
        auto peer = Socket::connect("127.0.0.1", static_cast<std::uint16_t>(port), 5000, error);
        if (!peer) return false;
        socket_ = std::move(*peer);
        opened_ = true;
        return true;
    }
    int read(std::uint8_t* buffer, std::size_t size, int timeout_ms) override {
        if (size == 0) return 0;
        const int n = socket_.receive(buffer, std::min(size, kChunk), timeout_ms);
        if (n == -2) return 0;
        if (n <= 0 || !pace(static_cast<std::size_t>(n))) return -1;
        for (int i = 0; i < n; ++i) buffer[i] &= mask();
        return n;
    }
    int write(const std::uint8_t* data, std::size_t size) override {
        std::vector<std::uint8_t> chunk;
        for (std::size_t at = 0; at < size; at += kChunk) {
            const auto count = std::min(kChunk, size - at);
            chunk.assign(data + at, data + at + count);
            for (auto& value : chunk) value &= mask();
            if (!pace(count) || !socket_.send_all(chunk.data(), chunk.size())) return -1;
        }
        return static_cast<int>(size);
    }
    void close() override {
        opened_ = false;
        wake_.notify_all();
        socket_.shutdown();
        socket_.close();
    }
    std::string describe() const override { return "test-serial " + config_.path + " " + std::to_string(config_.baud_rate) + " baud"; }
private:
    bool pace(std::size_t bytes) {
        const auto bits = 1u + config_.data_bits + (config_.parity == Parity::None ? 0u : 1u) + config_.stop_bits;
        const auto ns = (static_cast<std::uint64_t>(bytes) * bits * 1000000000ull + config_.baud_rate - 1) / config_.baud_rate;
        std::unique_lock<std::mutex> lock(wait_mutex_);
        return !wake_.wait_for(lock, std::chrono::nanoseconds(ns), [&] { return !opened_.load(); });
    }
    std::uint8_t mask() const { return static_cast<std::uint8_t>((1u << config_.data_bits) - 1); }
    static constexpr std::size_t kChunk = 16;
    DeviceConfig config_;
    Socket socket_;
    std::atomic<bool> opened_{false};
    std::mutex wait_mutex_;
    std::condition_variable wake_;
};

std::unique_ptr<Device> make_test_serial_device(const DeviceConfig& config) { return std::make_unique<TestSerial>(config); }

}  // namespace portgate::platform
