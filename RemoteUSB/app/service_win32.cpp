// Windows service: automatic start, restart on failure, least-privilege account (LocalService) and a state directory
// readable only by SYSTEM, Administrators and LocalService. Uses APIs available since Windows XP.
#include <windows.h>
#include <sddl.h>

#include <filesystem>
#include <iostream>

#include "service.hpp"

namespace remoteusb::service {

namespace {

constexpr wchar_t kName[] = L"RemoteUSB";
constexpr wchar_t kDisplayName[] = L"RemoteUSB";
constexpr wchar_t kDescription[] = L"Serial ports over the network for remote servers (RemoteUSB Link), administered remotely.";

SERVICE_STATUS_HANDLE g_status_handle = nullptr;
SERVICE_STATUS g_status{};
std::atomic<bool>* g_stop = nullptr;
const std::function<int(std::atomic<bool>&)>* g_body = nullptr;

std::wstring widen(const std::string& text) {
    if (text.empty()) return {};
    const int size = MultiByteToWideChar(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), nullptr, 0);
    std::wstring out(static_cast<std::size_t>(size), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), out.data(), size);
    return out;
}

int report(const char* what) {
    const DWORD code = GetLastError();
    std::cerr << what << " failed (error " << code << ")" << (code == ERROR_ACCESS_DENIED ? ": run as administrator" : "") << "\n";
    return 77;
}

std::wstring quote(const std::wstring& argument) {
    if (!argument.empty() && argument.find_first_of(L" \t\"") == std::wstring::npos) return argument;
    std::wstring out = L"\"";
    for (wchar_t c : argument) {
        if (c == L'"') out += L'\\';
        out += c;
    }
    return out + L"\"";
}

bool protect_directory(const std::string& directory) {
    std::error_code ignored;
    std::filesystem::create_directories(std::filesystem::u8path(directory), ignored);
    // Protected DACL: SYSTEM, Administrators and LocalService (the service account); no inheritance from ProgramData,
    // so ordinary users cannot read the sealed state or the event log.
    PSECURITY_DESCRIPTOR descriptor = nullptr;
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(L"D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;FA;;;LS)", SDDL_REVISION_1, &descriptor, nullptr))
        return false;
    const BOOL ok = SetFileSecurityW(widen(directory).c_str(), DACL_SECURITY_INFORMATION | PROTECTED_DACL_SECURITY_INFORMATION, descriptor);
    LocalFree(descriptor);
    return ok != FALSE;
}

void set_state(DWORD state, DWORD exit_code = NO_ERROR) {
    g_status.dwServiceType = SERVICE_WIN32_OWN_PROCESS;
    g_status.dwCurrentState = state;
    g_status.dwControlsAccepted = state == SERVICE_RUNNING ? SERVICE_ACCEPT_STOP | SERVICE_ACCEPT_SHUTDOWN : 0;
    g_status.dwWin32ExitCode = exit_code == NO_ERROR ? NO_ERROR : ERROR_SERVICE_SPECIFIC_ERROR;
    g_status.dwServiceSpecificExitCode = exit_code;
    g_status.dwWaitHint = state == SERVICE_RUNNING || state == SERVICE_STOPPED ? 0 : 10000;
    SetServiceStatus(g_status_handle, &g_status);
}

DWORD WINAPI control(DWORD code, DWORD, LPVOID, LPVOID) {
    if (code == SERVICE_CONTROL_STOP || code == SERVICE_CONTROL_SHUTDOWN) {
        set_state(SERVICE_STOP_PENDING);
        if (g_stop) *g_stop = true;
        return NO_ERROR;
    }
    return code == SERVICE_CONTROL_INTERROGATE ? NO_ERROR : ERROR_CALL_NOT_IMPLEMENTED;
}

void WINAPI service_main(DWORD, LPWSTR*) {
    g_status_handle = RegisterServiceCtrlHandlerExW(kName, control, nullptr);
    if (!g_status_handle) return;
    std::atomic<bool> stop{false};
    g_stop = &stop;
    set_state(SERVICE_RUNNING);
    const int result = (*g_body)(stop);
    set_state(SERVICE_STOPPED, static_cast<DWORD>(result));
}

}  // namespace

int command(const std::string& verb, const std::vector<std::string>& run_arguments, const std::string& state_directory, bool purge) {
    SC_HANDLE manager = OpenSCManagerW(nullptr, nullptr, verb == "install" || verb == "uninstall" ? SC_MANAGER_ALL_ACCESS : SC_MANAGER_CONNECT);
    if (!manager) return report("OpenSCManager");
    int result = 0;
    if (verb == "install") {
        wchar_t executable[MAX_PATH] = {};
        GetModuleFileNameW(nullptr, executable, MAX_PATH);
        std::wstring command_line = quote(executable) + L" service run";
        for (const auto& argument : run_arguments) command_line += L" " + quote(widen(argument));
        if (!protect_directory(state_directory)) {
            CloseServiceHandle(manager);
            return report("protecting the state directory");
        }
        SC_HANDLE service = CreateServiceW(manager, kName, kDisplayName, SERVICE_ALL_ACCESS, SERVICE_WIN32_OWN_PROCESS, SERVICE_AUTO_START,
                                           SERVICE_ERROR_NORMAL, command_line.c_str(), nullptr, nullptr, nullptr, L"NT AUTHORITY\\LocalService", nullptr);
        if (!service) {
            result = report("CreateService");
        } else {
            SERVICE_DESCRIPTIONW description{const_cast<LPWSTR>(kDescription)};
            ChangeServiceConfig2W(service, SERVICE_CONFIG_DESCRIPTION, &description);
            // Restart after 5 s, three times; the counter resets after one day without failures.
            SC_ACTION actions[3] = {{SC_ACTION_RESTART, 5000}, {SC_ACTION_RESTART, 5000}, {SC_ACTION_RESTART, 5000}};
            SERVICE_FAILURE_ACTIONSW failure{86400, nullptr, nullptr, 3, actions};
            ChangeServiceConfig2W(service, SERVICE_CONFIG_FAILURE_ACTIONS, &failure);
            std::cout << "service installed; state in " << state_directory << "\n";
            CloseServiceHandle(service);
        }
    } else {
        SC_HANDLE service = OpenServiceW(manager, kName, SERVICE_ALL_ACCESS);
        if (!service) {
            result = report("OpenService");
        } else if (verb == "start") {
            if (!StartServiceW(service, 0, nullptr)) result = report("StartService");
        } else if (verb == "stop" || verb == "uninstall") {
            SERVICE_STATUS status{};
            ControlService(service, SERVICE_CONTROL_STOP, &status);
            if (verb == "uninstall") {
                if (!DeleteService(service)) result = report("DeleteService");
                else if (purge) {
                    // Explicit and documented: removing the state is the way back to an unconfigured agent.
                    std::error_code ignored;
                    std::filesystem::remove_all(std::filesystem::u8path(state_directory), ignored);
                    std::cout << "state removed\n";
                }
            }
        }
        if (service) CloseServiceHandle(service);
    }
    CloseServiceHandle(manager);
    return result;
}

int dispatch(const std::function<int(std::atomic<bool>&)>& body) {
    g_body = &body;
    SERVICE_TABLE_ENTRYW table[] = {{const_cast<LPWSTR>(kName), service_main}, {nullptr, nullptr}};
    if (!StartServiceCtrlDispatcherW(table)) {
        if (GetLastError() == ERROR_FAILED_SERVICE_CONTROLLER_CONNECT) {
            std::cerr << "`service run` is started by the service control manager; use `remoteusb run` in a console\n";
            return 64;
        }
        return report("StartServiceCtrlDispatcher");
    }
    return 0;
}

}  // namespace remoteusb::service
