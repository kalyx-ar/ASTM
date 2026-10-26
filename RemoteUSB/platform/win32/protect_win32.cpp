// DPAPI with machine scope: the protected state can be read only on this machine (CryptProtectData exists since Windows 2000).
#include <windows.h>
#include <wincrypt.h>

#include "remoteusb/platform.hpp"

namespace remoteusb::platform {

namespace {

constexpr wchar_t kDescription[] = L"RemoteUSB state";

bool transform(const Bytes& input, Bytes& output, bool seal, std::string& error) {
    DATA_BLOB in{static_cast<DWORD>(input.size()), const_cast<BYTE*>(input.data())};
    DATA_BLOB out{};
    const BOOL ok = seal ? CryptProtectData(&in, kDescription, nullptr, nullptr, nullptr, CRYPTPROTECT_LOCAL_MACHINE | CRYPTPROTECT_UI_FORBIDDEN, &out)
                         : CryptUnprotectData(&in, nullptr, nullptr, nullptr, nullptr, CRYPTPROTECT_UI_FORBIDDEN, &out);
    if (!ok) {
        error = std::string(seal ? "CryptProtectData" : "CryptUnprotectData") + " failed (error " + std::to_string(GetLastError()) + ")";
        return false;
    }
    output.assign(out.pbData, out.pbData + out.cbData);
    SecureZeroMemory(out.pbData, out.cbData);
    LocalFree(out.pbData);
    return true;
}

}  // namespace

bool protect(const Bytes& plain, Bytes& sealed, std::string& error) { return transform(plain, sealed, true, error); }
bool unprotect(const Bytes& sealed, Bytes& plain, std::string& error) { return transform(sealed, plain, false, error); }

}  // namespace remoteusb::platform
