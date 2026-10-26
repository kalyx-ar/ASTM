# Administración remota del agente nativo (P-16)

**Decisiones humanas del 2026-10-26:**

1. El agente se administra de forma remota y centralizada; el archivo JSON deja de ser la vía de aprovisionamiento.
2. En Windows es un **servicio**, no un driver.
3. TLS es opcional. El material X.509 puede venir completo desde el servidor (aprovisionamiento completo).
4. No hay código de vinculación: complica obtenerlo, presentarlo y cargarlo en el servidor sin agregar seguridad real. El servidor que se conecta establece el modo de operación.
5. Versiones, historial y vuelta atrás los administra el **servidor**. El agente no guarda historia: menos datos comprometedores en el equipo.
6. El agente es **ignorante** de la identidad de los instrumentos: informa lo que ve y el servidor recuerda qué dispositivo corresponde a cada instrumento.
7. No hay `--import`. La configuración sin red se hace con una UI que consume el puerto de administración en `127.0.0.1`.

## Problema actual

- `atsm-agent --config agent.json` exige editar un archivo por equipo, copiar certificados y reiniciar a mano.
- El agente corre en primer plano: no hay servicio ni arranque automático.
- Los puertos se nombran por COM. Si Windows lo reasigna, hay que corregir el archivo.

## Servicio, no driver

El agente no necesita modo kernel: el puerto lo expone el driver del adaptador (usbser, FTDI, Prolific o el puerto nativo) y el agente lo abre como cualquier proceso. Un servicio de usuario arranca antes del inicio de sesión, se reinicia solo ante fallos y se instala con `CreateService`. Un driver exigiría firma de kernel certificada por Microsoft, una falla tiraría el equipo (pantalla azul) y no aporta nada aquí.

`atsm-agent service install|uninstall|start|stop` registra el servicio con inicio automático y recuperación. En Linux/Raspberry, unidad `systemd` con usuario dedicado del grupo `dialout`. Se retira `--config`/`--check` como vía de operación (regla 12).

## Modos que fija el servidor

Al conectarse al puerto de administración, el servidor establece:

| Aspecto | Opciones |
|---|---|
| Quién inicia | **Servidor** (el agente escucha) o **agente** (el agente conecta a la dirección del servidor; rol cliente de P-09) |
| Acceso | **Restringido** (sólo el servidor que lo configuró administra y usa los puertos) o **compartido** (varios controladores según la lista que entrega el servidor) |
| Transporte | **Plano** o **TLS**, por endpoint (administración y Link) |

Agente recién instalado: escucha administración en plano, sin restricción, en el puerto 28240. El primer servidor que lo configura en modo restringido queda como único administrador (identificado por certificado si hay TLS, o por dirección si es plano).

**Riesgo aceptado:** hasta que un servidor lo configura, cualquiera que alcance el puerto puede tomarlo; en modo plano la identidad del administrador es su dirección de red. Corresponde a redes de laboratorio confiables y se acota con el firewall del equipo; TLS restringido es el modo recomendado cuando la red no es confiable.

## Protocolo de administración

Extensión de Link v1 (mismo encuadre y CBOR), tipos `0x40–0x4F`, en un endpoint propio:

| Tipo | Dirección | Contenido |
|---|---|---|
| `AdminStatus` | agente → servidor | versión, plataforma, epoch, modo vigente, estado de cada puerto y controladores conectados |
| `ListDevices` | servidor → agente | puertos serie presentes: nombre (`COM5`), USB VID/PID, número de serie USB, ruta de ubicación física, nombre amigable, en uso o no |
| `SetMode` | servidor → agente | quién inicia (y dirección del servidor), restringido/compartido, plano/TLS |
| `SetConfig` | servidor → agente | configuración completa de puertos y tiempos; se valida entera y reemplaza a la vigente de una vez |
| `SetTls` | servidor → agente | certificado, clave privada y CA de controladores, provistos por el servidor |
| `DeviceEvent` | agente → servidor | aparición/desaparición de un dispositivo serie, con su identidad física |
| `Restart` | servidor → agente | reinicio controlado |

`GetConfig` devuelve sólo la configuración vigente. El agente no guarda revisiones anteriores, auditoría ni historial; el servidor lleva versión, historial y vuelta atrás, y para volver atrás reenvía una configuración completa.

## Identidad de los instrumentos

- La configuración de puertos usa el nombre del sistema (`COM5`) que el servidor eligió a partir de `ListDevices`.
- El agente no persiste correspondencias ni selectores. Informa con `ListDevices`/`DeviceEvent` la identidad física de cada puerto.
- El servidor recuerda instrumento ↔ identidad física (número de serie USB, ruta de ubicación) y, además, puede reconocer el equipo por su tráfico (por ejemplo, el número de serie del analizador en el encabezado ASTM/HL7). Si el adaptador aparece en otro COM, el servidor envía un `SetConfig` corregido.

## Qué guarda el agente

Sólo lo indispensable para arrancar como servicio: modo vigente, administrador autorizado, material TLS y configuración vigente de puertos, protegido (DPAPI de máquina en Windows, `0600` en POSIX). Sin historia. Se sobrescribe en cada cambio.

## Configuración local

Una UI de configuración (Diagnostics, o la de EDC) se conecta a `127.0.0.1:28240` con el mismo protocolo. No hay herramienta de importación aparte.

## Neutralidad y reparto

- ATSM: agente, especificación `spec/admin-v1.md`, vectores de conformidad y biblioteca .NET cliente `Atsm.Agent.Admin`, sin dependencia de un LIS.
- Diagnostics: UI local/remota sobre esa biblioteca.
- EDC/LIMS Equipos: aprovisionamiento central (inventario de agentes, modos, asignación de instrumentos, versiones/historial, certificados), con su propio Issue en EDC.

## Pendiente de definir

- Windows XP (P-06): SCM existe, pero las notificaciones de dispositivos y parte de SetupAPI difieren; `DeviceEvent` se obtendría por sondeo.
- Comportamiento de los puertos tras reiniciar el equipo en modo "inicio por servidor" cuando el servidor no está: propuesta, abrir con la configuración vigente guardada.

## Pruebas requeridas

Toma de control del agente nuevo y rechazo de un segundo administrador en modo restringido; modos plano/TLS y servidor/agente; `SetConfig` completo y rechazo de configuraciones inválidas sin aplicar cambios parciales; `SetTls` con material válido e inválido; `ListDevices`/`DeviceEvent` con dispositivos simulados y cambio de COM; instalación/desinstalación del servicio y reinicio con controladores conectados.
