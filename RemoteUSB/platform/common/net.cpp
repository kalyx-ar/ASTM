// BSD sockets / Winsock implementation shared by Windows and POSIX targets.
#include <chrono>
#include <cstring>

#include "remoteusb/platform.hpp"

#ifdef _WIN32
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <winsock2.h>
#include <ws2tcpip.h>
using socklen = int;
#define REMOTEUSB_CLOSE_SOCKET closesocket
#else
#include <arpa/inet.h>
#include <fcntl.h>
#include <netdb.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <sys/socket.h>
#include <unistd.h>
using socklen = socklen_t;
#define REMOTEUSB_CLOSE_SOCKET ::close
#endif

namespace remoteusb::platform {

namespace {

#ifdef _WIN32
using native_socket = SOCKET;
constexpr native_socket kInvalid = INVALID_SOCKET;
#else
using native_socket = int;
constexpr native_socket kInvalid = -1;
#endif

native_socket to_native(std::intptr_t handle) { return handle == -1 ? kInvalid : static_cast<native_socket>(handle); }
std::intptr_t from_native(native_socket s) { return s == kInvalid ? -1 : static_cast<std::intptr_t>(s); }

// select() instead of poll(): WSAPoll does not exist on Windows XP, and the agent handles few sockets.
bool select_readable(native_socket s, int timeout_ms) {
    fd_set set;
    FD_ZERO(&set);
    FD_SET(s, &set);
    timeval timeout{};
    timeout.tv_sec = timeout_ms / 1000;
    timeout.tv_usec = (timeout_ms % 1000) * 1000;
#ifdef _WIN32
    return ::select(0, &set, nullptr, nullptr, &timeout) > 0;
#else
    return ::select(s + 1, &set, nullptr, nullptr, &timeout) > 0;
#endif
}

void set_no_delay(native_socket s) {
    int one = 1;
    setsockopt(s, IPPROTO_TCP, TCP_NODELAY, reinterpret_cast<const char*>(&one), sizeof(one));
}

}  // namespace

std::uint64_t monotonic_ms() {
    using namespace std::chrono;
    return static_cast<std::uint64_t>(duration_cast<milliseconds>(steady_clock::now().time_since_epoch()).count());
}

std::uint64_t random_seed() {
    using namespace std::chrono;
    const auto t = static_cast<std::uint64_t>(high_resolution_clock::now().time_since_epoch().count());
    const auto a = static_cast<std::uint64_t>(reinterpret_cast<std::uintptr_t>(&t));
    return t ^ (a << 17) ^ 0x9E3779B97F4A7C15ull;
}

bool network_startup(std::string& error) {
#ifdef _WIN32
    WSADATA data;
    if (WSAStartup(MAKEWORD(2, 2), &data) != 0) {
        error = "WSAStartup failed";
        return false;
    }
#else
    (void)error;
#endif
    return true;
}

void network_cleanup() {
#ifdef _WIN32
    WSACleanup();
#endif
}

Socket& Socket::operator=(Socket&& other) noexcept {
    if (this != &other) {
        close();
        handle_ = other.handle_;
        other.handle_ = -1;
    }
    return *this;
}

std::optional<Socket> Socket::connect(const std::string& host, std::uint16_t port, int timeout_ms, std::string& error) {
    addrinfo hints{};
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = SOCK_STREAM;
    addrinfo* result = nullptr;
    if (getaddrinfo(host.c_str(), std::to_string(port).c_str(), &hints, &result) != 0 || !result) {
        error = "cannot resolve " + host;
        return std::nullopt;
    }
    std::optional<Socket> connected;
    for (auto* ai = result; ai && !connected; ai = ai->ai_next) {
        native_socket s = ::socket(ai->ai_family, ai->ai_socktype, ai->ai_protocol);
        if (s == kInvalid) continue;
        if (::connect(s, ai->ai_addr, static_cast<socklen>(ai->ai_addrlen)) == 0) {
            set_no_delay(s);
            connected.emplace(from_native(s));
        } else {
            REMOTEUSB_CLOSE_SOCKET(s);
        }
    }
    freeaddrinfo(result);
    (void)timeout_ms;  // blocking connect; the OS timeout applies
    if (!connected) error = "cannot connect to " + host + ":" + std::to_string(port);
    return connected;
}

bool Socket::send_all(const std::uint8_t* data, std::size_t size) {
    const auto s = to_native(handle_);
    std::size_t sent = 0;
    while (sent < size) {
#ifdef _WIN32
        const int n = ::send(s, reinterpret_cast<const char*>(data + sent), static_cast<int>(size - sent), 0);
#else
        const auto n = ::send(s, data + sent, size - sent, MSG_NOSIGNAL);
#endif
        if (n <= 0) return false;
        sent += static_cast<std::size_t>(n);
    }
    return true;
}

int Socket::receive(std::uint8_t* buffer, std::size_t size, int timeout_ms) {
    const auto s = to_native(handle_);
    if (s == kInvalid) return -1;
    if (!select_readable(s, timeout_ms)) return -2;
#ifdef _WIN32
    const int n = ::recv(s, reinterpret_cast<char*>(buffer), static_cast<int>(size), 0);
#else
    const auto n = ::recv(s, buffer, size, 0);
#endif
    return n < 0 ? -1 : static_cast<int>(n);
}

void Socket::shutdown() {
    if (handle_ == -1) return;
#ifdef _WIN32
    ::shutdown(to_native(handle_), SD_BOTH);
#else
    ::shutdown(to_native(handle_), SHUT_RDWR);
#endif
}

void Socket::close() {
    if (handle_ == -1) return;
    REMOTEUSB_CLOSE_SOCKET(to_native(handle_));
    handle_ = -1;
}

std::string Socket::peer() const {
    sockaddr_storage address{};
    socklen length = sizeof(address);
    if (getpeername(to_native(handle_), reinterpret_cast<sockaddr*>(&address), &length) != 0) return "?";
    char host[NI_MAXHOST] = {};
    char service[NI_MAXSERV] = {};
    if (getnameinfo(reinterpret_cast<sockaddr*>(&address), length, host, sizeof(host), service, sizeof(service), NI_NUMERICHOST | NI_NUMERICSERV) != 0)
        return "?";
    return std::string(host) + ":" + service;
}

bool Listener::open(const std::string& host, std::uint16_t port, std::string& error) {
    addrinfo hints{};
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = SOCK_STREAM;
    hints.ai_flags = AI_PASSIVE;
    addrinfo* result = nullptr;
    if (getaddrinfo(host.c_str(), std::to_string(port).c_str(), &hints, &result) != 0 || !result) {
        error = "cannot resolve listen address " + host;
        return false;
    }
    native_socket s = ::socket(result->ai_family, result->ai_socktype, result->ai_protocol);
    if (s == kInvalid) {
        freeaddrinfo(result);
        error = "cannot create listening socket";
        return false;
    }
#ifndef _WIN32
    int one = 1;
    setsockopt(s, SOL_SOCKET, SO_REUSEADDR, &one, sizeof(one));
#endif
    if (::bind(s, result->ai_addr, static_cast<socklen>(result->ai_addrlen)) != 0 || ::listen(s, 8) != 0) {
        freeaddrinfo(result);
        REMOTEUSB_CLOSE_SOCKET(s);
        error = "cannot listen on " + host + ":" + std::to_string(port);
        return false;
    }
    freeaddrinfo(result);
    sockaddr_storage bound{};
    socklen length = sizeof(bound);
    getsockname(s, reinterpret_cast<sockaddr*>(&bound), &length);
    port_ = bound.ss_family == AF_INET6 ? ntohs(reinterpret_cast<sockaddr_in6*>(&bound)->sin6_port)
                                        : ntohs(reinterpret_cast<sockaddr_in*>(&bound)->sin_port);
    handle_ = from_native(s);
    return true;
}

Socket Listener::accept(int timeout_ms) {
    const auto s = to_native(handle_);
    if (s == kInvalid || !select_readable(s, timeout_ms)) return Socket();
    native_socket client = ::accept(s, nullptr, nullptr);
    if (client == kInvalid) return Socket();
    set_no_delay(client);
    return Socket(from_native(client));
}

void Listener::close() {
    if (handle_ == -1) return;
    REMOTEUSB_CLOSE_SOCKET(to_native(handle_));
    handle_ = -1;
}

}  // namespace remoteusb::platform

namespace remoteusb::platform {

bool Socket::wait_readable(int timeout_ms) const {
    const auto s = to_native(handle_);
    return s != kInvalid && select_readable(s, timeout_ms);
}

}  // namespace remoteusb::platform

namespace remoteusb::platform {

int Socket::peek(std::uint8_t* buffer, std::size_t size, int timeout_ms) {
    const auto s = to_native(handle_);
    if (s == kInvalid) return -1;
    if (!select_readable(s, timeout_ms)) return -2;
#ifdef _WIN32
    const int n = ::recv(s, reinterpret_cast<char*>(buffer), static_cast<int>(size), MSG_PEEK);
#else
    const auto n = ::recv(s, buffer, size, MSG_PEEK);
#endif
    return n < 0 ? -1 : static_cast<int>(n);
}

}  // namespace remoteusb::platform
