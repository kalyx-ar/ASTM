# Dependencias

Registro de cada dependencia externa: origen, versión, licencia, consumidores, verificación y aprobación.

## Mbed TLS 4.1.1 (agente nativo)

| Campo | Valor |
|---|---|
| Uso | Transporte TLS del agente nativo (`native/tls`), modo `Tls` de ATSM Link v1 |
| Versión | 4.1.1, rama 4.1 LTS: correcciones de errores y de seguridad hasta al menos marzo de 2029 |
| Mantenedor | Proyecto Mbed TLS bajo TrustedFirmware.org (Linaro), patrocinado por Arm |
| Licencia | Apache-2.0 o GPL-2.0-or-later, a elección; ATSM la usa bajo Apache-2.0 |
| Origen | `https://github.com/Mbed-TLS/mbedtls/releases/download/mbedtls-4.1.1/mbedtls-4.1.1.tar.bz2` |
| Integridad | SHA-256 `3359a349e23db3d5536fcee032ae7b2ecbfc08972fab643089b5cbf2a375c98c`, igual al `mbedtls-4.1.1-sha256sum.txt` y al digest del release; fijado en `native/CMakeLists.txt` (`URL_HASH`) |
| Firma | El release no publica firma de código; la verificación es por hash del archivo oficial |
| Incluye | TF-PSA-Crypto (criptografía PSA) y el framework de build del mismo proyecto, dentro del mismo archivo |
| Herramientas de build | CMake; no requiere Python (los archivos generados vienen en el release) |
| Configuración propia | `native/tls/config`: `MBEDTLS_THREADING_C` con implementación sobre `std::mutex` y, en el build XP, entropía propia (`RtlGenRandom`) en lugar de `BCryptGenRandom` |
| Distribución | No se copia al repositorio; CMake la descarga y verifica, o usa `-DATSM_MBEDTLS_SOURCE_DIR=<carpeta>` para builds sin red |
| Aprobación | Responsable del proyecto, 24 de octubre de 2026: «usar directamente 4.1 LTS». Se tomó el último parche de la rama (4.1.1, 7 de julio de 2026), que corrige 13 avisos de seguridad de la 4.1.0 |
| Seguimiento | Revisar los avisos de seguridad de cada release 4.1.x y actualizar versión y hash juntos |

ESP32: ESP-IDF trae su propia versión de Mbed TLS. El firmware del agente usará la de ESP-IDF; su versión se registrará cuando exista ese build.

## Microsoft (.NET, aprobación implícita)

Las bibliotecas de ejecución .NET usan sólo la BCL de .NET 10: `System.Net.Security` (TLS del host), `System.Text.Json`, `System.Threading.Channels` y `System.Text.Encoding.CodePages`.

## Sólo pruebas

| Paquete | Versión | Licencia | Nota |
|---|---|---|---|
| Microsoft.NET.Test.Sdk | 18.11.0 | MIT | Oficial de Microsoft |
| xunit | 2.9.3 | Apache-2.0 | Misma versión que usa EDC; no se distribuye |
| xunit.runner.visualstudio | 4.0.1 | Apache-2.0 | Misma versión que usa EDC; no se distribuye |
