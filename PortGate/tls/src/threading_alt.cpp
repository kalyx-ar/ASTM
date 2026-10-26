// Mbed TLS threading layer on top of the C++ standard library, which the v141_xp toolset also supports on Windows XP.
#include <condition_variable>
#include <mutex>
#include <new>

extern "C" {
#include "mbedtls/threading.h"
}

namespace {

int mutex_init(mbedtls_platform_mutex_t* mutex) {
    mutex->impl = new (std::nothrow) std::mutex();
    return mutex->impl ? 0 : MBEDTLS_ERR_THREADING_USAGE_ERROR;
}

void mutex_destroy(mbedtls_platform_mutex_t* mutex) {
    delete static_cast<std::mutex*>(mutex->impl);
    mutex->impl = nullptr;
}

int mutex_lock(mbedtls_platform_mutex_t* mutex) {
    if (!mutex->impl) return MBEDTLS_ERR_THREADING_USAGE_ERROR;
    static_cast<std::mutex*>(mutex->impl)->lock();
    return 0;
}

int mutex_unlock(mbedtls_platform_mutex_t* mutex) {
    if (!mutex->impl) return MBEDTLS_ERR_THREADING_USAGE_ERROR;
    static_cast<std::mutex*>(mutex->impl)->unlock();
    return 0;
}

int cond_init(mbedtls_platform_condition_variable_t* cond) {
    cond->impl = new (std::nothrow) std::condition_variable_any();
    return cond->impl ? 0 : MBEDTLS_ERR_THREADING_USAGE_ERROR;
}

void cond_destroy(mbedtls_platform_condition_variable_t* cond) {
    delete static_cast<std::condition_variable_any*>(cond->impl);
    cond->impl = nullptr;
}

int cond_signal(mbedtls_platform_condition_variable_t* cond) {
    if (!cond->impl) return MBEDTLS_ERR_THREADING_USAGE_ERROR;
    static_cast<std::condition_variable_any*>(cond->impl)->notify_one();
    return 0;
}

int cond_broadcast(mbedtls_platform_condition_variable_t* cond) {
    if (!cond->impl) return MBEDTLS_ERR_THREADING_USAGE_ERROR;
    static_cast<std::condition_variable_any*>(cond->impl)->notify_all();
    return 0;
}

int cond_wait(mbedtls_platform_condition_variable_t* cond, mbedtls_platform_mutex_t* mutex) {
    if (!cond->impl || !mutex->impl) return MBEDTLS_ERR_THREADING_USAGE_ERROR;
    // The caller holds the mutex: adopt it for the wait and give it back locked.
    std::unique_lock<std::mutex> lock(*static_cast<std::mutex*>(mutex->impl), std::adopt_lock);
    static_cast<std::condition_variable_any*>(cond->impl)->wait(lock);
    lock.release();
    return 0;
}

}  // namespace

namespace portgate::tls {

void install_threading() {
    mbedtls_threading_set_alt(mutex_init, mutex_destroy, mutex_lock, mutex_unlock, cond_init, cond_destroy, cond_signal, cond_broadcast, cond_wait);
}

void uninstall_threading() { mbedtls_threading_free_alt(); }

}  // namespace portgate::tls
