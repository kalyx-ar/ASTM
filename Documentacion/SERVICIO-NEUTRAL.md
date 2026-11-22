# Servicio neutral de comunicaciones

## Alcance autorizado, 2026-11-03

El usuario autorizó implementar localmente un servicio .NET agnóstico dentro de
ASTM, con contrato bidireccional para un orquestador externo. Autorización
trasladada a este chat por el encargo aceptado de integración y ADR-0003 del
anfitrión. No se crean issues/PR ni se hace push (AGENTS ASTM, regla 6).

Se alojan los perfiles compilados, motores, sesiones, transportes y observadores
existentes. El anfitrión conserva negocio, persistencia, custodia protegida,
deduplicación, cola durable y reconciliación. No se agrega un spool ni una base.
No se toca EDC, otros anfitriones, funciones de Identidades, claves, equipos ni datos reales.
Sin dependencias nuevas: BCL y proyectos existentes de esta solución.

Verificaciones requeridas: contrato IPC y callbacks con simuladores, preparación
de custodia antes de transporte, estados de ingestión/entrega separados,
cancelación, límites, exclusión y reconexión; suites .NET/CTest repetidas antes
del commit. Linux, homologación física y ensayo de varios días se registrarán
como pendientes operativos si no se dispone del entorno.

## Implementación

`dotnet/src/Astm.Service` aloja el runtime sin referencias a fabricantes. El
ejecutable `dotnet/tools/Astm.Service.Host` compone los plugins reales y CLSI:
14 perfiles consultables, sin cargar DLL arbitrarias ni importar Legacy. No
depende de Diagnostics, de una aplicación web ni de un LIS concreto. El código
del consumidor Python es sólo una demostración de la frontera entre procesos.

El servicio puede mantenerse bajo un supervisor aunque se reinicien los workers
web. Un adaptador residente del anfitrión mantiene su conexión IPC y atiende los
callbacks. Cada conexión es propietaria de sus sesiones: al perderla se cierran
sus transportes. No hay reanudación implícita de una sesión ni cola durable en
ASTM. Un nuevo consumidor abre nuevas sesiones y generaciones después de
reconciliar sus intenciones persistidas.

## IPC `astm-service/1.0`

Windows usa `\\.\pipe\<endpoint>`, con DACL exclusiva de la cuenta del proceso y
`PIPE_REJECT_REMOTE_CLIENTS`. Linux/POSIX usa un socket Unix de ruta absoluta
dentro de un directorio privado, modo 0700 y socket 0600. El directorio y sus
padres deben pertenecer al despliegue y ser de confianza. No es una API TCP.
Ambos procesos se ejecutan bajo la cuenta local autorizada; otros modelos de
identidad o un despliegue remoto requieren su propia frontera autenticada.

`ownershipDirectory` es un directorio privado dedicado, compartido por **todas**
las instancias de esta cuenta que puedan controlar los mismos instrumentos.
Los locks de archivo se mantienen abiertos, con exclusión entre procesos, por
instrumento y recurso de transporte (COM normalizado, direcciones TCP o puerto
del agente). No se eliminan los archivos de lock al liberar: eliminar un inode
todavía bloqueado rompería la exclusión POSIX. Son metadatos vacíos, no un spool.
PortGate agrega su propio arbitraje y lease; un COM conserva la exclusión del
driver. Los alias de despliegue deben describir el mismo recurso y usar ese
directorio común. No cambiarlo para eludir una tenencia.

Cada trama contiene cuatro bytes de longitud **unsigned big endian**, seguidos
por exactamente esa cantidad de bytes UTF-8 JSON. No hay delimitadores de línea.
El receptor valida el límite antes de reservar el payload. Una trama parcial
tiene plazo; EOF, versión desconocida, framing inválido o error de escritura
terminan la conexión. Una conexión sin tráfico puede conservar una sesión sana;
su antigüedad no determina su estado.

```json
{"v":1,"id":"11111111-1111-4111-8111-111111111111","kind":"request","method":"service.hello","data":{}}
```

`id` es un UUID no vacío. `kind` es `request`, `result`, `error` o `callback`.
Las respuestas conservan el ID de su petición. Un callback usa un ID propio,
`sessionId`, `method`, `deadlineUtc`, `correlation` y `data`; el anfitrión responde
con `result` o `error` y el ID exacto del callback. Las peticiones de sesión
incluyen `sessionId`. `error` contiene un código técnico, sin mensajes originales
ni excepciones que puedan revelar datos. No se usan stdout/stderr para el IPC.

`correlation` contiene generación de sesión, instrumento configurado, perfil,
revisión y `traceId`. La recepción de contenido agrega `receptionId` a los
callbacks de negocio. En un canal vivo se conservan las identidades que ya
proporciona el runtime, el batch y la procedencia del fabricante; no se inventa
una identidad durable universal de la lectura física. El anfitrión registra
estas correlaciones antes de confirmar la recepción.

Los objetos usan camelCase; enums por nombre (`Accepted`, etc.), bytes en Base64,
UUID como texto e instantes ISO 8601. Los **decimal** canónicos se transmiten como
cadenas invariantes con punto; `rawText` permanece íntegro. No convertirlos a
float en Python. `service.hello` anuncia `decimalEncoding` y `binaryEncoding`.
Los schemas del motor permanecen en `contracts`; `wireOutputs` relaciona cada
referencia de salida con su proyección IPC versionada
`astm.service/<id>@<major>.<minor>` v1: `Number` decimal pasa a `String`, mientras
`Integer` conserva su representación JSON. No se cambia silenciosamente el
schema del motor ni se modifica el `data` de un `OperationEnvelope` de entrada.

La fuente exacta de DTOs/serialización es `ServiceContracts.cs`. Se usa metadata
generada para NativeAOT, rechazo de miembros no reconocidos y selección exacta
de contrato/perfil del motor. `v` diferente de 1 no se negocia ni se convierte.
El cliente comprueba `service.hello` antes de trabajar. No hay carga de tipos CLR
ni Python.NET.

### Peticiones

| Método | `data` | Resultado / significado |
|---|---|---|
| `service.hello` | `{}` | Versión, instancia, codificación, límites y métodos |
| `service.status` | `{}` | Sesiones de esta conexión; no expone las de otro consumidor |
| `service.shutdown` | `{}` | Confirma la respuesta, luego inicia la parada del proceso |
| `profiles.list` | `{}` | IDs, revisión, familia, descriptor, evidencia y conocimiento de todos los perfiles |
| `profiles.describe` | `profileId`, `queryBudgetMs`, `responseTimeoutMs`, `timeZone` | Catálogo, restricciones, contratos, proyecciones y transporte conocido. HL7 exige presupuestos explícitos; ASTM conserva los del perfil |
| `sessions.open` | `instrumentId`, `traceId` | Selecciona únicamente un instrumento configurado por el administrador. Prepara custodia antes de abrir transporte |
| `sessions.status` | `{}` | Estado, generación, revisión, progreso confirmado de traza, observador, operaciones y tenencia PortGate |
| `sessions.close` | `{}` | Cancela, cierra y espera los loops; libera propiedad. Los efectos pendientes necesitan reconciliación |
| `sessions.reconcile` | `success: true` | El anfitrión declara que revisó una tenencia PortGate con TX incierto/RX sin propietario y habilita nuevas operaciones |
| `messages.process` | `receptionId`, `content` Base64 | Contenido completo acotado; outcomes, respuestas completas sin framing y diagnósticos íntegros. Sólo modo `messages` |
| `operations.prepare` | `OperationEnvelope` del catálogo | Validación y bytes preparados; no los transmite ni confirma entrega |
| `operations.start` | `OperationEnvelope` | Admite en FIFO acotada y devuelve estado; la admisión no demuestra envío |
| `operations.status` | `operationId` | Estado y resultado de entrega separado |
| `operations.cancel` | `operationId` | Cancelación de cola; si ya está en curso, detiene la sesión y conserva incertidumbre |
| `operations.forget` | `operationId` | Retira un registro terminal después de que el anfitrión lo haya persistido; el ID queda retirado para el consumidor |
| `requests.cancel` | `requestId` | Cancela una petición pendiente de esta conexión; no equivale a cancelar una intención física ya admitida |
| `instruments.list` | `{}` | Revisión, instrumentos y agentes PortGate configurados |
| `gateways.list` | `{}` o `gatewayIds` | Agentes PortGate y sus puertos: dispositivo, parámetros serie anunciados, semáforo si se observa e instrumentos que los usan. No adquiere ni configura |
| `admin.challenge`, `admin.authenticate` | `{}`; `principalId`, `signature` | Autenticación de un principal por firma ECDSA P-256 sobre un nonce de un solo uso. Sólo con `administration` |
| `instruments.put`, `instruments.remove` | `revision` y el instrumento o su ID | Alta, edición y baja validadas, verificadas contra PortGate, auditadas y persistidas de forma atómica. Derecho `instruments` |
| `gateways.put`, `gateways.remove` | `revision` y el agente o su ID | Alta, edición y baja de agentes PortGate. Derecho `gateways` |

Las seis últimas filas son una extensión aditiva del 2026-11-22: `v` y `protocol`
no cambian; el cliente usa un método sólo si `service.hello` lo anuncia en
`methods` (los de administración, sólo si está habilitada). Un método desconocido
responde `method.unsupported`. Diseño, validaciones, auditoría y riesgos:
[ADMINISTRACION-SERVICIO](ADMINISTRACION-SERVICIO.md).

El estado de sesión es `Opening`, `Running`, `Ended`, `Stopped`, `Faulted` o
`Closed`. `LastProgressUtc`/`LastTraceSequence` avanzan cuando la custodia confirma
una entrada; ausencia de avance puede significar instrumento silencioso, y no
es por sí sola una prueba de falla ni de disponibilidad física. El supervisor
consulta además el observador y el estado del enlace/operaciones. EOF nunca
demuestra equipo libre.

Los registros de operación son `Queued`, `Running`, `Finished` o `Cancelled`.
Un ID ya registrado con el mismo envelope devuelve el registro existente; un
contenido diferente con ese ID falla. No hay deduplicación durable después de
`forget`, del cierre de sesión o del reinicio. El anfitrión conserva su ledger,
retira los IDs olvidados y decide la recuperación. Al agotar la historia se
rechazan nuevas admisiones, sin expulsar silenciosamente operaciones pendientes.

`Finished.delivery` distingue `Sent`, `Rejected` y `Uncertain`. LIS01 sólo permite
afirmar entrega cuando completa el intercambio; otros finales se conservan como
inciertos. HL7 conserva el resultado de su ACK correlacionado. Ninguno demuestra
ejecución física, aprobación clínica ni commit del negocio. Una incertidumbre
detiene la sesión: no se reintenta automáticamente una orden. La cancelación
en curso también cierra el canal; no intenta reutilizar un frame a medias.

Las operaciones explícitas se despachan una por vez y en FIFO. Las respuestas
automáticas a consultas siguen el loop y presupuesto del perfil sobre el mismo
canal; no se abre otro lector para QC, órdenes o calibración. Las respuestas de
consulta pueden preceder a operaciones aún en cola. Los límites y plazos acotan
la espera: no se incorpora una prioridad clínica ni un scheduler de negocio.

### Callbacks y custodia

| Callback | Entrada | Respuesta exigida |
|---|---|---|
| `trace.ready` | `traceId`, `generation` | `traceId`, `ready`. Fallar si la custodia protegida no está preparada o la traza no admite esa nueva generación |
| `trace.append` | `traceId`, `generation`, `entry` íntegra | `traceId`, `sequence`, `stored`. Confirmar la entrada exacta después de capturarla según la garantía del sink |
| `workflow.ingest` | `InstrumentObservationBatch` completo | `IngestOutcome`: `Accepted`, `Duplicate`, `Rejected`, `Deferred`, código/referencia opcionales |
| `workflow.query` | `OrderQuery` | `OrderQueryAnswer`: `Orders`, `NoInformation` o `Unavailable`, con las órdenes correspondientes |

El anfitrión administra cifrado, retención y disponibilidad. La traza es
síncrona y ordenada por secuencia: el puente no devuelve éxito al runtime por
haber encolado/enviado un callback. Espera confirmación de la entrada exacta.
Fallos y plazos se propagan; no hay fallback en disco. Los eventos de tenencia y
auditoría PortGate también atraviesan esa custodia. No confirmar `stored` por
mera admisión en una cola del consumidor. La confirmación define la garantía de
captura del `ITraceSink` del anfitrión, no un commit clínico.

`trace.ready` es idempotente para la misma traza/generación. El anfitrión rechaza
reutilizar una traza de otra generación. En sesiones vivas, los eventos
`service.diagnostic` conservan en `detail` el diagnóstico JSON completo, con
severidad y fuente; `messages.process` también devuelve los diagnósticos tipados.

`Accepted` exige commit durable del batch entero, incluidos QC, recursos,
calibración e inventario; `Duplicate` exige evidencia del ingreso durable previo.
Guardar antes de responder. Un recurso externo aceptado como referencia no se
descarga automáticamente. El runtime HL7 espera esa respuesta antes de AA;
`Rejected` produce AR y `Deferred`/indisponibilidad produce AE o termina la
sesión si además falla la custodia. Un ACK LIS01 de frame sólo reconoce el enlace.

Los callbacks tienen un plazo máximo de despliegue. La consulta publica el
menor entre ese plazo y su presupuesto de perfil; cancelaciones externas pueden
adelantarlo. El lector IPC continúa resolviendo respuestas mientras las
peticiones esperan. Las respuestas tardías no resucitan callbacks vencidos y
no revierten un commit que el anfitrión ya haya realizado: éste reconcilia por
las identidades registradas. El anfitrión no solicita un segundo canal durante
un callback ni espera una decisión humana para contestar una consulta.

`PendingOrdersAsync`, `MappingsAsync` y `AcknowledgeOrderAsync` no constituyen un
pump automático en el runtime actual. En este servicio el consumidor manda
intenciones explícitas por `operations.start`, consulta/persiste su entrega y
aporta las órdenes de `workflow.query`. Mappings y proyecciones de negocio
permanecen en el anfitrión; el batch conserva siempre códigos y originales.

### Capacidades conservadas

Todos los perfiles incluidos son descubribles aunque no exista consumidor.
ARCHITECT HL7 expone R22, consultas ZOS, O33, informes R24 e inventario INR/U06 e
INU/U05; ASI mantiene su alcance distinto. Consultar inventario requiere módulo
y serial; recibir U05 no inventa una correlación unívoca con una consulta.
No se anuncia inicio de calibración ni consulta de curvas históricas.

CFX conserva sus importaciones; U-WAM sus recursos QC; XN-L, CS-2500, QWALYS,
Ruby y CLSI conservan su catálogo y restricciones. UC/UF son receptores pasivos,
sin comandos activos inventados. Sus lectores incrementales pertenecen al
plugin, no al motor genérico; no tratan un fragmento como EOF ni acumulan toda
la sesión. La observación usa ambos sentidos cuando existen; estado desconocido
o cobertura incompleta no se convierten en `NoPendingOperations`.

## Configuración y operación

[Configuración sintética](ejemplos/servicio/config.synthetic.json) y
[consumidor Python](ejemplos/servicio/demo.py). Desde la raíz ASTM:

```powershell
dotnet run --project dotnet/tools/Astm.Service.Host -- --config Documentacion/ejemplos/servicio/config.synthetic.json
```

En otra consola, con Python 3 disponible:

```powershell
python Documentacion/ejemplos/servicio/demo.py --endpoint astm-synthetic --synthetic
```

Parada independiente del consumidor:

```powershell
dotnet run --project dotnet/tools/Astm.Service.Host -- --stop astm-synthetic
```

La demostración sólo usa mensajes sintéticos, traza en memoria e ingestión
`Deferred`. **No es una implementación de custodia durable**. No debe conectarse
a un canal físico. Windows usa Win32 solapado desde `ctypes`, sin pywin32;
Linux usa `AF_UNIX`. Referencia de la concurrencia de E/S:
[Microsoft, Synchronous and Overlapped Pipe I/O](https://learn.microsoft.com/en-us/windows/win32/ipc/synchronous-and-overlapped-input-and-output).

Para Linux, configurar `endpoint` como una ruta absoluta corta, por ejemplo
`/run/user/<uid>/astm/service.sock`, con su directorio privado previamente
preparado. `ownershipDirectory` debe ser absoluto y estable en despliegue; las
rutas relativas del ejemplo sólo sirven para la prueba local. El socket es un
recurso reservado al servicio y puede retirarse al arrancar bajo su lock para
recuperar una caída anterior. El supervisor inicia el proceso con usuario,
directorio de trabajo y configuración explícitos, y aplica su política de
reinicio. El consumidor reconstruye sus sesiones tras el reinicio.

El proceso responde a Ctrl+C, SIGTERM en POSIX y `--stop` por IPC. No instala
un servicio Windows, una unidad systemd ni un driver. Windows puede supervisar
el ejecutable con la infraestructura del anfitrión que permita el stop IPC;
no se presenta como un binario que implemente directamente SCM. Los códigos
de salida son 0 para parada ordenada, 1 para fallo y 2 para uso incorrecto.
Los diagnósticos genéricos sólo contienen códigos/tipos de error, sin payloads.

### Transportes configurados

`transport.kind` admite `messages`, `serial`, `tcp`, `tcp-listen` y `portgate`.
Los parámetros de conexión sólo se leen de la configuración local del servicio,
nunca de una petición IPC arbitraria. Desde el 2026-11-22 esa configuración local
también puede cambiarla un principal autenticado y autorizado con
`instruments.put`/`gateways.put`, que la validan, la auditan y la escriben de forma
atómica en el mismo archivo ([ADMINISTRACION-SERVICIO](ADMINISTRACION-SERVICIO.md));
`sessions.open` sigue eligiendo sólo un instrumento ya configurado.
`serial` usa `portName` y `serial` (o valores documentados
del perfil). TCP usa `host`/`port`; el listener requiere dirección IP numérica.
PortGate agrega `portId` y `host`/`port` o `gatewayId` (un agente de `gateways`),
configura su controller como `astm-service/<instrumentId>`
y comprueba la configuración serial anunciada. Un desacuerdo falla: no ejecuta
`SetConfig` ni modifica silenciosamente el agente.

TLS se solicita con `tls: true`: validación de plataforma o pin SHA-256 explícito
en `certificateSha256`, `tlsTargetHost` y certificado cliente existente mediante
`clientCertificatePath` cuando corresponda. El listener TLS requiere ese archivo
PKCS#12 como certificado servidor. Los archivos pertenecen al despliegue y se
protegen por la cuenta; no se generan ni copian claves en esta implementación.
No hay downgrade TLS ni aceptación universal de certificados. COM, TCP y
PortGate conservan las capacidades y límites de su perfil y del transporte
existente. Ver [TRANSPORTE](TRANSPORTE.md).

El modo `messages` acepta bytes completos en Base64: el anfitrión puede leer su
Stream o fuente, conservar el `receptionId` y entregar el contenido acotado.
No gestiona nombres de archivo, watchers, rotación, logs ni checkpoints. En un
canal vivo el servicio ya es propietario de la lectura, y rechaza una segunda
entrada por `messages.process`. Un medio pasivo puede preparar operaciones,
pero no ejecutarlas ni acreditar disponibilidad de un canal físico.

### Límites de despliegue

| Parámetro de `limits` | Valor inicial |
|---|---:|
| `maxFrameBytes` | 4194304 |
| `maxContentBytes` | 1048576 |
| `maxSessions` | 16 por proceso |
| `maxRequests` | 32 por conexión |
| `maxCallbacks` | 64 por conexión |
| `outboundCapacity` | 32 por conexión |
| `operationCapacity` | 16 pendientes por sesión |
| `operationHistory` | 1024 registros por sesión |
| `callbackTimeoutMs` | 10000; también acota escritura y trama parcial |
| `requestTimeoutMs` | 120000 |

Hay como máximo ocho clientes IPC simultáneos. Las colas del enlace ASTM se
acotan a 128 entradas/16 recepciones en este host y una recepción al límite de
contenido. PortGate acota unidades/eventos; ante un consumidor que no drena,
termina el canal en lugar de descartar datos silenciosamente. Saturación de la
cola de operaciones o peticiones se rechaza; presión de salida espera con plazo.
Un payload canónico expandido que excede la trama tampoco se acepta a medias.
Estos son límites operativos configurables, **no timers nuevos del instrumento
ni SLO homologados**. El costo de confirmar trazas cruza la frontera de procesos
y debe incluirse al ensayar los presupuestos de cada equipo.

## Compilación y validación

```powershell
dotnet test dotnet/Astm.slnx
ctest --test-dir PortGate/build/win-x64 -C Release
dotnet publish dotnet/tools/Astm.Service.Host/Astm.Service.Host.csproj -c Release -r win-x64 -p:PublishAot=true -p:AstmRequireStrongName=true
```

El build usa exclusivamente BCL y proyectos/paquetes ya aprobados. La SNK de
distribución permanece fuera del repositorio; no se genera otra. Strong Name,
firma NuGet y Authenticode son verificaciones diferentes. La salida NativeAOT
se prueba ejecutándola, además de compilarla. No se crea ZIP ni se instala nada.

La evidencia local final se registra en ESTADO y en el changelog de esta tarea.
Linux real, COM físico, homologación de instrumento y estabilidad de varios días
permanecen como validaciones operativas del despliegue. No se equipara Windows
sintético, compilación cruzada o una prueba corta con esas validaciones. Los
casos/hallazgos de seguridad específicos se conservan localmente fuera de Git.
