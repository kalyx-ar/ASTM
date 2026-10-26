// portgate: serial ↔ TCP bridge with exclusive controller arbitration (PortGate Link v1), administered remotely
// (spec/admin-v1.md). It keeps only the configuration in force and always starts with it.
#include <algorithm>
#include <atomic>
#include <csignal>
#include <cstdio>
#include <cstdlib>
#include <ctime>
#include <fstream>
#include <iostream>
#include <mutex>
#include <string>
#include <vector>

#include "portgate/supervisor.hpp"
#include "service.hpp"

namespace {

std::atomic<bool> g_stop{false};

void on_signal(int) { g_stop = true; }

const char* level_name(portgate::LogLevel level) {
    switch (level) {
        case portgate::LogLevel::Debug: return "DEBUG";
        case portgate::LogLevel::Info: return "INFO";
        case portgate::LogLevel::Warning: return "WARN";
        case portgate::LogLevel::Error: return "ERROR";
    }
    return "?";
}

int usage() {
    std::cerr << "usage: portgate run [options]                 run in this console\n"
                 "       portgate service install [options]     install the service (automatic start)\n"
                 "       portgate service uninstall [--state <dir>] [--purge]\n"
                 "       portgate service start | stop\n"
                 "options: --state <dir>  --admin <host:port> (default 0.0.0.0:28240)  --name <agent>\n"
                 "         --server-cert <sha256> | --psk <identity>:<hex>   fix the administering server at installation\n"
                 "         --quiet\n";
    return 64;
}

std::string stamp() {
    const std::time_t now = std::time(nullptr);
    std::tm utc{};
#ifdef _WIN32
    gmtime_s(&utc, &now);
#else
    gmtime_r(&now, &utc);
#endif
    char text[32];
    std::strftime(text, sizeof(text), "%Y-%m-%dT%H:%M:%SZ", &utc);
    return text;
}

portgate::Logger console_logger(bool quiet) {
    static std::mutex log_mutex;
    return [quiet](portgate::LogLevel level, const std::string& message) {
        if (quiet && level == portgate::LogLevel::Debug) return;
        std::lock_guard<std::mutex> lock(log_mutex);
        std::cerr << stamp() << " " << level_name(level) << " " << message << std::endl;
    };
}

/// Service log next to the state (the directory is protected by `service install`), bounded to two files of 1 MiB.
portgate::Logger file_logger(const std::string& directory, bool quiet) {
    static std::mutex log_mutex;
    const std::string path = portgate::platform::join_path(directory, "portgate.log");
    return [quiet, path](portgate::LogLevel level, const std::string& message) {
        if (quiet && level == portgate::LogLevel::Debug) return;
        std::lock_guard<std::mutex> lock(log_mutex);
        std::string ignored;
        if (portgate::platform::file_size(path) > 1024 * 1024) portgate::platform::replace_file(path, path + ".1", ignored);
        const std::string line = stamp() + " " + level_name(level) + " " + message + "\n";
        portgate::platform::append_file(path, portgate::platform::Bytes(line.begin(), line.end()), ignored);
    };
}

std::string environment(const char* name, const std::string& fallback) {
#ifdef _WIN32
    char* value = nullptr;
    std::size_t length = 0;
    std::string out = fallback;
    if (_dupenv_s(&value, &length, name) == 0 && value) {
        out = value;
        std::free(value);
    }
    return out;
#else
    const char* value = std::getenv(name);
    return value ? value : fallback;
#endif
}

std::string default_state_directory() {
#ifdef _WIN32
    return environment("ProgramData", "C:\\ProgramData") + "\\PortGate";
#else
    return "/var/lib/portgate";
#endif
}

std::string host_name() {
#ifdef _WIN32
    return environment("COMPUTERNAME", "portgate");
#else
    std::ifstream file("/etc/hostname");
    std::string name;
    return file && std::getline(file, name) && !name.empty() ? name : "portgate";
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

struct RunOptions {
    portgate::SupervisorOptions supervisor;
    bool quiet = false;
    bool purge = false;
};

/// Parses the `run` options; returns 0 or an exit code.
int parse(const std::vector<std::string>& args, RunOptions& out) {
    auto& options = out.supervisor;
    options.state_directory = default_state_directory();
    options.agent_name = host_name();
#ifdef _WIN32
    options.platform_profile = "windows-desktop";
#else
    options.platform_profile = "posix";
#endif
    for (std::size_t i = 0; i < args.size(); ++i) {
        const std::string& arg = args[i];
        const bool has_value = i + 1 < args.size();
        if (arg == "--state" && has_value) {
            options.state_directory = args[++i];
        } else if (arg == "--admin" && has_value) {
            const std::string& value = args[++i];
            const auto colon = value.rfind(':');
            if (colon == std::string::npos) return usage();
            options.admin_host = value.substr(0, colon);
            options.admin_port = static_cast<std::uint16_t>(std::stoul(value.substr(colon + 1)));
        } else if (arg == "--name" && has_value) {
            options.agent_name = args[++i];
        } else if (arg == "--server-cert" && has_value) {
            options.install_server_certificate_sha256 = args[++i];
            for (auto& c : options.install_server_certificate_sha256) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
        } else if (arg == "--psk" && has_value) {
            const std::string& value = args[++i];
            const auto colon = value.find(':');
            if (colon == std::string::npos || !parse_hex(value.substr(colon + 1), options.install_psk) || options.install_psk.size() < 16) {
                std::cerr << "--psk needs <identity>:<hex> with at least 16 random bytes\n";
                return 64;
            }
            options.install_psk_identity = value.substr(0, colon);
        } else if (arg == "--quiet") {
            out.quiet = true;
        } else if (arg == "--purge") {
            out.purge = true;
        } else {
            return usage();
        }
    }
    return 0;
}

int run(const RunOptions& options, const portgate::Logger& logger, std::atomic<bool>& stop, bool announce) {
    std::string error;
    if (!portgate::platform::network_startup(error)) {
        logger(portgate::LogLevel::Error, error);
        return 71;
    }
    portgate::Supervisor supervisor(options.supervisor, logger);
    if (!supervisor.start(error)) {
        logger(portgate::LogLevel::Error, "cannot start: " + error);
        portgate::platform::network_cleanup();
        return 69;
    }
    // Machine-readable line for supervisors and tests that use an ephemeral port.
    if (announce) std::cout << "PORTGATE-ADMIN " << supervisor.admin_port() << " " << supervisor.admin_certificate_sha256() << std::endl;
    supervisor.run(stop);
    supervisor.shutdown();
    portgate::platform::network_cleanup();
    return 0;
}

}  // namespace

int main(int argc, char** argv) {
    const std::vector<std::string> args(argv + 1, argv + argc);
    if (args.empty()) return usage();
    RunOptions options;
    if (args[0] == "run") {
        if (const int code = parse({args.begin() + 1, args.end()}, options)) return code;
        std::signal(SIGINT, on_signal);
        std::signal(SIGTERM, on_signal);
        return run(options, console_logger(options.quiet), g_stop, true);
    }
    if (args[0] == "service" && args.size() >= 2) {
        const std::vector<std::string> rest(args.begin() + 2, args.end());
        if (const int code = parse(rest, options)) return code;
        if (args[1] == "run") {
            std::string ignored;
            portgate::platform::ensure_directory(options.supervisor.state_directory, ignored);
            const auto logger = file_logger(options.supervisor.state_directory, options.quiet);
            return portgate::service::dispatch([&](std::atomic<bool>& stop) { return run(options, logger, stop, false); });
        }
        if (args[1] == "install" || args[1] == "uninstall" || args[1] == "start" || args[1] == "stop") {
            std::vector<std::string> stored;
            for (const auto& arg : rest)
                if (arg != "--purge") stored.push_back(arg);
            // The state directory is fixed in the service command line so `uninstall --purge` and the ACL agree.
            if (args[1] == "install" && std::find(stored.begin(), stored.end(), "--state") == stored.end()) {
                stored.push_back("--state");
                stored.push_back(options.supervisor.state_directory);
            }
            return portgate::service::command(args[1], stored, options.supervisor.state_directory, options.purge);
        }
    }
    return usage();
}
