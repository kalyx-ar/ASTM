# Servicio neutral: puertos PortGate y administración autenticada

Diseño e implementación del 2026-11-22. Extiende [SERVICIO-NEUTRAL](SERVICIO-NEUTRAL.md)
para que un anfitrión (por ejemplo, la pantalla de administración de analizadores
de un LIS) pueda:

1. ver los agentes PortGate y sus puertos (COM o TCP), con su estado;
2. dar de alta, editar y quitar instrumentos y agentes PortGate sin editar el
   archivo de configuración a mano.

Aprobación del responsable: implementar 1 y 2 sin push ni PR. Sin dependencias
nuevas (BCL y proyectos existentes), sin cambios en PortGate (C++), sin datos de
pacientes ni equipos de producción en las pruebas.

## Extensión del contrato sin romper `astm-service/1.0`

`v` sigue siendo 1 y `service.hello` sigue anunciando `protocol:
"astm-service/1.0"` sin campos nuevos. La extensión es **aditiva**: sólo agrega
métodos, que `service.hello` anuncia en `methods`. Ésa es la negociación:

- El cliente invoca un método nuevo sólo si figura en `methods`. Un servicio
  anterior no lo anuncia; si igual se lo invoca, responde un error.
- Un método desconocido responde `method.unsupported` (antes, sin `sessionId`,
  respondía `session.unknown`).
- Los métodos de administración se anuncian sólo cuando el servicio tiene la
  administración habilitada (sección `administration` y archivo de configuración
  con escritura).
- Ningún DTO existente cambia de forma: un cliente estricto con los DTO de 0.6.7
  sigue leyendo `service.hello` y los demás resultados.

Un cambio incompatible futuro usará `v: 2` y `astm-service/2.0`. No se agrega un
`astm-service/1.1` porque un cliente que compara la versión exacta dejaría de
funcionar sin necesidad.

## Configuración nueva

```json
{
  "endpoint": "astm-lab",
  "ownershipDirectory": "C:/ProgramData/astm/owner",
  "revision": 0,
  "gateways": [
    { "id": "pc-quimica", "host": "10.0.0.21", "port": 28241,
      "controllerId": "astm-service/discovery", "observe": false }
  ],
  "instruments": [
    { "id": "architect-1", "profileId": "abbott.architect.asi",
      "transport": { "kind": "portgate", "gatewayId": "pc-quimica", "portId": "com3" } }
  ],
  "administration": {
    "auditPath": "C:/ProgramData/astm/audit/configuracion.jsonl",
    "principals": [
      { "id": "lis-servidor", "publicKey": "<SPKI P-256 en Base64>", "rights": ["instruments", "gateways"] }
    ]
  }
}
```

- `gateways` (opcional): agentes PortGate. `host`, `port` (Link), TLS con los mismos
  campos que un transporte (`tls`, `certificateSha256`, `tlsTargetHost`,
  `clientCertificatePath`), `connectTimeoutMs`, `controllerId` con que el servicio
  se presenta al consultar (predeterminado `astm-service/discovery`) y `observe`.
- Un transporte `portgate` puede usar `gatewayId` en lugar de `host`/`port`/TLS
  (son excluyentes). La sesión sigue presentándose como
  `astm-service/<instrumentId>`; la lista de controladores de cada puerto la
  decide el administrador de PortGate. Los transportes `portgate` con
  `host`/`port` propios siguen funcionando igual.
- `revision`: número de la configuración; cada cambio por IPC lo incrementa.
- `administration` (opcional): sin ella no hay escritura por IPC. Los
  principales sólo se dan de alta en el archivo local; ningún método IPC los
  agrega ni amplía sus derechos.

## Listado de agentes y puertos: `gateways.list`

Método de lectura disponible para cualquier consumidor del IPC (la misma cuenta
local, como el resto del contrato). `data`: `{}` o `{"gatewayIds": ["..."]}`.

Para cada agente configurado (y para cada `host:port` usado directamente por un
instrumento `portgate`, con `implicit: true`) el servicio abre una conexión Link,
recibe `Hello` y `Capabilities` (spec/link-v1.md) y se presenta con
`controllerId`. **No adquiere puertos ni envía `SetConfig`.** Devuelve:

| Campo | Contenido |
|---|---|
| `id`, `host`, `port`, `tls`, `implicit` | Lo configurado |
| `reachable`, `code` | Si respondió; código técnico si no (`gateway.unreachable`, `gateway.refused`, `gateway.timeout`) |
| `agentId`, `agentEpoch`, `platform`, `observerSoftKill` | Lo que anuncia `Hello`/`Capabilities` |
| `ports[]` | `portId`, `instrumentId` anunciado, `ownership` (`none`/`advisory`/`enforced`), `traceCapture`, `maxWriteBytes`, `queuedAcquisition`, `device` (`kind` `serial`/`tcp`/`tcp-listen`, `path`, `host`, `port`, `serial` con los parámetros vigentes), `virtualCom` |
| `ports[].state` | Semáforo (`open`, `owner`, `generation`, `queued`, `secureClosed`, `quarantined`, `shared`) sólo si `observe` está activo y el puerto le da al `controllerId` el derecho de observación; si no, `null` y `stateCode` (`state.not-observed` o el código del agente, p. ej. `PORTGATE-OBSERVE-NOT-ALLOWED`) |
| `ports[].instruments[]` | Instrumentos de este servicio que usan ese puerto: `instrumentId`, `inSession` (sesión abierta en este servicio) y `settingsMatch` (si los parámetros serie esperados coinciden con los anunciados; `null` si no aplica) |

Observar un puerto genera los eventos `observe-start`/`observe-stop` en el
registro del agente: no hay lectura oculta (ADMINISTRACION-REMOTA). Por eso
`observe` es explícito por agente. En un puerto compartido la observación copia
tráfico: el servicio descarta esas copias sin leerlas y deja de observar en
cuanto recibe el estado.

Las consultas se hacen en paralelo, una por agente, con el plazo de conexión
de cada uno. Un solo listado a la vez por servicio; los demás esperan. La lista
de dispositivos físicos del equipo (`ListDevices`, USB VID/PID) es de la
administración de PortGate y no se expone: exige la autorización del
administrador del agente, que el servicio no tiene.

## Configuración autenticada

### Autenticación del principal

El pipe/socket ya restringe el acceso a la cuenta del proceso. Para escribir se
exige además un **principal** que demuestre tener la clave privada asociada a una
clave pública del archivo local. El servicio no guarda secretos y por el IPC no
viaja un token reutilizable:

1. `admin.challenge` `{}` → `{nonce, instanceId, expiresUtc, algorithm: "ES256-DER"}`.
   El nonce (32 bytes aleatorios, Base64) es de esa conexión, vale 60 s y se usa
   una vez.
2. `admin.authenticate` `{principalId, signature}`: firma ECDSA P-256 con SHA-256,
   codificación DER (RFC 3279), en Base64, sobre los bytes UTF-8 de
   `"astm-service/admin-v1\n" + instanceId + "\n" + nonce + "\n" + principalId`
   (`instanceId` en formato `D` en minúsculas, `nonce` tal como llegó). Resultado:
   `{principalId, rights}`.

La autenticación dura lo que la conexión. Un principal desconocido y una firma
inválida responden el mismo `admin.unauthorized`. Tres fallos en la conexión la
bloquean para administración (`admin.locked`). Cada intento, aceptado o no, queda
en la auditoría.

Generación de la clave fuera del servicio (la privada queda en la custodia del
anfitrión):

```powershell
openssl ecparam -name prime256v1 -genkey -noout -out lis-admin.pem
openssl ec -in lis-admin.pem -pubout -outform DER | openssl base64 -A
```

En .NET, `ServiceAdministration.Sign(ecdsa, instanceId, nonce, principalId)`
produce la firma; en Python, `cryptography`
(`private_key.sign(payload, ec.ECDSA(hashes.SHA256()))`, ya en DER).

### Métodos

| Método | Derecho | `data` | Resultado |
|---|---|---|---|
| `instruments.list` | ninguno | `{}` | `{revision, instruments, gateways}` tal como están configurados |
| `instruments.put` | `instruments` | `{revision, instrument, dryRun?, allowUnreachableGateway?}` | `{revision, instrument, verification}` |
| `instruments.remove` | `instruments` | `{revision, instrumentId}` | `{revision}` |
| `gateways.put` | `gateways` | `{revision, gateway, dryRun?}` | `{revision, gateway}` |
| `gateways.remove` | `gateways` | `{revision, gatewayId}` | `{revision}` |

`revision` es la que el anfitrión leyó: si cambió, `configuration.revision-mismatch`
(concurrencia optimista). `put` da de alta o reemplaza completo; no hay parches
parciales. `dryRun` valida (y verifica contra PortGate) sin escribir ni auditar.

### Validación

Se aplica la misma validación que al arrancar, más reglas para lo que llega por IPC:

- `id`: `[A-Za-z0-9][A-Za-z0-9._-]{0,127}`, porque forma el `controllerId`.
- Perfil conocido y `Bind` correcto (zona horaria, presupuestos HL7).
- Transporte: `kind` conocido; cada tipo admite sólo sus campos (`messages` ninguno;
  `serial` `portName` (`COMn` en Windows, ruta absoluta en `/dev` en POSIX) y
  `serial`; `tcp` `host`/`port`; `tcp-listen` IP numérica y certificado si hay TLS;
  `portgate` `portId` y `gatewayId` o `host`/`port`). TLS: huella de 64
  hexadecimales y certificado existente; sin TLS no se aceptan campos de TLS.
- Parámetros serie: los del instrumento o, si faltan, los del perfil, y válidos
  según el manual del perfil (`TransportKnowledge`). Un perfil sin parámetros
  serie exige que el instrumento los declare para `serial`.
- Un recurso (COM, `tcp-listen` y puerto PortGate) no puede quedar asignado a dos
  instrumentos: `transport.resource.duplicate`.
- `portgate`: el servicio consulta el agente. Si el puerto no existe,
  `transport.portgate.port-unknown`; si los parámetros serie no coinciden,
  `transport.portgate.settings-mismatch`. **No se corrige el agente:** no hay
  `SetConfig` ni ningún cambio en PortGate. Si el agente no responde, falla con
  `transport.portgate.unreachable`, salvo `allowUnreachableGateway: true`, que
  guarda el cambio con `verification: "unverified"`; la apertura de la sesión
  vuelve a comprobarlo y falla ante un desacuerdo, como hasta ahora.
- Un instrumento con sesión abierta no se edita ni se quita (`instrument.busy`);
  mientras se escribe, `sessions.open` de ese instrumento responde `instrument.busy`.
  Un agente usado por un instrumento en sesión no se edita (`gateway.busy`) y un
  agente referenciado no se quita (`gateway.referenced`).

### Persistencia atómica

El host pasa al servicio la ruta del archivo de configuración. Para escribir:

1. Se vuelve a leer el archivo y se compara su SHA-256 con el último leído o
   escrito. Si alguien lo editó a mano mientras el servicio corría,
   `configuration.changed-externally`: hay que reiniciar el servicio para tomarlo.
2. Se escribe la configuración completa en un temporal del mismo directorio
   (escritura directa a disco; `0600` en POSIX) y se renombra sobre el original.
   Un corte deja el archivo anterior o el nuevo, nunca uno a medias.
3. Recién después se aplica en memoria. Las sesiones abiertas no cambian; las
   siguientes usan la configuración nueva.

### Auditoría

`administration.auditPath` es un archivo JSON por línea, sólo de agregado, en un
directorio privado de la cuenta (misma regla que `ownershipDirectory`). Cada
registro tiene `timeUtc`, `instanceId`, `connectionId`, `principalId`, `action`
(`authenticate`, `instruments.put`, ...), `target`, `outcome` (`accepted`,
`denied`, `pending`, `applied`, `failed`), `code`, `revisionBefore`,
`revisionAfter`, `beforeSha256` (de la entrada anterior) y `after` (la entrada
nueva completa). Un cambio escribe `pending` antes de tocar el archivo y
`applied` o `failed` después. Si la auditoría no se puede escribir, el cambio no
se hace (`audit.unavailable`).

La configuración no contiene secretos ni datos de pacientes (sólo rutas, hosts,
puertos e identificadores), por eso se registra completa. No es una traza ni un
spool de datos del instrumento (AGENTS, regla 9). La retención y el respaldo del
archivo son del despliegue.

## Riesgos considerados

| Riesgo | Mitigación |
|---|---|
| Otro proceso de la misma cuenta reconfigura instrumentos | Firma de un principal cuya clave privada está en la custodia del anfitrión; el servicio sólo tiene la pública |
| Repetición de una autenticación capturada | Nonce aleatorio por conexión, de un solo uso, 60 s, ligado a la instancia |
| Fuerza bruta | Tres fallos bloquean la conexión; todo intento queda auditado |
| Escalada de privilegios por IPC | Principales y derechos sólo en el archivo local |
| Reconfigurar un equipo en uso | `instrument.busy` / `gateway.busy` y bloqueo de apertura durante la escritura |
| Desacuerdo con PortGate | Rechazo; sin `SetConfig` silencioso |
| Edición manual concurrente | Hash del archivo antes de escribir |
| Archivo a medias | Temporal + renombrado |
| `gateways.list` como escáner de red | Sólo consulta agentes configurados o ya usados por instrumentos; no acepta direcciones arbitrarias |

## Pruebas

`ServiceAdministrationTests`: firma válida, nonce reutilizado, principal
desconocido, firma inválida y bloqueo; derechos; métodos anunciados con y sin
administración; alta, edición y baja con revisión; `dryRun`; validaciones de
transporte y recurso duplicado; busy durante una sesión; persistencia releída
desde disco y rechazo ante edición externa; auditoría; desacuerdo PortGate,
puerto inexistente y agente inaccesible con y sin `allowUnreachableGateway`;
`gateways.list` con un agente simulado. Con PortGate compilado (`PortGateFact`),
`gateways.list` y `instruments.put` contra el agente real, con y sin observación.
