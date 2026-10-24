# Estado de la implementación

Instantánea del 24 de octubre de 2026. Describe lo que existe y lo que se comprobó; no reemplaza a la propuesta ni a la matriz de [IMPLEMENTACION](IMPLEMENTACION.md).

## Implementado

| Pieza | Ubicación | Comprobación |
|---|---|---|
| Contratos neutrales (canal, modelo canónico, `ILabWorkflowHost`, traza, diagnósticos) | `dotnet/src/Atsm.Abstractions` | Compila sin advertencias (`TreatWarningsAsErrors`) |
| Capa baja CLSI LIS01-A2 como máquina de estados pura | `dotnet/src/Atsm.Protocol.Lis01` | Pruebas de frames, checksum (vector de traza real), ETB, retransmisión, contención, timeouts, interrupción, división del stream en todas las posiciones |
| Codec CLSI LIS02-A2 | `dotnet/src/Atsm.Protocol.Lis02` | Delimitadores declarados, escapes, builder, orden de registros |
| Framing MLLP | `dotnet/src/Atsm.Protocol.Mllp` | Bloques partidos, ruido, desbordamiento |
| SDK de plugins y perfil declarativo | `dotnet/src/Atsm.Plugins` | Usado por el plugin Architect |
| Runtime de sesión ASTM | `dotnet/src/Atsm.Runtime` | Sesión completa con simulador: resultados, host query con presupuesto, respuesta negativa, descarga de órdenes, reproducción de traza |
| Canales: Stream, TCP nativo (TLS 1.2/1.3 por endpoint), Replay, SerialTunnel (Link v1) | `dotnet/src/Atsm.Channels` | Vectores compartidos con C++; prueba cruzada con el agente nativo |
| Plugin Abbott ARCHITECT (ASI RS-232) | `dotnet/plugins/Atsm.Plugin.Abbott.Architect` | Mensajes sintéticos con la estructura del manual 91407-108 y trazas reales locales (ver abajo) |
| Importador de logs Glyms y anonimizador por campo | `dotnet/src/Atsm.TraceTools`, CLI `atsm-trace` | Log sintético y logs reales locales |
| Agente nativo C++17 | `native/` | 536 verificaciones nativas (CBOR, Link v1, árbitro, configuración, sesión completa por TCP) |
| Especificación ATSM Link v1 y vectores | `spec/link-v1.md`, `conformance/link-v1/vectors.txt` | Iguales en C++ y .NET |

Pruebas: `dotnet test dotnet/Atsm.slnx` → 58 correctas (incluye 2 contra el `atsm-agent.exe` compilado); `ctest` en `native/build/...` → 536 verificaciones, 0 fallos. Ambas repetidas 5 veces sin intermitencias.

## Verificación con datos reales (sólo local)

Las trazas reales no salen de `Documentacion/trazas_externas/` (ignorada). Los resultados agregados, sin identificadores:

| Fuente | Resultado |
|---|---|
| 310 archivos de exportación ARCHITECT (`Importar/`) | 1.071 resultados interpretados; 100 % con valor numérico o interpretación y fecha. 309 avisos `UNEXPECTED-RECORD-ORDER`: archivos de control sin registro P |
| `I1000Interface.log` (40.879 entradas, reproducción) | 4.751 resultados y 648 consultas; 5 frames con número inesperado registrados como evidencia |
| `C4000Interface.log` (15.926 entradas, reproducción) | 2.370 resultados, 62 pruebas no realizadas (X) y 177 consultas |
| Anonimización de ambos logs | 17.787 frames reescritos, 0 hallazgos residuales, 0 nombres reales encontrados en la salida; la interpretación antes y después es idéntica |

La reproducción descubrió un defecto que se corrigió: una sesión que responde consultas sobre un instrumento grabado queda en contención. La reproducción diagnóstica es ahora sólo de recepción (`TransmitHostMessages = false`).

## Diferencias con la propuesta

- El formato de traza v1 es JSON Lines (`atsm-trace/1`, bytes en base64) en lugar del binario con hash encadenado; es legible y suficiente para el corpus. El binario queda para cuando se requiera durabilidad en el agente.
- El agente v1 no implementa `TraceRead`, observadores ni rol de conexión invertido; anuncia `traceCapture = 0`.
- Los bytes recibidos sin dueño se cuentan y se informan en `Granted`; no se entregan.
- TLS en el agente nativo: no compilado. Un endpoint `Tls` se niega a arrancar.

## Pendiente

| Tema | Bloqueo |
|---|---|
| TLS del agente (Mbed TLS 3.6 LTS) | Aprobación de la dependencia |
| Build Windows XP | Instalar el toolset `v141_xp` (componente "C++ Windows XP Support for VS 2017") y probar en XP real |
| Build Linux / Raspberry | Distro WSL o equipo Linux; el adaptador POSIX está escrito pero no compilado |
| ESP32 | Plataforma `platform/esp32` sin escribir; requiere ESP-IDF y hardware |
| Canal `LocalSerial` .NET | Paquete oficial `System.IO.Ports` (no estaba en la caché local) |
| `FileDrop` | Sin implementar |
| HL7 v2 / LAW | Codec y flujos sin implementar; existe el framing MLLP |
| Plugins CELL-DYN Ruby y Sysmex U-WAM | Sin implementar; hay trazas y manual (U-WAM) |
| Importador de logs Sysmex OnlineInterface | Sin implementar |
| Outbox durable del runtime | Sin implementar: si el anfitrión falla, el lote se informa como diagnóstico |
| Firma Strong Name de ensamblados ATSM | Decidir clave propia del proyecto |
| Licencia del repositorio | Decisión del responsable |

## Decisiones aplicadas que conviene revisar

- **Sexo del paciente en Architect:** por defecto se envía el valor del anfitrión. `SexPolicy.AlwaysUnknown` reproduce el comportamiento legacy de Glyms (siempre `U`).
- **Tipo de resultado Architect:** el código de ensayo no se modifica; F/P/I viaja en `ResultKind`. Glyms agregaba `I` al código para las interpretaciones.
- **Página de códigos Architect:** MS-DOS 850, según la tabla 4.1 del manual (Glyms leía como texto del sistema).
- **xUnit 2.9.3** sólo en pruebas, misma versión que ya usa EDC. Las bibliotecas de ejecución usan únicamente la BCL de .NET.
