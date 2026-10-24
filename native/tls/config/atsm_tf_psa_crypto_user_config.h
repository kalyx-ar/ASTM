/* ATSM additions to the TF-PSA-Crypto configuration of Mbed TLS 4.1 (appended to the default configuration). */
#ifndef ATSM_TF_PSA_CRYPTO_USER_CONFIG_H
#define ATSM_TF_PSA_CRYPTO_USER_CONFIG_H

/* The agent runs one thread per controller connection: PSA crypto must be thread-safe.
 * Mutexes and condition variables come from native/tls/src/threading_alt.cpp (portable to Windows XP). */
#define MBEDTLS_THREADING_C
#define MBEDTLS_THREADING_ALT

/* Windows XP has no BCryptGenRandom: the XP build provides mbedtls_platform_get_entropy() (native/tls/src/entropy_win32_xp.c). */
#if defined(ATSM_TLS_CUSTOM_ENTROPY)
#undef MBEDTLS_PSA_BUILTIN_GET_ENTROPY
#define MBEDTLS_PSA_DRIVER_GET_ENTROPY
#endif

#endif
