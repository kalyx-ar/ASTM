// POSIX termios serial port (Linux, Raspberry Pi OS, macOS).
#include <fcntl.h>
#include <poll.h>
#include <termios.h>
#include <unistd.h>

#include <cerrno>
#include <cstring>

#include "atsm/platform.hpp"

namespace atsm::platform {

namespace {

speed_t to_speed(std::uint32_t baud) {
    switch (baud) {
        case 1200: return B1200;
        case 2400: return B2400;
        case 4800: return B4800;
        case 9600: return B9600;
        case 19200: return B19200;
        case 38400: return B38400;
        case 57600: return B57600;
        case 115200: return B115200;
        case 230400: return B230400;
        default: return 0;
    }
}

}  // namespace

class PosixSerial final : public Device {
public:
    explicit PosixSerial(DeviceConfig config) : config_(std::move(config)) {}
    ~PosixSerial() override { close(); }

    bool open(std::string& error) override {
        fd_ = ::open(config_.path.c_str(), O_RDWR | O_NOCTTY | O_NONBLOCK);
        if (fd_ < 0) return fail(error, "open");
        termios tio{};
        if (tcgetattr(fd_, &tio) != 0) return fail(error, "tcgetattr");
        cfmakeraw(&tio);
        const speed_t speed = to_speed(config_.baud_rate);
        if (speed == 0) {
            error = "unsupported baud rate " + std::to_string(config_.baud_rate);
            close();
            return false;
        }
        cfsetispeed(&tio, speed);
        cfsetospeed(&tio, speed);
        tio.c_cflag &= ~static_cast<tcflag_t>(CSIZE | PARENB | PARODD | CSTOPB);
        tio.c_cflag |= CLOCAL | CREAD;
        switch (config_.data_bits) {
            case 5: tio.c_cflag |= CS5; break;
            case 6: tio.c_cflag |= CS6; break;
            case 7: tio.c_cflag |= CS7; break;
            default: tio.c_cflag |= CS8; break;
        }
        if (config_.stop_bits == 2) tio.c_cflag |= CSTOPB;
        if (config_.parity == Parity::Odd) tio.c_cflag |= PARENB | PARODD;
        if (config_.parity == Parity::Even) tio.c_cflag |= PARENB;
        if (config_.parity == Parity::Mark || config_.parity == Parity::Space) {
            error = "mark/space parity is not supported by this POSIX adapter";
            close();
            return false;
        }
#ifdef CRTSCTS
        if (config_.flow_control == FlowControl::RtsCts) tio.c_cflag |= CRTSCTS; else tio.c_cflag &= ~static_cast<tcflag_t>(CRTSCTS);
#endif
        if (config_.flow_control == FlowControl::XonXoff) tio.c_iflag |= IXON | IXOFF;
        tio.c_cc[VMIN] = 0;
        tio.c_cc[VTIME] = 0;
        if (tcsetattr(fd_, TCSANOW, &tio) != 0) return fail(error, "tcsetattr");
        tcflush(fd_, TCIOFLUSH);
        return true;
    }

    int read(std::uint8_t* buffer, std::size_t size, int timeout_ms) override {
        pollfd fd{fd_, POLLIN, 0};
        const int ready = ::poll(&fd, 1, timeout_ms);
        if (ready == 0) return 0;
        if (ready < 0) return errno == EINTR ? 0 : -1;
        const auto n = ::read(fd_, buffer, size);
        if (n < 0) return errno == EAGAIN ? 0 : -1;
        return static_cast<int>(n);
    }

    int write(const std::uint8_t* data, std::size_t size) override {
        std::size_t written = 0;
        while (written < size) {
            const auto n = ::write(fd_, data + written, size - written);
            if (n < 0) {
                if (errno == EAGAIN || errno == EINTR) {
                    pollfd fd{fd_, POLLOUT, 0};
                    if (::poll(&fd, 1, 5000) <= 0) return static_cast<int>(written);
                    continue;
                }
                return written == 0 ? -1 : static_cast<int>(written);
            }
            written += static_cast<std::size_t>(n);
        }
        tcdrain(fd_);
        return static_cast<int>(written);
    }

    void close() override {
        if (fd_ >= 0) {
            ::close(fd_);
            fd_ = -1;
        }
    }

    std::string describe() const override { return "serial://" + config_.path; }

private:
    bool fail(std::string& error, const char* what) {
        error = std::string(what) + " failed on " + config_.path + ": " + std::strerror(errno);
        close();
        return false;
    }

    DeviceConfig config_;
    int fd_ = -1;
};

std::unique_ptr<Device> make_serial_device(const DeviceConfig& config) { return std::make_unique<PosixSerial>(config); }

}  // namespace atsm::platform
