# Diagnóstico de mantenimiento y catálogo de instrumentos

Entrega del 2026-10-24, trazada en [kalyx-ar/EDC#205](https://github.com/kalyx-ar/EDC/issues/205). Decisión humana: aplicación gráfica Windows independiente de EDC; prioridad Bio-Rad CFX, ampliar Sysmex y luego QWALYS, favoreciendo equipos con evidencia de presencia en Argentina. No cambia las derivaciones de Identidades ni la decisión de hospedar las trazas EDC en Application/SQLCipher.

## Aplicación Windows

`dotnet/tools/Atsm.Diagnostics.Windows` es una aplicación WPF .NET 10. `Atsm.Diagnostics.Core` compone el catálogo y el anfitrión de diagnóstico; ninguno depende de EDC. Los plugins siguen separados del motor genérico. Se distribuyen 12 perfiles integrados: no se admiten DLL arbitrarias desde una carpeta.

Compilar/publicar desde la raíz ATSM:

```powershell
dotnet publish dotnet/tools/Atsm.Diagnostics.Windows/Atsm.Diagnostics.Windows.csproj -c Release --no-restore -p:AtsmRequireStrongName=true
```

El ejecutable es `dotnet/tools/Atsm.Diagnostics.Windows/bin/Release/net10.0-windows/publish/atsm-diagnostics.exe`. Conservar **toda la carpeta publish**, no sólo el EXE. Requiere Windows con .NET 10 Windows Desktop Runtime; este primer paquete depende del framework instalado. No es la herramienta para Windows XP (esa plataforma corresponde al agente nativo pendiente de verificación). Strong Name usa la SNK de distribución, sin incluirla; Authenticode y el instalador corresponden al release.

Uso:

1. Elegir el perfil del instrumento y el tipo de entrada; abrir una exportación, mensaje completo, bytes de cable, log Sysmex/Glyms o traza ATSM. `Ejemplo CFX` permite probar con datos sintéticos (`--demo` también).
2. Inspeccionar trama, mensajes, observaciones, hallazgos y eventos. Seleccionar una fila muestra su detalle. El catálogo indica evidencia y alcance; un perfil genérico no acredita compatibilidad de todos los equipos ASTM.
3. Guardar un caso `.atsmcase` si se necesita conservar la captura. Los originales permanecen intactos; abrir un archivo no activa una interfaz ni publica resultados.
4. Para captura en vivo, seleccionar TCP cliente/servidor o SerialTunnel, endpoint, identidad y TLS según la configuración real. El servidor escucha en una IP local literal. Un servidor TLS exige PFX; mTLS cliente puede utilizar PFX, con clave efímera y contraseña sólo en memoria. La validación TLS usa la plataforma, sin bypass ni degradación automática.
5. Elegir el archivo de caso antes de conectar. `Capturar sin responder` es el modo predeterminado. `Simular host: ACK, sin órdenes` activa la sesión ASTM/HL7; la aplicación toma custodia durable de lotes de diagnóstico, responde consultas soportadas sin órdenes y nunca envía pedidos a un LIMS.
6. Detener para cerrar y sellar el caso. Se puede reabrir con el mismo usuario Windows. El cierre durante una captura solicita cancelación y espera su finalización.

La captura abre una conexión propia: **no es un sniffer de la comunicación EDC/instrumento existente**. SerialTunnel adquiere control exclusivo aun en captura sin respuestas. Observadores remotos del agente son P-09 y no están implementados. Para RS232 se usa el agente nativo; el puerto serial local administrado .NET sigue pendiente (P-10).

Para HL7 activo se deben ingresar los plazos configurados en el equipo, en segundos; no se inventan valores del fabricante. El perfil disponible es ARCHITECT ZOS con ACK original, distinto de un actor LAW completo. Las fechas del instrumento se interpretan con la zona local de la estación; los logs permiten ingresar su offset UTC (predeterminado `-03:00`). Elegir un log de un solo instrumento/dialecto; no se autodetecta una marca desde cualquier texto.

## Custodia y límites

Incremento QC: pestaña **Calidad** con mediciones, estadísticas reportadas, ámbito y campo fuente; detalle con lote/módulo/calibración y referencias externas sin abrirlas. Perfil adicional CFX CSV de curva estándar, con columnas `Target` (o `Fluor`), `Efficiency %`, `Slope`, `Y-intercept`, `R^2`. Matriz de evidencia, puertos y límites en [CALIDAD-Y-CALIBRACION](CALIDAD-Y-CALIBRACION.md). No aplica reglas de aceptación QC ni calibra el equipo.

La **aplicación** proporciona `DiagnosticTraceSink` y comprueba el almacén antes de abrir la interfaz. En esta herramienta independiente se usa DPAPI `CurrentUser` de Windows, por registros autenticados y encadenados, con identidad y secuencia de caso. No hay fallback plano, claves propias ni spool de SerialTunnel. El proveedor está fuera del motor; no introduce una dependencia a Windows en las bibliotecas neutrales.

Cada registro se vuelca a disco antes de aceptar custodia. Se guardan los bytes de comunicación y, en simulación de host, los lotes canónicos custodiados. Los casos protegidos no son portables entre usuarios/equipos por diseño; no sustituyen el SQLCipher estándar que debe implementar Application de EDC ni constituyen un mecanismo de recuperación/reenvío del instrumento.

Un corte al final de un registro permite recuperar los registros previos completos y marca el caso incompleto. Un fallo criptográfico, reordenamiento o cadena rota se rechaza; falta de cierre, error de captura o límite alcanzado quedan visibles. Guardar nuevamente un caso incompleto conserva esa condición. Un caso sellado sólo acredita el cierre de su captura: no garantiza que el instrumento haya completado su protocolo ni que los resultados estén validados clínicamente.

Entrada/captura: máximo 32 MiB y 100.000 entradas; CFX: 16 MiB de contenido/XML y 64 miembros ZIP. La UI muestra las últimas 2.000 filas y hasta 64 KiB del detalle seleccionado; el caso conserva la captura admitida. No es un monitor continuo de producción sin rotación. Los metadatos del caso guardan limitaciones del origen y líneas omitidas por adaptadores; los errores de enlace y los mensajes incompletos aparecen entre los hallazgos.

## Repertorio y evidencia

| Plugin/perfil | Entrada y alcance implementado | Evidencia y límites |
|---|---|---|
| Bio-Rad CFX Maestro: CSV | Cuantificación exportada, columnas predeterminadas `Well,Sample,Target,Cq`, UTF-8, coma y decimal con punto; comillas/escapes | Manual 2.3 10000126764B, pp.189–194 y tabla 19. En la API `CfxCsvOptions` permite otros nombres, delimitador y cultura explícitos; la GUI usa el formato predeterminado. No se adivinan separadores/decimales. |
| Bio-Rad CFX Maestro: RDML | XML o ZIP con `rdml_data.xml` (o un único XML), versiones 1.0/1.1; muestra/target/reacción/Cq y exclusiones | Subconjunto de cuantificación, no validador completo de esquema ni lector de curvas. Referencias desconocidas/versiones no soportadas se rechazan. DTD/entidades externas deshabilitadas. |
| Sysmex XN-L | E1381-02/E1394-97, recepción; identificador de prueba en R.3.5, dilución R.3.6, muestra O.4.3 u O.3.3 | Guía LIS 1273-MKT rev.3 local. XN-350/450/550 como familia de interfaz, pendiente corpus/equipo. R.9 es juicio del instrumento/repetición; no se convierte en validación clínica. No habilita automáticamente toda la serie XN. |
| Sysmex CS-2500 | E1381-02/E1394-97, recepción; muestra O.4.3, código/nombre/dilución/tipo en R.3; flags crudos | Host Interface Specifications rev.2.04 local. Sin consultas/órdenes, descarga de imágenes ni acceso a rutas del instrumento. |
| Sysmex U-WAM | Los dos perfiles anteriores, con/sin analizador químico; incluye UF-5000 y UC-3500/UC-1000 a través del middleware | Manual R315168 rev.22 y trazas locales; no equivale a conexión directa universal de esos analizadores. |
| DIAGAST QWALYS 3 | Recepción ASTM ASCII; muestra O.4, reacción R.4.7, identidad R.3, comentarios C.3 | Connection Manual local. Estados R/M se preservan como automatizado/modificado; R.5 es soporte, no unidad de medida. Sin órdenes, interpretación clínica de grupos ni manejo de archivos. Fecha de modificación preservada cruda. |
| Abbott ARCHITECT | ASI ASTM y HL7/MLLP original ZOS | ASTM con manual/trazas; HL7 con manual y pruebas sintéticas. LAW completo pendiente. |
| Abbott CELL-DYN Ruby | ASTM derivado de trazas | Hipótesis de encuadre/respuesta pendientes de manual. |
| Estándar LIS02 y adaptadores de logs | Perfil genérico, Sysmex port monitor y Glyms sólo como fuente de pruebas/diagnóstico | No prueban compatibilidad de otro modelo por sí solos. |

CFX soporta **exportaciones de Maestro**, sin recalcular Cq ni asignar positividad. El manual de [Bio-Rad CFX Maestro 2.3](https://www.bio-rad.com/sites/default/files/webroot/web/pdf/lsr/literature/10000126764.pdf) enumera CFX96/384 Touch, Connect, Opus 96/384/Deepwell y Duet; compartir exportación no acredita control directo ni homologación de cada hardware. `.pcrd`, otros formatos propietarios, versiones RDML distintas y software Dx específico quedan fuera. Cq no disponible, valores originales, exclusiones y falta de identificador de muestra permanecen visibles; un pozo no se transforma en muestra clínica por inferencia.

El contrato `IInstrumentContentPlugin` recibe bytes y perfil, devuelve observaciones y diagnósticos, y nunca abre archivos. Los CSV inválidos no devuelven un lote parcial. Los perfiles nuevos ASTM son de **recepción**: consultas no soportadas generan aviso y no fabrican respuestas; no habilitar esa función en el instrumento durante su prueba. Ninguno de los perfiles nuevos está homologado en hardware.

## Prioridad Argentina y ampliaciones

Consulta del 2026-10-24: [IMEX/CONICET](https://imex.conicet.gov.ar/biologia_molecular_celular/) identifica CFX Connect 96 entre sus equipos instalados. El catálogo propio del distribuidor argentino [Müller](https://www.distribuidoramuller.com.ar/equipos.php?id_marca=2) ofrece XN-1000 y XN-L 350/450/550. Son evidencias de presencia/oferta local para priorizar; no prueban stock, habilitación actual ni equivalencia de sus interfaces.

Secuencia de ampliación: validar exportaciones reales CFX y los tres XN-L; completar consultas/QC/extensiones sólo con evidencia; incorporar XN-1000/XN y revisar el manual local SUIT como trabajos por interfaz, no copiando nombres al catálogo. CS-2500 ya tiene un primer perfil de recepción. Después QWALYS según la prioridad humana; su presencia argentina no está confirmada por esta revisión. Otros Bio-Rad requieren identificar modelo, software y exportación concretos antes de declarar compatibilidad.

Los manuales/corpus locales no se copian al repositorio ni al ejecutable. Las pruebas nuevas son sintéticas. Para declarar un plugin de producción faltan P-03: corpus anonimizado y revisado, resultado esperado por modelo/firmware y pruebas con equipo de consultas, repetición, QC, desconexión y recuperación.

## Archivos, integración EDC y siguientes pasos

CFX usa exportaciones y QWALYS contempla intercambio por archivos **append**: son ejemplos reales de interfaces basadas en ficheros. El Scheduler de EDC detecta novedades; el ETL toma custodia, verifica que la escritura terminó, conserva identidad/orden y entrega contenido completo al plugin/procesador. Ordenar sólo por creación no asegura integridad ni idempotencia ante cambios manuales, copias o escrituras todavía abiertas. En append hace falta cursor/checkpoint por archivo y mensajes completos; un evento no equivale necesariamente a un fichero nuevo ni a un único mensaje. ATSM no añade un filedrop.

La GUI es un anfitrión de mantenimiento. Sigue pendiente integrar ATSM en EDC (P-13: `ILabWorkflowHost`, Scheduler/ETL, SQLCipher, permisos/UI), completar LAW (P-02), outbox durable (P-04), carga/admisión dinámica de plugins (P-05), observadores/captura remota (P-09), LocalSerial (P-10) y distribución firmada/CI (P-14). El journal de diagnóstico no resuelve la outbox ni habilita reenvío de escrituras inciertas. Mantener SerialTunnel sin spool de disco; ver [la decisión de recuperación](MENSAJES-TRAZAS-HL7.md).

Validación de esta entrega: suite .NET con casos CSV/RDML, posiciones/estados de fabricantes, custodia antes de aceptación, simulación ASTM completa, reapertura/reanálisis, corrupción, reordenamiento y cortes del journal; pruebas nativas de regresión; build WPF, prueba DPAPI del usuario y render sintético inspeccionado. Los conteos finales y commits se registran en ESTADO y en un changelog nuevo.
