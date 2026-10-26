// remoteusb: serial ↔ TCP bridge with exclusive controller arbitration (RemoteUSB Link v1), administered remotely
// (spec/admin-v1.md). It keeps only the configuration in force and always starts with it.
#include <atomic>
#include <csignal>
#include <cstdio>
#include <cstdlib>
#include <ctime>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <mutex>
#include <sstream>
#include <string>

#include "remoteusb/agent.hpp"
#include "remoteusb/supervisor.hpp"

namespace {

std::atomic<bool> g_stop{false};

void on_signal(int) { g_stop = true; }

const char* level_name(remoteusb::LogLevel level) {
    switch (level) {
        case remoteusb::LogLevel::Debug: return "DEBUG";
        case remoteusb::LogLevel::Info: return "INFO";
        case remoteusb::LogLevel::Warning: return "WARN";
        case remoteusb::LogLevel::Error: return "ERROR";
    }
    return "?";
}

int usage() {
    std::cerr << "usage: remoteusb run [--state <dir>] [--admin <host:port>] [--name <agent>]\n"
                 "                     [--server-cert <sha256>] [--psk <identity>:<hex>] [--quiet]\n"
                 "       remoteusb --config <agent.json> [--quiet]   (legacy, retired in P-16 stage 4)\n";
    return 64;
}

remoteusb::Logger console_logger(bool quiet) {
    static std::mutex log_mutex;
    return [quiet](remoteusb::LogLevel level, const std::string& message) {
        if (quiet && level == remoteusb::LogLevel::Debug) return;
        std::lock_guard<std::mutex> lock(log_mutex);
        const std::time_t now = std::time(nullptr);
        std::tm utc{};
#ifdef _WIN32
        gmtime_s(&utc, &now);
#else
        gmtime_r(&now, &utc);
#endif
        char stamp[32];
        std::strftime(stamp, sizeof(stamp), "%Y-%m-%dT%H:%M:%SZ", &utc);
        std::cerr << stamp << " " << level_name(level) << " " << message << std::endl;
    };
}

std::string default_state_directory() {
#ifdef _WIN32
    char* value = nullptr;
    std::size_t length = 0;
    std::string base = "C:\\ProgramData";
    if (_dupenv_s(&value, &length, "ProgramData") == 0 && value) {
        base = value;
        std::free(value);
    }
    return base + "\\RemoteUSB";
#else
    return "/var/lib/remoteusb";
#endif
}

std::string host_name() {
#ifdef _WIN32
    char* value = nullptr;
    std::size_t length = 0;
    std::string name = "remoteusb";
    if (_dupenv_s(&value, &length, "COMPUTERNAME") == 0 && value) {
        name = value;
        std::free(value);
    }
    return name;
#else
    std::ifstream file("/etc/hostname");
    std::string name;
    return file && std::getline(file, name) && !name.empty() ? name : "remoteusb";
#endif
}

bool parse_hex(const std::string& text, std::vector<std::uint8_t>& out) {
    if (text.size() % 2 != 0) return false;
    for (std::size_t i = 0; i < text.size(); i += 2) {
        const auto byte = text.substr(i, 2);
        if (byte.find_first_not_of("0123456789abcdefABCDEF") != std::string::npos) return false;
        out.push_back(static_cast<std::uint8_t>(std::stoul(byte, nullptr, 16)));
    }
    return true;
}

int run_supervisor(int argc, char** argv) {
    remoteusb::SupervisorOptions options;
    options.state_directory = default_state_directory();
    options.agent_name = host_name();
#ifdef _WIN32
    options.platform_profile = "windows-desktop";
#else
    options.platform_profile = "posix";
#endif
    bool quiet = false;
    for (int i = 2; i < argc; ++i) {
        const std::string arg = argv[i];
        const bool has_value = i + 1 < argc;
        if (arg == "--state" && has_value) {
            options.state_directory = argv[++i];
        } else if (arg == "--admin" && has_value) {
            const std::string value = argv[++i];
            const auto colon = value.rfind(':');
            if (colon == std::string::npos) return usage();
            options.admin_host = value.substr(0, colon);
            options.admin_port = static_cast<std::uint16_t>(std::stoul(value.substr(colon + 1)));
        } else if (arg == "--name" && has_value) {
            options.agent_name = argv[++i];
        } else if (arg == "--server-cert" && has_value) {
            options.install_server_certificate_sha256 = argv[++i];
            for (auto& c : options.install_server_certificate_sha256) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
        } else if (arg == "--psk" && has_value) {
            const std::string value = argv[++i];
            const auto colon = value.find(':');
            if (colon == std::string::npos || !parse_hex(value.substr(colon + 1), options.install_psk) || options.install_psk.size() < 16) {
                std::cerr << "--psk needs <identity>:<hex> with at least 16 random bytes\n";
                return 64;
            }
            options.install_psk_identity = value.substr(0, colon);
        } else if (arg == "--quiet") {
            quiet = true;
        } else {
            return usage();
        }
    }
    std::string error;
    if (!remoteusb::platform::network_startup(error)) {
        std::cerr << error << "\n";
        return 71;
    }
    remoteusb::Supervisor supervisor(options, console_logger(quiet));
    if (!supervisor.start(error)) {
        std::cerr << "cannot start: " << error << "\n";
        remoteusb::platform::network_cleanup();
        return 69;
    }
    // Machine-readable lines for supervisors and tests that use an ephemeral port.
    std::cout << "REMOTEUSB-ADMIN " << supervisor.admin_port() << " " << supervisor.admin_certificate_sha256() << std::endl;

    std::signal(SIGINT, on_signal);
    std::signal(SIGTERM, on_signal);
    supervisor.run(g_stop);
    supervisor.shutdown();
    remoteusb::platform::network_cleanup();
    return 0;
}

int run_legacy(int argc, char** argv) {
    std::string config_path;
    bool quiet = false;
    for (int i = 1; i < argc; ++i) {
        const std::string arg = argv[i];
        if (arg == "--config" && i + 1 < argc) config_path = argv[++i];
        else if (arg == "--quiet") quiet = true;
        else return usage();
    }
    if (config_path.empty()) return usage();
    std::ifstream file(config_path, std::ios::binary);
    if (!file) {
        std::cerr << "cannot read " << config_path << "\n";
        return 66;
    }
    std::stringstream text;
    text << file.rdbuf();
    std::string error;
    auto config = remoteusb::parse_config(text.str(), error);
    if (!config) {
        std::cerr << "invalid configuration: " << error << "\n";
        return 65;
    }
    const auto base = std::filesystem::absolute(std::filesystem::path(config_path)).parent_path();
    for (auto* path : {&config->tls_settings.certificate_file, &config->tls_settings.private_key_file, &config->tls_settings.client_ca_file}) {
        if (!path->empty() && std::filesystem::path(*path).is_relative()) *path = (base / *path).string();
    }
    if (!remoteusb::platform::network_startup(error)) {
        std::cerr << error << "\n";
        return 71;
    }
    remoteusb::Agent agent(std::move(*config), console_logger(quiet));
    if (!agent.start(error)) {
        std::cerr << "cannot start: " << error << "\n";
        remoteusb::platform::network_cleanup();
        return 69;
    }
    std::cout << "REMOTEUSB-LISTENING " << agent.listen_port() << std::endl;
    std::signal(SIGINT, on_signal);
    std::signal(SIGTERM, on_signal);
    agent.run(g_stop);
    agent.shutdown();
    remoteusb::platform::network_cleanup();
    return 0;
}

}  // namespace

int main(int argc, char** argv) {
    if (argc >= 2 && std::string(argv[1]) == "run") return run_supervisor(argc, argv);
    return run_legacy(argc, argv);
}
