# Presentación MAUI, publicación autónoma y NativeAOT

Decisión humana del 2026-10-25, [EDC#205](https://github.com/kalyx-ar/EDC/issues/205#issuecomment-6395906842): Diagnostics debe usar MAUI como EDC. WPF fue una elección del agente sin preferencia expresa del proyecto y se retira. La carga en caliente corresponde sólo a EDC; ASTM y Diagnostics componen los plugins en compilación.

## Fallo reproducido

El EXE de la primera entrega terminó con `FileNotFoundException`: `Astm.Diagnostics.Core.dll` de la publicación seguía referenciando el ensamblado anterior al renombrado legacy (`0e0c4c7`). En esa carpeta faltaba la DLL esperada. El equipo sí tiene .NET 10.0.13 y Windows Desktop Runtime; el error no era evidencia de runtime ausente. El render WPF fuera de pantalla realizado antes del renombrado no validaba la publicación incoherente posterior.

La migración conserva el núcleo neutral, catálogo, archivos/logs, vistas de trama/mensaje/observación/QC, TCP/TLS/SerialTunnel, cancelación y custodia antes de conectar. Los archivos y el HWND de los diálogos se resuelven por `IDiagnosticPlatformServices`; la página utiliza controles MAUI sin WPF ni referencias a EDC. Windows es el anfitrión verificado de esta entrega. Otros sistemas requieren su entrada de plataforma y adaptador de archivos/custodia; adoptar MAUI no acredita por sí solo pruebas Android/iOS/macOS/Linux.

## Artefactos y compilación

| Componente | Entrega / estado |
|---|---|
| Agente serial | C++ nativo existente; no requiere .NET. Plataformas verificadas según ESTADO. |
| Núcleo/plugins ASTM | Bibliotecas administradas neutrales para EDC u otro anfitrión; pueden compilarse dentro de ejecutables NativeAOT de catálogo estático. No se sustituye su API CLR por una DLL nativa de ABI incompatible. |
| `astm-trace` | Perfil `NativeAot`, Windows x64, compilación nativa real y prueba de catálogo, CFX/QC, JSON, custodia cifrada y procesador ASTM/anfitrión. Sin runtime externo ni carga dinámica. No acredita cada API/canal en todos los destinos AOT. |
| Diagnostics MAUI Windows | .NET 10 self-contained + Windows App SDK self-contained; ReadyToRun de MAUI Release. No requiere instalar .NET/Windows App SDK aparte y **no se declara NativeAOT**. |
| Anfitrión EDC | Conserva carga/admisión dinámica, identidad, almacenamiento y sus decisiones propias. |

La ausencia de carga dinámica favorece NativeAOT, pero también importa la compatibilidad del framework, reflexión, serialización y plataforma. Microsoft documenta [MAUI .NET 10 por plataforma](https://learn.microsoft.com/en-us/dotnet/maui/deployment/runtimes-compilation?view=net-maui-10.0) y [NativeAOT de MAUI iOS/Mac Catalyst](https://learn.microsoft.com/en-us/dotnet/maui/deployment/nativeaot?view=net-maui-10.0). Windows .NET 10 usa CoreCLR/ReadyToRun; no se migró a una versión preview del SDK para cambiar ese límite. Ver también [limitaciones generales de NativeAOT](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/).

Se reemplazó la serialización por reflexión por metadatos generados: `AstmJsonContext`, `DiagnosticCaseJsonContext` y los contextos CLI. `TraceJson.Options` conserva camelCase, omisión de nulos y enums numéricos anteriores; el informe de anonimización conserva PascalCase. Casos DPAPI previos mantienen formato/proveedor. El perfil AOT y la GUI deshabilitan el fallback JSON por reflexión. No se agrega un resolver permisivo de tipos ni una lista de confianza.

Desde la raíz ASTM:

```powershell
dotnet publish dotnet/tools/Astm.Diagnostics.Maui/Astm.Diagnostics.Maui.csproj -c Release -p:AstmRequireStrongName=true
```

Entrega directa: dotnet/tools/Astm.Diagnostics.Maui/bin/Release/astm-diagnostics.exe y sus dependencias. No se generan comprimidos ni se mantiene un script para mover/comprimir. La verificación usa los comandos self-test y verify-publication sobre la salida real. PublicationAudit inspecciona metadatos PE sin cargar código; la prueba propia comprueba CLR/WinUI privados y casos protegidos.

Prueba de publicación: la aplicación crea la ventana MAUI y su handler, valida HWND propietario de diálogos, comprueba que usa .NET y WinUI privados, abre el catálogo, interpreta datos CFX/QC sintéticos y guarda/reabre un caso cifrado DPAPI. Informa sólo PASS/FAIL y nombres de pruebas; no vuelca datos sensibles. El control visual asistido de Windows agotó su autorización: no se afirma inspección manual de los diálogos ni del diseño. En errores de inicio la aplicación muestra un código seguro en un diálogo nativo, sin generar fallback de logs planos.

CLI nativa:

```powershell
dotnet publish dotnet/tools/Astm.Tools.Trace/Astm.Tools.Trace.csproj -c Release -r win-x64 -p:PublishProfile=NativeAot -p:AstmRequireStrongName=true
```

Resultado en `dotnet/tools/Astm.Tools.Trace/bin/Release/Native/astm-trace.exe`. `self-test` ejercita contenido sintético en memoria. `verify-publication <directorio>` detecta referencias propias faltantes, incluida la publicación anterior rota. No se interpreta el tamaño del EXE como prueba de AOT: se verifica la compilación nativa y se ejecuta el artefacto.

## Firma y alcance de verificación

Las 37 dependencias NuGet oficiales Microsoft de MAUI se verificaron con `dotnet nuget verify --all`; versiones, autores, licencias, repositorios y hashes están en [diagnostics-maui-dependencies.json](diagnostics-maui-dependencies.json). MAUI 10.0.110 coincide con la versión usada por EDC. No se incorporan Syncfusion, toolkits ni paquetes de terceros.

**Advertencia visible:** Microsoft.Maui.Controls, Microsoft.Maui, Microsoft.Maui.Graphics, Microsoft.Maui.Essentials y Microsoft.Maui.Controls.Xaml se distribuyen sin Strong Name. Su firma NuGet no es un Strong Name de DLL ni Authenticode. `CS8002` se mantiene visible, únicamente fuera de errores en el EXE MAUI; no se utiliza `NoWarn`. Los ensamblados propios conservan la SNK/token de distribución; el ejecutable NativeAOT es nativo y no tiene un Strong Name CLR. Authenticode propio sigue pendiente de release. Registro previo de esta condición: [comentario de EDC#205](https://github.com/kalyx-ar/EDC/issues/205#issuecomment-6395998752).

No se modificaron claves/derivaciones de Identidades, SNK, admisión de editores/hashes ni baselines de EDC. La nueva interfaz conserva la responsabilidad de custodia de la aplicación y no añade spool plano, filedrop ni reenvío de escrituras inciertas. Pruebas y commits finales en ESTADO/changelog; sin homologación adicional de instrumentos físicos.

Ampliación posterior de uso/idiomas: [USABILIDAD-DIAGNOSTICS](USABILIDAD-DIAGNOSTICS.md). Se inspeccionó un candidato de la ventana por Computer Use y accesibilidad; el usuario detuvo el control con Esc antes del arrastre. Los ajustes siguientes se validaron por la prueba interna de ventana, handlers y alineación, sin afirmar inspección manual del diseño final. La referencia directa a Essentials para el tema claro produce una quinta CS8002 visible; procede del mismo MAUI oficial ya inventariado.
