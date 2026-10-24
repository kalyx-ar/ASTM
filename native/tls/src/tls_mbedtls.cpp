// TLS server transport on Mbed TLS 4.1 LTS (PSA crypto). TLS 1.2 minimum, TLS 1.3 when the peer supports it,
// client certificates verified against the configured CA when required.
#include <cstring>
#include <mutex>
#include <string>

#include "atsm/transport.hpp"

extern "C" {
#include "mbedtls/error.h"
#include "mbedtls/net_sockets.h"
#include "mbedtls/pk.h"
#include "mbedtls/ssl.h"
#include "mbedtls/x509_crt.h"
#include "psa/crypto.h"
}

namespace atsm::tls {
void install_threading();
}

namespace atsm {

namespace {

constexpr std::uint32_t kReadTimeoutMs = 100;

std::string describe(int code) {
    char buffer[160] = {};
    mbedtls_strerror(code, buffer, sizeof(buffer));
    return std::string(buffer) + " (-0x" + [&] {
        static const char* digits = "0123456789ABCDEF";
        std::string hex;
        for (int shift = 12; shift >= 0; shift -= 4) hex.push_back(digits[(-code >> shift) & 0xF]);
        return hex;
    }() + ")";
}

bool global_init(std::string& error) {
    static std::once_flag once;
    static psa_status_t status = PSA_SUCCESS;
    std::call_once(once, [] {
        tls::install_threading();
        status = psa_crypto_init();
    });
    if (status != PSA_SUCCESS) {
        error = "psa_crypto_init failed (" + std::to_string(status) + ")";
        return false;
    }
    return true;
}

class MbedTlsTransport final : public Transport {
public:
    MbedTlsTransport(platform::Socket socket, const mbedtls_ssl_config* config) : socket_(std::move(socket)) {
        mbedtls_ssl_init(&ssl_);
        setup_ = mbedtls_ssl_setup(&ssl_, config);
        mbedtls_ssl_set_bio(&ssl_, this, &MbedTlsTransport::bio_send, nullptr, &MbedTlsTransport::bio_receive);
    }

    ~MbedTlsTransport() override { mbedtls_ssl_free(&ssl_); }

    bool handshake(int timeout_ms, std::string& error) {
        if (setup_ != 0) {
            error = "ssl setup: " + describe(setup_);
            return false;
        }
        const auto deadline = platform::monotonic_ms() + static_cast<std::uint64_t>(timeout_ms);
        int ret;
        while ((ret = mbedtls_ssl_handshake(&ssl_)) != 0) {
            if ((ret == MBEDTLS_ERR_SSL_TIMEOUT || ret == MBEDTLS_ERR_SSL_WANT_READ || ret == MBEDTLS_ERR_SSL_WANT_WRITE) &&
                platform::monotonic_ms() < deadline)
                continue;
            error = "TLS handshake failed: " + describe(ret);
            return false;
        }
        if (const mbedtls_x509_crt* peer = mbedtls_ssl_get_peer_cert(&ssl_)) {
            authenticated_ = mbedtls_ssl_get_verify_result(&ssl_) == 0;
            fingerprint_ = sha256_hex(peer->raw.p, peer->raw.len);
            common_name_ = common_name(peer);
        }
        return true;
    }

    bool send_all(const std::uint8_t* data, std::size_t size) override {
        std::lock_guard<std::mutex> lock(mutex_);
        std::size_t sent = 0;
        while (sent < size) {
            const int ret = mbedtls_ssl_write(&ssl_, data + sent, size - sent);
            if (ret == MBEDTLS_ERR_SSL_WANT_WRITE || ret == MBEDTLS_ERR_SSL_WANT_READ) continue;
            if (ret <= 0) return false;
            sent += static_cast<std::size_t>(ret);
        }
        return true;
    }

    int receive(std::uint8_t* buffer, std::size_t size, int timeout_ms) override {
        // Wait on the socket without holding the session lock, so writers are never blocked by an idle reader.
        bool buffered;
        {
            std::lock_guard<std::mutex> lock(mutex_);
            buffered = mbedtls_ssl_get_bytes_avail(&ssl_) > 0 || mbedtls_ssl_check_pending(&ssl_) != 0;
        }
        if (!buffered && !socket_.wait_readable(timeout_ms)) return -2;
        std::lock_guard<std::mutex> lock(mutex_);
        const int ret = mbedtls_ssl_read(&ssl_, buffer, size);
        if (ret > 0) return ret;
        if (ret == MBEDTLS_ERR_SSL_TIMEOUT || ret == MBEDTLS_ERR_SSL_WANT_READ || ret == MBEDTLS_ERR_SSL_WANT_WRITE) return -2;
        if (ret == 0 || ret == MBEDTLS_ERR_SSL_PEER_CLOSE_NOTIFY) return 0;
        return -1;
    }

    void shutdown() override {
        {
            std::unique_lock<std::mutex> lock(mutex_, std::try_to_lock);
            if (lock.owns_lock()) mbedtls_ssl_close_notify(&ssl_);
        }
        socket_.shutdown();
    }

    std::string peer() const override { return socket_.peer(); }
    bool authenticated() const override { return authenticated_; }
    std::string peer_common_name() const override { return authenticated_ ? common_name_ : std::string(); }
    std::string peer_certificate_sha256() const override { return authenticated_ ? fingerprint_ : std::string(); }

private:
    static int bio_send(void* context, const unsigned char* data, std::size_t size) {
        auto* self = static_cast<MbedTlsTransport*>(context);
        return self->socket_.send_all(data, size) ? static_cast<int>(size) : MBEDTLS_ERR_NET_SEND_FAILED;
    }

    static int bio_receive(void* context, unsigned char* buffer, std::size_t size, std::uint32_t timeout_ms) {
        auto* self = static_cast<MbedTlsTransport*>(context);
        const int n = self->socket_.receive(buffer, size, timeout_ms == 0 ? 1000 : static_cast<int>(timeout_ms));
        if (n == -2) return MBEDTLS_ERR_SSL_TIMEOUT;
        if (n < 0) return MBEDTLS_ERR_NET_RECV_FAILED;
        return n;  // 0 means the peer closed the connection
    }

    static std::string sha256_hex(const unsigned char* data, std::size_t size) {
        unsigned char hash[32];
        std::size_t length = 0;
        if (psa_hash_compute(PSA_ALG_SHA_256, data, size, hash, sizeof(hash), &length) != PSA_SUCCESS) return {};
        static const char* digits = "0123456789abcdef";
        std::string hex;
        for (std::size_t i = 0; i < length; ++i) {
            hex.push_back(digits[hash[i] >> 4]);
            hex.push_back(digits[hash[i] & 15]);
        }
        return hex;
    }

    static std::string common_name(const mbedtls_x509_crt* certificate) {
        char subject[512] = {};
        if (mbedtls_x509_dn_gets(subject, sizeof(subject), &certificate->subject) < 0) return {};
        const std::string text(subject);
        const auto start = text.find("CN=");
        if (start == std::string::npos) return {};
        const auto end = text.find(", ", start);
        return text.substr(start + 3, end == std::string::npos ? std::string::npos : end - start - 3);
    }

    platform::Socket socket_;
    mbedtls_ssl_context ssl_;
    std::mutex mutex_;
    int setup_ = 0;
    bool authenticated_ = false;
    std::string fingerprint_;
    std::string common_name_;
};

class MbedTlsServer final : public TlsServer {
public:
    ~MbedTlsServer() override {
        mbedtls_ssl_config_free(&config_);
        mbedtls_pk_free(&key_);
        mbedtls_x509_crt_free(&certificate_);
        mbedtls_x509_crt_free(&ca_);
    }

    bool init(const TlsServerSettings& settings, std::string& error) {
        mbedtls_ssl_config_init(&config_);
        mbedtls_pk_init(&key_);
        mbedtls_x509_crt_init(&certificate_);
        mbedtls_x509_crt_init(&ca_);
        if (!global_init(error)) return false;

        int ret = mbedtls_x509_crt_parse_file(&certificate_, settings.certificate_file.c_str());
        if (ret != 0) return fail(error, "certificate " + settings.certificate_file, ret);
        ret = mbedtls_pk_parse_keyfile(&key_, settings.private_key_file.c_str(),
                                       settings.private_key_password.empty() ? nullptr : settings.private_key_password.c_str());
        if (ret != 0) return fail(error, "private key " + settings.private_key_file, ret);
        if (!settings.client_ca_file.empty()) {
            ret = mbedtls_x509_crt_parse_file(&ca_, settings.client_ca_file.c_str());
            if (ret != 0) return fail(error, "client CA " + settings.client_ca_file, ret);
        } else if (settings.require_client_certificate) {
            error = "requireClientCertificate needs clientCa";
            return false;
        }

        ret = mbedtls_ssl_config_defaults(&config_, MBEDTLS_SSL_IS_SERVER, MBEDTLS_SSL_TRANSPORT_STREAM, MBEDTLS_SSL_PRESET_DEFAULT);
        if (ret != 0) return fail(error, "ssl defaults", ret);
        mbedtls_ssl_conf_min_tls_version(&config_, settings.minimum_tls13 ? MBEDTLS_SSL_VERSION_TLS1_3 : MBEDTLS_SSL_VERSION_TLS1_2);
        mbedtls_ssl_conf_read_timeout(&config_, kReadTimeoutMs);
        ret = mbedtls_ssl_conf_own_cert(&config_, &certificate_, &key_);
        if (ret != 0) return fail(error, "own certificate", ret);
        if (!settings.client_ca_file.empty()) mbedtls_ssl_conf_ca_chain(&config_, &ca_, nullptr);
        mbedtls_ssl_conf_authmode(&config_, settings.require_client_certificate ? MBEDTLS_SSL_VERIFY_REQUIRED
                                            : settings.client_ca_file.empty() ? MBEDTLS_SSL_VERIFY_NONE
                                                                              : MBEDTLS_SSL_VERIFY_OPTIONAL);
        return true;
    }

    std::unique_ptr<Transport> accept(platform::Socket socket, int timeout_ms, std::string& error) override {
        auto transport = std::make_unique<MbedTlsTransport>(std::move(socket), &config_);
        if (!transport->handshake(timeout_ms, error)) {
            transport->shutdown();
            return nullptr;
        }
        return transport;
    }

private:
    static bool fail(std::string& error, const std::string& what, int code) {
        error = what + ": " + describe(code);
        return false;
    }

    mbedtls_ssl_config config_;
    mbedtls_pk_context key_;
    mbedtls_x509_crt certificate_;
    mbedtls_x509_crt ca_;
};

}  // namespace

std::unique_ptr<TlsServer> TlsServer::create(const TlsServerSettings& settings, std::string& error) {
    auto server = std::make_unique<MbedTlsServer>();
    if (!server->init(settings, error)) return nullptr;
    return server;
}

bool TlsServer::available() { return true; }

}  // namespace atsm
