# Instrucciones para agentes de IA (ATSM)

ATSM es un repositorio **neutral** (`github.com/kalyx-ar/ATSM`): integración de instrumentos de laboratorio sin dependencia de ningún LIS/LIMS. EDC es el primer anfitrión, pero no se referencia desde aquí. Antes de trabajar, leer [Documentacion/CONTINUACION.md](Documentacion/CONTINUACION.md) (pendientes y forma de retomarlos) y [Documentacion/ESTADO.md](Documentacion/ESTADO.md) (qué existe y qué se verificó).

## Reglas

1. **Neutralidad.** El motor genérico (`dotnet/src`, `native/`) no contiene código para un LIS, un fabricante ni un formato de log particular. Lo específico de un fabricante o serie va en su plugin (`dotnet/plugins/Atsm.Plugin.<Fabricante>.<Serie>`). Lo de otros sistemas (por ejemplo, Glyms) va sólo en plugins de pruebas.
2. **Valores de interfaz con evidencia.** Timers, encuadre, páginas de códigos y límites salen del manual de la marca o serie, o de trazas reales. Se declaran en el perfil del plugin con su cita (`AstmProfile.Evidence`). Lo inferido de trazas sin manual se marca como hipótesis.
3. **Datos reales fuera del repositorio.** `Documentacion/trazas_externas/` y `Documentacion/manuales/` están ignorados: contienen datos de pacientes y documentos con derechos. Sólo se publica corpus anonimizado (`atsm-trace anonymize`, informe limpio y revisión). Nunca imprimir nombres ni documentos de pacientes en salidas, commits o documentos; sólo agregados.
4. **Dependencias.** Las bibliotecas de ejecución usan sólo la BCL de .NET y Mbed TLS (agente nativo). Cualquier otra dependencia requiere aprobación humana, que se registra en [Documentacion/DEPENDENCIAS.md](Documentacion/DEPENDENCIAS.md) con origen, versión, licencia, hash y consumidores. Para actualizar Mbed TLS se cambian la versión y el `URL_HASH` juntos.
5. **Pruebas antes de commitear.** `dotnet test dotnet/Atsm.slnx` y `ctest`, repetidos (hay concurrencia). Una prueba que pasa por timeout no cuenta como prueba: verificar la duración.
6. **Commits.** Mensajes en español, con prefijo convencional (`feat:`, `fix:`, `docs:`, `build:`, `test:`). Un tema por commit. No hacer push ni crear issues o PR sin pedido explícito.
7. **Documentación.** En español. Actualizar `ESTADO.md` y `CONTINUACION.md` al terminar un pendiente; no reescribir decisiones registradas.
8. **Firma.** Strong Name con la SNK de la distribución (EDC: `Kalyx.EDC.snk`, fuera de este repositorio); no generar claves nuevas. Authenticode se aplica en el release.

## Entorno conocido (Windows, sesión de desarrollo)

- .NET SDK 10.0.4xx. CMake 4.5 en `C:\Program Files\CMake\bin` (puede no estar en `PATH`). Visual Studio 18 (2026) Community con MSVC 14.51 (`-G "Visual Studio 18 2026" -A x64`).
- No instalados al cerrar esta etapa: toolset `v141_xp`, distribución WSL, ESP-IDF.
- Python no está disponible; usar .NET, PowerShell o bash.
- El repositorio vive dentro de un checkout de EDC (`EDC/LIMS/ATSM`); `dotnet/Directory.Build.props` y `.targets` aíslan el build de los archivos de EDC.
