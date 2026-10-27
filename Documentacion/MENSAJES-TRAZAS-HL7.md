# Mensajes completos, custodia de trazas y primer incremento HL7

Decisión humana del 2026-10-24, registrada antes de editar en [EDC #205](https://github.com/kalyx-ar/EDC/issues/205#issuecomment-6389732963). Complementa las propuestas iniciales; sustituye su asignación de FileDrop al motor, sin borrar el historial de decisiones. ATSM sigue siendo neutral y no referencia EDC.

## P-01 resuelto: la fuente pertenece al anfitrión

ATSM no vigila carpetas ni mueve, archiva, rechaza o elimina originales. La aplicación orquestante adquiere el recurso y entrega bytes o un `Stream` abierto a `AstmMessageProcessor`. El procesador comparte interpretación y entrega al host con `AstmInstrumentSession`; LIS01 sólo se utiliza cuando está presente en el transporte real.

`ProcessAsync(payload/stream, receptionId, cancellationToken)` exige una recepción no vacía y estable que el anfitrión asigna y conserva antes de reintentar. La sobrecarga de Stream no cierra el recurso y limita el contenido en memoria. `ProcessRecordedAsync` reutiliza rangos ya capturados por una sesión. Mensajes sin H/L o con errores de delimitadores no se ingieren. El resultado devuelve los estados del host, diagnósticos y respuestas construidas; el llamador conserva la decisión de disposición del original.

`WholeMessageInstrumentSession` consume un canal del anfitrión que declara `MessageBoundaries`: acumula fragmentos hasta `ReadOutcome.IsMessageBoundary`, limita el tamaño y rechaza EOF incompleto. No emite ENQ/ACK ni inventa una conversación LIS01. Su confirmación de escritura sólo expresa progreso técnico. Para importación idempotente de archivos se usa el procesador directo con `receptionId` persistido; una sesión de canal no inventa la identidad durable de una fuente externa.

`ReceptionIdentity` deriva UUIDv8 mediante SHA-256 del ID de recepción, perfil y ordinal. Reintentar la misma recepción conserva los IDs de lote y observaciones; una recepción distinta del mismo contenido conserva hechos distintos. La interpretación de dialecto aislada sigue generando UUIDv7. No se deduplica universalmente por hash, valor clínico o conexión; el host resuelve retransmisión, conflictos y correcciones con la identidad original del fabricante y su propio commit idempotente. El adaptador conserva versión de motor/plugin/perfil y hash original: reinterpretar no publica otra medición automáticamente.

En EDC, Scheduler identifica la versión y dispara el caso del conector LIMS que utiliza ETL. Ese conector adquiere custodia, verifica el hash del contenido realmente leído y decide archivo/revisión/rechazo. La autopurga de Scheduler sólo se confirma tras custodia durable o aceptación del propietario. El historial del watcher no conserva los bytes. El orden de creación del filesystem no garantiza el orden de negocio; cuando sea necesario, se declara secuencia y procesamiento por origen. Los intercambios con laboratorios clientes, incluido el formato propio DeltaLab, quedan en el conector EDC/ETL; no se modelan como instrumentos ficticios.

## Trazas antes de cualquier comunicación

La aplicación orquestante administra almacenamiento, cifrado, claves, retención, consultas y exportación. ATSM recibe un `ITraceSink` ya hospedado y no crea un fallback a disco. El nuevo método obligatorio `EnsureReady()` declara disponibilidad y debe fallar si el proveedor no está operativo. Los adaptadores anteriores deben implementar este método; es un cambio de contrato fuente que debe contemplarse al empaquetar. `Write` devuelve una secuencia sólo si el proveedor aceptó la entrada y no silencia fallos. El proveedor declara si esa aceptación ya es durable; una cola en RAM no equivale a SQLCipher confirmado.

`InstrumentCommunicationHost.RunAsync` comprueba la traza antes de invocar la fábrica del canal (incluido Connect/TLS de SerialTunnel/TCP) y administra su cierre. Las sesiones vuelven a comprobarla antes de adquirir el instrumento. La aplicación que crea un canal directamente debe efectuar la misma comprobación antes de abrirlo; la biblioteca no puede demostrar por reflexión cómo persistió un proveedor externo.

Los fallos de captura se propagan. En LIS01, los fallos de los bucles de lectura/negocio ya no se descartan al cerrar; EOF drena mensajes completos pendientes antes de cancelar el trabajo. El anfitrión debe detener la sesión ante un fallo de captura y conservar el estado incierto, nunca sustituir el proveedor por un logger plano.

Para **EDC**, la decisión adoptada exige SQLCipher estándar por propietario/tenant para las trazas de comunicación, abierto por Application antes del canal, con consulta y diagnóstico desde UI sujetos a permisos y lifecycle de EDC. No se permite un volcado plano alternativo de datos sensibles. La UI, el adaptador SQLCipher, migraciones y composición del módulo LIMS siguen pendientes de P-13: este incremento no crea un módulo EDC ni incorpora una dependencia SQL a ATSM, ni modifica derivación de claves.

`MemoryTraceSink` sirve para simulaciones/diagnóstico en memoria; no acredita durabilidad. JSON Lines es un **formato de serialización/intercambio explícito**, no el almacén operativo EDC. Los serializers reciben streams del llamador y no eligen rutas. La CLI es una aplicación diagnóstica independiente: una exportación sensible requiere la política del anfitrión; para compartir se anonimiza y revisa. Base64, hashes encadenados y Strong Name no cifran datos clínicos.

## P-02: alcance implementado y límites

- `Atsm.Protocol.Hl7v2`: MSH con numeración especial, separadores declarados, segmentos/campos/repeticiones/componentes/subcomponentes, nulos explícitos y escapes estándar/hex UTF-8; conserva extensiones desconocidas. Un bloque contiene un solo mensaje, no un lote de MSH concatenados. Código y originales permanecen separados.
- `Hl7Acknowledgements`: correlación por MSA-2 y códigos/fases de ACK original y mejorado; políticas AL/NE/SU/ER. Las primitivas de modo mejorado no acreditan una sesión con custodia commit implementada.
- `Hl7InstrumentSession` / `Hl7MessageProcessor`: MLLP fragmentado o agrupado, mensajes completos del anfitrión, ACK **original**, ingestión de lote antes de AA, AR por rechazo, AE por aplazamiento/fallo, consulta con presupuesto, respuesta ORL correlacionada y resultado incierto ante timeout/corte, sin reenvío automático de órdenes. El modo mejorado se rechaza explícitamente antes de ingerir; no se envía CA sin contrato de custodia. La lectura termina con error si queda un bloque parcial.
- `ArchitectHl7Plugin` / `ArchitectHl7Dialect`, dentro del plugin Abbott ARCHITECT: OUL^R22, resultados F/P/I y excepción CE, comparadores/numéricos, unidades originales, flags, estado, fechas en zona configurada, notas y lotes; QBP^ZOS/RSP^ZOS positivos/negativos; OML^O33 con una muestra/acción, ORL^O34 como acuse del mensaje. El manual permite rechazos posteriores por ensayo: ORL/AA **no confirma** que cada ensayo fue aceptado ni ejecutado. Presupuestos y zona son configuración obligatoria del despliegue, sin timers universales inventados.
- `LawQueryMessages`: primer corte LAB-27 para consulta WOS por contenedor, identificación LAB-27^IHE y RSP^K11 con QPD reflejado. La respuesta confirma la consulta y **no lleva órdenes incrustadas**: LAB-28 es otra transacción.

No se presenta como conformidad IHE LAW completa. Faltan actor/AWOS y lifecycle LAB-28/LAB-29, opciones y validación exhaustiva de cardinalidades/segmentos de LAW, reconocimiento por ensayo y un kit de conformidad de equipo. ARCHITECT 206797-101 es un perfil particular de 2012: usa ZOS y declara no soportar el identificador WOS de IHE. No se fuerza a compartir el perfil Q11/K11.

También quedan pendientes OUL^R24/calibración de curvas y estados/inventario/notificaciones del fabricante; no se aceptan como resultados de paciente. La pila, métricas y anonimización de trazas históricas actuales son ASTM; no se afirma que la CLI anonimice HL7. Las nuevas pruebas HL7 son sintéticas y no se exportó corpus real.

Fuentes primarias:

- Manual local **ARCHITECT System HL7 Interface Manual**, Abbott 206797-101, enero de 2012: §1-2, §1-5, §1-8/1-10, §2-3/2-6, §2-8/2-11, §2-13/2-16 y §3-48/3-56. Archivo ignorado `manuales/ASTM Alinity 206797-101.pdf`.
- [IHE LAW rev. 1.4, Trial Implementation, 2015-03-16](https://www.ihe.net/uploadedFiles/Documents/Laboratory/IHE_LAB_Suppl_LAW.pdf), §3.Q.5: LAB-27 QBP^Q11/RSP^K11 y query por contenedor. Es la revisión usada para este corte, no una afirmación sobre la edición más reciente ni una certificación.
- [HL7 v2.5.1, capítulo 2](https://hl7.eu/HL7v2x/v251/std251/ch02.html), §2.9 y MSH/MSA: acuses, correlación y fases.

## Evaluación del buffer en SerialTunnel

**Recomendación: no agregar un buffer persistente propio al cliente SerialTunnel en este incremento.**

| Interrupción | Qué conserva un buffer de disco en el host | Alternativa / límite |
|---|---|---|
| Caída del proceso después de recibir bytes | Sólo bytes ya recibidos; duplica el almacén de trazas si éste confirmó custodia | La Application conserva originales/rangos en SQLCipher y una outbox/inbox durable; no equiparar traza con commit de negocio |
| Corte de red host-agente | No puede guardar bytes que no llegaron | Sólo el agente o el instrumento pueden retener esa evidencia |
| Corte a mitad de frame | Puede conservar un fragmento anterior | No permite concatenarlo con una nueva conexión/generación como si hubiera continuidad |
| Orden enviada sin respuesta | Puede conservar intención y bytes TX | No prueba recepción/ejecución y no autoriza reenviar la orden |
| Reinicio del agente | No conserva el puerto remoto ni su memoria | Se requiere captura durable **en el agente**, por epoch/puerto/dirección y con rangos perdidos explícitos |

Sin captura habilitada, el agente anuncia `traceCapture = 0` y cuenta/descarta RX sin propietario; el cliente encola datos recibidos en RAM, sin spool durable. Con captura habilitada por el servidor (P-09, 2026-10-26), ver el párrafo siguiente. Un anillo RAM en el agente podría ayudar en cortes breves y diagnóstico, pero no sobrevive a reinicios.

Una futura captura durable del agente puede aportar valor si se demuestra pérdida operativa que el instrumento no pueda recuperar. Necesita almacenamiento cifrado y autenticado, claves/provisión/rotación a cargo del anfitrión, soporte real por plataforma, cuotas y retención, borrado tras confirmación de custodia, identidad epoch/puerto/dirección/generación/offset, detección de huecos y reanudación con deduplicación. Un hash encadenado sin cifrado no satisface la política EDC. No se presupone que la SNK o el certificado TLS sean claves de cifrado de almacenamiento.

El agente es transparente: no responde ACK ASTM en lugar del host. Aunque guardara bytes, el instrumento puede vencer su timeout y retransmitir o detenerse durante el corte; un buffer no mantiene por sí solo la conversación ni ofrece exactly-once. Después de reconectar, la evidencia recuperada se utiliza primero para diagnóstico/conciliación; una continuación operativa requiere un plan explícito compatible con el protocolo y el instrumento. 

Implementación P-09 (2026-10-26, `spec/admin-v1.md` § Sealed capture): captura opcional por puerto, AES-256-GCM con clave de 32 bytes que provee el anfitrión sólo por TLS (no la SNK ni el certificado), AAD con agente/puerto/secuencia/clave, identidad epoch/puerto/dirección/generación/offset, cuota y retención, huecos explícitos y borrado sólo tras `AckCapture` de custodia. El agente nunca descifra y no hay spool plano. La reanudación operativa automática sigue sin implementarse: la evidencia recuperada sirve para diagnóstico y conciliación, deduplicada por (agente, puerto, secuencia). Falta la custodia en EDC/SQLCipher (P-13) y la rotación de clave con registros pendientes.
