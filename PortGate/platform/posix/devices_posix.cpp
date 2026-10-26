// Serial port enumeration on Linux: udev links under /dev/serial/by-id (identity) and /dev/serial/by-path (location).
#include <algorithm>
#include <filesystem>
#include <fstream>
#include <map>

#include "portgate/platform.hpp"

namespace portgate::platform {

namespace fs = std::filesystem;

namespace {

std::map<std::string, std::string> links(const char* directory) {
    std::map<std::string, std::string> out;  // resolved device -> link name
    std::error_code code;
    for (const auto& entry : fs::directory_iterator(directory, code)) {
        const auto target = fs::canonical(entry.path(), code);
        if (!code) out[target.string()] = entry.path().filename().string();
    }
    return out;
}

std::string sysfs(const std::string& tty, const char* attribute, bool hex = true) {
    // /sys/class/tty/ttyUSB0/device -> interface; the USB device with idVendor is one or two levels up.
    std::error_code code;
    auto device = fs::canonical("/sys/class/tty/" + tty + "/device", code);
    for (int level = 0; level < 4 && !code; ++level, device = device.parent_path()) {
        std::ifstream file(device / attribute);
        std::string value;
        if (file && std::getline(file, value)) {
            if (hex) for (auto& c : value) c = static_cast<char>(std::toupper(static_cast<unsigned char>(c)));
            return value;
        }
    }
    return {};
}

}  // namespace

std::vector<SerialDeviceInfo> list_serial_devices() {
    std::vector<SerialDeviceInfo> devices;
    const auto by_id = links("/dev/serial/by-id");
    const auto by_path = links("/dev/serial/by-path");
    std::error_code code;
    for (const auto& entry : fs::directory_iterator("/dev", code)) {
        const auto name = entry.path().filename().string();
        if (name.rfind("ttyUSB", 0) != 0 && name.rfind("ttyACM", 0) != 0 && name.rfind("ttyS", 0) != 0 && name.rfind("ttyAMA", 0) != 0) continue;
        const auto path = entry.path().string();
        const auto id = by_id.find(path);
        const auto location = by_path.find(path);
        if (name.rfind("ttyS", 0) == 0 && id == by_id.end() && !fs::exists("/sys/class/tty/" + name + "/device", code)) continue;
        SerialDeviceInfo info;
        info.name = path;
        info.instance_id = id == by_id.end() ? std::string() : id->second;
        info.friendly_name = info.instance_id.empty() ? name : info.instance_id;
        info.location = location == by_path.end() ? std::string() : location->second;
        info.usb_vid = sysfs(name, "idVendor");
        info.usb_pid = sysfs(name, "idProduct");
        info.usb_serial = sysfs(name, "serial", false);
        devices.push_back(std::move(info));
    }
    std::sort(devices.begin(), devices.end(), [](const SerialDeviceInfo& a, const SerialDeviceInfo& b) { return a.name < b.name; });
    return devices;
}

}  // namespace portgate::platform
