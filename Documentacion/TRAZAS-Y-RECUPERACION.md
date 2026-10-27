# Herramientas de trazas y recuperación

## Principio de diseño

La traza reúne evidencia de lo que ocurrió. Un proceso puede contener pasos reversibles, irreversibles, repetibles o que no conviene revertir. El plugin, el proceso de negocio y las capacidades del instrumento determinan la recuperación de cada paso.

El mecanismo transversal permite capturar, correlacionar, consultar, reproducir para diagnóstico y ejecutar planes explícitos de recuperación. No impone una política universal de cancelar y repetir.

## Tres niveles correlacionados

| Nivel | Contenido | Productor |
|---|---|---|
| Técnico | Bytes originales RX/TX, conexión, arranque, generación, offsets, señales, errores y discontinuidades | Agente y adaptador del ETL |
| Protocolo | Tramas, checksum, registros, ACK/NAK, cambios de conversación, timeouts y sesión interpretada | Motor ASTM y plugin |
| Operación | Intención, identificadores del fabricante, muestra/orden, pasos, efectos y evidencia de recuperación | Plugin y Application |

Los niveles derivados referencian rangos del nivel original. Una corrección de interpretación produce una nueva versión derivada, manteniendo la captura original y el historial de decisiones.

## Registro técnico mínimo

```text
traceId, eventId, captureSource, instrumentId, agentId, agentEpoch, portId
connectionId, controllerId?, generation?, direction?
sequence?, byteOffset?, byteCount?, payloadReference?
timestampUtc, monotonicTicks, monotonicFrequency
eventType, errorCode?, continuity, correlationIds
effectiveConfigurationVersion, capabilitiesSnapshot
```

`captureSource` identifica agente o ETL. Los relojes monotónicos sólo se comparan dentro de su origen/arranque y tienen unidad/frecuencia declaradas; UTC ayuda a correlacionar máquinas, sin asumir sincronización perfecta.

El offset indica la posición de captura, no un checkpoint seguro de negocio. Una escritura contiene su `writeId` y progreso observado. Los eventos diferencian aceptación por el agente, escritura al driver y pérdida de conocimiento después de un fallo. La recepción o ejecución por el instrumento se acredita mediante evidencia del protocolo/proceso.

Toda exportación declara rangos disponibles, rangos perdidos, truncamiento, origen y nivel de persistencia. Un hueco se representa como hueco; no se reconstruye con bytes inventados.

La persistencia de traza debe declarar su punto de durabilidad y límites. En ESP puede existir sólo captura RAM o un almacenamiento externo según el perfil; el ETL no debe asumir que puede recuperar una traza local después de un reinicio si esa capacidad no está anunciada.

## Registro semántico y checkpoints

Un checkpoint propuesto contiene:

```text
checkpointId, instrumentId, protocolSessionId, operationId?
pluginId, pluginVersion, engineVersion, configurationVersion
messageReferences, rawTraceRanges, manufacturerIdentifiers
protocolState, operationStep, knownEffects, supportingEvidence
recoveryCapabilities, capturedAt, assessmentVersion
```

El checkpoint expresa el conocimiento del sistema y sus límites. Se diferencia estado observado del instrumento, estado persistido en EDC y estado inferido. Las pruebas o reglas que permiten inferir un estado se registran con el plugin.

No se adopta un único checkpoint para toda la operación si sus pasos requieren decisiones diferentes. El inicio de un análisis, la consulta de estado y la recepción de un resultado pueden tener capacidades de recuperación distintas.

## Herramientas comunes

| Herramienta | Función |
|---|---|
| Visor de traza | Línea temporal, dirección, bytes/hex, tramas, registros, operaciones y cambios de controlador |
| Comparador | Diferencias entre intercambios de referencia, versiones de plugin o perfiles |
| Reproductor diagnóstico | Alimentar el motor/plugin desde una captura, con puerto y efectos externos sustituidos |
| Evaluador de recuperación | Presentar evidencia, pasos candidatos, precondiciones y consecuencias conocidas |
| Ejecutor de recuperación | Ejecutar el plan vigente autorizado por el proceso y registrar su resultado |
| Simulador de instrumento/transporte | Fragmentación, duplicados, pérdida de conexión, timeouts y respuestas controladas |
| Exportador | Paquete de rangos y versiones suficiente para analizar un incidente en otro entorno |

El reproductor diagnóstico no abre un canal físico ni invoca casos de uso con efectos. Si una intervención requiere emitir bytes reales o actuar sobre una operación, utiliza el ejecutor de recuperación, con el permiso actual y el contexto del proceso.

## Plan de recuperación

`RecoveryAssessment` devuelve pasos con tipo, alcance, evidencia, precondiciones, efectos esperados, capacidades necesarias y resultado de evaluación. Acciones posibles:

- Esperar la finalización de una operación conocida.
- Consultar el estado o recuperar resultados por identificador del fabricante.
- Reintentar un intercambio completo o continuar desde un checkpoint válido.
- Reinicializar la conversación o solicitar una cancelación soportada por el instrumento.
- Conciliar un efecto ya observado con el estado del EDC.
- Descartar estado transitorio o registrar una decisión manual.

La lista es un vocabulario de capacidades, no una secuencia que todos deban ejecutar. Antes de ejecutar se revalidan propiedad, versión/configuración, evidencia y vigencia del plan; la situación puede haber cambiado desde el análisis.

## Escenarios realistas a caracterizar

| Escenario | Evidencia a reunir | Decisión que depende del plugin/proceso |
|---|---|---|
| Fragmentación normal en múltiples lecturas | Bytes completos y ordenados, sin discontinuidad | El motor reconstruye la trama; no se trata como fallo |
| Corte en mitad de una trama | Fragmento, timeout, generación y estado de conversación | Reintentar intercambio, reiniciar conversación o descartar sólo el estado incompleto |
| Orden enviada sin respuesta concluyente | Progreso TX, identificador de orden/ejecución y respuestas capturadas | Consultar estado, esperar o determinar si es válido repetir |
| Análisis iniciado con identificador único | Evidencia del inicio y semántica de unicidad del equipo | Recuperar estado/resultados de esa ejecución; un rechazo por ID existente puede aportar evidencia |
| Resultado recibido y efecto ya aplicado | Identidad de mensaje/ejecución, referencias y commit de negocio | Reconocer retransmisión o aceptar una medición nueva según su identidad y versión |
| Failover con conversación abierta | Transferencia de propiedad, bytes pendientes, trazas y checkpoints accesibles | Continuar, consultar o resincronizar la conversación según capacidades |
| Vuelve el controlador anterior | Generación obsoleta y trabajo local pendiente | Rechazar su escritura; conciliar las operaciones en el plano de negocio |
| Reinicio del agente | Nuevo `agentEpoch`, rangos durables o perdidos y estado del instrumento | Reabrir el canal y evaluar la recuperación con la evidencia restante |
| Cliente local resolvió mientras el servidor estaba fuera | Identidades de operación, mensajes y efectos locales | Conciliar al reconectar según las transacciones del EDC |
| Instrumento sin consulta ni cancelación verificables | Último intercambio y señales disponibles, incertidumbre identificada | Procedimiento específico y, cuando corresponda, decisión del operador |
| Disco o memoria de captura agotados | Capacidad, último rango íntegro y evento de saturación | Continuar o interrumpir según los requisitos del perfil, dejando explícita la pérdida de trazabilidad |

Una respuesta de error no se interpreta globalmente como fracaso sin efectos: su significado se define en el dialecto y el proceso. Del mismo modo, una respuesta técnica favorable no acredita por sí sola la finalización de todos los pasos de negocio.

## Failover y trazas disponibles

El árbitro entrega al nuevo controlador rangos y estado técnico conocido. El almacén de trazas/checkpoints del ETL entrega la interpretación disponible. El receptor verifica si tiene suficiente evidencia para continuar; puede acceder a una cola durable o almacén común según el despliegue.

Si el cliente local debe resolver sin servidor, necesita perfiles/plugins y datos de trabajo locales, además de persistencia y conciliación. La sincronización del registro de negocio no se sustituye por copiar una traza serial.

Los consumidores en espera pueden recibir diagnósticos si está habilitado, pero sólo el propietario puede emitir al instrumento. Ningún lector de trazas activa por sí mismo una reproducción operativa.

## Retención y acceso

Las capturas pueden contener identificadores y datos clínicos. La configuración define acceso por dominio/rol, retención, límites, exportación y protección del almacenamiento. Los ejemplos del paquete son sintéticos y las claves/certificados se referencian por ubicación, sin incorporar su contenido.

La traza conserva qué versiones interpretaron o modificaron un estado, quién ejecutó una recuperación y qué evidencia sustentó la decisión. Los valores de retención y los permisos finales se integran con las políticas existentes del EDC.

## Reconstrucción de trazas reales y teóricas

ASTM genérico no conoce ningún formato de log. Trabaja con **contenido puro**: bytes del cable, texto de mensajes (registros sin framing) o eventos, siempre con la dirección vista desde el host (`TraceSegment`). Un mismo método construye dos trazas:

| Traza | Cómo se construye | Para qué |
|---|---|---|
| Real | Los segmentos tal como se capturaron (`TraceReconstruction.Real`) | Evidencia: qué pasó |
| Teórica | Los mismos mensajes reproducidos por dos enlaces LIS01-A2 con reloj virtual, timers del perfil, velocidad de línea, tiempo de reacción y las pausas reales entre mensajes (`TraceReconstruction.Theoretical`) | Referencia: cómo debería verse según la norma y el perfil |

Ambas pasan por la misma **pila de eventos** (`TraceStackBuilder`): técnica (bytes), enlace (observador pasivo LIS01: ENQ, frames, ACK/NAK, retransmisiones, contención, fin de transmisión), mensaje (LIS02 y diagnósticos) y operación (hechos canónicos, si se indica el perfil del plugin). `TraceMetrics` y `TraceComparison` dan la analítica: mensajes iguales o distintos, eventos de enlace que la traza real tuvo de más, duración de mensajes, latencia de respuesta del host y consultas sin respuesta.

Un log que sólo registra mensajes, sin frames, también produce una traza teórica completa del cable.

**Los plugins** sólo agregan adaptadores de log de su serie de equipos. El SDK ofrece `RegexLogAdapter`: el plugin declara una expresión regular con grupos `time`, `dir`, `payload` (y opcionalmente `length` o `label`), el formato de hora, la codificación, los tokens de control y qué significa cada dirección para quien escribió el log. No se programan parsers.

**Evidencia del registrador.** El observador no descarta un frame con checksum incorrecto si el receptor lo confirmó con ACK. En ese caso el original era válido y lo alteró quien registró la traza: se reconstruye y se marca `lis01.altered-by-recorder`. El anonimizador conserva esa diferencia de checksum.

```bash
astm-trace extract sysmex.port-monitor "AMS.txt" ams.astmtrace --recorded-by instrument
astm-trace theoretical ams.astmtrace ams.theory.astmtrace --profile sysmex.uwam.lis2a2.without-chemistry --baud 0
astm-trace compare ams.astmtrace ams.theory.astmtrace --profile sysmex.uwam.lis2a2.without-chemistry
```

## Decisión humana del 2026-10-24: custodia de la aplicación

La aplicación orquestante administra todo volcado y almacenamiento de trazas. `ITraceSink.EnsureReady` debe confirmar disponibilidad antes de conectar (`InstrumentCommunicationHost` lo hace antes de la fábrica); `Write` propaga fallos y el proveedor declara su punto de durabilidad. EDC exige SQLCipher estándar hospedado desde Application, diagnóstico por UI autorizada y ningún fallback plano de datos sensibles. Esta decisión no instala persistencia EDC dentro de ASTM. JSON Lines es serialización/intercambio explícito; las herramientas históricas de anonimización son ASTM y no acreditan anonimización HL7.

No se añade un spool de disco en SerialTunnel: duplica custodia de bytes ya recibidos y no recupera lo perdido antes del host. La futura captura del agente requiere custodia cifrada, límites/rangos/identidades y recuperación por proceso; un hash encadenado no cifra. El [análisis completo y los límites](MENSAJES-TRAZAS-HL7.md#evaluación-del-buffer-en-serialtunnel) distinguen caídas de proceso, red y agente.
