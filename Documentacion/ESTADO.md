# Estado de la implementación

Instantánea del 24 de octubre de 2026. Describe lo que existe y lo que se comprobó; no reemplaza a la propuesta ni a la matriz de [IMPLEMENTACION](IMPLEMENTACION.md).

## Implementado

| Pieza | Ubicación | Comprobación |
|---|---|---|
| Contratos neutrales (canal, modelo canónico, `ILabWorkflowHost`, traza, segmentos puros) | `dotnet/src/Atsm.Abstractions` | Compila sin advertencias (`TreatWarningsAsErrors`) |
| Capa baja CLSI LIS01-A2: máquina de estados activa y observador pasivo | `dotnet/src/Atsm.Protocol.Lis01` | Frames, checksum (vector de traza real), ETB, retransmisión, contención, timeouts, interrupción, división del stream; observación de NAK, retransmisión y alteración por el registrador |
| Codec CLSI LIS02-A2 | `dotnet/src/Atsm.Protocol.Lis02` | Delimitadores declarados, escapes, builder, orden de registros |
| Mensajes completos P-01 | `AstmMessageProcessor`, `WholeMessageInstrumentSession` | Bytes/Stream del anfitrión, sin watcher; IDs estables por recepción y límites/EOF |
| HL7 v2.5.1 / ARCHITECT | `Atsm.Protocol.Hl7v2`, `Hl7MessageProcessor`, `Hl7InstrumentSession`, `ArchitectHl7Plugin` | OUL/R22, ZOS, OML/ORL, ACK original, MLLP fragmentado y correlación; sólo pruebas sintéticas |
| LAW LAB-27 parcial | `LawQueryMessages` | WOS por contenedor y RSP/K11; no actor LAW/AWOS completo |
| Custodia de trazas | `ITraceSink.EnsureReady`, `InstrumentCommunicationHost` | Comprobación anterior a la fábrica/canal, fallos propagados; persistencia/UI del anfitrión |
| Framing MLLP | `dotnet/src/Atsm.Protocol.Mllp` | Bloques partidos, ruido, desbordamiento |
| SDK de plugins: perfil declarativo, dialecto LIS02-A2 estándar, adaptador regex de logs | `dotnet/src/Atsm.Plugins` | Usado por los plugins Architect, U-WAM y legacy (pruebas) |
| Runtime de sesión ASTM | `dotnet/src/Atsm.Runtime` | Sesión completa con simulador: resultados, consulta con presupuesto, respuesta negativa, descarga de órdenes, reproducción de traza |
| Canales: Stream, TCP nativo (TLS 1.2/1.3 por endpoint), Replay, SerialTunnel (Link v1) | `dotnet/src/Atsm.Channels` | Vectores compartidos con C++; prueba cruzada con el agente nativo |
| Trazas neutrales: real y teórica por el mismo método, pila de eventos, métricas, comparación, anonimizador | `dotnet/src/Atsm.Tracing`, CLI `atsm-trace` | Pruebas sintéticas y trazas reales locales (ver abajo) |
| Plugin Abbott ARCHITECT (ASI RS-232) | `dotnet/plugins/Atsm.Plugin.Abbott.Architect` | Mensajes sintéticos según el manual 91407-108 y trazas reales locales |
| Plugin Sysmex U-WAM (LIS2-A2 / E1381-02) y adaptador del monitor de puertos de Sysmex | `dotnet/plugins/Atsm.Plugin.Sysmex.Uwam` | Mensajes sintéticos según R315168 rev. 22 y trazas reales locales |
| Plugin Abbott CELL-DYN Ruby (LIS2-A, derivado de trazas) | `dotnet/plugins/Atsm.Plugin.Abbott.CellDynRuby` | Mensajes sintéticos con la estructura de las trazas y trazas reales locales |
| Plugin de pruebas legacy (sólo adaptador de log, fuera del motor neutral) | `dotnet/plugins/Atsm.Plugin.Legacy.Testing` | Log sintético y logs reales locales |
| Bio-Rad CFX Maestro, CSV/RDML 1.0/1.1 | `dotnet/plugins/Atsm.Plugin.BioRad.Cfx` | Exportaciones sintéticas; referencias/exclusiones/Cq original, sin control directo ni homologación de modelos |
| Sysmex XN-L y CS-2500 | `dotnet/plugins/Atsm.Plugin.Sysmex.XnL`, `Atsm.Plugin.Sysmex.Cs2500` | Perfiles de recepción según manual; componentes de prueba/muestra y estados crudos; consultas/órdenes sin implementar |
| DIAGAST QWALYS 3 | `dotnet/plugins/Atsm.Plugin.Diagast.Qwalys` | Recepción ASCII, reacción R.4.7, comentarios C.3, soporte/estados preservados; sin homologación en equipo |
| Diagnóstico MAUI Windows independiente | `dotnet/tools/Atsm.Diagnostics.Maui`, `Atsm.Diagnostics.Core` | 8 equipos/familias y 12 perfiles técnicos, interpretación/grupos predeterminados, detección de logs/exportaciones, TCP/SerialTunnel y host de mantenimiento; journal DPAPI del usuario, custodia durable y reanálisis; ventana/handler, runtime privado y DPAPI comprobados por self-test |
| Agente nativo C++17 con TLS (Mbed TLS 4.1.1) | `native/` | 542 verificaciones nativas (CBOR, Link v1, árbitro, configuración, TLS sin archivos válidos, sesión completa por TCP); pruebas cruzadas mTLS desde .NET |
| Especificación ATSM Link v1 y vectores | `spec/link-v1.md`, `conformance/link-v1/vectors.txt` | Iguales en C++ y .NET |
| Datos QC/calibración y consulta | `AnalyticalQualityContext`, `IQualityDataSource`, `ObservationQualityDataSource`, `IQualityResourceContent` | Lote/material/módulo, INV/ZCL, estadísticas/curva CSV CFX y referencias S.7 U-WAM con custodia del lote completo. Sin reglas QC, descarga de archivos ni informe R24. Ver [CALIDAD-Y-CALIBRACION](CALIDAD-Y-CALIBRACION.md) |
| Firma Strong Name con la SNK de la distribución | `dotnet/Directory.Build.targets` | Mismo token que EDC (`066190a23251f202`); la clave no está en el repositorio |

Pruebas de esta entrega: `dotnet test dotnet/Atsm.slnx -c Release --no-restore -p:AtsmRequireStrongName=true` → 180 correctas, 0 omitidas (incluye 7 contra el `atsm-agent.exe` compilado, 5 de ellas por TLS con certificados de prueba); `ctest` → 542 verificaciones, 0 fallos. Dos corridas completas correctas en la ampliación de usabilidad. La intermitencia TLS de la migración anterior queda registrada en su changelog histórico. GUI: publicación MAUI autónoma y prueba de ventana/handler/runtime privado/DPAPI (`--self-test`); cinco advertencias CS8002 de MAUI oficiales sin Strong Name conservadas visibles, sin homologación de hardware. Repertorio, fuentes argentinas, uso y límites: [DIAGNOSTICO-MANTENIMIENTO](DIAGNOSTICO-MANTENIMIENTO.md).

## Verificación con datos reales (sólo local)

Las trazas reales no salen de `Documentacion/trazas_externas/` (ignorada). Resultados agregados, sin identificadores:

| Fuente | Resultado |
|---|---|
| Sysmex, puerto AMS → LIS (monitor de Sysmex, nivel cable) | 118 transmisiones, 9.572 resultados interpretados por el dialecto U-WAM; 2.064 frames con checksum distinto en el log y aceptados por el LIS: el monitor escribe como espacio el byte 0xB5 (µ de las unidades), confirmado al recalcular el checksum |
| Sysmex, puerto 2 AMS ↔ LIS | 124 consultas, 117 respondidas (latencia p50 252 ms, máx. 1,1 s) y 7 sin respuesta; 5 ENQ de más en cada puerto (reintentos de establecimiento), coherentes con los `Communication time-out` del log de Sysmex |
| Sysmex UF-5000 ↔ U-WAM (log de registros, sin frames) | 425 mensajes, 5.006 resultados y 155 consultas; la traza teórica reconstruye el cable completo (7.354 frames) |
| Real vs teórica (mismo método) | Mensajes idénticos en todos los casos; la comparación aísla lo que hizo el enlace real (reintentos, frames alterados, tiempos de reacción del LIS) |
| Exportaciones ARCHITECT (`Importar/`) | 1.071 resultados; 309 avisos de archivos de control sin registro P |
| Log legacy I1000 (adaptador del plugin de pruebas) | 837 mensajes recibidos, 4.753 resultados, 648 consultas, 1 sin respuesta |
| CELL-DYN Ruby (log legacy, plugin `abbott.celldyn-ruby`) | 7.492 transmisiones con mensajes completos en un frame y sondeos vacíos; 10.962 resultados, 336 alarmas, 2.361 consultas (1.989 `^ALL`), 1.309 respondidas (p50 120 ms) y 18 sin respuesta; los 1.316 mensajes del host legacy fueron rechazados por el equipo (`Parsing Error^RecordID`) |
| Anonimización de las trazas Sysmex y del sistema legacy | 0 hallazgos residuales; ningún nombre real de los registros P en la salida; interpretación idéntica antes y después |

## Diferencias con la propuesta

- Formato de traza v1: JSON Lines (`atsm-trace/1`). La aplicación orquestante posee la persistencia; cualquier captura durable del agente requiere custodia cifrada y alcance propio.
- El agente v1 no implementa `TraceRead`, observadores ni rol de conexión invertido; anuncia `traceCapture = 0`.
- Los bytes recibidos sin dueño se cuentan y se informan en `Granted`; no se entregan.
- TLS del agente con Mbed TLS 4.1.1: servidor TLS 1.2/1.3 con certificado de cliente obligatorio por defecto. El rol de cliente TLS del agente (conexión saliente) no está implementado.
- Catálogo integrado compartido por `atsm-trace` y la GUI; la carga/admisión de DLL en caliente corresponde sólo a EDC (decisión 2026-10-25). CFX se analiza desde GUI/API, no con los comandos ASTM de la CLI.

## Pendiente

Detalle, alcance y criterios de aceptación de cada pendiente: [CONTINUACION](CONTINUACION.md).

| Tema | Bloqueo |
|---|---|
| Build Windows XP | Toolset `v141_xp` y prueba en XP real; el código ya prevé la entropía propia (`-DATSM_WINDOWS_XP=ON`) |
| Build Linux / Raspberry | Distro WSL o equipo Linux; adaptador POSIX escrito, no compilado |
| ESP32 | Sin escribir; requiere ESP-IDF y hardware |
| Canal `LocalSerial` .NET | Paquete oficial `System.IO.Ports` |
| HL7 / LAW restante | Actor AWOS/LAB-28/LAB-29 y conformidad; R24/estado/inventario del fabricante, traza/anonimización HL7; ver P-02 |
| Manual del CELL-DYN Ruby | El plugin se apoya en trazas; confirmar timers, encuadre del host y respuesta negativa |
| Outbox durable del runtime | Sin implementar |
| Carga dinámica ATSM | Retirada 2026-10-25: sólo EDC carga en caliente; catálogo compilado en ATSM/Diagnostics |

## Decisiones registradas

- **Licencia:** libre uso, según el responsable del proyecto. No hay archivo de licencia formal.
- **Firma:** Strong Name con la misma SNK que EDC; Authenticode en el release.
- **Valores de interfaz:** los fija la documentación de cada marca o serie de equipos, en el perfil del plugin y con su cita.
- **Trazas:** ATSM genérico es neutral. Reconstruye trazas reales y teóricas a partir de contenido puro, y los plugins aportan adaptadores de log de su serie de equipos. El código del sistema legacy vive sólo en un plugin de pruebas; un plugin de integración con el sistema legacy sería un trabajo aparte.
- **Architect:** se envía el sexo del paciente (`SexPolicy.AlwaysUnknown` reproduce el comportamiento legacy); el tipo de resultado F/P/I va en `ResultKind`; página de códigos MS-DOS 850.
- **xUnit 2.9.3** sólo en pruebas, misma versión que EDC.
- **Mbed TLS 4.1 LTS** aprobado para el agente; se usa el parche 4.1.1 ([DEPENDENCIAS](DEPENDENCIAS.md)).
- **UC-3500:** cubierto por R315168. El analizador químico informa al LIS a través de U-WAM en el mismo ASTM (parámetros `C-*`, `C-Error Code` con el apéndice K, límite de 8 s); `UC-3500.txt` es el enlace interno U-WAM↔UC-3500, que el LIS no ve.

## Ampliación humana del 2026-10-24

P-01 y la independencia de LIS01 se entregan mediante mensajes completos. Las trazas pertenecen a la aplicación orquestante; para EDC, Application debe hospedar SQLCipher estándar antes de conectar y la futura UI ofrecerá diagnóstico autorizado, sin fallback plano de datos sensibles. No se implementó esa UI/almacén EDC en la biblioteca neutral. No se agrega spool de disco a SerialTunnel; la captura remota requiere diseño específico. Fuentes, autorización, casos y límites: [MENSAJES-TRAZAS-HL7](MENSAJES-TRAZAS-HL7.md).

## Corrección de GUI y distribución del 2026-10-25

MAUI reemplaza WPF por pedido humano. La publicación anterior fallaba por una referencia faltante al plugin previo al renombrado legacy, con .NET instalado. Ahora se publica en directorio limpio y se valida dependencia/arranque antes de promoverlo. `atsm-trace` se compiló y ejecutó con NativeAOT; la GUI MAUI Windows .NET 10 usa self-contained/ReadyToRun. Alcance, advertencias y límites en [MAUI-Y-NATIVEAOT](MAUI-Y-NATIVEAOT.md).

## Usabilidad de Diagnostics (2026-10-25)

Interpretación activable y predeterminada, grupos con contador/subfilas completos, tabla alineada a la izquierda y anchos automáticos al contenido/ventana, controles contrastados y casillas compactas; hexadecimal disponible en el detalle. Lista única de equipos con configuraciones internas (U-WAM comparte intérprete y sólo difiere en presupuesto de consulta). Detección por contenido y orientación Sysmex: UF-5000 vuelve a 5.006 observaciones; UC-3500 interno se conserva como protocolo sin intérprete, sin falsos registros. Paquete con 3 carpetas de idioma y 4 MUI nativos, sin borrar PRI/DLL. Seeds español neutral y adaptador Lang condicional (compilación + 1 prueba del Lang real con conexión sintética), sin dependencia EDC en la publicación estándar. Se mantiene el límite de inspección visual tras Esc del usuario. Detalles en [USABILIDAD-DIAGNOSTICS](USABILIDAD-DIAGNOSTICS.md).
