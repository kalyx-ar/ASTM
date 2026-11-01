// Explicit, elevated provisioning commands. Normal PortGate startup never installs a driver or changes device ACLs.
#include <windows.h>
#include <initguid.h>
#include <devguid.h>
#include <setupapi.h>
#include <cfgmgr32.h>
#include <sddl.h>
#include <cctype>
#include <algorithm>
#include <iostream>
#include "portgate/platform.hpp"
#include "portgate/virtual_com.hpp"
#include "vcom_setup.hpp"
namespace portgate {
namespace {
const wchar_t hardware_id[]=L"ROOT\\PortGateVCom";
struct Devices { HDEVINFO handle=INVALID_HANDLE_VALUE; ~Devices(){if(handle!=INVALID_HANDLE_VALUE) SetupDiDestroyDeviceInfoList(handle);} };
int failure(const char* operation,DWORD error=GetLastError()) { std::cerr<<operation<<" failed (error "<<error<<"). Administrator access and a trusted signed driver package are required.\n";return 77; }
std::wstring wide(const std::string& s) {
    const auto n=MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,s.c_str(),-1,nullptr,0);
    if(!n) return {}; std::wstring out(static_cast<std::size_t>(n),L'\0'); MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,s.c_str(),-1,out.data(),n); out.pop_back();return out;
}
std::wstring property(HDEVINFO devices,SP_DEVINFO_DATA& device,DWORD property_id) {
    wchar_t text[1024]{};
    return SetupDiGetDeviceRegistryPropertyW(devices,&device,property_id,nullptr,reinterpret_cast<PBYTE>(text),sizeof(text)-sizeof(wchar_t),nullptr)?text:L"";
}
std::wstring port_name(HDEVINFO devices,SP_DEVINFO_DATA& device) {
    const HKEY key=SetupDiOpenDevRegKey(devices,&device,DICS_FLAG_GLOBAL,0,DIREG_DEV,KEY_QUERY_VALUE);
    if(key==INVALID_HANDLE_VALUE) return {};
    wchar_t name[32]{}; DWORD size=sizeof(name)-sizeof(wchar_t),type=0;
    const auto result=RegQueryValueExW(key,L"PortName",nullptr,&type,reinterpret_cast<PBYTE>(name),&size);RegCloseKey(key);
    return result==ERROR_SUCCESS && type==REG_SZ?name:L"";
}
bool query_remove(SP_DEVINFO_DATA& device) {
    PNP_VETO_TYPE veto=PNP_VetoTypeUnknown; wchar_t description[256]{};
    const auto result=CM_Query_And_Remove_SubTreeW(device.DevInst,&veto,description,256,CM_REMOVE_UI_NOT_OK);
    if(result==CR_SUCCESS) return true;
    std::cerr<<"Device removal was vetoed ("<<result<<", type "<<veto<<"). Close its sessions or complete retirement first.\n";return false;
}
int create(const std::string& source) {
    const auto input=wide(source); wchar_t absolute[32768]{};
    if(input.empty() || !GetFullPathNameW(input.c_str(),32768,absolute,nullptr)) return failure("driver path");
    GUID type{}; wchar_t class_name[256]{};
    if(!SetupDiGetINFClassW(absolute,&type,class_name,256,nullptr) || !IsEqualGUID(type,GUID_DEVCLASS_PORTS)) return failure("PortGate Ports-class INF",ERROR_INVALID_DATA);
    // Windows performs catalog/trust validation. Do not add a certificate, disable checks, or accept a publisher here.
    if(!SetupCopyOEMInfW(absolute,nullptr,SPOST_PATH,0,nullptr,0,nullptr,nullptr)) return failure("stage signed driver");
    Devices devices;devices.handle=SetupDiCreateDeviceInfoList(&GUID_DEVCLASS_PORTS,nullptr);
    if(devices.handle==INVALID_HANDLE_VALUE) return failure("create device list");
    SP_DEVINFO_DATA device{};device.cbSize=sizeof(device);
    if(!SetupDiCreateDeviceInfoW(devices.handle,L"PortGateVCom",&GUID_DEVCLASS_PORTS,L"PortGate Virtual COM",nullptr,DICD_GENERATE_ID,&device)) return failure("create virtual COM node");
    // MULTI_SZ includes its explicit and implicit terminator.
    const wchar_t ids[]=L"ROOT\\PortGateVCom\0";
    if(!SetupDiSetDeviceRegistryPropertyW(devices.handle,&device,SPDRP_HARDWAREID,reinterpret_cast<const BYTE*>(ids),sizeof(ids))) return failure("set virtual COM hardware id");
    SP_DEVINSTALL_PARAMS_W parameters{};parameters.cbSize=sizeof(parameters);
    if(!SetupDiGetDeviceInstallParamsW(devices.handle,&device,&parameters)) return failure("get install parameters");
    parameters.Flags|=DI_ENUMSINGLEINF|DI_QUIETINSTALL;
    if(wcslen(absolute)>=MAX_PATH) return failure("INF install path is too long",ERROR_FILENAME_EXCED_RANGE);
    wcscpy_s(parameters.DriverPath,absolute);
    if(!SetupDiSetDeviceInstallParamsW(devices.handle,&device,&parameters) || !SetupDiBuildDriverInfoList(devices.handle,&device,SPDIT_COMPATDRIVER)) return failure("select compatible driver");
    SP_DRVINFO_DATA_W driver{};driver.cbSize=sizeof(driver);
    if(!SetupDiEnumDriverInfoW(devices.handle,&device,SPDIT_COMPATDRIVER,0,&driver) || !SetupDiSetSelectedDriverW(devices.handle,&device,&driver)) return failure("select PortGate driver");
    if(!SetupDiCallClassInstaller(DIF_REGISTERDEVICE,devices.handle,&device)) return failure("register virtual COM node");
    if(!SetupDiCallClassInstaller(DIF_INSTALLDEVICE,devices.handle,&device)) {
        const auto error=GetLastError(); SetupDiCallClassInstaller(DIF_REMOVE,devices.handle,&device); return failure("install virtual COM driver",error);
    }
    const auto name=port_name(devices.handle,device); wchar_t instance[512]{};
    SetupDiGetDeviceInstanceIdW(devices.handle,&device,instance,512,nullptr);
    std::wcout<<L"PORTGATE-VCOM CREATED "<<name<<L" "<<instance<<L"\n";return 0;
}
int remove(const std::string& instance) {
    Devices devices;devices.handle=SetupDiGetClassDevsW(&GUID_DEVCLASS_PORTS,nullptr,nullptr,DIGCF_PRESENT);
    SP_DEVINFO_DATA device{};device.cbSize=sizeof(device);const auto id=wide(instance);
    if(devices.handle==INVALID_HANDLE_VALUE || !SetupDiOpenDeviceInfoW(devices.handle,id.c_str(),nullptr,0,&device)) return failure("find virtual COM");
    if(_wcsicmp(property(devices.handle,device,SPDRP_HARDWAREID).c_str(),hardware_id)!=0) return failure("device is not a PortGate virtual COM",ERROR_ACCESS_DENIED);
    if(!query_remove(device)) return 75;
    SP_REMOVEDEVICE_PARAMS parameters{};parameters.ClassInstallHeader.cbSize=sizeof(SP_CLASSINSTALL_HEADER);
    parameters.ClassInstallHeader.InstallFunction=DIF_REMOVE;parameters.Scope=DI_REMOVEDEVICE_GLOBAL;
    if(!SetupDiSetClassInstallParamsW(devices.handle,&device,&parameters.ClassInstallHeader,sizeof(parameters)) || !SetupDiCallClassInstaller(DIF_REMOVE,devices.handle,&device)) return failure("remove PortGate virtual COM");
    std::cout<<"PortGate virtual COM removed\n";return 0;
}
int protect_physical(const std::string& name) {
    // Use the service SID, not the shared LocalService account: other services also must not open the real COM.
    SC_HANDLE manager=OpenSCManagerW(nullptr,nullptr,SC_MANAGER_CONNECT);
    if(!manager) return failure("service manager");
    SC_HANDLE service=OpenServiceW(manager,L"PortGate",SERVICE_QUERY_CONFIG|SERVICE_QUERY_STATUS|SERVICE_CHANGE_CONFIG);
    CloseServiceHandle(manager); if(!service) return failure("install PortGate service before protecting the COM");
    SERVICE_STATUS service_status{}; SERVICE_SID_INFO service_sid{}; DWORD needed=0;
    const BOOL configured=QueryServiceConfig2W(service,SERVICE_CONFIG_SERVICE_SID_INFO,reinterpret_cast<BYTE*>(&service_sid),sizeof(service_sid),&needed);
    if(!configured || !(service_sid.dwServiceSidType&SERVICE_SID_TYPE_UNRESTRICTED)) {
        if(!QueryServiceStatus(service,&service_status) || service_status.dwCurrentState!=SERVICE_STOPPED) {
            CloseServiceHandle(service); return failure("stop PortGate before commissioning its service SID",ERROR_BUSY);
        }
        service_sid.dwServiceSidType=SERVICE_SID_TYPE_UNRESTRICTED;
        if(!ChangeServiceConfig2W(service,SERVICE_CONFIG_SERVICE_SID_INFO,&service_sid)) { const auto error=GetLastError();CloseServiceHandle(service);return failure("PortGate service SID",error); }
    }
    CloseServiceHandle(service);
    BYTE sid[SECURITY_MAX_SID_SIZE]{}; DWORD sid_bytes=sizeof(sid),domain_chars=256;wchar_t domain[256]{};SID_NAME_USE use{};
    if(!LookupAccountNameW(nullptr,L"NT SERVICE\\PortGate",sid,&sid_bytes,domain,&domain_chars,&use)) return failure("resolve PortGate service SID");
    LPWSTR sid_text=nullptr;if(!ConvertSidToStringSidW(sid,&sid_text)) return failure("PortGate SID text");
    const auto policy=std::wstring(L"D:P(A;;GA;;;SY)(A;;GA;;;")+sid_text+L")";LocalFree(sid_text);
    Devices devices;devices.handle=SetupDiGetClassDevsW(&GUID_DEVCLASS_PORTS,nullptr,nullptr,DIGCF_PRESENT);
    if(devices.handle==INVALID_HANDLE_VALUE) return failure("serial device list");
    SP_DEVINFO_DATA device{};device.cbSize=sizeof(device);const auto wanted=wide(name);
    for(DWORD i=0;SetupDiEnumDeviceInfo(devices.handle,i,&device);++i) {
        if(_wcsicmp(port_name(devices.handle,device).c_str(),wanted.c_str())!=0) continue;
        if(_wcsicmp(property(devices.handle,device,SPDRP_HARDWAREID).c_str(),hardware_id)==0) return failure("protect requires the physical COM",ERROR_INVALID_PARAMETER);
        DEVINST parent=0; if(CM_Get_Parent(&parent,device.DevInst,0)!=CR_SUCCESS) return failure("physical device parent",ERROR_NOT_FOUND);
        if(!query_remove(device)) return 75;
        // Persist the physical device policy, including while PortGate is stopped. Interactive legacy apps use the virtual endpoint.
        const BOOL applied=SetupDiSetDeviceRegistryPropertyW(devices.handle,&device,SPDRP_SECURITY_SDS,reinterpret_cast<const BYTE*>(policy.c_str()),static_cast<DWORD>((policy.size()+1)*sizeof(wchar_t)));
        const auto error=GetLastError();const auto resumed=CM_Reenumerate_DevNode(parent,CM_REENUMERATE_SYNCHRONOUS);
        if(!applied) return failure("protect physical COM",error);
        if(resumed!=CR_SUCCESS) {std::cerr<<"Physical COM policy saved; device re-enumeration requires attention ("<<resumed<<").\n";return 75;}
        std::cout<<"Physical COM restricted to SYSTEM and the PortGate service SID; verify reopening with the PortGate service\n";return 0;
    }
    return failure("physical COM not found",ERROR_NOT_FOUND);
}
}
int virtual_com_command(const std::vector<std::string>& args) {
    if(!virtual_com_supported()) {std::cerr<<"Virtual COM is offered on Windows 10 1803+/11 x64, not XP.\n";return 69;}
    if(args==std::vector<std::string>{"list"}) {
        for(const auto& port:platform::list_serial_devices()) {
            auto id=port.instance_id;std::transform(id.begin(),id.end(),id.begin(),[](unsigned char c){return static_cast<char>(std::toupper(c));});
            if(id.rfind("ROOT\\PORTGATEVCOM\\",0)==0) std::cout<<port.name<<" "<<port.instance_id<<"\n";
        }
        return 0;
    }
    if(args.size()==3 && args[0]=="create" && args[1]=="--inf") return create(args[2]);
    if(args.size()==3 && args[0]=="remove" && args[1]=="--instance") return remove(args[2]);
    if(args.size()==3 && args[0]=="protect" && args[1]=="--physical") return protect_physical(args[2]);
    std::cerr<<"usage: portgate vcom list | create --inf <signed INF> | remove --instance <PortGate ROOT id> | protect --physical <COM>\n";return 64;
}
}
