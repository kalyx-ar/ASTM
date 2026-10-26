/* Threading primitives for Mbed TLS (MBEDTLS_THREADING_ALT); implemented in native/tls/src/threading_alt.cpp. */
#ifndef PORTGATE_THREADING_ALT_H
#define PORTGATE_THREADING_ALT_H

typedef struct mbedtls_platform_mutex_t {
    void *impl;
} mbedtls_platform_mutex_t;

typedef struct mbedtls_platform_condition_variable_t {
    void *impl;
} mbedtls_platform_condition_variable_t;

#endif
