# Estado de la implementación

Instantánea del 24 de octubre de 2026. Describe lo que existe y lo que se comprobó; no reemplaza a la propuesta ni a la matriz de [IMPLEMENTACION](IMPLEMENTACION.md).

## Implementado

| Pieza | Ubicación | Comprobación |
|---|---|---|
| Contratos neutrales (canal, modelo canónico, `ILabWorkflowHost`, traza, segmentos puros) | `dotnet/src/Atsm.Abstractions` | Compila sin advertencias (`TreatWarningsAsErrors`) |
| Capa baja CLSI LIS01-A2: máquina de estados activa y observador pasivo | `dotnet/src/Atsm.Protocol.Lis01` | Frames, checksum (vector de traza real), ETB, retransmisión, contención, timeouts, interrupción, división del stream; observación de NAK, retransmisión y alteración por el registrador |
| Codec CLSI LIS02-A2 | `dotnet/src/Atsm.Protocol.Lis02` | Delimitadores declarados, escapes, builder, orden de registros |
| Framing MLLP | `dotnet/src/Atsm.Protocol.Mllp` | Bloques partidos, ruido, desbordamiento |
| SDK de plugins: perfil declarativo, dialecto LIS02-A2 estándar, adaptador regex de logs | `dotnet/src/Atsm.Plugins` | Usado por los plugins Architect, U-WAM y Glyms (pruebas) |
| Runtime de sesión ASTM | `dotnet/src/Atsm.Runtime` | Sesión completa con simulador: resultados, consulta con presupuesto, respuesta negativa, descarga de órdenes, reproducción de traza |
| Canales: Stream, TCP nativo (TLS 1.2/1.3 por endpoint), Replay, SerialTunnel (Link v1) | `dotnet/src/Atsm.Channels` | Vectores compartidos con C++; prueba cruzada con el agente nativo |
| Trazas neutrales: real y teórica por el mismo método, pila de eventos, métricas, comparación, anonimizador | `dotnet/src/Atsm.Tracing`, CLI `atsm-trace` | Pruebas sintéticas y trazas reales locales (ver abajo) |
| Plugin Abbott ARCHITECT (ASI RS-232) | `dotnet/plugins/Atsm.Plugin.Abbott.Architect` | Mensajes sintéticos según el manual 91407-108 y trazas reales locales |
| Plugin Sysmex U-WAM (LIS2-A2 / E1381-02) y adaptador del monitor de puertos de Sysmex | `dotnet/plugins/Atsm.Plugin.Sysmex.Uwam` | Mensajes sintéticos según R315168 rev. 22 y trazas reales locales |
| Plugin de pruebas Glyms (sólo adaptador de log, fuera del motor neutral) | `dotnet/plugins/Atsm.Plugin.Glyms.Testing` | Log sintético y logs reales locales |
| Agente nativo C++17 | `native/` | 536 verificaciones nativas (CBOR, Link v1, árbitro, configuración, sesión completa por TCP) |
| Especificación ATSM Link v1 y vectores | `spec/link-v1.md`, `conformance/link-v1/vectors.txt` | Iguales en C++ y .NET |
| Firma Strong Name con la SNK de la distribución | `dotnet/Directory.Build.targets` | Mismo token que EDC (`066190a23251f202`); la clave no está en el repositorio |

Pruebas: `dotnet test dotnet/Atsm.slnx` → 67 correctas (incluye 2 contra el `atsm-agent.exe` compilado); `ctest` → 536 verificaciones, 0 fallos. Repetidas sin intermitencias.

## Verificación con datos reales (sólo local)

Las trazas reales no salen de `Documentacion/trazas_externas/` (ignorada). Resultados agregados, sin identificadores:

| Fuente | Resultado |
|---|---|
| Sysmex, puerto AMS → LIS (monitor de Sysmex, nivel cable) | 118 transmisiones, 9.572 resultados interpretados por el dialecto U-WAM; 2.064 frames con checksum distinto en el log y aceptados por el LIS: el monitor escribe como espacio el byte 0xB5 (µ de las unidades), confirmado al recalcular el checksum |
| Sysmex, puerto 2 AMS ↔ LIS | 124 consultas, 117 respondidas (latencia p50 252 ms, máx. 1,1 s) y 7 sin respuesta; 5 ENQ de más en cada puerto (reintentos de establecimiento), coherentes con los `Communication time-out` del log de Sysmex |
| Sysmex UF-5000 ↔ U-WAM (log de registros, sin frames) | 425 mensajes, 5.006 resultados y 155 consultas; la traza teórica reconstruye el cable completo (7.354 frames) |
| Real vs teórica (mismo método) | Mensajes idénticos en todos los casos; la comparación aísla lo que hizo el enlace real (reintentos, frames alterados, tiempos de reacción del LIS) |
| Exportaciones ARCHITECT (`Importar/`) | 1.071 resultados; 309 avisos de archivos de control sin registro P |
| Log Glyms I1000 (adaptador del plugin de pruebas) | 837 mensajes recibidos, 4.753 resultados, 648 consultas, 1 sin respuesta |
| Anonimización de las trazas Sysmex y Glyms | 0 hallazgos residuales; ningún nombre real de los registros P en la salida; interpretación idéntica antes y después |

## Diferencias con la propuesta

- Formato de traza v1: JSON Lines (`atsm-trace/1`). El binario con hash encadenado queda para la traza durable del agente.
- El agente v1 no implementa `TraceRead`, observadores ni rol de conexión invertido; anuncia `traceCapture = 0`.
- Los bytes recibidos sin dueño se cuentan y se informan en `Granted`; no se entregan.
- TLS en el agente nativo: no compilado. Un endpoint `Tls` se niega a arrancar.
- Los plugins se registran de forma estática en `atsm-trace`; la carga desde una carpeta de plugins queda pendiente.

## Pendiente

| Tema | Bloqueo |
|---|---|
| TLS del agente (Mbed TLS 3.6 LTS) | Aprobación de la dependencia |
| Build Windows XP | Toolset `v141_xp` y prueba en XP real |
| Build Linux / Raspberry | Distro WSL o equipo Linux; adaptador POSIX escrito, no compilado |
| ESP32 | Sin escribir; requiere ESP-IDF y hardware |
| Canal `LocalSerial` .NET | Paquete oficial `System.IO.Ports` |
| `FileDrop` | Sin implementar |
| HL7 v2 / LAW | Codec y flujos sin implementar; existe el framing MLLP |
| Plugin CELL-DYN Ruby | Sin implementar; hay trazas |
| UC-3500 (protocolo propio Sysmex) | Visible en los logs, fuera de ASTM; requiere su manual |
| Outbox durable del runtime | Sin implementar |
| Carga dinámica de plugins | Catálogo estático por ahora |

## Decisiones registradas

- **Licencia:** libre uso, según el responsable del proyecto. No hay archivo de licencia formal.
- **Firma:** Strong Name con la misma SNK que EDC; Authenticode en el release.
- **Valores de interfaz:** los fija la documentación de cada marca o serie de equipos, en el perfil del plugin y con su cita.
- **Trazas:** ATSM genérico es neutral. Reconstruye trazas reales y teóricas a partir de contenido puro, y los plugins aportan adaptadores de log de su serie de equipos. El código de Glyms vive sólo en un plugin de pruebas; un plugin de integración con Glyms sería un trabajo aparte.
- **Architect:** se envía el sexo del paciente (`SexPolicy.AlwaysUnknown` reproduce el comportamiento legacy); el tipo de resultado F/P/I va en `ResultKind`; página de códigos MS-DOS 850.
- **xUnit 2.9.3** sólo en pruebas, misma versión que EDC.
