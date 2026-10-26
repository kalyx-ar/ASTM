// File helpers for the protected state and the event log (portable C++17).
#include <chrono>
#include <filesystem>
#include <fstream>
#include <system_error>

#include "remoteusb/platform.hpp"

namespace remoteusb::platform {

namespace fs = std::filesystem;

namespace {

void owner_only(const fs::path& path) {
#ifndef _WIN32
    std::error_code ignored;
    fs::permissions(path, fs::perms::owner_read | fs::perms::owner_write, fs::perm_options::replace, ignored);
#else
    (void)path;  // Windows: the directory ACL set by `service install` and DPAPI protect the content.
#endif
}

}  // namespace

std::uint64_t utc_ms() {
    using namespace std::chrono;
    return static_cast<std::uint64_t>(duration_cast<milliseconds>(system_clock::now().time_since_epoch()).count());
}

bool ensure_directory(const std::string& path, std::string& error) {
    std::error_code code;
    fs::create_directories(fs::u8path(path), code);
    if (code && !fs::is_directory(fs::u8path(path))) {
        error = "cannot create " + path + ": " + code.message();
        return false;
    }
#ifndef _WIN32
    fs::permissions(fs::u8path(path), fs::perms::owner_all, fs::perm_options::replace, code);
#endif
    return true;
}

bool read_file(const std::string& path, Bytes& out) {
    std::ifstream file(fs::u8path(path), std::ios::binary);
    if (!file) return false;
    out.assign(std::istreambuf_iterator<char>(file), std::istreambuf_iterator<char>());
    return !file.bad();
}

bool write_file_atomic(const std::string& path, const Bytes& data, std::string& error) {
    const auto target = fs::u8path(path);
    auto temporary = target;
    temporary += ".tmp";
    {
        std::ofstream file(temporary, std::ios::binary | std::ios::trunc);
        if (!file) {
            error = "cannot write " + temporary.u8string();
            return false;
        }
        owner_only(temporary);
        file.write(reinterpret_cast<const char*>(data.data()), static_cast<std::streamsize>(data.size()));
        file.flush();
        if (!file) {
            error = "cannot write " + temporary.u8string();
            return false;
        }
    }
    return replace_file(temporary.u8string(), path, error);
}

bool append_file(const std::string& path, const Bytes& data, std::string& error) {
    const auto target = fs::u8path(path);
    const bool created = !fs::exists(target);
    std::ofstream file(target, std::ios::binary | std::ios::app);
    if (!file) {
        error = "cannot append to " + path;
        return false;
    }
    if (created) owner_only(target);
    file.write(reinterpret_cast<const char*>(data.data()), static_cast<std::streamsize>(data.size()));
    file.flush();
    if (!file) {
        error = "cannot append to " + path;
        return false;
    }
    return true;
}

std::uint64_t file_size(const std::string& path) {
    std::error_code code;
    const auto size = fs::file_size(fs::u8path(path), code);
    return code ? 0 : static_cast<std::uint64_t>(size);
}

bool replace_file(const std::string& from, const std::string& to, std::string& error) {
    std::error_code code;
    fs::rename(fs::u8path(from), fs::u8path(to), code);  // replaces an existing target on Windows and POSIX
    if (code) {
        error = "cannot replace " + to + ": " + code.message();
        return false;
    }
    return true;
}

std::string join_path(const std::string& directory, const std::string& name) { return (fs::u8path(directory) / fs::u8path(name)).u8string(); }

}  // namespace remoteusb::platform
