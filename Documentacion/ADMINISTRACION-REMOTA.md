# PortGate: servicio y administración remota (P-16)

**Decisiones humanas del 2026-10-26:**

1. El agente se llama **PortGate** (decisión humana 2026-10-26; antes `atsm-agent` en `native/` y, brevemente, RemoteUSB). Es una puerta de puertos serie y Ethernet hacia TCP, agnóstica del protocolo del instrumento, con semáforo, kill switch y administración remota: lo usa ATSM neutral y la parte específica la aporta el plugin.
2. Se administra de forma remota y centralizada; el archivo JSON deja de ser la vía de aprovisionamiento.
3. En Windows es un **servicio**, no un driver.
4. TLS es opcional. El material X.509 puede venir completo desde el servidor.
5. No hay código de vinculación. El servidor que se conecta establece el modo de operación.
6. Versiones, historial y vuelta atrás de la **configuración** los administra el **servidor**; PortGate no guarda versiones de configuración porque son innecesarias. Sí puede guardar historia de eventos (semáforo, seguridad, puertos) para auditoría (aclaración humana del 2026-10-26).
7. PortGate es **ignorante** de la identidad de los instrumentos: informa lo que ve y el servidor recuerda qué dispositivo corresponde a cada instrumento.
8. No hay `--import`. La configuración sin red se hace con una UI que consume el puerto de administración en `127.0.0.1`.
9. **Siempre arranca con la última configuración instalada**, esté o no disponible el servidor. Para cambiarla hay que conectarse y reemplazarla con la autorización vigente. Nunca vuelve al estado "sin configurar" por sí solo: eso permitiría que un servidor desconocido lo reconfigure.

## Servicio, no driver

PortGate no necesita modo kernel: el puerto lo expone el driver del adaptador (usbser, FTDI, Prolific o el puerto nativo) y PortGate lo abre como cualquier proceso. Un servicio de usuario arranca antes del inicio de sesión, se reinicia solo ante fallos y se instala con `CreateService`. Un driver exigiría firma de kernel certificada por Microsoft, una falla tiraría el equipo (pantalla azul) y no aporta nada aquí.

`portgate service install|uninstall|start|stop` registra el servicio con inicio automático y recuperación. En Linux/Raspberry, unidad `systemd` con usuario dedicado del grupo `dialout`. Se retira `--config`/`--check` como vía de operación (regla 12).

## Modos que fija el servidor

| Aspecto | Opciones |
|---|---|
| Quién inicia | **Servidor** (PortGate escucha) o **agente** (PortGate conecta a la dirección del servidor; rol cliente de P-09) |
| Acceso | **Restringido** (sólo el servidor que lo configuró administra y usa los puertos) o **compartido** (varios controladores según la lista que entrega el servidor) |
| Transporte | **Plano** o **TLS**, por endpoint (administración y Link) |

Recién instalado, PortGate escucha administración en el puerto 28240. El primer servidor que lo configura en modo restringido queda como único administrador. Esa configuración persiste y se usa en cada arranque.

## Protocolo de administración

Extensión de PortGate Link v1 (mismo encuadre y CBOR), tipos `0x40–0x4F`, en un endpoint propio:

| Tipo | Dirección | Contenido |
|---|---|---|
| `AdminStatus` | agente → servidor | versión, plataforma, epoch, modo vigente, estado de cada puerto y controladores conectados |
| `ListDevices` | servidor → agente | puertos serie presentes: nombre (`COM5`), USB VID/PID, número de serie USB, ruta de ubicación física, nombre amigable, en uso o no |
| `SetMode` | servidor → agente | quién inicia (y dirección del servidor), restringido/compartido, plano/TLS |
| `SetConfig` | servidor → agente | configuración completa de puertos y tiempos; se valida entera y reemplaza a la vigente de una vez |
| `SetTls` | servidor → agente | certificado, clave privada y CA de controladores, provistos por el servidor |
| `DeviceEvent` | agente → servidor | aparición/desaparición de un dispositivo serie, con su identidad física |
| `RevokePort` | servidor → agente | revoca la tenencia vigente de un puerto (mantenimiento o controlador defectuoso) |
| `RequestHandover` / `HandoverAnswer` | controlador ↔ agente ↔ dueño | solicitud de cesión cooperativa y respuesta del dueño |
| `KillSwitch`, `Contest`, `SecureClose` | servidor → agente | interrupción forzada, impugnación y cierre por seguridad (sólo TLS con autenticación mutua y permiso explícito) |
| `ReadEvents` | servidor → agente | consulta del registro de eventos |
| `Restart` | servidor → agente | reinicio controlado |

`GetConfig` devuelve sólo la configuración vigente; para volver atrás el servidor reenvía una configuración completa.

## Identidad de los instrumentos

- La configuración de puertos usa el nombre del sistema (`COM5`) que el servidor eligió a partir de `ListDevices`.
- PortGate no persiste correspondencias ni selectores; informa la identidad física de cada puerto.
- El servidor recuerda instrumento ↔ identidad física y puede reconocer el equipo por su tráfico (número de serie del analizador en el encabezado ASTM/HL7). Si el adaptador aparece en otro COM, envía un `SetConfig` corregido.

## Qué guarda PortGate

- **Configuración vigente:** modo, administrador autorizado, material TLS y puertos, protegida (DPAPI de máquina en Windows, `0600` en POSIX). Sin versiones anteriores; se sobrescribe en cada cambio y se usa en cada arranque.
- **Registro de eventos:** tomas, liberaciones, pedidos de cesión, kill switch, impugnaciones, cierres por seguridad, aparición/desaparición de dispositivos y cambios de configuración (quién, cuándo, revisión; no su contenido). Persistente, con tamaño acotado y rotación, protegido igual que la configuración. Consultable por el servidor y los observadores autorizados para auditar aunque no estuvieran conectados en el momento del evento.

## Revisión de riesgos

| # | Riesgo | Consecuencia | Mitigación propuesta |
|---|---|---|---|
| R1 | **Toma del agente recién instalado.** Entre la instalación y la primera configuración, quien alcance el puerto 28240 puede configurarlo en modo restringido y quedarse con él. | El laboratorio pierde el control del equipo; recuperarlo exige reinstalar con permisos de administrador del sistema. | **Decidido:** opción de fijar el servidor al instalar (`service install --server host:puerto [--server-cert sha256 | --psk …]`): arranca ya restringido e iniciado por el agente. Sin ese parámetro ni firewall, el riesgo lo asume quien opera la instalación. |
| R2 | **Administración en plano.** Sin TLS, el administrador se reconoce por su dirección IP. En la misma red se puede suplantar (ARP spoofing) o interceptar. | Un tercero reconfigura el agente, lee o inyecta datos del instrumento. | **Decidido:** el plano es una opción legítima (puede haber motivos para no usar TLS). TLS es la recomendación documentada; no usarla es un riesgo que asume el operador. |
| R3 | **Clave privada enviada en plano.** `SetTls` lleva la clave privada al agente. | Si el canal es plano, la clave queda expuesta y el TLS posterior no protege nada. | **Decidido:** cuando se requiere TLS, su aprovisionamiento viaja por un canal seguro. PortGate rechaza `SetTls` con clave privada por un canal plano. El canal inicial puede ser TLS con el certificado autogenerado del agente (huella fijada por el servidor) o TLS-PSK; ver [Aprovisionamiento seguro](#aprovisionamiento-seguro). |
| R4 | **Acceso local por 127.0.0.1.** Cualquier proceso del equipo llega al puerto local. | Un usuario sin privilegios usa o reconfigura el agente. | **Decidido:** el acceso local se restringe sólo si el servidor lo restringe. El uso local es legítimo: una instancia local (p. ej. EDC local) puede tomar el puerto por `127.0.0.1` cuando el servidor cae, o como failover por latencia, compitiendo por el mismo semáforo. |
| R5 | **Servidor suplantado en modo "inicia el agente".** PortGate conecta a una dirección; DNS o IP pueden apuntar a otro equipo. | Un servidor falso le entrega configuración o recibe los datos. | Con TLS, fijar el certificado del servidor. En plano no hay mitigación técnica: sólo red confiable. |
| R6 | **Servidor perdido.** Si el servidor administrador desaparece (reinstalado, certificado perdido), nadie puede cambiar la configuración. | El equipo sigue funcionando con la última configuración, pero no se puede administrar. | Es el comportamiento buscado (decisión 9). Recuperación: reinstalar el servicio con permisos de administrador del sistema, lo que borra el estado. El servidor debe respaldar su identidad (certificado y clave). |
| R7 | **Clave privada en reposo.** DPAPI de máquina protege contra otros equipos, no contra un administrador local. | Un administrador del equipo puede extraer la clave TLS. | Aceptado: quien administra el sistema operativo ya controla el puerto serie. Documentarlo. |
| R8 | **Escritura al instrumento.** Un controlador con el puerto concedido envía bytes crudos al analizador. | Comandos no deseados (borrado, reprocesos) desde un controlador autorizado o comprometido. | Lista de controladores entregada por el servidor; modo restringido por defecto; el compartido se habilita explícitamente. |
| R9 | **Denegación de servicio.** Muchas conexiones o handshakes TLS (hoy un hilo por conexión). | El agente deja de atender al servidor legítimo. | Límites de conexiones y handshakes y espera entre intentos fallidos de administración (P-12). |
| R10 | **Actualización del binario.** Fuera de este diseño. | Un ejecutable reemplazado tiene acceso a todo lo anterior. | Firma Authenticode del release y permisos del directorio de instalación; actualización remota queda como pendiente aparte. |
| R11 | **Dominio del semáforo.** Un controlador que renueva su lease indefinidamente, o un timeout mal configurado, retiene el puerto aunque otros esperan; una conexión semiabierta lo retiene hasta que vence el lease o se detecta la caída. | Los servidores redundantes no pueden tomar el puerto (failover, mantenimiento, observación de calidad). | Ver [Semáforo de uso](#semáforo-de-uso-varios-servidores-y-varios-puertos): lease y heartbeat fijados por el servidor dentro de rangos que valida el agente, tope opcional de tenencia cuando hay espera y revocación administrativa. Los controladores que operan de forma asíncrona deben liberar el puerto al terminar. |

## Aprovisionamiento seguro

Cuando el servidor requiere TLS, el material se entrega por un canal ya cifrado. Dos formas, ambas soportadas por Mbed TLS 4.1 sin dependencias nuevas:

- **TLS con el certificado autogenerado del agente.** PortGate genera su par de claves al instalarse; el servidor fija la huella en el primer contacto y por ese canal entrega certificado, clave y CA. Protege contra quien escucha; no contra un intermediario activo en ese primer contacto, salvo que la huella se verifique o se pase con `--server-cert`.
- **TLS-PSK.** Sí, sirve: una clave compartida pasada al instalar (`--psk`) autentica a ambos extremos desde el primer contacto, sin certificados. Condiciones: usar el modo PSK con (EC)DHE (TLS 1.3 `psk_dhe_ke` o TLS 1.2 `ECDHE-PSK`) para tener secreto hacia adelante; la PSK debe ser aleatoria de al menos 128 bits, no una contraseña, porque una PSK débil admite ataque de diccionario sobre un handshake capturado; una identidad PSK por agente, y rotación desde el servidor. La PSK puede quedar como autenticación permanente o usarse sólo para entregar los certificados.

## Semáforo de uso: varios servidores y varios puertos

PortGate sirve muchos puertos a un servidor, o varios puertos a varios servidores. Ejemplo: cuatro servidores, tres redundantes y uno de estadística de calidad u observabilidad, conectados a la vez. Por puerto, el árbitro ya implementado:

- concede el puerto a un solo controlador (lease con generación y `transferId`); los demás quedan en cola por prioridad y luego orden de llegada, sin expropiar al dueño;
- libera el puerto cuando el dueño lo suelta (`Release`), cuando cierra la conexión, cuando pasan tres heartbeats sin tráfico o cuando vence el lease sin `Renew`, y lo concede al siguiente en cola;
- vence las esperas en cola según el plazo de cada pedido.

Lo que agrega la administración remota:

- `leaseMs`, `heartbeatMs`, tamaño de cola y prioridades los fija el servidor por puerto; el agente valida rangos (mínimo y máximo) para que un error de configuración no deje el puerto tomado por horas ni lo revoque en cada pausa normal.
- Tope opcional de tenencia `maxHoldMs`: si hay controladores esperando, el dueño no puede renovar más allá de ese tope y recibe `Revoked`. Sin espera, puede seguir renovando.
- `RevokePort` administrativo para mantenimiento o para destrabar un controlador defectuoso.
- La lista de controladores y prioridades incluye a la instancia local (`127.0.0.1`) cuando el servidor quiere permitir failover local.

Observadores (decisión humana del 2026-10-27): un controlador con el derecho de observación sólo lee el flujo bidireccional cuando el acceso del puerto es **compartido** (`shared`, por puerto). En un puerto **exclusivo** (predeterminado) el tráfico es sólo del dueño y el observador ve el semáforo y el estado (`PortState`: dispositivo abierto, dueño, generación, cola, cierre por seguridad, cuarentena) y los eventos de seguridad. Ningún observador afecta el rendimiento del controlador que tomó el puerto: cada conexión tiene su cola y su hilo de escritura, y las copias para un observador atrasado se descartan con hueco visible en la secuencia.

Responsabilidad de los controladores: quien opere de forma asíncrona o con varios destinos debe configurar su lease y liberar el puerto al terminar; PortGate acota el daño, pero no adivina cuándo terminó una tarea.

## Solicitud de cesión, kill switch e impugnación

Decisión humana del 2026-10-26. Asegurar el kill switch es indispensable; el kill switch y su impugnación son **opciones de seguridad** que el servidor habilita por puerto para la empresa que quiera tenerlas a mano. Las reglas de cuándo un corte es legítimo (ventana de mantenimiento, failover) son de la empresa y las aplica el servidor u observador, no PortGate.

### Solicitud de cesión cooperativa (handover request)

- Un controlador conectado que observa el semáforo envía `RequestHandover{portId, motivo}`. PortGate lo reenvía al dueño y lo notifica a los observadores.
- El dueño responde `HandoverAnswer{cede | mantiene}`. Si cede, libera y el puerto pasa por la cola normal (prioridad, luego orden). Si mantiene, el solicitante recibe la negativa.
- Si el dueño no responde, **mantiene**: ceder por silencio permitiría robar el puerto saturando al dueño. Un dueño caído ya pierde el puerto al vencer su lease.
- Límite de pedidos por controlador e intervalo, para que no se interrumpa al dueño en cada operación.

### Kill switch (interrupción forzada)

Requisitos mínimos:

1. Sólo por TLS con autenticación de ambos extremos (certificado de cliente o PSK). Un TLS que sólo cifra no identifica a quien corta. Sin TLS no hay kill switch.
2. Permiso explícito asignado por el servidor administrador; estar en la lista de controladores no alcanza.
3. **Quien corta no se queda con el puerto:** tras el corte, el puerto queda en cuarentena sin dueño durante un plazo configurable (la ventana de impugnación) y luego vuelve a la cola normal. Así cortar no sirve para tomar el puerto.
4. Espera mínima entre cortes del mismo solicitante.
5. Evento `KillSwitch` emitido a todos los controladores y observadores y guardado en el registro: solicitante (identidad TLS y dirección), dueño cortado, puerto, generación, bytes en tránsito o escritura incierta, motivo declarado, hora y plazo de impugnación.

El dueño cortado recibe `Revoked` con código propio; el siguiente dueño recibe la discontinuidad (`PortEvent`) y debe resincronizar la sesión del instrumento.

### Impugnación

- Un observador autorizado (también por TLS y con permiso explícito) envía `Contest{eventId, motivo}` dentro del plazo de impugnación.
- Efecto: PortGate **corta la conexión del solicitante del kill switch** y lo bloquea por un plazo configurable (o hasta que el administrador lo habilite). No restablece la interfaz por sí misma: el puerto sigue en cuarentena y luego en la cola normal, salvo que el impugnante pida cierre por seguridad.
- Cierre por seguridad (`SecureClose`): opcional junto con la impugnación, deja el puerto cerrado para todos. Sólo el servidor administrador lo reabre, no quien lo cerró; así el cierre no se convierte en un modo de dejar el equipo fuera de servicio a voluntad.
- Impugnación y cierre quedan en el registro y se notifican a todos, igual que el kill switch, para que un sistema de seguridad avanzado marque amenazas en su red. Ejemplo: un kill switch fuera de una ventana de mantenimiento o sin failover en curso es altamente impugnable según las reglas de la empresa.

### Alcance de la protección

- Cubre: controlador defectuoso o abusivo que no suelta el puerto, controlador o servidor no autorizado, intruso en la red cuando hay TLS, y el uso del kill switch para robar el puerto (cuarentena, evidencia e impugnación).
- No cubre por sí solo: un servidor autorizado y comprometido que corta repetidamente; queda limitado por la espera entre cortes y expuesto a la impugnación y al bloqueo. Tampoco a instalaciones que operan sin TLS, donde no hay kill switch.

## Configuración local

Una UI de configuración (Diagnostics, o la de EDC) se conecta a `127.0.0.1:28240` con el mismo protocolo y la misma autorización.

## Neutralidad y reparto

- PortGate: agente C++ en `PortGate/`, especificación `spec/link-v1.md` y su extensión de administración, vectores de conformidad.
- ATSM neutral: biblioteca .NET cliente (canal `SerialTunnel` y administración), sin dependencia de un LIS. El plugin aporta lo específico del instrumento.
- Diagnostics: UI local/remota sobre esa biblioteca.
- EDC/LIMS Equipos: aprovisionamiento central (inventario, modos, asignación de instrumentos, versiones/historial, certificados), con su propio Issue en EDC.

## Pendiente de definir

- Windows XP (P-06): SCM existe, pero las notificaciones de dispositivos y parte de SetupAPI difieren; `DeviceEvent` se obtendría por sondeo.

## Pruebas requeridas

Solicitud de cesión (cede, mantiene, sin respuesta, límite de pedidos); kill switch rechazado sin TLS o sin permiso, cuarentena sin ventaja para el solicitante, espera entre cortes, impugnación que corta y bloquea al solicitante, cierre por seguridad reabierto sólo por el administrador y registro persistente de eventos; semáforo: varios controladores conectados, espera y toma tras `Release`, caída de conexión, vencimiento de lease, `maxHoldMs` con y sin espera y `RevokePort`; PSK válida e inválida;  y rechazo de un segundo administrador en modo restringido; arranque con la última configuración sin servidor; modos plano/TLS y servidor/agente; `SetConfig` completo y rechazo de configuraciones inválidas sin cambios parciales; `SetTls` válido, inválido y rechazado por canal plano; `ListDevices`/`DeviceEvent` con dispositivos simulados y cambio de COM; instalación/desinstalación del servicio y reinicio con controladores conectados.

## Estado de implementación (2026-10-26)

Implementado y probado según [spec/admin-v1.md](../spec/admin-v1.md); detalle y pendientes en [CONTINUACION § P-16](CONTINUACION.md). Diferencias respecto del diseño:

- R1: `--server-cert <sha256>` y `--psk <identidad>:<hex>` fijan el servidor al instalar. Con `--server host:puerto` el agente además inicia las conexiones hacia ese servidor (modo iniciado por el agente, implementado el 2026-10-26; ver spec/admin-v1.md § Agent-initiated connections).
- Kill switch e impugnación exigen TLS con par autenticado: certificado de cliente verificado contra la CA del enlace o PSK.
- El éxito de un kill switch o una impugnación se confirma al solicitante con `SecurityResult` (0x1E), además del evento difundido.
- Cambios de dispositivo en caliente (2026-10-26): si una configuración sólo cambia dispositivos (ajustes serie, ruta o endpoint TCP), cada puerto afectado se reabre sin reiniciar Link ni cortar a los demás controladores; el dueño conserva la tenencia y recibe `PORTGATE-PORT-RECONFIGURED`.
- Modo iniciado por el agente (2026-10-26): el agente mantiene N conexiones salientes; el servidor habla primero y asigna a cada una el rol `admin` o `controller`. Con TLS, el agente fija el certificado del servidor (o usa la PSK) y presenta el suyo, que el servidor fija. No abre puertos de entrada para Link.
