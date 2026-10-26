# Dependencias

Registro de cada dependencia externa: origen, versión, licencia, consumidores, verificación y aprobación.

## Mbed TLS 4.1.1 (PortGate)

| Campo | Valor |
|---|---|
| Uso | Transporte TLS del agente nativo (`PortGate/tls`), modo `Tls` de PortGate Link v1 |
| Versión | 4.1.1, rama 4.1 LTS: correcciones de errores y de seguridad hasta al menos marzo de 2029 |
| Mantenedor | Proyecto Mbed TLS bajo TrustedFirmware.org (Linaro), patrocinado por Arm |
| Licencia | Apache-2.0 o GPL-2.0-or-later, a elección; ATSM la usa bajo Apache-2.0 |
| Origen | `https://github.com/Mbed-TLS/mbedtls/releases/download/mbedtls-4.1.1/mbedtls-4.1.1.tar.bz2` |
| Integridad | SHA-256 `3359a349e23db3d5536fcee032ae7b2ecbfc08972fab643089b5cbf2a375c98c`, igual al `mbedtls-4.1.1-sha256sum.txt` y al digest del release; fijado en `PortGate/CMakeLists.txt` (`URL_HASH`) |
| Firma | El release no publica firma de código; la verificación es por hash del archivo oficial |
| Incluye | TF-PSA-Crypto (criptografía PSA) y el framework de build del mismo proyecto, dentro del mismo archivo |
| Herramientas de build | CMake; no requiere Python (los archivos generados vienen en el release) |
| Configuración propia | `PortGate/tls/config`: `MBEDTLS_THREADING_C` con implementación sobre `std::mutex` y, en el build XP, entropía propia (`RtlGenRandom`) en lugar de `BCryptGenRandom` |
| Distribución | No se copia al repositorio; CMake la descarga y verifica, o usa `-DPORTGATE_MBEDTLS_SOURCE_DIR=<carpeta>` para builds sin red |
| Aprobación | Responsable del proyecto, 24 de octubre de 2026: «usar directamente 4.1 LTS». Se tomó el último parche de la rama (4.1.1, 7 de julio de 2026), que corrige 13 avisos de seguridad de la 4.1.0 |
| Seguimiento | Revisar los avisos de seguridad de cada release 4.1.x y actualizar versión y hash juntos |

ESP32: ESP-IDF trae su propia versión de Mbed TLS. El firmware del agente usará la de ESP-IDF; su versión se registrará cuando exista ese build.

## Microsoft (.NET, aprobación implícita)

Las bibliotecas de ejecución .NET usan sólo la BCL de .NET 10: `System.Net.Security` (TLS del host), `System.Text.Json`, `System.Threading.Channels` y `System.Text.Encoding.CodePages`.

### Antecedente WPF retirado (2026-10-24; sustituido por MAUI el 2026-10-25)

La GUI solicitada incorpora WPF y `System.Security.Cryptography.ProtectedData` desde **Microsoft.WindowsDesktop.App 10.0.13**, instalado con el SDK oficial 10.0.402 en `C:\Program Files\dotnet`. Es una referencia de framework (`UseWPF`), no un paquete NuGet adicional ni una DLL tomada de terceros. Consumidor: `Atsm.Diagnostics.Windows`; `Atsm.Diagnostics.Core` y el motor siguen en `net10.0` sin dependencia de Windows. Las referencias entre proyectos son internas a ATSM.

Origen: instalación oficial Microsoft .NET, runtime bajo `shared/Microsoft.WindowsDesktop.App/10.0.13`; código fuente oficial en [dotnet/wpf](https://github.com/dotnet/wpf) y [dotnet/runtime](https://github.com/dotnet/runtime), con licencia MIT del código fuente. La instalación binaria conserva sus términos **MICROSOFT .NET LIBRARY**, `C:\Program Files\dotnet\LICENSE.txt`. El paquete publicado depende del framework instalado: no redistribuye un runtime ni agrega transitivas NuGet de producción. Aprobación implícita de librería oficial dentro de la GUI Windows elegida por el usuario; alcance registrado antes de editar en [EDC#205](https://github.com/kalyx-ar/EDC/issues/205#issuecomment-6390140976), con esta evidencia de distribución complementaria.

Verificación del runtime local (no sólo las referencias), 2026-10-24:

| Ensamblado (versión CLR 10.0.0.0) | Token Strong Name | SHA-256 | Authenticode |
|---|---|---|---|
| `PresentationFramework.dll` | `31bf3856ad364e35` | `c6950a0d6addfb7a365c11fe0aa82a4677043a263102f1779aa9be8fd3624019` | `Valid` |
| `System.Security.Cryptography.ProtectedData.dll` | `b03f5f7f11d50a3a` | `e8b3b537062fc54282d3ce1379100bde27921a15ad1c53a7f7c92b04ae3abadb` | `Valid` |

No se añade editor/hash a listas de confianza EDC. Los ensamblados propios se verifican con Strong Name de distribución; su Authenticode queda pendiente del release y no se confunde con la firma de Microsoft. No se incorporó ningún paquete de terceros.

## Sólo pruebas

| Paquete | Versión | Licencia | Nota |
|---|---|---|---|
| Microsoft.NET.Test.Sdk | 18.11.0 | MIT | Oficial de Microsoft |
| xunit | 2.9.3 | Apache-2.0 | Misma versión que usa EDC; no se distribuye |
| xunit.runner.visualstudio | 4.0.1 | Apache-2.0 | Misma versión que usa EDC; no se distribuye |

## Diagnostics MAUI y NativeAOT (2026-10-25, EDC#205)

Presentación oficial Microsoft.Maui.Controls 10.0.110, SDK MAUI instalado por Visual Studio, mismo que EDC; licencia MIT. Windows App SDK 1.8.260529003 y transitivas oficiales según el grafo resuelto; System.Security.Cryptography.ProtectedData 10.0.13 preserva DPAPI anterior. Runtime .NET incluido 10.0.13; compilador NativeAOT Microsoft.DotNet.ILCompiler/runtime.win-x64.Microsoft.DotNet.ILCompiler 10.0.13 del SDK oficial (herramienta de compilación, no dependencia distribuida). Licencias de Windows App SDK/SDK nativo y .NET conservadas por la distribución.

Inventario exacto de las 37 dependencias NuGet de MAUI con versión, autor, licencia, repositorio, SHA-256 y SHA-512: [diagnostics-maui-dependencies.json](diagnostics-maui-dependencies.json). Todas verificaron `dotnet nuget verify --all`, salida 0. Origen/nuspec/repositorio oficial y firmas se comprobaron antes de la distribución. Aprobación: solicitud humana expresa de MAUI y aprobación implícita de bibliotecas oficiales dentro de ese alcance; registro previo [EDC#205](https://github.com/kalyx-ar/EDC/issues/205#issuecomment-6395906842). Sin nuevos paquetes de terceros, Syncfusion o toolkits; consumidores MAUI separados del motor neutral.

**Alerta:** Microsoft.Maui.Controls, Microsoft.Maui, Microsoft.Maui.Graphics y Microsoft.Maui.Controls.Xaml tienen PublicKeyToken nulo. Firma NuGet verificada no equivale a Strong Name de DLL o Authenticode. CS8002 sigue visible exclusivamente como advertencia del EXE MAUI, con alcance registrado [antes del ajuste](https://github.com/kalyx-ar/EDC/issues/205#issuecomment-6395998752). Ensamblados propios firmados con token de distribución; ejecutables NativeAOT nativos sin Strong Name CLR. Authenticode propio pendiente del release. No se re-firman bibliotecas oficiales ni se alteran baselines, listas de confianza o derivaciones EDC. Ver [MAUI-Y-NATIVEAOT](MAUI-Y-NATIVEAOT.md).

## Adaptador opcional Kalyx.Globalization.Lang (2026-10-25)

Pedido humano explícito de usar Lang para UI y diagnóstico de plugins. Alcance previo: [EDC#205](https://github.com/kalyx-ar/EDC/issues/205#issuecomment-6398309342); inventario precisado antes de compilar en [este comentario](https://github.com/kalyx-ar/EDC/issues/205#issuecomment-6398449976). EnableKalyxLang=true y KalyxGlobalizationProject habilitan la referencia al proyecto propio existente, sin modificarlo. Consumidores: Diagnostics.Core y Diagnostics.Maui. **La publicación estándar conserva seed español y no incluye Lang/SQLite**. La conexión y el proveedor de persistencia pertenecen al anfitrión; no se agrega otro proveedor nativo.

| Dependencia condicional existente | Origen / licencia | Verificación |
|---|---|---|
| Kalyx.Globalization | Código propio del checkout EDC; condiciones de distribución EDC | Strong Name de distribución; fuente local, sin copiar ni alterar su implementación |
| Microsoft.Data.Sqlite.Core 10.0.13 | Microsoft, MIT | Firma NuGet completa correcta; SHA256 661e6696b35e88a5cdceb1ac223dafa303a5af27c7df8bee22156c0ddc5ee0d7 |
| SQLitePCLRaw.core 2.1.13 | Eric Sink/SourceGear, Apache-2.0, [fuente](https://github.com/ericsink/SQLitePCL.raw) | Transitiva de Sqlite.Core ya presente en EDC; firma NuGet completa correcta; SHA256 d93d5f7ab3f864c47722a2d90048c31a1aac743e153d87b9f4a07589ee2f9daf |

Compilación contra Lang existente correcta y prueba con runtime real/conexión DbConnection sintética: traducción de diagnóstico y vocabulario U-WAM, registro de seeds de cada ensamblado y conexión conservada abierta. No acredita persistencia SQLCipher de Diagnostics en EDC ni descarga de traducciones.

**Alerta de firma actualizada:** Microsoft.Maui.Essentials también tiene PublicKeyToken=null; al usar su tipo AppTheme se agrega a las cuatro advertencias directas anteriores. Los cinco CS8002 siguen visibles. Son los paquetes oficiales MAUI 10.0.110 ya verificados, sin nuevos paquetes ni cambios de admisión. Firma NuGet no equivale a Strong Name ni Authenticode; firma Authenticode propia pendiente del release.
