# Administración remota del agente nativo (P-16)

Propuesta del 2026-10-26. **Decisión humana del mismo día:** el agente se administra de forma remota y centralizada; la configuración por archivo JSON deja de ser la vía de aprovisionamiento. Este documento fija el diseño y lista las decisiones abiertas antes de implementar.

## Problema actual

- `atsm-agent --config agent.json` exige editar un archivo por equipo, copiar certificados y reiniciar a mano.
- El agente corre en primer plano: no hay servicio ni arranque automático.
- Cada puerto se identifica por el nombre del sistema (`COM3`). Si Windows reasigna el COM (otro puerto USB, reinstalación del adaptador), el instrumento “desaparece” hasta corregir el archivo.

## Objetivo

1. Instalar el agente como **servicio** (Windows SCM; `systemd` en Linux/Raspberry) que arranca solo, se recupera ante fallos y sobrevive al cierre de sesión.
2. Arrancar **sin configuración**: el agente recién instalado sólo expone un puerto de administración.
3. Un **administrador remoto** (EDC u otro anfitrión neutral; Diagnostics para soporte) se vincula una vez y desde ahí ve los puertos serie, los asigna a instrumentos, fija tiempos (lease, heartbeat), controladores autorizados y material TLS.
4. Identificar cada instrumento por la **identidad física** del puerto, no por el número COM.

## Diseño

### Ciclo de vida

| Estado | Qué expone | Cómo se sale |
|---|---|---|
| Sin vincular | Puerto de administración TLS con certificado autogenerado; código de vinculación de un solo uso | Un administrador presenta el código |
| Vinculado | Administración sólo para el administrador vinculado (mTLS); puertos Link v1 según la configuración recibida | Desvinculación remota o local (reset) |

- `atsm-agent service install|uninstall|start|stop` registra el servicio con inicio automático y acciones de recuperación. En Windows corre como cuenta de servicio con privilegio mínimo; en Linux, usuario dedicado con acceso a `dialout`.
- **Servicio, no driver (Windows).** El agente no necesita modo kernel: los puertos ya los expone el driver del fabricante (usbser, FTDI, Prolific, puertos nativos) y el agente los abre como cualquier proceso. Un servicio de usuario arranca antes del inicio de sesión, se reinicia solo ante fallos y se instala con `CreateService`. Un driver exigiría firma de kernel con certificación de Microsoft, una falla tiraría el equipo entero (pantalla azul) y no aporta nada para este caso.
- El estado (identidad del agente, administrador vinculado, configuración vigente y anterior) vive en un directorio del servicio, protegido: DPAPI de máquina en Windows y permisos `0600` en POSIX. No es un archivo editable a mano.
- Reset local: `atsm-agent admin reset` (requiere administrador del sistema operativo) vuelve a “sin vincular” y genera un código nuevo.

### Vinculación de un solo uso

1. Al quedar sin vincular, el agente genera su par de claves, un certificado autofirmado y un **código de vinculación** (aleatorio, ~10 caracteres legibles, vence en un TTL configurable al instalar, por ejemplo 30 min). Lo muestra en el log del servicio / `atsm-agent admin code` y se puede pasar al instalar (`service install --pairing-code …`) para aprovisionamiento masivo.
2. El administrador conecta a `IP:puertoAdmin` (por defecto 28240; `127.0.0.1` para administración local) y envía `Pair{code, adminCertificate}`. Muestra al operador la huella del certificado del agente para confirmarla (TOFU con verificación humana).
3. Si el código es válido, el agente fija el certificado del administrador, invalida el código y cierra el modo de vinculación. Intentos fallidos se limitan (p. ej. 5 y bloqueo temporal).
4. Desde ahí la administración exige TLS mutuo con ese certificado. La rotación del certificado del administrador es una operación administrativa más.

### Protocolo de administración

Extensión de Link v1 en un endpoint separado (mismo encuadre y CBOR), tipos `0x40–0x4F`:

| Tipo | Dirección | Contenido |
|---|---|---|
| `AdminStatus` | agente → admin | versión, plataforma, epoch, estado de cada puerto y controladores conectados |
| `ListDevices` | admin → agente | puertos serie presentes: nombre (`COM5`), **identidad física** (USB VID/PID/número de serie, ruta de ubicación, nombre amigable), en uso o no |
| `GetConfig` / `SetConfig` | ambos | configuración completa versionada; `SetConfig` lleva `expectedRevision`, se valida entera, se aplica atómicamente y conserva la anterior para `Rollback` |
| `CreateCsr` / `InstallCertificate` | ambos | el agente genera la clave de Link y entrega un CSR; el admin devuelve el certificado firmado y la CA de controladores. La clave privada nunca sale del agente |
| `SetPairing`, `Unpair` | admin → agente | rotar el certificado del administrador o desvincular |
| `Restart` | admin → agente | reinicio controlado del servicio |

Todo cambio queda en un registro de auditoría del agente (quién, cuándo, revisión), consultable por el administrador.

### Identidad del puerto y cambio de COM

Cada puerto de la configuración declara un **selector** en lugar de una ruta fija:

```json
{ "portId": "uf5000", "instrumentId": "uf5000-lab1",
  "match": { "usbVid": "067B", "usbPid": "2303", "usbSerial": "A10KX3QZ" },
  "serial": { "baudRate": 9600, "dataBits": 8, "parity": "None", "stopBits": 1 } }
```

- Orden de preferencia: número de serie USB → ruta de ubicación física (adaptadores sin número de serie, mismo puerto USB) → nombre del sistema (`COM3`, sólo puertos nativos).
- El agente resuelve el selector al abrir y ante eventos de conexión/desconexión (Windows: notificación de dispositivos + SetupAPI; Linux: `/dev/serial/by-id` y `by-path`). Si el adaptador reaparece en otro COM, el puerto vuelve con el mismo `portId` y se emite `PortEvent` *reopened* con el nuevo nombre.
- Ambigüedad (dos dispositivos coinciden) o ausencia: el puerto queda en falla con código explícito, nunca se elige uno al azar.

### Tiempos configurables

`heartbeatMs`, `leaseMs` por puerto, `maxQueuedRequests`, TTL del código de vinculación y límites de conexiones/handshakes (P-12) pasan a ser parte de la configuración remota con rangos validados por el agente.

## Neutralidad y reparto

- ATSM aporta el agente, la especificación (`spec/admin-v1.md`), vectores de conformidad y una biblioteca .NET cliente (`Atsm.Agent.Admin`) sin dependencia de un LIS.
- Diagnostics usa esa biblioteca para soporte local/remoto (vincular, ver puertos, editar configuración).
- EDC/LIMS Equipos es el aprovisionamiento central: inventario de agentes, vinculación, asignación de instrumentos y emisión de certificados. Se trabaja con su propio Issue en EDC.

## Decisiones abiertas

1. **Dirección de la conexión administrativa.** Propuesta: entrante al agente (como se pidió) y, en una segunda etapa, agente → servidor (rol cliente de P-09) para sitios sin puertos entrantes.
2. **JSON local.** Propuesta: retirar `--config` como vía de operación (regla 12) y conservar sólo `--import` para aprovisionamiento sin red y para pruebas.
3. **Emisor de certificados de Link.** Propuesta: el administrador firma los CSR con una CA propia del anfitrión (EDC); el agente no acepta claves privadas enviadas desde afuera.
4. **Windows XP (P-06).** SCM existe en XP, pero las notificaciones de dispositivos y algunas APIs de SetupAPI difieren: resolver selectores por sondeo en esa plataforma.

## Pruebas requeridas

Vinculación (código válido, vencido, reutilizado, fuerza bruta), mTLS del administrador, `SetConfig` atómico con revisión concurrente y rollback, CSR/instalación de certificado, selector ante cambio de COM, ambigüedad y ausencia (con dispositivos simulados), instalación/desinstalación del servicio y reinicio con controladores conectados.
