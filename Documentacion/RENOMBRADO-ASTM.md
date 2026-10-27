# Nombre ASTM — corrección completa en Alpha

Decisión humana del 2026-10-27, [EDC#205](https://github.com/kalyx-ar/EDC/issues/205#issuecomment-6433861713): corregir el nombre completo a ASTM, sin mantener legacy. La sigla corresponde al estándar ASTM, usado junto a sus denominaciones CLSI LIS01/LIS02.

## Nombres vigentes

| Componente | Nombre |
|---|---|
| Repositorio y carpeta | `kalyx-ar/ASTM`, `LIMS/ASTM` |
| Solución | `dotnet/Astm.slnx` |
| Proyectos, namespaces y paquetes | `Astm.*` |
| GUI MAUI | `dotnet/tools/Astm.Diagnostics.Maui/bin/Release/astm-diagnostics.exe` |
| CLI NativeAOT | `dotnet/tools/Astm.Tools.Trace/bin/Release/Native/astm-trace.exe` |
| MSBuild de distribución | `AstmRequireStrongName`, `AstmStrongNameKeyFile` |
| Adaptador EDC | `Kalyx.Lims.Astm`, `LimsAstmOptions`, contrato `kalyx.lims.astm` |
| Feed local EDC | `Feeds/astm`, paquetes propios 0.3.0 |
| Trazas/casos | `.astmtrace`, `astm-trace/1`, `.astmcase`, `ASTMCASE1` |
| Diagnósticos y eventos | `ASTM-*`, `astm.*` |
| Sistemas de códigos de proveedor | `urn:astm:vendor:*` |

PortGate conserva su nombre: es un transporte neutral. Su paquete 0.3.0 depende de `Astm.Transport` 0.3.0. No se incorporan ni actualizan dependencias de terceros. Se usa la misma SNK de distribución.

## Alcance de Alpha

Se retiran nombres, APIs, paquetes y salidas anteriores. Los lectores aceptan únicamente las firmas actuales; no hay aliases, shims, lectores de transición ni migración automática de traducciones/formatos. La identidad idempotente del adaptador LIMS también utiliza el nombre vigente. Archivos, capturas y bases existentes no se reescriben ni se eliminan; cualquier incidencia con ellos se resuelve por caso, según la decisión humana.

Los changelogs y commits anteriores conservan sus contenidos como evidencia histórica; la carpeta propietaria de nuevos changelogs se llama ASTM. No representan una implementación legacy. Las funciones y baselines protegidos de Identity y la admisión de confianza no se modifican.

## Verificación

Recompilar desde las rutas nuevas; retirar bin/obj y ensamblados generados con el nombre anterior, y sustituir los paquetes del feed. Ejecutar .NET/CTest repetidos, Lang real, pruebas del consumidor EDC, self-tests GUI/NativeAOT, auditoría de referencias y escenario PortGate desde la entrega.

Durante la verificación se detectó una carrera en `PortGateChannel.AcquireAsync`: enviar `ConfirmTransfer` no acreditaba que el agente ya lo hubiese procesado cuando se devolvía el Stream. Se espera una respuesta `Renew` en esa misma conexión ordenada como confirmación de activación; se conserva el protocolo y sus presupuestos. Sin sleeps ni aumento de timeouts. Una ejecución detectó además un cierre intermitente durante handover; las dos suites completas siguientes pasaron (412 correctas/una omitida, 4/3 s). Se conserva esa evidencia, sin ocultar el fallo.

Las cinco advertencias CS8002 conocidas de MAUI oficial sin Strong Name permanecen visibles. Authenticode de la entrega y firma NuGet propias pendientes, diferenciadas del token Strong Name. Los resultados finales y hashes se registran en el changelog del incremento y en #205.
