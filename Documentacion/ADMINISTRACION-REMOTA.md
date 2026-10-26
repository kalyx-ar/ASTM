# RemoteUSB: servicio y administración remota (P-16)

**Decisiones humanas del 2026-10-26:**

1. El agente se llama **RemoteUSB** (antes `atsm-agent`, directorio `native/`). Es un puente serie ↔ TCP agnóstico del protocolo del instrumento: lo administra ATSM neutral y la parte específica la aporta el plugin.
2. Se administra de forma remota y centralizada; el archivo JSON deja de ser la vía de aprovisionamiento.
3. En Windows es un **servicio**, no un driver.
4. TLS es opcional. El material X.509 puede venir completo desde el servidor.
5. No hay código de vinculación. El servidor que se conecta establece el modo de operación.
6. Versiones, historial y vuelta atrás los administra el **servidor**. RemoteUSB no guarda historia.
7. RemoteUSB es **ignorante** de la identidad de los instrumentos: informa lo que ve y el servidor recuerda qué dispositivo corresponde a cada instrumento.
8. No hay `--import`. La configuración sin red se hace con una UI que consume el puerto de administración en `127.0.0.1`.
9. **Siempre arranca con la última configuración instalada**, esté o no disponible el servidor. Para cambiarla hay que conectarse y reemplazarla con la autorización vigente. Nunca vuelve al estado "sin configurar" por sí solo: eso permitiría que un servidor desconocido lo reconfigure.

## Servicio, no driver

RemoteUSB no necesita modo kernel: el puerto lo expone el driver del adaptador (usbser, FTDI, Prolific o el puerto nativo) y RemoteUSB lo abre como cualquier proceso. Un servicio de usuario arranca antes del inicio de sesión, se reinicia solo ante fallos y se instala con `CreateService`. Un driver exigiría firma de kernel certificada por Microsoft, una falla tiraría el equipo (pantalla azul) y no aporta nada aquí.

`remoteusb service install|uninstall|start|stop` registra el servicio con inicio automático y recuperación. En Linux/Raspberry, unidad `systemd` con usuario dedicado del grupo `dialout`. Se retira `--config`/`--check` como vía de operación (regla 12).

## Modos que fija el servidor

| Aspecto | Opciones |
|---|---|
| Quién inicia | **Servidor** (RemoteUSB escucha) o **agente** (RemoteUSB conecta a la dirección del servidor; rol cliente de P-09) |
| Acceso | **Restringido** (sólo el servidor que lo configuró administra y usa los puertos) o **compartido** (varios controladores según la lista que entrega el servidor) |
| Transporte | **Plano** o **TLS**, por endpoint (administración y Link) |

Recién instalado, RemoteUSB escucha administración en el puerto 28240. El primer servidor que lo configura en modo restringido queda como único administrador. Esa configuración persiste y se usa en cada arranque.

## Protocolo de administración

Extensión de RemoteUSB Link v1 (mismo encuadre y CBOR), tipos `0x40–0x4F`, en un endpoint propio:

| Tipo | Dirección | Contenido |
|---|---|---|
| `AdminStatus` | agente → servidor | versión, plataforma, epoch, modo vigente, estado de cada puerto y controladores conectados |
| `ListDevices` | servidor → agente | puertos serie presentes: nombre (`COM5`), USB VID/PID, número de serie USB, ruta de ubicación física, nombre amigable, en uso o no |
| `SetMode` | servidor → agente | quién inicia (y dirección del servidor), restringido/compartido, plano/TLS |
| `SetConfig` | servidor → agente | configuración completa de puertos y tiempos; se valida entera y reemplaza a la vigente de una vez |
| `SetTls` | servidor → agente | certificado, clave privada y CA de controladores, provistos por el servidor |
| `DeviceEvent` | agente → servidor | aparición/desaparición de un dispositivo serie, con su identidad física |
| `Restart` | servidor → agente | reinicio controlado |

`GetConfig` devuelve sólo la configuración vigente; para volver atrás el servidor reenvía una configuración completa.

## Identidad de los instrumentos

- La configuración de puertos usa el nombre del sistema (`COM5`) que el servidor eligió a partir de `ListDevices`.
- RemoteUSB no persiste correspondencias ni selectores; informa la identidad física de cada puerto.
- El servidor recuerda instrumento ↔ identidad física y puede reconocer el equipo por su tráfico (número de serie del analizador en el encabezado ASTM/HL7). Si el adaptador aparece en otro COM, envía un `SetConfig` corregido.

## Qué guarda RemoteUSB

Sólo la configuración vigente: modo, administrador autorizado, material TLS y puertos, protegida (DPAPI de máquina en Windows, `0600` en POSIX). Sin historia; se sobrescribe en cada cambio y se usa en cada arranque.

## Revisión de riesgos

| # | Riesgo | Consecuencia | Mitigación propuesta |
|---|---|---|---|
| R1 | **Toma del agente recién instalado.** Entre la instalación y la primera configuración, quien alcance el puerto 28240 puede configurarlo en modo restringido y quedarse con él. | El laboratorio pierde el control del equipo; recuperarlo exige reinstalar con permisos de administrador del sistema. | Permitir fijar el servidor **al instalar** (`service install --server host:puerto [--server-cert sha256]`): RemoteUSB arranca ya restringido e iniciado por el agente, sin ventana abierta y sin código. Sin ese parámetro, la ventana existe; acotarla con el firewall del equipo y configurar inmediatamente después de instalar. |
| R2 | **Administración en plano.** Sin TLS, el administrador se reconoce por su dirección IP. En la misma red se puede suplantar (ARP spoofing) o interceptar. | Un tercero reconfigura el agente, lee o inyecta datos del instrumento (datos de pacientes, comandos al analizador). | Plano sólo en redes confiables y segmentadas. Recomendación: el canal de administración siempre con TLS, aunque Link vaya en plano; RemoteUSB genera su propio certificado y el servidor fija su huella en el primer contacto (sin intervención humana). |
| R3 | **Clave privada enviada en plano.** `SetTls` lleva la clave privada del servidor al agente. | Si el canal es plano, la clave queda expuesta en la red y el TLS posterior no protege nada. | Rechazar `SetTls` con clave privada por un canal de administración plano. Compatible con R2. |
| R4 | **Acceso local por 127.0.0.1.** Cualquier proceso o usuario del equipo llega al puerto local. | Un usuario sin privilegios reconfigura el agente. | En modo restringido, la conexión local exige la misma autorización que la remota; no hay excepción por ser local. Sin configurar, aplica R1. |
| R5 | **Servidor suplantado en modo "inicia el agente".** RemoteUSB conecta a una dirección; DNS o IP pueden apuntar a otro equipo. | Un servidor falso le entrega configuración o recibe los datos. | Con TLS, fijar el certificado del servidor. En plano no hay mitigación técnica: sólo red confiable. |
| R6 | **Servidor perdido.** Si el servidor administrador desaparece (reinstalado, certificado perdido), nadie puede cambiar la configuración. | El equipo sigue funcionando con la última configuración, pero no se puede administrar. | Es el comportamiento buscado (decisión 9). Recuperación: reinstalar el servicio con permisos de administrador del sistema, lo que borra el estado. El servidor debe respaldar su identidad (certificado y clave). |
| R7 | **Clave privada en reposo.** DPAPI de máquina protege contra otros equipos, no contra un administrador local. | Un administrador del equipo puede extraer la clave TLS. | Aceptado: quien administra el sistema operativo ya controla el puerto serie. Documentarlo. |
| R8 | **Escritura al instrumento.** Un controlador con el puerto concedido envía bytes crudos al analizador. | Comandos no deseados (borrado, reprocesos) desde un controlador autorizado o comprometido. | Lista de controladores entregada por el servidor; modo restringido por defecto; el compartido se habilita explícitamente. |
| R9 | **Denegación de servicio.** Muchas conexiones o handshakes TLS (hoy un hilo por conexión). | El agente deja de atender al servidor legítimo. | Límites de conexiones y handshakes y espera entre intentos fallidos de administración (P-12). |
| R10 | **Actualización del binario.** Fuera de este diseño. | Un ejecutable reemplazado tiene acceso a todo lo anterior. | Firma Authenticode del release y permisos del directorio de instalación; actualización remota queda como pendiente aparte. |

Decisiones a confirmar a partir de esta revisión: R1 (`--server` al instalar), R2/R3 (administración siempre con TLS y `SetTls` sólo por TLS) y R4 (sin excepción local).

## Configuración local

Una UI de configuración (Diagnostics, o la de EDC) se conecta a `127.0.0.1:28240` con el mismo protocolo y la misma autorización.

## Neutralidad y reparto

- RemoteUSB: agente C++ en `RemoteUSB/`, especificación `spec/link-v1.md` y su extensión de administración, vectores de conformidad.
- ATSM neutral: biblioteca .NET cliente (canal `SerialTunnel` y administración), sin dependencia de un LIS. El plugin aporta lo específico del instrumento.
- Diagnostics: UI local/remota sobre esa biblioteca.
- EDC/LIMS Equipos: aprovisionamiento central (inventario, modos, asignación de instrumentos, versiones/historial, certificados), con su propio Issue en EDC.

## Pendiente de definir

- Windows XP (P-06): SCM existe, pero las notificaciones de dispositivos y parte de SetupAPI difieren; `DeviceEvent` se obtendría por sondeo.

## Pruebas requeridas

Toma de control del agente nuevo y rechazo de un segundo administrador en modo restringido; arranque con la última configuración sin servidor; modos plano/TLS y servidor/agente; `SetConfig` completo y rechazo de configuraciones inválidas sin cambios parciales; `SetTls` válido, inválido y rechazado por canal plano; `ListDevices`/`DeviceEvent` con dispositivos simulados y cambio de COM; instalación/desinstalación del servicio y reinicio con controladores conectados.
