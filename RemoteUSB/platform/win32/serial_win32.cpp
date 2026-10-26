// Win32 serial port. Uses only APIs available since Windows XP.
#include <windows.h>

#include "remoteusb/platform.hpp"

namespace remoteusb::platform {

class Win32Serial final : public Device {
public:
    explicit Win32Serial(DeviceConfig config) : config_(std::move(config)) {}
    ~Win32Serial() override { close(); }

    bool open(std::string& error) override {
        // "COM10" and above need the device namespace prefix.
        const std::string name = config_.path.rfind("\\\\.\\", 0) == 0 ? config_.path : "\\\\.\\" + config_.path;
        handle_ = CreateFileA(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
        if (handle_ == INVALID_HANDLE_VALUE) {
            error = "cannot open " + config_.path + " (error " + std::to_string(GetLastError()) + ")";
            return false;
        }
        DCB dcb{};
        dcb.DCBlength = sizeof(dcb);
        if (!GetCommState(handle_, &dcb)) return fail(error, "GetCommState");
        dcb.BaudRate = config_.baud_rate;
        dcb.ByteSize = config_.data_bits;
        dcb.fBinary = TRUE;
        dcb.StopBits = config_.stop_bits == 2 ? TWOSTOPBITS : ONESTOPBIT;
        switch (config_.parity) {
            case Parity::None: dcb.Parity = NOPARITY; dcb.fParity = FALSE; break;
            case Parity::Odd: dcb.Parity = ODDPARITY; dcb.fParity = TRUE; break;
            case Parity::Even: dcb.Parity = EVENPARITY; dcb.fParity = TRUE; break;
            case Parity::Mark: dcb.Parity = MARKPARITY; dcb.fParity = TRUE; break;
            case Parity::Space: dcb.Parity = SPACEPARITY; dcb.fParity = TRUE; break;
        }
        dcb.fOutxCtsFlow = config_.flow_control == FlowControl::RtsCts;
        dcb.fRtsControl = config_.flow_control == FlowControl::RtsCts ? RTS_CONTROL_HANDSHAKE : RTS_CONTROL_ENABLE;
        dcb.fDtrControl = DTR_CONTROL_ENABLE;
        dcb.fOutX = dcb.fInX = config_.flow_control == FlowControl::XonXoff;
        dcb.fOutxDsrFlow = FALSE;
        dcb.fDsrSensitivity = FALSE;
        dcb.fNull = FALSE;
        dcb.fAbortOnError = FALSE;
        if (!SetCommState(handle_, &dcb)) return fail(error, "SetCommState");
        PurgeComm(handle_, PURGE_RXCLEAR | PURGE_TXCLEAR);
        return true;
    }

    int read(std::uint8_t* buffer, std::size_t size, int timeout_ms) override {
        if (timeout_ms != timeout_) {
            // Return as soon as any byte is available, or after timeout_ms with nothing.
            COMMTIMEOUTS timeouts{};
            timeouts.ReadIntervalTimeout = MAXDWORD;
            timeouts.ReadTotalTimeoutMultiplier = MAXDWORD;
            timeouts.ReadTotalTimeoutConstant = static_cast<DWORD>(timeout_ms);
            timeouts.WriteTotalTimeoutConstant = 5000;
            if (!SetCommTimeouts(handle_, &timeouts)) return -1;
            timeout_ = timeout_ms;
        }
        DWORD read = 0;
        if (!ReadFile(handle_, buffer, static_cast<DWORD>(size), &read, nullptr)) return -1;
        return static_cast<int>(read);
    }

    int write(const std::uint8_t* data, std::size_t size) override {
        DWORD written = 0;
        if (!WriteFile(handle_, data, static_cast<DWORD>(size), &written, nullptr)) return -1;
        return static_cast<int>(written);
    }

    void close() override {
        if (handle_ != INVALID_HANDLE_VALUE) {
            CloseHandle(handle_);
            handle_ = INVALID_HANDLE_VALUE;
        }
    }

    std::string describe() const override { return "serial://" + config_.path; }

private:
    bool fail(std::string& error, const char* what) {
        error = std::string(what) + " failed on " + config_.path + " (error " + std::to_string(GetLastError()) + ")";
        close();
        return false;
    }

    DeviceConfig config_;
    HANDLE handle_ = INVALID_HANDLE_VALUE;
    int timeout_ = -1;
};

std::unique_ptr<Device> make_serial_device(const DeviceConfig& config) { return std::make_unique<Win32Serial>(config); }

}  // namespace remoteusb::platform
