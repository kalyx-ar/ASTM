// atsm-agent: serial ↔ TCP bridge with exclusive controller arbitration (ATSM Link v1).
#include <atomic>
#include <csignal>
#include <cstdio>
#include <ctime>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <mutex>
#include <sstream>
#include <string>

#include "atsm/agent.hpp"

namespace {

std::atomic<bool> g_stop{false};

void on_signal(int) { g_stop = true; }

const char* level_name(atsm::LogLevel level) {
    switch (level) {
        case atsm::LogLevel::Debug: return "DEBUG";
        case atsm::LogLevel::Info: return "INFO";
        case atsm::LogLevel::Warning: return "WARN";
        case atsm::LogLevel::Error: return "ERROR";
    }
    return "?";
}

int usage() {
    std::cerr << "usage: atsm-agent --config <agent.json> [--quiet]\n"
                 "       atsm-agent --check <agent.json>\n";
    return 64;
}

}  // namespace

int main(int argc, char** argv) {
    std::string config_path;
    bool check_only = false;
    bool quiet = false;
    for (int i = 1; i < argc; ++i) {
        const std::string arg = argv[i];
        if ((arg == "--config" || arg == "--check") && i + 1 < argc) {
            check_only = arg == "--check";
            config_path = argv[++i];
        } else if (arg == "--quiet") {
            quiet = true;
        } else {
            return usage();
        }
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
    auto config = atsm::parse_config(text.str(), error);
    if (!config) {
        std::cerr << "invalid configuration: " << error << "\n";
        return 65;
    }
    // TLS files may be given relative to the configuration file.
    const auto base = std::filesystem::absolute(std::filesystem::path(config_path)).parent_path();
    for (auto* path : {&config->tls_settings.certificate_file, &config->tls_settings.private_key_file, &config->tls_settings.client_ca_file}) {
        if (!path->empty() && std::filesystem::path(*path).is_relative()) *path = (base / *path).string();
    }
    if (check_only) {
        std::cout << "configuration is valid: " << config->ports.size() << " port(s)\n";
        return 0;
    }

    if (!atsm::platform::network_startup(error)) {
        std::cerr << error << "\n";
        return 71;
    }
    std::mutex log_mutex;
    atsm::Agent agent(std::move(*config), [&](atsm::LogLevel level, const std::string& message) {
        if (quiet && level == atsm::LogLevel::Debug) return;
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
    });
    if (!agent.start(error)) {
        std::cerr << "cannot start: " << error << "\n";
        atsm::platform::network_cleanup();
        return 69;
    }
    // Machine-readable line for supervisors and tests that use an ephemeral port.
    std::cout << "ATSM-AGENT-LISTENING " << agent.listen_port() << std::endl;

    std::signal(SIGINT, on_signal);
    std::signal(SIGTERM, on_signal);
    agent.run(g_stop);
    agent.shutdown();
    atsm::platform::network_cleanup();
    return 0;
}
