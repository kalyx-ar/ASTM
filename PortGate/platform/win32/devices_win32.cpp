// Serial port enumeration with SetupAPI and the configuration manager (available since Windows XP). Reads only
// properties; never opens a port.
#include <windows.h>
#include <initguid.h>
#include <devguid.h>
#include <setupapi.h>
#include <cfgmgr32.h>

#include <algorithm>
#include <cctype>

#include "portgate/platform.hpp"

namespace portgate::platform {

namespace {

std::string narrow(const std::wstring& text) {
    if (text.empty()) return {};
    const int size = WideCharToMultiByte(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
    std::string out(static_cast<std::size_t>(size), '\0');
    WideCharToMultiByte(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), out.data(), size, nullptr, nullptr);
    return out;
}

std::wstring registry_property(HDEVINFO set, SP_DEVINFO_DATA& data, DWORD property) {
    wchar_t buffer[512] = {};
    DWORD type = 0;
    if (!SetupDiGetDeviceRegistryPropertyW(set, &data, property, &type, reinterpret_cast<PBYTE>(buffer), sizeof(buffer) - sizeof(wchar_t), nullptr))
        return {};
    return buffer;  // REG_MULTI_SZ: the first string is enough (most specific hardware id)
}

std::wstring node_id(DEVINST node) {
    wchar_t buffer[MAX_DEVICE_ID_LEN + 1] = {};
    return CM_Get_Device_IDW(node, buffer, MAX_DEVICE_ID_LEN, 0) == CR_SUCCESS ? std::wstring(buffer) : std::wstring();
}

std::wstring node_location(DEVINST node) {
    wchar_t buffer[256] = {};
    ULONG size = sizeof(buffer) - sizeof(wchar_t);
    return CM_Get_DevNode_Registry_PropertyW(node, CM_DRP_LOCATION_INFORMATION, nullptr, buffer, &size, 0) == CR_SUCCESS ? std::wstring(buffer)
                                                                                                                        : std::wstring();
}

std::string upper(std::string text) {
    for (auto& c : text) c = static_cast<char>(std::toupper(static_cast<unsigned char>(c)));
    return text;
}

/// Four hex digits after "VID_"/"PID_" in a hardware or instance id ("USB\VID_067B&PID_2303", "FTDIBUS\VID_0403+PID_6001+...").
std::string field(const std::string& id, const std::string& key) {
    const auto at = upper(id).find(key);
    if (at == std::string::npos || at + key.size() + 4 > id.size()) return {};
    return upper(id.substr(at + key.size(), 4));
}

/// Serial number of the adapter, when the instance id carries a real one. Windows generates an instance with '&'
/// when the device has no serial number ("5&2a1b3c&0&2"); that one is a location, not an identity.
std::string usb_serial(const std::string& id) {
    if (upper(id).rfind("FTDIBUS\\", 0) == 0) {
        // FTDIBUS\VID_0403+PID_6001+A10KX3QZA\0000: serial followed by the channel letter.
        const auto plus = id.rfind('+');
        const auto slash = id.find('\\', plus == std::string::npos ? 0 : plus);
        if (plus == std::string::npos || slash == std::string::npos || slash <= plus + 2) return {};
        return id.substr(plus + 1, slash - plus - 2);
    }
    if (upper(id).rfind("USB\\", 0) != 0) return {};
    const auto slash = id.rfind('\\');
    if (slash == std::string::npos) return {};
    const auto last = id.substr(slash + 1);
    return last.find('&') == std::string::npos ? last : std::string();
}

}  // namespace

std::vector<SerialDeviceInfo> list_serial_devices() {
    std::vector<SerialDeviceInfo> devices;
    HDEVINFO set = SetupDiGetClassDevsW(&GUID_DEVCLASS_PORTS, nullptr, nullptr, DIGCF_PRESENT);
    if (set == INVALID_HANDLE_VALUE) return devices;
    SP_DEVINFO_DATA data{};
    data.cbSize = sizeof(data);
    for (DWORD i = 0; SetupDiEnumDeviceInfo(set, i, &data); ++i) {
        SerialDeviceInfo info;
        if (HKEY key = SetupDiOpenDevRegKey(set, &data, DICS_FLAG_GLOBAL, 0, DIREG_DEV, KEY_READ); key != INVALID_HANDLE_VALUE) {
            wchar_t name[64] = {};
            DWORD size = sizeof(name) - sizeof(wchar_t);
            DWORD type = 0;
            if (RegQueryValueExW(key, L"PortName", nullptr, &type, reinterpret_cast<LPBYTE>(name), &size) == ERROR_SUCCESS && type == REG_SZ)
                info.name = narrow(name);
            RegCloseKey(key);
        }
        if (info.name.rfind("COM", 0) != 0) continue;  // LPT and other port classes
        info.friendly_name = narrow(registry_property(set, data, SPDRP_FRIENDLYNAME));
        info.instance_id = narrow(node_id(data.DevInst));
        // The USB identity lives on the COM node (FTDI) or on its parent USB device (CDC/ACM, Prolific, CH340...).
        std::string identity = info.instance_id;
        DEVINST parent = 0;
        if (field(identity, "VID_").empty() && CM_Get_Parent(&parent, data.DevInst, 0) == CR_SUCCESS) {
            identity = narrow(node_id(parent));
            info.location = narrow(node_location(parent));
        } else {
            info.location = narrow(node_location(data.DevInst));
            if (info.location.empty() && CM_Get_Parent(&parent, data.DevInst, 0) == CR_SUCCESS) info.location = narrow(node_location(parent));
        }
        info.usb_vid = field(identity, "VID_");
        info.usb_pid = field(identity, "PID_");
        info.usb_serial = usb_serial(identity);
        devices.push_back(std::move(info));
    }
    SetupDiDestroyDeviceInfoList(set);
    std::sort(devices.begin(), devices.end(), [](const SerialDeviceInfo& a, const SerialDeviceInfo& b) { return a.name < b.name; });
    return devices;
}

}  // namespace portgate::platform
