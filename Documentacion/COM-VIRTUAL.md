# COM virtual de PortGate (EDC #404)

Decisión humana 2026-11-01: el legacy usa un COM convencional y sólo PortGate posee el COM físico. Aviso de próximo apagado/retiro fuera del protocolo del equipo; cesión diferida sólo al finalizar una operación confirmada. [Seguimiento](https://github.com/kalyx-ar/EDC/issues/404).

## Estado y plataformas

Agente/SDK PortGate 0.4; driver propio UMDF 2.15 experimental 0.1 para Windows 10 1803+/11 x64. Admite clientes Win32 de 32/64 bits. No se instala un driver al arrancar PortGate. **XP no ofrece COM virtual**; una futura evaluación puede excluirlo definitivamente. El núcleo C++17 sigue separado del backend y conserva sus puertos físicos Win32/POSIX. El frontend virtual POSIX aún no está implementado y devuelve indisponibilidad explícita.

Build del driver e INF verificados, más pruebas del protocolo/arbitraje; todavía **sin firma Authenticode/catálogo firmado, instalación ni ensayo CreateFile/SerialPort sobre el dispositivo virtual**. No se presenta como una integración homologada. Por decisión humana se cierra la creación experimental EDC #404 y se traslada la validación instalada/mejora continua a [EDC #408](https://github.com/kalyx-ar/EDC/issues/408). No se modificaron dispositivos, servicio, ACL ni seguridad de arranque de la estación de desarrollo.

## Arquitectura

`driver/windows`: FDO UMDF, interfaz COM, nombre asignado por PortsClassInstaller y registro DEVICEMAP propio. Colas WDF cancelables y timeouts, buffers de sesión, lectura/escritura y controles básicos. Los IOCTL de hardware no implementados fallan. Las líneas/baudios virtuales no reconfiguran el dispositivo físico; éste sigue bajo la configuración del agente.

`VirtualComProtocol`: negociación Link, generación/confirmación, renovación, fragmentación de escritura y sus resultados; discontinuidad y revocación. El driver nunca abre el COM físico ni crea otro árbitro. `platform/win32/virtual_com_win32.cpp` conecta el frontend local al mismo `Agent` y `Arbiter`; la identidad `vcom:COMn` y el índice del puerto quedan vinculados y no se admiten por conexiones de red.

IPC local, DACL SYSTEM/LocalService y comprobación del propietario del pipe. El servicio existente sigue bajo LocalService. La provisión habilita su SID específico y permite proteger el COM físico con ese SID, en vez de concederlo a toda aplicación que comparta la cuenta. Administradores/SYSTEM siguen administrando Windows.

`app/vcom_setup_win32.cpp`: `portgate vcom list`, `create --inf <paquete firmado>`, `remove --instance <ROOT id>` y `protect --physical <COM>`. Create usa exclusivamente el hardware ID ROOT de PortGate y Windows valida el paquete; no acepta certificados ni desactiva comprobaciones. Remove se limita a dispositivos propios; remove/protect consultan veto PnP. Protect requiere poner el dispositivo en servicio sin handles activos, guarda la política para SYSTEM/SID PortGate y reenumera. **Create/remove/protect y el cambio de SID no se ejecutaron: requieren pruebas en destino.**

Primero instalar/provisionar el driver firmado y servicio, proteger el COM físico y anotar el COM virtual asignado; luego configurar sus claves 10/11 y el controlador. La política persistente protege también con el agente detenido. El handle físico exclusivo mantiene la propiedad durante el funcionamiento.

## Retiro ordenado

Administración `PrepareRetirement` 0x4D: puerto, motivo, retirar/ceder. El agente avisa al dueño por `RetirementNotice` 0x26. Retirar niega cola/adquisiciones hasta Reopen; ceder permite la cola normal tras finalizar. No revoca por silencio ni por duración de la operación; las reglas explícitas de lease/perdida de conexión y killswitch duro siguen separadas.

La aplicación compatible puede usar `PortGateVComIoctl.h`: STATE/WAIT_STATE y COMPLETE_TASK con identificador opaco del aviso. El perfil manual también termina por CloseHandle. Un legacy sin esa API obtiene desconexión/error de I/O cuando se confirma el fin; los últimos datos recibidos quedan disponibles para lectura antes del error. No se insertan avisos en el stream del equipo.

`protocols/Lis01ResultsBoundary` es opt-in `lis01-results-batch`: un lote del instrumento, ENQ/ACK, frames con número/checksum correctos, registros H/P/O/R/C/L, R presente, L normal `L|1|N`, ACK explícitos y EOT. No consulta ni responde al equipo. Registros desconocidos, consultas/listas sin R, errores o ACK ausentes no dan prueba de finalización. Un lote no equivale al proceso completo de una consulta multi-turno: para eso hace falta un perfil con la evidencia del equipo/ciclo concreto. Manual y otros perfiles no se autodetectan por marca.

La barrera `RetireAtBoundary` compara generación, offset/secuencia RX y versión TX. Espera escrituras confirmadas y se coordina con el mutex de escritura y el árbitro. Tráfico nuevo rechaza la cesión; las escrituras concurrentes esperan la respuesta, y no se repiten al cambiar de tenencia. El agente libera con el árbitro existente y el frontend marca desconectado. Libera la tenencia, sin apagar físicamente el equipo.

## Build y prueba pendiente

Agente y pruebas: comandos vigentes de CONTINUACION. Driver separado, con WDK Microsoft verificado y SDK correspondiente instalado:

```powershell
cmake -S PortGate/driver/windows -B PortGate/build/vcom-26100 -G "Visual Studio 18 2026" -A "x64,version=10.0.26100.0" "-DPORTGATE_WDK_ROOT=H:/Github/EDC/LIMS/ASTM/PortGate/build/wdk/Microsoft.Windows.WDK.26100"
cmake --build PortGate/build/vcom-26100 --config Release
```

La DLL y el INF quedan juntos en Release. La generación/firma del catálogo y la firma del driver siguen el procedimiento de publicación aprobado; este build no genera certificados ni modifica confianza. Herramientas/licencias/hashes en DEPENDENCIAS. Strong Name del SDK .NET no sustituye a la firma del driver.

Aceptación pendiente en Windows de prueba: instalar el paquete firmado, crear/quitar el dispositivo propio, comprobar veto con handles abiertos y política del COM real, abrir con cliente Win32 y SerialPort, fragmentación/lectura/escritura/cancelación/timeouts/reapertura, contienda con Link, aviso y cesión sin cruces entre sesiones, pérdida de agente y Driver Verifier/WDF. No instalar en una estación de trabajo ni cambiar Secure Boot/test signing para hacer pasar esa verificación.

Evaluación completa del ecosistema EDC y ámbito de la consola: `EDC/Documentacion/Modulos/Ports/com-virtual-404.md`. No hay datos clínicos persistidos por este backend ni nuevas dependencias de terceros.
