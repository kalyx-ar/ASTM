# Estado de la implementación

Instantánea del 26 de octubre de 2026. Describe lo que existe y lo que se comprobó; no reemplaza a la propuesta ni a la matriz de [IMPLEMENTACION](IMPLEMENTACION.md).

Campos UF de 102 bytes (2026-10-26): 1.981 mensajes de estado/contadores decodificados por estructura, con siete registros de contador/nivel y campos iniciales separados. Las 34.371 lecturas con datos quedan relacionadas con campos de su mensaje: un 06 numérico no aparece como ACK ni un 04 como EOT. El puerto neutral `BinaryFields` conserva rangos, valores y explicaciones traducibles; no hay lógica Sysmex en la presentación. Resumen del bloque con valores y detalle completo sin abrir cada fragmento. Se retiró el reconocimiento especulativo de los 13 bytes como mensaje aislado. Disponibilidad de reactivos es una hipótesis respaldada por la evolución de contadores y referencias del fabricante; siguen pendientes asignación física, unidades y códigos operativos de estado. [Disposición, evidencia y límites](UF-CONTROL-Y-TIEMPOS.md#campos-del-tipo-2).

Salida de campos UF verificada: GUI MAUI autónoma/ReadyToRun y CLI NativeAOT `0.1.0+62aa329e5f755ad5b1c1fc084811c13b3e44aecc`. 391 pruebas .NET en dos pasadas finales, CTest repetido y 542 comprobaciones nativas; Lang real 1 correcta (258 ms). Self-test GUI correcto (7,77 s), NativeAOT y auditoría de dependencias correctos. Corpus: 2.193 mensajes/349 avisos UF binarios, UF ASTM 425/5.006, UC 501/2.251 y AMS 118/9.572, sin pérdida. 19 DLL propias con token de distribución, cinco CS8002 MAUI oficiales y Authenticode pendiente; notas/enlaces retirados ausentes en DLL propias y CLI. La primera pasada tuvo un fallo intermitente de identidad TLS; pasó aislado y en las dos suites finales, sin cambiar TLS ni timeouts. [Informe y límites en #205](https://github.com/kalyx-ar/EDC/issues/205#issuecomment-6419638927).

Entrega de normalización verificada (2026-10-26): GUI MAUI autónoma/ReadyToRun y CLI NativeAOT `0.1.0+33abd2486c15b3cc80e40114ba2f7c8a93219580`. Parser binario/hex neutral con layout declarativo, vocabulario ASTM y traducciones comunes en el SDK, extensiones específicas en plugins. 384 pruebas .NET y 542 comprobaciones nativas repetidas; Lang real 1 correcta (171 ms). Self-tests de ambas salidas y auditoría de dependencias correctos; GUI verifica expansión/contracción sin perder posición y grupos completos en borde de página. Corpus UF binario 2.193 mensajes/349 avisos, UF ASTM 425/5.006, UC 501/2.251 y AMS 118/9.572 sin pérdida. Cadenas de notas/enlaces retiradas comprobadas ausentes en DLL propias y CLI. 19 DLL propias con token de distribución; cinco CS8002 MAUI oficiales y Authenticode pendiente, sin dependencias nuevas. [Informe en #205](https://github.com/kalyx-ar/EDC/issues/205#issuecomment-6409576587).

UF binario y comunicación (2026-10-26): encuadre por longitud en la misma familia Sysmex, 2.193 mensajes completos que cubren los 500.444 bytes de 34.373 entradas. Se extraen 349 avisos técnicos por estructura: cierre, material de control/reactivo caducado, RBC alto y necesidades de mantenimiento; también se muestran los identificadores recibidos. No depende de nombres de archivo ni de una lista cerrada de frases. Trama agrupa bloques completos y sólo repeticiones consecutivas idénticas; las lecturas originales siguen como subfilas. Tiempos HH:mm:ss.fff en Trama/Comunicación, imágenes por referencia comunes a los perfiles y notas de desarrollo retiradas también del código compilado. Respuestas, operación total y proceso permanecen separados; las instrucciones no prueban ejecución. 8 familias, 14 perfiles, 8 vistas y SDK API 2. 384 pruebas .NET. [Evidencia y límites](UF-CONTROL-Y-TIEMPOS.md).

Ampliación de knowledge (2026-10-25): U-WAM incorpora 11 valores de investigación y 19 comentarios de revisión; XN-L, 49 indicadores IP y flags por contexto; CS-2500, los 68 códigos numéricos de evaluación de la tabla 19 y el ejemplo de reactivo insuficiente; QWALYS, tipos/soportes/estados y reacciones documentadas; ARCHITECT, tipos y flags con significado explícito. Valores interpretados traducibles por DisplayText, originales intactos y códigos de investigación/IP fuera de estadísticas numéricas. 326 pruebas .NET. [Cobertura y límites](DIAGNOSTICO-MANTENIMIENTO.md#tablas-de-conocimiento-ampliadas-2026-10-25).

Entrega knowledge verificada: GUI MAUI y CLI NativeAOT 0.1.0+daef5fbaa0fe1da325f87b2397fd87abe1d34720, self-tests correctos y auditoría de referencias correcta. 326 pruebas .NET y 542 comprobaciones nativas repetidas; Lang real: 1 prueba correcta (164 ms). Corpus: UF-5000 425/5.006 y AMS 118/9.572 mensajes/observaciones sin pérdida; UF-5000Control 34.373 entradas recuperadas, aún sin decodificador semántico. 19 DLL propias con token de distribución; cinco CS8002 MAUI oficiales sin Strong Name y Authenticode pendiente. [Informe en #205](https://github.com/kalyx-ar/EDC/issues/205#issuecomment-6405834890). Sin nuevas dependencias ni homologación física.

Incremento Calidad (2026-10-25): Tipo y Unidad independientes, Estado real en lugar de Señales/Una medición, estados mixtos con frecuencias y estados originales por subfila. Fallbacks legibles para todos los perfiles del catálogo, sin valores como /73.5 en el tipo; unidades compuestas vacías/repetidas normalizadas en presentación conservando el original. UF-5000 QC: R.4.2 sólo es formato si contiene un código documentado y O.4.3 identifica el material cuando O.3 está vacío (mapeo derivado de traza); R.3.5 sólo se interpreta como método para A/M. UC-3500: unidades separadas y explícitas cuando están definidas; concentración sin unidad transmitida indica No informada. 00CC muestra Error de solicitud (00CC). [Detalle](CALIDAD-DIAGNOSTICA.md#tipo-y-unidad-independientes-2026-10-25).

Entrega Calidad verificada: GUI MAUI y CLI NativeAOT 0.1.0+ba8b2f405480c05fa1671005029b791df4c97756, self-tests y auditoría de referencias correctos. 290 pruebas .NET y 542 comprobaciones nativas repetidas; Lang real: 1 prueba correcta (170 ms). Corpus: UF-5000 33 registros QC con material conservado y estado Normal; UC-3500 111 resultados 00CC interpretados; AMS 220 códigos 9902 con correspondencia 00CC. Sin inferir unidades de concentración, canales binarios ni homologación física. 19 DLL propias con token de distribución; cinco CS8002 MAUI conocidas y Authenticode pendiente. [Resultado en #205](https://github.com/kalyx-ar/EDC/issues/205#issuecomment-6405151002).

Incremento de interpretación semántica (2026-10-25): tabla K completa U-WAM en el plugin (38 códigos, incluido 0000; variantes UC-3500/UC-1000), tablas de tipos/formatos/origen y flags contextuales. 9902 muestra «Error de solicitud», con correspondencia 00CC en detalle. N muestra únicamente «Normal» y no cuenta como alarma; IF muestra «Imagen» en tipo/resultado, sin estadísticas numéricas ni apertura de referencias. Mensajes conservan identificación/muestra/solicitud, omiten H/L en el resumen, explican consultas por muestra y añaden «Con error» sólo a los mensajes afectados. 271 pruebas .NET y 542 verificaciones nativas; evidencia de la salida final en el changelog de este incremento. [Conocimiento y límites](DIAGNOSTICO-MANTENIMIENTO.md#conocimiento-y-resúmenes-semánticos-2026-10-25).

UF binario: conserva la corrección de 315 lecturas perdidas por decoración entre líneas: 34.373 entradas (34.371 lecturas y 2 eventos), 500.444 bytes y 399.939 NUL, sin diferencias con contadores. Las 34.058 entradas anteriores eran extracción parcial. El encuadre completo ya está reconstruido: 1.981 mensajes tipo 2, 105 tipo 6, 105 tipo 11 y 2 tipo 12. Los patrones de 13 bytes están dentro de los payloads tipo 2 y no se duplican como mensajes independientes. P-15 conserva la semántica de campos numéricos y restantes, integridad, roles físicos y límites de operaciones. El otro log del mismo UF-5000 contiene resultados ASTM (425 mensajes/5.006 observaciones).

Entrega semántica verificada: GUI MAUI y CLI NativeAOT 0.1.0+4df462fc2266a7b25d83306ab614221621c6cfa3, self-tests correctos y auditoría de referencias GUI correcta. 271 pruebas .NET y 542 verificaciones nativas repetidas; Lang real: 1 prueba correcta (159 ms) de Normal/Imagen/9902. Corpus AMS: sus 220 registros 9902 se identifican con correspondencia 00CC; se conservan 118 mensajes/9.572 observaciones. 19 DLL propias con token de distribución y cinco advertencias CS8002 MAUI conocidas; Authenticode pendiente. [Resultado en #205](https://github.com/kalyx-ar/EDC/issues/205#issuecomment-6404375177). Sin nuevas dependencias ni homologación física.

## Implementado

Ajuste de presentación (2026-10-25): hallazgos genéricos UC-3500 abreviados a «Error relacionado con las tiras reactivas», «Error de análisis» y «Error del sistema óptico»; código y referencia permanecen en detalle.

Revisión UF-5000 (2026-10-25): reanálisis de casos antes marcados como incompatibles y reconocimiento de cabeceras partidas por lecturas, sin mezclar RX/TX. Firmas de cabecera en cada plugin mediante IInstrumentHeaderProbe: U-WAM y modelos documentados UF/UC/UD, XN-350/450/550, CS-2500, ASI y CDRuby; sin inferir modelos desde comentarios o nombres. Se mantienen 8 familias/13 perfiles. Lote ausente «-», RAW «Original». [Cobertura y límites por modelo](DIAGNOSTICO-MANTENIMIENTO.md#revisión-de-detección-y-modelos-2026-10-25).

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
| Transporte sobre `Stream` (decisión 2026-10-26): sesiones ASTM/HL7/mensajes completos reciben el transporte abierto por el anfitrión; `TransportKnowledge` en perfiles con evidencia (ARCHITECT, QWALYS, CS-2500, XN-L, U-WAM) | `dotnet/src/Atsm.Abstractions/Transport`, `Atsm.Runtime/Sessions` | Ver [TRANSPORTE](TRANSPORTE.md) |
| `Atsm.Transport` (opcional) | `dotnet/src/Atsm.Transport` | COM local con `System.IO.Ports` (mapeo probado sin hardware), TCP/TLS cliente y servidor, enrutador; `ReplayStream` en `Atsm.Tracing` |
| `PortGate.Client` | `dotnet/src/PortGate.Client` | Link, administración, cesión, kill switch e impugnación; proveedor de transporte que aplica los mismos `SerialSettings` detrás del túnel; vectores compartidos con C++ |
| Trazas neutrales: real y teórica por el mismo método, pila de eventos, métricas, comparación, anonimizador | `dotnet/src/Atsm.Tracing`, CLI `atsm-trace` | Pruebas sintéticas y trazas reales locales (ver abajo) |
| Plugin Abbott ARCHITECT (ASI RS-232) | `dotnet/plugins/Atsm.Plugin.Abbott.Architect` | Mensajes sintéticos según el manual 91407-108 y trazas reales locales |
| Plugin Sysmex U-WAM (LIS2-A2 / E1381-02) y adaptador del monitor de puertos de Sysmex | `dotnet/plugins/Atsm.Plugin.Sysmex.Urinalysis` | Mensajes sintéticos según R315168 rev. 22 y trazas reales locales |
| Plugin Abbott CELL-DYN Ruby (LIS2-A, derivado de trazas) | `dotnet/plugins/Atsm.Plugin.Abbott.CellDynRuby` | Mensajes sintéticos con la estructura de las trazas y trazas reales locales |
| Plugin de pruebas legacy (sólo adaptador de log, fuera del motor neutral) | `dotnet/plugins/Atsm.Plugin.Legacy.Testing` | Log sintético y logs reales locales |
| Bio-Rad CFX Maestro, CSV/RDML 1.0/1.1 | `dotnet/plugins/Atsm.Plugin.BioRad.Cfx` | Exportaciones sintéticas; referencias/exclusiones/Cq original, sin control directo ni homologación de modelos |
| Sysmex XN-L y CS-2500 | `dotnet/plugins/Atsm.Plugin.Sysmex.XnL`, `Atsm.Plugin.Sysmex.Cs2500` | Perfiles de recepción según manual; componentes de prueba/muestra y estados crudos; consultas/órdenes sin implementar |
| DIAGAST QWALYS 3 | `dotnet/plugins/Atsm.Plugin.Diagast.Qwalys` | Recepción ASCII, reacción R.4.7, comentarios C.3, soporte/estados preservados; sin homologación en equipo |
| Diagnóstico MAUI Windows independiente | `dotnet/tools/Atsm.Diagnostics.Maui`, `Atsm.Diagnostics.Core` | 8 equipos/familias y 13 perfiles técnicos, texto seleccionable en etiquetas/celdas, interpretación/grupos predeterminados, detección de logs/exportaciones, TCP/SerialTunnel y host de mantenimiento; journal DPAPI del usuario, custodia durable y reanálisis; ventana/handler, runtime privado y DPAPI comprobados por self-test |
| UC-3500 serial propio | `Atsm.Plugin.Sysmex.Urinalysis`, puerto `IInstrumentTracePlugin` | Reconstrucción pasiva RQ02/CM00/ER, resultados, BCC/SUM/SUM2/NONE, controles y correlación. Captura sin envío; no host activo. 501 mensajes/2.251 observaciones en captura local, sin falsa interpretación ASTM. Ver [UC3500-PROTOCOLO](UC3500-PROTOCOLO.md) |
| PortGate, agente C++17 con TLS (Mbed TLS 4.1.1) | `PortGate/` | 639 verificaciones nativas (CBOR, Link v1, árbitro con tope de tenencia/cuarentena/cierre, configuración CBOR, estado DPAPI, registro de eventos con rotación, administración, cesión, rechazo de kill switch en plano, PSK de instalación); pruebas cruzadas .NET: mTLS, administración restringida por certificado, kill switch/impugnación/cierre y reapertura |
| Administración remota de PortGate (P-16) | `PortGate/core/src/supervisor.cpp`, `spec/admin-v1.md`, `dotnet/src/PortGate.Client` | Estado protegido sin historial de configuración, arranque con la última configuración, endpoint TLS/plano en un puerto, certificado autogenerado, SetTls sólo por TLS, PSK, ListDevices (SetupAPI), servicio Windows y unidad systemd. `service install` no se ejecutó en esta sesión (requiere elevación); POSIX sin compilar; cambios de dispositivo en caliente y modo iniciado por el agente (salientes con rol asignado por el servidor, TLS con fijación mutua o PSK) probados |
| Especificación PortGate Link v1 y vectores | `spec/link-v1.md`, `conformance/link-v1/vectors.txt` | Iguales en C++ y .NET |
| Datos QC/calibración y consulta | `AnalyticalQualityContext`, `IQualityDataSource`, `ObservationQualityDataSource`, `IQualityResourceContent` | Lote/material/módulo, INV/ZCL, estadísticas/curva CSV CFX y referencias S.7 U-WAM con custodia del lote completo. Sin reglas QC, descarga de archivos ni informe R24. Ver [CALIDAD-Y-CALIBRACION](CALIDAD-Y-CALIBRACION.md) |
| Firma Strong Name con la SNK de la distribución | `dotnet/Directory.Build.targets` | Mismo token que EDC (`066190a23251f202`); la clave no está en el repositorio |

Pruebas de esta entrega: dotnet test dotnet/Atsm.slnx -c Release --no-restore -p:AtsmRequireStrongName=true → 251 correctas, 0 omitidas; ctest → 542 verificaciones, 0 fallos (~0,25 s). Incluye 7 contra el agente compilado, 5 por TLS. Los 20 casos nuevos cubren firmas por modelo, todas las divisiones de la cabecera UF-5000 en ambas direcciones, reanálisis sin mutar evidencia, rechazo de tramas y falsos indicios/ambigüedad. Prueba opcional del Lang real y self-tests de salida: evidencia final en el changelog de esta revisión. Se mantienen visibles cinco advertencias CS8002 de MAUI oficiales sin Strong Name; ensamblados propios firmados, Authenticode pendiente. Sin homologación física.

Salida final de esta revisión: GUI MAUI y CLI NativeAOT 0.1.0+a9ee096d587340c9a6947730a299ce7e72784bc1. Ambas suites repetidas; self-test MAUI PASS/salida 0 (ventana, runtime privado, DPAPI, siete vistas y lote ausente), self-test NativeAOT y auditoría de referencias GUI correctos. Corpus local verificado desde NativeAOT: UF-5000 425/5.006, UC-3500 501/2.251 y U-WAM AMS 118/9.572 mensajes/observaciones. 19 DLL propias con token 066190A23251F202; Authenticode pendiente. No se repitió el test opcional contra Lang real: no cambió su adaptador; sí se comprobó el seed español de las etiquetas modificadas. El usuario autorizó publicar el informe y se envió el 2026-10-25 a [#205](https://github.com/kalyx-ar/EDC/issues/205#issuecomment-6402918370), resolviendo el bloqueo previo de revisión automática. El alcance previo ya estaba registrado en #205 antes de editar.

La intermitencia TLS de Certificate_must_identify_the_declared_controller permanece pendiente. En el incremento UC-3500 también falló una vez Fragmented_MLLP_and_coalesced_blocks_are_acknowledged_individually; pasó aislada en 122 ms y en las corridas finales completas. No se alteraron protocolos existentes ni timeouts para obtener un resultado correcto. Repertorio y límites: [DIAGNOSTICO-MANTENIMIENTO](DIAGNOSTICO-MANTENIMIENTO.md).

Calidad: resumen por material/lote, prueba y tipo/unidad, con mínimo, mediana/resultado compacto, máximo, DE, total de mediciones comunicadas y columna Sin medir; cruces ordinales y límites semicuantitativos con comparadores conservados, distribución completa en detalle, posibles atípicos/alarmas resaltados y originales en subfilas. El detalle separa el conteo numérico usado para estadística del total comunicado. U-WAM único con interpretación automática y opción manual; plugin de familia con conocimiento por contexto. Ver [CALIDAD-DIAGNOSTICA](CALIDAD-DIAGNOSTICA.md).

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
- PortGate implementa (2026-10-26, P-09) el rol de conexión invertido, observadores de sólo lectura con derecho explícito y captura sellada AES-256-GCM con clave del anfitrión, borrado por custodia y huecos explícitos; anuncia `traceCapture = 2` sólo en puertos con captura. Falta la custodia en EDC/SQLCipher (P-13).
- Los bytes recibidos sin dueño se cuentan y se informan en `Granted`; no se entregan.
- TLS del agente con Mbed TLS 4.1.1: servidor TLS 1.2/1.3 con certificado de cliente obligatorio por defecto. El rol de cliente TLS del agente (conexión saliente) no está implementado.
- Catálogo integrado compartido por `atsm-trace` y la GUI; la carga/admisión de DLL en caliente corresponde sólo a EDC (decisión 2026-10-25). CFX se analiza desde GUI/API, no con los comandos ASTM de la CLI.

## Pendiente

Detalle, alcance y criterios de aceptación de cada pendiente: [CONTINUACION](CONTINUACION.md).

| Tema | Bloqueo |
|---|---|
| Build Windows XP | Toolset `v141_xp` y prueba en XP real; el código ya prevé la entropía propia (`-DPORTGATE_WINDOWS_XP=ON`) |
| Build Linux / Raspberry | Distro WSL o equipo Linux; adaptador POSIX escrito, no compilado |
| ESP32 | Sin escribir; requiere ESP-IDF y hardware |
| COM local con hardware real | `Atsm.Transport` implementado; falta probar con un adaptador físico |
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

Interpretación activable y predeterminada, grupos con contador/subfilas completos, tabla alineada a la izquierda y anchos automáticos al contenido/ventana, controles contrastados y casillas compactas; hexadecimal disponible en el detalle. Lista única de equipos con configuraciones internas (U-WAM comparte intérprete y sólo difiere en presupuesto de consulta). Detección por contenido y orientación Sysmex: UF-5000 vuelve a 5.006 observaciones; UC-3500 dispone ahora de intérprete serial propio, sin pasar por ASTM; ver [UC3500-PROTOCOLO](UC3500-PROTOCOLO.md). Paquete con 3 carpetas de idioma y 4 MUI nativos, sin borrar PRI/DLL. Seeds español neutral y adaptador Lang condicional (compilación + 1 prueba del Lang real con conexión sintética), sin dependencia EDC en la publicación estándar. Se mantiene el límite de inspección visual tras Esc del usuario. Detalles en [USABILIDAD-DIAGNOSTICS](USABILIDAD-DIAGNOSTICS.md).
