#include <windows.h>
#include <sddl.h>
#include <aclapi.h>
#include <algorithm>
#include <cstring>
#include <atomic>
#include <thread>
#include "portgate/virtual_com.hpp"

namespace portgate {
bool virtual_com_supported() {
#ifdef PORTGATE_WINDOWS_XP
    return false;
#else
    // Read the real OS version independently of application compatibility manifests.
    using QueryVersion=LONG(WINAPI*)(OSVERSIONINFOW*);
    const auto address=GetProcAddress(GetModuleHandleW(L"ntdll.dll"),"RtlGetVersion");
    QueryVersion query=nullptr; static_assert(sizeof(query)==sizeof(address)); std::memcpy(&query,&address,sizeof(query));
    OSVERSIONINFOW version{}; version.dwOSVersionInfoSize=sizeof(version);
    if(!query || query(&version)!=0 || version.dwMajorVersion<10 || (version.dwMajorVersion==10 && version.dwBuildNumber<17134)) return false;
#ifdef _WIN64
    return true;
#else
    BOOL wow=FALSE; return IsWow64Process(GetCurrentProcess(),&wow) && wow;
#endif
#endif
}
namespace {
class PipeTransport final : public Transport {
public:
    explicit PipeTransport(HANDLE pipe) : pipe_(pipe), stopped_(CreateEventW(nullptr, TRUE, FALSE, nullptr)) {}
    ~PipeTransport() override { CloseHandle(pipe_); CloseHandle(stopped_); }
    bool send_all(const std::uint8_t* data, std::size_t size) override {
        while (size) {
            const int n = transfer(true, const_cast<std::uint8_t*>(data), static_cast<DWORD>((std::min)(size, std::size_t{65536})), 10000);
            if (n <= 0) return false;
            data += n; size -= static_cast<std::size_t>(n);
        }
        return true;
    }
    int receive(std::uint8_t* data, std::size_t size, int timeout) override {
        return transfer(false, data, static_cast<DWORD>((std::min)(size, std::size_t{65536})), timeout);
    }
    void shutdown() override { SetEvent(stopped_); }
    std::string peer() const override { return "local-vcom-driver"; }
private:
    int transfer(bool write, std::uint8_t* data, DWORD size, int timeout) {
        OVERLAPPED operation{}; operation.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (!operation.hEvent) return -1;
        DWORD n = 0;
        const BOOL done = write ? WriteFile(pipe_, data, size, &n, &operation) : ReadFile(pipe_, data, size, &n, &operation);
        int result = -1;
        if (done) result = static_cast<int>(n);
        else if (GetLastError() == ERROR_IO_PENDING) {
            HANDLE events[]{operation.hEvent, stopped_};
            const DWORD ready = WaitForMultipleObjects(2, events, FALSE, static_cast<DWORD>(timeout));
            if (ready == WAIT_OBJECT_0 && GetOverlappedResult(pipe_, &operation, &n, FALSE)) result = static_cast<int>(n);
            else {
                CancelIo(pipe_); // Same thread that started this I/O; do not free OVERLAPPED before completion.
                GetOverlappedResult(pipe_, &operation, &n, TRUE);
                result = ready == WAIT_TIMEOUT ? -2 : -1;
            }
        }
        CloseHandle(operation.hEvent);
        return result;
    }
    HANDLE pipe_, stopped_;
};

class Listener final : public VirtualComListener {
public:
    Listener(HANDLE pipe, std::function<void(std::unique_ptr<Transport>)> serve) : pipe_(pipe), serve_(std::move(serve)) {
        stopped_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        thread_ = std::thread([this] {
            // One instance per virtual COM; the serial driver itself admits only one legacy open at a time.
            while (WaitForSingleObject(stopped_, 0) == WAIT_TIMEOUT) {
                OVERLAPPED op{}; op.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
                BOOL connected = ConnectNamedPipe(pipe_, &op);
                const DWORD code = connected ? ERROR_SUCCESS : GetLastError();
                if (code == ERROR_IO_PENDING) {
                    HANDLE events[]{op.hEvent, stopped_};
                    const auto ready = WaitForMultipleObjects(2, events, FALSE, INFINITE);
                    if (ready != WAIT_OBJECT_0) {
                        CancelIo(pipe_); DWORD ignored = 0; GetOverlappedResult(pipe_, &op, &ignored, TRUE);
                        CloseHandle(op.hEvent); break;
                    }
                    DWORD ignored = 0; connected = GetOverlappedResult(pipe_, &op, &ignored, FALSE);
                } else connected = connected || code == ERROR_PIPE_CONNECTED;
                CloseHandle(op.hEvent);
                if (!connected) break;
                HANDLE session = INVALID_HANDLE_VALUE;
                if (!DuplicateHandle(GetCurrentProcess(), pipe_, GetCurrentProcess(), &session, 0, FALSE, DUPLICATE_SAME_ACCESS)) break;
                serve_(std::make_unique<PipeTransport>(session));
                DisconnectNamedPipe(pipe_);
            }
        });
    }
    ~Listener() override { stop(); CloseHandle(pipe_); CloseHandle(stopped_); }
    void stop() override { SetEvent(stopped_); if (thread_.joinable()) thread_.join(); }
private:
    HANDLE pipe_, stopped_;
    std::function<void(std::unique_ptr<Transport>)> serve_;
    std::thread thread_;
};
}

std::unique_ptr<VirtualComListener> VirtualComListener::start(const std::string& name,
    std::function<void(std::unique_ptr<Transport>)> serve, std::string& error) {
#ifdef PORTGATE_WINDOWS_XP
    (void)name; (void)serve;
    error="Virtual COM is not offered on Windows XP"; return nullptr;
#else
    if(!virtual_com_supported()) { error="Virtual COM requires Windows 10 1803+/11 on x64"; return nullptr; }
    PSECURITY_DESCRIPTOR descriptor = nullptr;
    // WUDFHost is LocalService; ordinary legacy applications cannot enter the broker control channel.
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(L"D:P(A;;GA;;;SY)(A;;GA;;;LS)", SDDL_REVISION_1, &descriptor, nullptr)) {
        error = "virtual COM IPC security descriptor"; return nullptr;
    }
    SECURITY_ATTRIBUTES attributes{sizeof(SECURITY_ATTRIBUTES), descriptor, FALSE};
    const auto path = "\\\\.\\pipe\\PortGateVCom-" + name;
    const HANDLE pipe = CreateNamedPipeA(path.c_str(), PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED | FILE_FLAG_FIRST_PIPE_INSTANCE,
        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | 0x00000008 /* PIPE_REJECT_REMOTE_CLIENTS, Vista+ */, 1, 65536, 65536, 0, &attributes);
    LocalFree(descriptor);
    if (pipe == INVALID_HANDLE_VALUE) { error = "virtual COM IPC " + name + " (error " + std::to_string(GetLastError()) + ")"; return nullptr; }
    return std::make_unique<Listener>(pipe, std::move(serve));
#endif
}
std::unique_ptr<Transport> connect_virtual_com(const std::string& name, std::string& error) {
    const auto path="\\\\.\\pipe\\PortGateVCom-"+name;
    const auto pipe=CreateFileA(path.c_str(),GENERIC_READ|GENERIC_WRITE,0,nullptr,OPEN_EXISTING,FILE_FLAG_OVERLAPPED,nullptr);
    if (pipe==INVALID_HANDLE_VALUE) { error="PortGate virtual COM broker is unavailable"; return nullptr; }
    // A same-name pipe created before PortGate by an ordinary user must not impersonate the broker to WUDFHost.
    PSID owner=nullptr; PSECURITY_DESCRIPTOR descriptor=nullptr;
    const auto result=GetSecurityInfo(pipe,SE_FILE_OBJECT,OWNER_SECURITY_INFORMATION,&owner,nullptr,nullptr,nullptr,&descriptor);
    BYTE system_sid[SECURITY_MAX_SID_SIZE]{}, service_sid[SECURITY_MAX_SID_SIZE]{}; DWORD system_size=sizeof(system_sid),service_size=sizeof(service_sid);
    const bool trusted=result==ERROR_SUCCESS && CreateWellKnownSid(WinLocalSystemSid,nullptr,system_sid,&system_size) &&
        CreateWellKnownSid(WinLocalServiceSid,nullptr,service_sid,&service_size) && (EqualSid(owner,system_sid) || EqualSid(owner,service_sid));
    if(descriptor) LocalFree(descriptor);
    if(!trusted) { CloseHandle(pipe); error="Virtual COM broker must run as the LocalService/SYSTEM PortGate service"; return nullptr; }
    return std::make_unique<PipeTransport>(pipe);
}
}
