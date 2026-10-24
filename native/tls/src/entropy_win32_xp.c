/* Entropy source for Windows XP builds (MBEDTLS_PSA_DRIVER_GET_ENTROPY). RtlGenRandom (advapi32 SystemFunction036)
 * exists since Windows XP; newer Windows builds use the Mbed TLS built-in source based on BCryptGenRandom. */
#include <windows.h>

#include "psa/crypto.h"
#include "mbedtls/platform.h"

#define RtlGenRandom SystemFunction036
BOOLEAN NTAPI RtlGenRandom(PVOID RandomBuffer, ULONG RandomBufferLength);
#pragma comment(lib, "advapi32.lib")

int mbedtls_platform_get_entropy(psa_driver_get_entropy_flags_t flags, size_t *estimate_bits, unsigned char *output, size_t output_size)
{
    if (flags != 0) {
        return PSA_ERROR_NOT_SUPPORTED;
    }
    if (output_size > (ULONG) -1) {
        return PSA_ERROR_INSUFFICIENT_ENTROPY;
    }
    if (!RtlGenRandom(output, (ULONG) output_size)) {
        return PSA_ERROR_INSUFFICIENT_ENTROPY;
    }
    *estimate_bits = 8 * output_size;
    return 0;
}
