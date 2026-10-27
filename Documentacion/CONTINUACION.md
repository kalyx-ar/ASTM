# Continuación del trabajo

Guía para retomar ATSM desde otra sesión o agente. Estado al 27 de octubre de 2026, `main` en `github.com/kalyx-ar/ATSM`. Lo implementado y verificado está en [ESTADO](ESTADO.md); las reglas de trabajo, en [AGENTS.md](../AGENTS.md).

Incremento de transporte/diagnóstico: COM de prueba propio de PortGate, sin driver; escenarios derivados de trazas con orden del plugin → interpretación del instrumento simulado → resultado y verificación de tiempos. GUI: Probar PortGate, COM local y opciones PortGate; catálogo de acciones opcional del plugin. [Uso y límites](PRUEBAS-SERIE.md). API/paquetes 0.2.0 para EDC. No equiparar este ensayo con validación del driver COM o del hardware.

Salida vigente verificada: GUI MAUI autónoma y CLI NativeAOT `0.2.0+d6f101d0e1d4fa290c92aa6854d0ea51d7cb696a`, con agente PortGate incluido. Self-tests, auditoría de referencias, ensayos UF/AMS y cancelación correctos. Suite .NET: 411 correctas/una omitida, repetida; nativo: 746 comprobaciones correctas repetidas. EDC consume esos paquetes: cinco pruebas de adaptador correctas repetidas. P-13 mantiene servicio Scheduler, custodia SQLCipher y monitor integrado pendientes. Firma Authenticode pendiente; advertencias conocidas y copia ReadyToRun de Microsoft documentadas en PRUEBAS-SERIE.

## Cómo retomar

Presentación UC-3500: los hallazgos genéricos usan frases breves; la conservación de códigos/originales se explica en el detalle, sin repetirla en cada fila.

```bash
dotnet test dotnet/Atsm.slnx
```

```bash
cmake -S PortGate -B PortGate/build/win-x64 -G "Visual Studio 18 2026" -A x64
```

```bash
cmake --build PortGate/build/win-x64 --config Release
```

```bash
ctest --test-dir PortGate/build/win-x64 -C Release
```

Al cerrar los campos UF: 391 pruebas .NET (7 contra el agente compilado, 5 por TLS) y 542 verificaciones nativas. Repetir ambas antes de commitear. GUI Windows Release y self-test de ventana/runtime privado/DPAPI por separado; incluye 8 vistas, 14 perfiles, avisos y contadores UF, campos por lectura, bloques cronológicos y tiempos HH:mm:ss.fff. SDK API 2: discriminación por perfil, intervalos de respuesta/operación/proceso separados y campos binarios con rangos. Las pruebas cruzadas se omiten (y lo informan) si el agente no está compilado.

Salida anterior verificada: GUI y CLI 0.1.0+62aa329e5f755ad5b1c1fc084811c13b3e44aecc; self-tests, auditoría de referencias y corpus agregado correctos. Lang real: 1 prueba correcta (258 ms). [Informe de campos binarios en #205](https://github.com/kalyx-ar/EDC/issues/205#issuecomment-6419638927). Parser neutral BinaryWireParser/BinaryFrameLayout y lectura de campos de 1–4 bytes; vocabulario/seed ASTM común en Atsm.Plugins. BinaryFields y FirstByteOffset relacionan campos reconstruidos con lecturas, incluso de un byte, sin falsos ACK/EOT dentro de números. UF binario: 34.373 entradas, 2.193 mensajes completos, 349 avisos y 1.981 mensajes de contadores. Las 34.371 lecturas con datos tienen contexto de campos. El nombre del archivo no participa. P-15 conserva asignación física/unidades de contadores, nombres operativos de estados, campos desconocidos, integridad, roles y límites de operaciones; no inventar porcentajes ni nombres de reactivos a partir de la hipótesis documentada. Expansiones mantienen posición y no se cortan hijos al paginar.

Aplicación Windows: dotnet publish dotnet/tools/Atsm.Diagnostics.Maui/Atsm.Diagnostics.Maui.csproj -c Release -p:AtsmRequireStrongName=true; salida directa en dotnet/tools/Atsm.Diagnostics.Maui/bin/Release. No hay script de empaquetado ni ZIP.

Verificación local con trazas reales (no se publican; imprimir sólo agregados):

```bash
dotnet run --project dotnet/tools/Atsm.Tools.Trace -- plugins
```

```bash
dotnet run --project dotnet/tools/Atsm.Tools.Trace -- extract sysmex.port-monitor "Documentacion/trazas_externas/Sysmex/20251105_AMS .txt" ams.atsmtrace --recorded-by instrument
```

```bash
dotnet run --project dotnet/tools/Atsm.Tools.Trace -- metrics ams.atsmtrace --profile sysmex.uwam.lis2a2.without-chemistry
```

Los mismos pasos sirven con `legacy.interface-log` (logs `Documentacion/trazas_externas/legacy/Logs/*.log`) y los perfiles `abbott.architect.asi-rs232` o `abbott.celldyn-ruby.lis2a`. `theoretical` y `compare` construyen la traza teórica y la comparan con la real. Las cifras de referencia están en ESTADO § Verificación.

## Mapa rápido

| Pieza | Dónde |
|---|---|
| Contratos neutrales | `dotnet/src/Atsm.Abstractions` |
| LIS01-A2 (enlace activo y observador pasivo) | `dotnet/src/Atsm.Protocol.Lis01` |
| LIS02-A2 / MLLP | `dotnet/src/Atsm.Protocol.Lis02`, `Atsm.Protocol.Mllp` |
| SDK de plugins, dialecto estándar, adaptador regex de logs | `dotnet/src/Atsm.Plugins` |
| Sesión ASTM | `dotnet/src/Atsm.Runtime/Sessions/AstmInstrumentSession.cs` |
| Transporte opcional y cliente PortGate | `dotnet/src/Atsm.Transport`, `dotnet/src/PortGate.Client` |
| Trazas real/teórica, pila, métricas, anonimizador | `dotnet/src/Atsm.Tracing`, CLI `dotnet/tools/Atsm.Tools.Trace` |
| Plugins | `dotnet/plugins/*` (Architect, Ruby, U-WAM, XN-L, CS-2500, Bio-Rad CFX, QWALYS y legacy de pruebas) |
| Catálogo compartido y anfitrión de mantenimiento | `dotnet/tools/Atsm.Diagnostics.Core` |
| GUI MAUI Windows | `dotnet/tools/Atsm.Diagnostics.Maui` (DPAPI del usuario; no depende de EDC) |
| Agente nativo | `PortGate/core`, `PortGate/platform`, `PortGate/tls`, `PortGate/app` |
| Protocolo agente ↔ host | `spec/link-v1.md`, `conformance/link-v1/vectors.txt` |
| Sistema legacy (sólo consulta) | `../LEGACY` (fuera del repositorio; ver [REFERENCIAS-LEGACY](REFERENCIAS-LEGACY.md)) |

## Pendientes

Orden sugerido por valor y por dependencias. Cada uno debe terminar con pruebas, `ESTADO.md` actualizado y esta tabla corregida.

| ID | Tema | Bloqueo |
|---|---|---|
| P-02 | Parcial: HL7/MLLP y ARCHITECT implementados; completar actor LAW/AWOS y conformidad | Requiere el alcance restante de perfiles/equipo |
| P-03 | Kits de conformidad por plugin y corpus anonimizado | Revisión humana del corpus antes de publicarlo |
| P-04 | Outbox durable del runtime | Ninguno |
| P-05 | Retirado: catálogo compilado ATSM/Diagnostics (decisión 2026-10-25); carga en caliente sólo EDC | No corresponde implementar ALC en ATSM |
| P-06 | Build Windows XP | Instalar toolset `v141_xp`; equipo o VM XP |
| P-07 | Build Linux / Raspberry Pi | Distro WSL o equipo Linux/ARM |
| P-08 | Firmware ESP32 | ESP-IDF y hardware con interfaz RS232 |
| P-09 | Resuelto el 2026-10-26 en PortGate: rol cliente TLS (P-16), captura sellada AES-256-GCM con clave del anfitrión y borrado por custodia, y observadores de sólo lectura con derecho explícito | Falta integrar la custodia en EDC/SQLCipher (P-13) y la rotación de claves con reencriptado de lo pendiente |
| P-10 | Resuelto: COM local en `Atsm.Transport` (`System.IO.Ports` aprobado 2026-10-26); falta prueba con hardware | Adaptador USB-serie real |
| P-11 | Confirmación del CELL-DYN Ruby | Manual LIS del equipo o analizador de prueba |
| P-12 | Robustez del agente | Ninguno |
| P-13 | Integración con EDC | Proceso de EDC (Issue de decisión, aprobación de paquetes) |
| P-14 | Empaquetado NuGet, API pública y CI | Decidir el feed |
| P-15 | Completar semántica binaria UF en la familia Sysmex | Encuadre completo: 2.193 mensajes/500.444 bytes, 349 avisos y 1.981 mensajes de estado/contadores; lecturas relacionadas con campos. Faltan asignación física/unidades de los siete contadores/niveles, nombres operativos de estados/indicadores, campos restantes, integridad, roles y límites de operaciones reales. Hipótesis de disponibilidad de reactivos documentada. No convertir instrucciones/necesidades en ejecuciones ni tiempos entre lecturas en RTT. Ver UF-CONTROL-Y-TIEMPOS |
| P-16 | Parcial: PortGate como servicio, administración remota, cambios de dispositivo en caliente y modo iniciado por el agente implementados; faltan verificación de `service install` elevado, build POSIX e integración EDC | Ver § P-16 |

### P-01 Resuelto: mensajes completos del anfitrión

La decisión humana del 2026-10-24 reemplaza el canal FileDrop propuesto: ATSM no vigila ni administra archivos. `AstmMessageProcessor` recibe bytes/Stream con un `receptionId` conservado por el anfitrión; `WholeMessageInstrumentSession` consume límites de mensajes sin LIS01 y comparte el procesador con la sesión ASTM. El host conserva custodia/idempotencia y disposición del original. EDC lo compone con Scheduler + conector ETL; DeltaLab queda allí.

Aceptación comprobada: equivalencia semántica con el dialecto existente, recepción fragmentada, rechazo de mensajes incompletos/límites, Stream del llamador abierto, IDs estables al reintentar y distintos para otra recepción, cancelación y fallos de traza observables. No se atribuye gestión de carpetas a ATSM.

### P-02 HL7 v2.5.1 / IHE LAW — primer incremento entregado

Implementados codec HL7 con MSH/separadores/escapes, primitivas ACK original/mejorado, sesión MLLP **original**, host query con presupuesto, ingestión antes de AA y correlación de ORL. `ArchitectHl7Plugin` en el plugin Abbott interpreta OUL^R22, responde QBP^ZOS con RSP^ZOS y construye OML^O33; no se sustituye ZOS por Q11. Los presupuestos se suministran desde la configuración del equipo.

LAW: `LawQueryMessages` implementa primitivas LAB-27 WOS por contenedor y RSP^K11, sin incrustar órdenes (LAB-28 es separado). No se implementó un actor LAW completo ni se declara conformidad. Quedan AWOS/lifecycle LAB-28/LAB-29, opciones y cardinalidades exhaustivas; OUL^R24, estados/inventario/notificaciones del fabricante, traza semántica/anonimizador HL7 y kit de conformidad. El runtime rechaza modo mejorado; disponer de sus códigos no acredita custodia commit.

Manual local usado: `manuales/ASTM Alinity 206797-101.pdf`, cuyo contenido es ARCHITECT System HL7 Interface Manual 206797-101 (2012). Detalles, pruebas, fuentes y límites en [MENSAJES-TRAZAS-HL7](MENSAJES-TRAZAS-HL7.md).

### P-03 Kits de conformidad por plugin

- PLUGINS § Kit de conformidad está propuesto pero no implementado: `conformance/<plugin>/corpus/*.atsmtrace` anonimizado, `expected/*.json` con la salida canónica, `scenarios.json` y `CONFORMANCE.md`.
- `Atsm.Testing.ConformanceRunner`: reproduce cada traza con el plugin y compara contra `expected/`.
- **Orden:** Architect, U-WAM, CELL-DYN Ruby. Los corpus salen de las trazas locales mediante `atsm-trace anonymize`. **No publicar sin revisión humana**, aunque el informe sea limpio.

### P-04 Outbox durable del runtime

Si `ILabWorkflowHost.IngestAsync` falla, hoy el lote sólo queda como diagnóstico. Hace falta un buzón local durable (orden e identidad por `ObservationId`), reenvío con espera, y un lugar en la traza para lo que se reenvió. Ver MODELO-CANONICO § Puertos.

### P-05 Retirado: carga dinámica sólo en el anfitrión EDC

Decisión humana 2026-10-25: ATSM/Diagnostics conservan catálogo compilado, preparado para AOT; EDC gestiona carga/admisión en caliente. La propuesta previa de AssemblyLoadContext en ATSM se retira. Mantener bibliotecas administradas neutrales reutilizables por EDC; no modificar su confianza desde Diagnostics. Estado MAUI/NativeAOT y publicación limpia en [MAUI-Y-NATIVEAOT](MAUI-Y-NATIVEAOT.md).

### Entrega de mantenimiento y repertorio (2026-10-24)

GUI Windows implementada con log/exportación y TCP/SerialTunnel, caso protegido preparado antes de conectar y simulación de host sin órdenes; 11 perfiles. Añadidos CFX CSV/RDML y perfiles de recepción Sysmex XN-L, CS-2500 y QWALYS. Fuentes, capacidades, presencia argentina y límites en [DIAGNOSTICO-MANTENIMIENTO](DIAGNOSTICO-MANTENIMIENTO.md).

Continuar por validación real CFX/XN-L, kits P-03 y extensión por modelo/firmware (prioridad humana CFX → Sysmex → QWALYS). La revisión de SUIT v8 confirma un dialecto E1238-94 con OBR/OBX y QC S, distinto de XN-L/U-WAM: requiere su propio intérprete, corpus y puertos de recepción; no reutilizar el parser de tipos de un carácter para aparentar soporte de XN-1000/XN. [Matriz y campos QC documentados](DIAGNOSTICO-MANTENIMIENTO.md#revisión-de-detección-y-modelos-2026-10-25). No declarar soporte de toda una marca por compartir protocolo. La GUI no completa P-04, P-09, P-10 ni P-13; P-05 fue retirado por la decisión posterior. Para EDC las trazas continúan siendo responsabilidad de Application/SQLCipher.

### Datos QC y calibración (incremento 2026-10-24)

Implementados contexto analítico tipado, lector opcional `IQualityDataSource` sobre lotes custodiados, recurso S.7 U-WAM en la misma frontera `IngestAsync` (lotes sin observaciones válidos), puerto opcional `IQualityResourceContent` del anfitrión, estadísticas/curva estándar CSV de CFX y pestaña Calidad. El catálogo pasa a 12 perfiles; no se abre ni descarga un recurso externo automáticamente. Matriz de fabricante, ejemplos y límites: [CALIDAD-Y-CALIBRACION](CALIDAD-Y-CALIBRACION.md).

Continuación: R24/ED de calibración ARCHITECT (incluido en P-02; puerto de informes documentado, sin implementar), importador/asociación de CSV de objetivos/límites U-WAM, señales/curvas RFU y SUIT con corpus por modelo. Reglas/estadísticas derivadas, valores asignados/versionados y almacenamiento/consulta autorizados son del anfitrión (P-13 para EDC), no criterios genéricos de aceptación ATSM.

### P-06 Build Windows XP

- Instalar el componente "C++ Windows XP Support for VS 2017 (v141) tools" y configurar con `-T v141_xp -A Win32 -DPORTGATE_WINDOWS_XP=ON`.
- Verificar que compilen Mbed TLS 4.1.1 y el agente. Riesgos: APIs posteriores a XP dentro de Mbed TLS más allá de la entropía, `std::filesystem` en el STL de v141, `getaddrinfo` y `select`.
- Probar en XP real: puerto serial, TLS 1.2/1.3 contra el host .NET y entropía (`entropy_win32_xp.c`). Documentar exactamente qué se probó.

### P-07 Build Linux / Raspberry Pi

`PortGate/platform/posix/serial_posix.cpp` está escrito pero nunca se compiló. Compilar con GCC/Clang (`-Werror`), correr `ctest`, probar con un adaptador USB-serial real y generar el binario ARM (Raspberry). En POSIX, `threading_alt.cpp` funciona igual; no hace falta `MBEDTLS_THREADING_PTHREAD`.

### P-08 Firmware ESP32

Crear `PortGate/platform/esp32` (UART, lwIP, NVS y tarea FreeRTOS). ESP-IDF trae su propia versión de Mbed TLS: verificar compatibilidad con la API 4.x usada en `PortGate/tls` o adaptar, y registrar la versión en DEPENDENCIAS. Hay que dimensionar la memoria (buffers, sesiones TLS simultáneas) y anunciar `traceCapture = 0` o RAM.

### P-09 Agente: lo reservado en Link v1

Resuelto el 2026-10-26 (`spec/admin-v1.md` § Sealed capture y § Semaphore extensions):

- **Rol cliente TLS:** el agente conecta hacia el host, para sitios sin puertos entrantes (P-16).
- **Captura sellada:** opcional por puerto (`capture`), AES-256-GCM con clave de 32 bytes entregada por el anfitrión sólo por TLS (`SetCaptureKey`) y guardada en el estado sellado de máquina. AAD con agente, puerto, secuencia y clave; el agente nunca descifra ni guarda texto plano. `ReadCapture` entrega registros sellados, `AckCapture` borra tras la custodia; cuota, retención y huecos explícitos (`no-key`, `seal-failed`, `write-failed`, `quota`, `retention`, `backlog`). `Capabilities` anuncia `traceCapture = 2` en esos puertos. Cliente: `PortGateAdminClient.ReadCaptureAsync` descifra con `AesGcm`.
- **Observadores:** `Observe` con el derecho 4 del controlador; reciben `ObservedData` (copia de lectura y escritura con dirección), sin poder escribir ni tomar el puerto. Inicio y fin se anuncian como SecurityEvent y quedan en el registro. Cliente: `PortGateChannel.ObserveAsync`/`ObservationsAsync`.
- **Decisión humana del 2026-10-27:** un observador sólo lee el flujo bidireccional cuando el acceso del puerto es compartido (`shared`); en un puerto exclusivo (predeterminado) sólo observa el semáforo y el estado (`PortState`). Ningún observador debe afectar el rendimiento de comunicación del controlador que tomó el puerto. Implementado: cola y hilo de escritura por conexión; las copias para un observador con más de 256 KiB de atraso se descartan (salto en `seq`) y una conexión con más de 4 MiB de atraso en otras unidades se cierra. Prueba: ráfaga de 8 MiB con un observador que no lee, recibida completa por el dueño.
- Pendiente: custodia en EDC/SQLCipher (P-13) y rotación de clave con registros pendientes de la clave anterior. Resuelto el 2026-10-27 (pedido humano: implementar si aumenta seguridad y rendimiento): el hilo del puerto sólo encola la captura (máximo 4 MiB en claro en memoria); un hilo propio sella, escribe y borra la copia en claro tras sellar. Antes, cifrado, disco y `ReadCapture`/`AckCapture` del administrador (reescritura del archivo con el mismo mutex) demoraban al dueño. Lo que no entra en la cola queda como hueco `backlog`, nunca silencioso ni en claro. El borrado de memoria es de mejor esfuerzo: no alcanza copias internas de la pila de red ni del registro CBOR original.

### P-10 Canal LocalSerial .NET

Resuelto el 2026-10-26: `Atsm.Transport.LocalSerial` abre el COM con los `SerialSettings` del perfil (`TransportKnowledge`) y entrega el `Stream`; el mapeo se prueba sin hardware. Falta la prueba con un adaptador real y, en Linux/macOS, verificar las bibliotecas nativas del paquete. Ver [TRANSPORTE](TRANSPORTE.md).

### P-11 Confirmación del CELL-DYN Ruby

Sin manual, el plugin se apoya en trazas. Quedan por confirmar:
- **Encuadre del host:** si el equipo acepta el mensaje completo en un frame. Es la hipótesis actual, porque los 1.316 mensajes enviados un registro por frame fueron rechazados con `Parsing Error^RecordID`.
- **Respuesta negativa:** el formato que espera el equipo.
- **Valores:** timers, página de códigos, semántica de `W` en R.9 y forma de pedir órdenes (código de panel o de parámetro).

Con el manual o un equipo de prueba, actualizar el perfil y quitar las marcas de hipótesis.

### P-16 PortGate: servicio y administración remota — primer incremento entregado

Implementado según [ADMINISTRACION-REMOTA](ADMINISTRACION-REMOTA.md) y [spec/admin-v1.md](../spec/admin-v1.md): estado protegido (DPAPI de máquina; 0600 en POSIX) con sólo la configuración vigente, arranque siempre con la última configuración, registro persistente de eventos con rotación, endpoint de administración TLS/plano en el puerto 28240 con certificado autogenerado y fijación por huella, PSK con (EC)DHE, `SetTls` rechazado por canal plano, `ListDevices` con identidad USB (SetupAPI) y eventos de aparición/retiro, semáforo con `maxHoldMs`, solicitud de cesión cooperativa, kill switch, impugnación, cierre por seguridad y reapertura administrativa. Cliente .NET `PortGateAdminClient`, configuración tipada `PortGateConfiguration` y extensiones de `SerialTunnelChannel`. Servicio Windows (`LocalService`, ACL protegida, reinicio ante fallos) y unidad systemd. Se retiró la configuración JSON (`--config`/`--check`).

Pendiente:

- **Resuelto (2026-10-26):** modo iniciado por el agente (conexiones salientes con rol asignado por el servidor, TLS con fijación mutua o PSK, reintento exponencial) y cambios de dispositivo en caliente.
- **Verificar `service install` elevado** en Windows (instalación, ACL, arranque automático y recuperación). En esta sesión sólo se comprobaron los caminos sin elevación.
- **POSIX**: `devices_posix.cpp`, `protect_posix.cpp` y la unidad systemd están escritos sin compilar (igual que P-07).
- **Windows XP (P-06)**: `std::filesystem` del STL v141 y SetupAPI por sondeo sin probar.
- **Integración EDC/LIMS Equipos**: inventario de agentes, versiones/historial de configuración y reconocimiento del instrumento por identidad USB y tráfico (Issue propio en EDC).
- Prueba con adaptadores USB-serie reales del cambio de COM y de la identidad informada.

### P-12 Robustez del agente

- Resuelto el 2026-10-27: los envíos pasan por una cola con hilo de escritura por conexión; ni `dispatch()` ni el hilo del puerto esperan a un par lento. Al cerrar por rechazo, el agente y la administración leen lo pendiente un momento para que el reset TCP no descarte la respuesta (causa de la intermitencia de `Administration_is_restricted_to_the_certificate_that_configured_the_agent`, 2 de 8 corridas antes del cambio y 8 de 8 correctas después).
- Falta un límite de conexiones simultáneas y de handshakes TLS en curso (un hilo por conexión).
- `pendingTxBytes` en `Granted` siempre es 0; `uncertainTx` sólo detecta escrituras en curso.
- Faltan pruebas de estrés: muchos controladores, desconexiones durante escrituras y reinicio del agente con controladores conectados (T05–T09 de IMPLEMENTACION).

### P-13 Integración con EDC

Seguir [INTEGRACION-EDC](INTEGRACION-EDC.md) y las reglas del repositorio EDC (`Documentacion/agente.md` de EDC: Issue antes de editar, changelog y aprobación de dependencias):
1. Issue de decisión en EDC para adoptar ATSM como paquetes.
2. Especificación mínima del módulo LIMS.
3. `LIMS.Infrastructure.Atsm` implementando `ILabWorkflowHost`.
4. Prueba punta a punta con `Replay` y el corpus de P-03.

El módulo LIMS de EDC todavía no existe.

### P-14 Empaquetado, API pública y CI

Paquetes NuGet versionados con SemVer, seguimiento de API pública (`PublicAPI.Shipped.txt`), build de release con `AtsmRequireStrongName=true` y firma Authenticode. CI cuando se decida dónde corre: el agente necesita MSVC y, más adelante, GCC y ESP-IDF.

## Hipótesis y decisiones abiertas

- **CELL-DYN Ruby:** encuadre del host y respuesta negativa (P-11).
- **DeltaLab** por archivo: resuelto el 2026-10-24, conector EDC/ETL, fuera de los plugins instrumentales ATSM (P-01).
- **ARCHITECT:** 309 exportaciones de control llegan sin registro P y generan `ATSM-L02-UNEXPECTED-RECORD-ORDER`. Decidir si el perfil lo admite como variante documentada o si sigue como aviso.
- **U-WAM:** el modo E1381-95 (sin timeouts según R315168 5.2.5) no tiene perfil propio.

## Deuda conocida

- El commit `a932694` no compila solo (la solución ya referenciaba el plugin, que llegó en el commit siguiente). No afecta a `main`.
- Los ejemplos `Documentacion/ejemplos/agente.json` e `instrumento.json` son de la etapa de propuesta y no siguen el esquema implementado. PortGate ya no usa archivos de configuración: se aprovisiona por `spec/admin-v1.md`.
- `atsm-trace` registra los plugins de forma estática por decisión humana; no es deuda de carga dinámica.

### Usabilidad y Lang (2026-10-25)

Entregados agrupación sin pérdida, interpretación por defecto, tabla automática/redimensionable, contraste de controles, catálogo de 8 equipos con 12 configuraciones técnicas, detección de formato y del lado del registrador, y filtro de idiomas WinUI. Revisión local UF-5000: 425 mensajes/5.006 observaciones; UC-3500 interno: 2.006 entradas conservadas sin falsa interpretación ASTM. [USABILIDAD-DIAGNOSTICS](USABILIDAD-DIAGNOSTICS.md) describe el modo técnico y el adaptador Lang opcional. No confundir detector de formato de log con soporte de un protocolo propietario. Extender vocabulario y firmas por plugin con evidencia/corpus; inspección manual de arrastre pendiente tras Esc del usuario, sin ampliar homologación física.

P-12: investigar la intermitencia de Certificate_must_identify_the_declared_controller. En esta sesión reapareció una vez; pasó aislada en 749 ms y en las dos corridas completas finales (181/181, 0 omitidas). No se modificaron el test, el agente ni los timeouts.


### UC-3500 y selección de texto (2026-10-25)

Resuelta la brecha del protocolo directo que la entrega de usabilidad conservaba como incompatible: plugin sysmex.uc3500.serial, puerto neutral IInstrumentTracePlugin, 9 equipos/13 perfiles. Mensajes RQ02/CM00/ER y resultados/reflectancias/serie C, comprobación del checksum separado del monitor, correlación y diagnósticos en español. Captura directa sin envío; simulación activa rechazada antes de conectar. MAUI habilita selección nativa en etiquetas y celdas; detalle conserva copia. Ver [UC3500-PROTOCOLO](UC3500-PROTOCOLO.md).

Dos corridas finales 201/201, 0 omitidas; nativo 542/542 repetido, GUI self-test seleccionable y CLI NativeAOT correctos; Lang opcional 1 correcta. No afirmar host activo UC-3500, homologación física, estadística QC calculada ni interpretación de siglo/orden de fechas abreviadas. Revisar tabla original para significados específicos de errores cuya extracción pierde columnas; 8010 sólo tiene familia de tira. La prueba HL7 Fragmented_MLLP_and_coalesced_blocks_are_acknowledged_individually falló una vez, pasó aislada (122 ms) y en las corridas finales; causa sin confirmar, sin cambiar timeouts.


### U-WAM único y calidad descriptiva (2026-10-25)

Atsm.Plugin.Sysmex.Urinalysis reúne ASTM U-WAM y serial UC; retirados proyectos/DLL redundantes. Una entrada U-WAM, ocho familias/trece perfiles. Interpretación automática por contenido, opción manual y conocimiento contextual por perfil/registro/campo/código. Captura automática sin enviar ACK supuestos; simulación exige configuración expresa.

Calidad prioriza mediana y DE, con material/lote literal, mínimo/máximo/n, señales y originales en subfilas. Media/último/método y estadísticas informadas en detalle. Los cálculos descriptivos pertenecen al diagnosticador, sin reglas de aceptación QC. Diez pruebas nuevas: 211 en total; ver [CALIDAD-DIAGNOSTICA](CALIDAD-DIAGNOSTICA.md).


### Resultados cualitativos y conteo de comunicaciones (2026-10-25)

Calidad muestra normal y otros textos/códigos con frecuencias. La columna Mediciones cuenta todos los registros comunicados, incluso cualitativos, duplicados y no realizados. NumericCount queda separado para mediana/DE y se explica en detalle. No mostrar Sin valores numéricos como defecto de un dato cualitativo. Tres casos nuevos de pruebas (214 total), self-test GUI con normal (4) y cuatro mediciones, y NativeAOT con cualitativos. Conservar identificadores literales; los asteriscos del control UC ya vienen de la captura.


### Semicuantitativos y resumen compacto (2026-10-25)

Valor 2+ clasificado por el conocimiento del plugin como Semicuantitativo (cruces), con orden de categorías y sin aritmética de concentraciones. >=50 / <=50 conservan Comparator y se presentan en el extremo finito correspondiente; nunca se usan como valores exactos de mediana/DE. Nuevo puerto neutral IInstrumentValueKnowledge y resumen QualityReportedRange. No inferir orden de categorías desconocidas ni extremos finitos cuando no están acotados.

Calidad agrega Sin medir con el conteo comunicado y retira esa descripción de Mediana/resultado y Señales. La celda central muestra mediana/categoría central/moda, con distribución completa en detalle. Catorce casos nuevos, 228 pruebas totales; GUI self-test de cruces, ambos comparadores y columna separada. Ver CALIDAD-DIAGNOSTICA.


### Negativo en cero cruces (2026-10-25)

Validado el guion en contexto UC: estado 0 analizado con resultado negativo no es Sin medir; estado 1 no realizado lleva campos vacíos. Mostrar 0 cruces sólo en escalas documentadas, conservando RawText y Numeric=null. No inventar concentración cero, ni convertir normal, campos vacíos, guiones de concentración o parámetros desconocidos. Tres pruebas nuevas (231 total), incluida la decodificación real del estado. Ver CALIDAD-DIAGNOSTICA.
