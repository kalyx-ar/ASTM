# COM virtual de PortGate (EDC #404)

Decisiones humanas vigentes 2026-11-01: legacy sin conocimiento de PortGate; sólo el observador interpreta el protocolo y declara que no hay operaciones. [Issue](https://github.com/kalyx-ar/EDC/issues/404).

## Estado y plataformas

Cierre de la corrección, 2026-11-01: el usuario acepta cerrar #404 como resuelto con los commits ASTM c47e630 y EDC 657c2b85. #408 permanece abierto para firma y validación instalada. Esta decisión posterior sustituye el estado abierto indicado durante la entrega; no convierte las pruebas sintéticas en homologación del driver.

Agente/SDK fuente 0.5, driver UMDF 2.15 experimental 0.2 para Windows 10 1803+/11 x64 (clientes Win32 x86/x64). XP no ofrece VCOM; TTY/PTY POSIX pendiente. El núcleo y los puertos físicos siguen separados por plataforma.

Código/build/pruebas del transporte y arbitraje; sin driver instalado ni firma Authenticode/catálogo firmado. No acredita interoperabilidad de una aplicación legacy. El cierre anterior de #404 fue revocado por el diseño incorrecto. Firma, instalación, Win32/SerialPort/Glyms, PnP y ACL continúan en [#408](https://github.com/kalyx-ar/EDC/issues/408).

## Arquitectura y propiedad

El driver nunca abre el COM físico. Expone la interfaz serial Windows, colas cancelables, timeouts y controles básicos; envía bytes por IPC local al mismo Agent/Arbiter como controlador reservado vcom:COMn. El IPC sólo acepta clientes locales con DACL SYSTEM/LocalService y el worker verifica al propietario del pipe. La identidad reservada no se admite por Link de red.

Se retiraron OperationBoundary, Lis01ResultsBoundary, el perfil de finalización y PortGateVComIoctl.h. No hay API propietaria requerida en el legacy. Los controles serie virtuales no cambian DTR/RTS, baudios o configuración del dispositivo físico.

El servicio sigue LocalService con SID específico. Los comandos vcom list/create/remove/protect mantienen la provisión del ROOT propio y la política física para SYSTEM/SID PortGate. Create requiere paquete firmado y Windows lo valida; remove/protect consultan veto PnP. Sólo se ejecutó list: no se instalaron dispositivos ni se cambiaron servicio, ACL, certificados, confianza o Secure Boot.

## Soft kill switch por observador

El solicitante tiene Acquire pendiente y Observe activo sobre flujo compartido, con los permisos existentes. Declara conocimiento del protocolo, preparación y no_pending_operations junto a época/generación/siguiente secuencia interpretada. Una copia atrasada o con pérdidas desde el inicio de observación se rechaza. Tras resincronizar su protocolo, renueva explícitamente Observe. PortGate no evalúa la interpretación del observador ni crea permisos nuevos.

La configuración de puerto lleva 10 nombre VCOM y 12 guarda en ms: 5000 predeterminados por elección humana, rango 0..600000. Cero desactiva sólo la espera. La antigua clave 11 se rechaza con diagnóstico de reprovisión explícita: no se elimina ni recrea ningún estado automáticamente.

Durante la guarda cualquier RX/TX cancela el intento, sin rearme/reintento automático. No cuentan heartbeats/controles del túnel. El solicitante decide reintentar o pedir hard explícito, con sus reglas existentes; no hay escalamiento automático.

Al concluir la guarda se coordina la admisión de I/O con el árbitro y se pide la pausa al frontend local. Éste marca el endpoint desconectado y completa accesos pendientes mediante la interfaz COM estándar. El acuse interno se envía después de aplicar ese estado en WDF, sin esperar mensaje/CloseHandle del legacy. Después el árbitro transfiere al solicitante exacto, incluso si otro candidato tiene mayor prioridad en la cola. Nunca se inyecta un mensaje ni una señal de desconexión al dispositivo físico.

Una lectura esperando datos no es una operación de protocolo. PortGate tampoco inspecciona ACKs, resultados o incertidumbre semántica; esa decisión pertenece al observador y su riesgo fue aceptado. Los locks/generaciones impiden que el acceso anterior continúe usando el flujo tomado por otro dueño.

Los intentos se correlacionan con un token interno único, no con IDs reutilizables de clientes. Cancelación, pérdida de conexión/observación, cambio de dueño/generación o retiro invalidan el intento. Un fallo o timeout técnico del frontend cancela y rehabilita la tenencia anterior aún vigente. El timeout utiliza la configuración técnica de handover existente, independiente de la guarda del dispositivo.

Al liberar el nuevo dueño y quedar verde el semáforo, el VCOM está disponible. Un handle legacy todavía abierto solicita tenencia nueva y restaura I/O; sin aplicación abierta no se reserva. No hay restauración de la sesión del protocolo ni replay de bytes entre generaciones. Los últimos bytes del receptor anterior permanecen en su buffer mientras está desconectado y se limpian al abrir la generación siguiente.

PrepareRetirement/RetirementNotice son avisos cooperativos administrativos para dueños compatibles de red o cierre normal. El frontend virtual los ignora: no son la solicitud soft del observador ni prueban fin de una operación.

## SDK y pruebas

PortGateChannel ofrece ObserveAsync, CursorAfter y SoftKillSwitchAsync mientras AcquireAsync está pendiente. Las tres declaraciones son explícitas y el cursor debe representar lo ya interpretado. Cancelar la llamada pide cancelar el intento; no revierte una transferencia que ya se haya aplicado. Capabilities anuncia soporte para diagnosticar agentes anteriores.

Las pruebas nativas usan dispositivo TCP simulado y un sustituto del frontend local, sin modificar la admisión de identidades del producto. Comprueban declaraciones, frescura/pérdidas, guarda opcional, RX/TX, tokens atrasados y entrega exacta. La máquina del VCOM comprueba bytes binarios, pausa/errores y disponibilidad posterior sin interpretación. No equivale a prueba del driver instalado.

Verificación 2026-11-01: build Release del agente y driver correctos. CTest repetido: 855 verificaciones del agente y 33 del transporte VCOM, cero fallos, 7,53 y 7,51 s. `dotnet test dotnet/Astm.slnx --no-restore -m:1 -v quiet` repetido: 424 correctas, una omitida, cero fallos, 13 s por ejecución. La omitida requiere una traza privada seleccionada expresamente; no se usa información clínica real para esta validación. InfVerif `/w` correcto e Inf2Cat sin errores/advertencias; generan un catálogo **sin firma**. Hash de la DLL y procedencia del kit en DEPENDENCIAS.

## Build y prueba pendiente

Agente y pruebas: comandos vigentes de CONTINUACION. Driver separado, con WDK Microsoft verificado y SDK correspondiente instalado:

```powershell
cmake -S PortGate/driver/windows -B PortGate/build/vcom-26100 -G "Visual Studio 18 2026" -A "x64,version=10.0.26100.0" "-DPORTGATE_WDK_ROOT=H:/Github/EDC/LIMS/ASTM/PortGate/build/wdk/Microsoft.Windows.WDK.26100"
cmake --build PortGate/build/vcom-26100 --config Release
```

La DLL y el INF quedan juntos en Release. La generación/firma del catálogo y la firma del driver siguen el procedimiento de publicación aprobado; este build no genera certificados ni modifica confianza. Herramientas/licencias/hashes en DEPENDENCIAS. Strong Name del SDK .NET no sustituye a la firma del driver.

Aceptación pendiente en Windows de prueba: instalar el paquete firmado, crear/quitar el dispositivo propio, comprobar veto con handles abiertos y política del COM real, abrir con cliente Win32 y SerialPort, fragmentación/lectura/escritura/cancelación/timeouts/reapertura, contienda con Link, soft por observador, desconexión y disponibilidad posterior sin cruces entre generaciones, pérdida de agente y Driver Verifier/WDF. No instalar en una estación de trabajo ni cambiar Secure Boot/test signing para hacer pasar esa verificación.

Evaluación completa del ecosistema EDC y ámbito de la consola: `EDC/Documentacion/Modulos/Ports/com-virtual-404.md`. No hay datos clínicos persistidos por este backend ni nuevas dependencias de terceros.
