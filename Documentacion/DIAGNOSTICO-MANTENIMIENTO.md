# Diagnóstico de mantenimiento y catálogo de instrumentos

Entrega del 2026-10-24, trazada en [kalyx-ar/EDC#205](https://github.com/kalyx-ar/EDC/issues/205). Decisión humana: aplicación gráfica Windows independiente de EDC; prioridad Bio-Rad CFX, ampliar Sysmex y luego QWALYS, favoreciendo equipos con evidencia de presencia en Argentina. No cambia las derivaciones de Identidades ni la decisión de hospedar las trazas EDC en Application/SQLCipher.

## Aplicación Windows

`dotnet/tools/Atsm.Diagnostics.Maui` es la presentación MAUI vigente desde el 2026-10-25, alineada con EDC; WPF está retirado. Núcleo/catálogo en `Atsm.Diagnostics.Core`, sin dependencia de EDC. Trece perfiles compilados, sin carga en caliente.

Compilar con dotnet publish dotnet/tools/Atsm.Diagnostics.Maui/Atsm.Diagnostics.Maui.csproj -c Release -p:AtsmRequireStrongName=true. Ejecutable: dotnet/tools/Atsm.Diagnostics.Maui/bin/Release/atsm-diagnostics.exe. Conservar sus dependencias de la misma carpeta; incluye .NET y Windows App SDK. No se generan ZIP ni se usa un publicador PowerShell. MAUI Windows .NET 10 es self-contained/ReadyToRun; NativeAOT corresponde a la CLI.

Uso:

1. Elegir el perfil del instrumento y el tipo de entrada; abrir una exportación, mensaje completo, bytes de cable, log Sysmex/legacy o traza ATSM. `Ejemplo CFX` permite probar con datos sintéticos (`--demo` también).
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

Entrada/captura: máximo 32 MiB y 100.000 entradas; CFX: 16 MiB de contenido/XML y 64 miembros ZIP. La UI agrupa y pagina 300 filas visibles, conservando todos los registros admitidos y hasta 64 KiB del detalle seleccionado; el caso conserva la captura admitida. No es un monitor continuo de producción sin rotación. Los metadatos del caso guardan limitaciones del origen y líneas omitidas por adaptadores; los errores de enlace y los mensajes incompletos aparecen entre los hallazgos.

## Repertorio y evidencia

| Plugin/perfil | Entrada y alcance implementado | Evidencia y límites |
|---|---|---|
| Bio-Rad CFX Maestro: CSV | Cuantificación exportada, columnas predeterminadas `Well,Sample,Target,Cq`, UTF-8, coma y decimal con punto; comillas/escapes | Manual 2.3 10000126764B, pp.189–194 y tabla 19. En la API `CfxCsvOptions` permite otros nombres, delimitador y cultura explícitos; la GUI usa el formato predeterminado. No se adivinan separadores/decimales. |
| Bio-Rad CFX Maestro: RDML | XML o ZIP con `rdml_data.xml` (o un único XML), versiones 1.0/1.1; muestra/target/reacción/Cq y exclusiones | Subconjunto de cuantificación, no validador completo de esquema ni lector de curvas. Referencias desconocidas/versiones no soportadas se rechazan. DTD/entidades externas deshabilitadas. |
| Sysmex XN-L | E1381-02/E1394-97, recepción; identificador de prueba en R.3.5, dilución R.3.6, muestra O.4.3 u O.3.3 | Guía LIS 1273-MKT rev.3 local. XN-350/450/550 como familia de interfaz, pendiente corpus/equipo. R.9 es juicio del instrumento/repetición; no se convierte en validación clínica. No habilita automáticamente toda la serie XN. |
| Sysmex CS-2500 | E1381-02/E1394-97, recepción; muestra O.4.3, código/nombre/dilución/tipo en R.3; flags crudos | Host Interface Specifications rev.2.04 local. Sin consultas/órdenes, descarga de imágenes ni acceso a rutas del instrumento. |
| Sysmex U-WAM | Dos perfiles ASTM, con/sin analizador químico; UF-3000/3000x/4000/5000, UC-1000/3500 y UD-10 a través del middleware; UC-3500 directo con su intérprete serial | Manual R315168 rev.22 y trazas locales; no equivale a conexión directa universal de esos analizadores. |
| DIAGAST QWALYS 3 | Recepción ASTM ASCII; muestra O.4, reacción R.4.7, identidad R.3, comentarios C.3 | Connection Manual local. Estados R/M se preservan como automatizado/modificado; R.5 es soporte, no unidad de medida. Sin órdenes, interpretación clínica de grupos ni manejo de archivos. Fecha de modificación preservada cruda. |
| Abbott ARCHITECT | ASI ASTM y HL7/MLLP original ZOS | ASTM con manual/trazas; HL7 con manual y pruebas sintéticas. LAW completo pendiente. |
| Abbott CELL-DYN Ruby | ASTM derivado de trazas | Hipótesis de encuadre/respuesta pendientes de manual. |
| Estándar LIS02 y adaptadores de logs | Perfil genérico, Sysmex port monitor y el legacy sólo como fuente de pruebas/diagnóstico | No prueban compatibilidad de otro modelo por sí solos. |

CFX soporta **exportaciones de Maestro**, sin recalcular Cq ni asignar positividad. El manual de [Bio-Rad CFX Maestro 2.3](https://www.bio-rad.com/sites/default/files/webroot/web/pdf/lsr/literature/10000126764.pdf) enumera CFX96/384 Touch, Connect, Opus 96/384/Deepwell y Duet; compartir exportación no acredita control directo ni homologación de cada hardware. `.pcrd`, otros formatos propietarios, versiones RDML distintas y software Dx específico quedan fuera. Cq no disponible, valores originales, exclusiones y falta de identificador de muestra permanecen visibles; un pozo no se transforma en muestra clínica por inferencia.

El contrato `IInstrumentContentPlugin` recibe bytes y perfil, devuelve observaciones y diagnósticos, y nunca abre archivos. Los CSV inválidos no devuelven un lote parcial. Los perfiles nuevos ASTM son de **recepción**: consultas no soportadas generan aviso y no fabrican respuestas; no habilitar esa función en el instrumento durante su prueba. Ninguno de los perfiles nuevos está homologado en hardware.

## Prioridad Argentina y ampliaciones

Consulta del 2026-10-24: [IMEX/CONICET](https://imex.conicet.gov.ar/biologia_molecular_celular/) identifica CFX Connect 96 entre sus equipos instalados. El catálogo propio del distribuidor argentino [Müller](https://www.distribuidoramuller.com.ar/equipos.php?id_marca=2) ofrece XN-1000 y XN-L 350/450/550. Son evidencias de presencia/oferta local para priorizar; no prueban stock, habilitación actual ni equivalencia de sus interfaces.

Secuencia de ampliación: validar exportaciones reales CFX y los tres XN-L; completar consultas/QC/extensiones sólo con evidencia; incorporar XN-1000/XN y revisar el manual local SUIT como trabajos por interfaz, no copiando nombres al catálogo. CS-2500 ya tiene un primer perfil de recepción. Después QWALYS según la prioridad humana; su presencia argentina no está confirmada por esta revisión. Otros Bio-Rad requieren identificar modelo, software y exportación concretos antes de declarar compatibilidad.

Los manuales/corpus locales no se copian al repositorio ni al ejecutable. Las pruebas nuevas son sintéticas. Para declarar un plugin de producción faltan P-03: corpus anonimizado y revisado, resultado esperado por modelo/firmware y pruebas con equipo de consultas, repetición, QC, desconexión y recuperación.

## Archivos, integración EDC y siguientes pasos

CFX usa exportaciones y QWALYS contempla intercambio por archivos **append**: son ejemplos reales de interfaces basadas en ficheros. El Scheduler de EDC detecta novedades; el ETL toma custodia, verifica que la escritura terminó, conserva identidad/orden y entrega contenido completo al plugin/procesador. Ordenar sólo por creación no asegura integridad ni idempotencia ante cambios manuales, copias o escrituras todavía abiertas. En append hace falta cursor/checkpoint por archivo y mensajes completos; un evento no equivale necesariamente a un fichero nuevo ni a un único mensaje. ATSM no añade un filedrop.

La GUI es un anfitrión de mantenimiento. Sigue pendiente integrar ATSM en EDC (P-13: `ILabWorkflowHost`, Scheduler/ETL, SQLCipher, permisos/UI), completar LAW (P-02), outbox durable (P-04), observadores/captura remota (P-09), LocalSerial (P-10) y distribución firmada/CI (P-14). El journal de diagnóstico no resuelve la outbox ni habilita reenvío de escrituras inciertas. Mantener SerialTunnel sin spool de disco; ver [la decisión de recuperación](MENSAJES-TRAZAS-HL7.md).

Validación de esta entrega: suite .NET con casos CSV/RDML, posiciones/estados de fabricantes, custodia antes de aceptación, simulación ASTM completa, reapertura/reanálisis, corrupción, reordenamiento y cortes del journal; pruebas nativas de regresión; build MAUI, prueba de ventana/handler/runtime privado y DPAPI del usuario. Los conteos finales y commits se registran en ESTADO y en un changelog nuevo.

## Mejoras de uso del 2026-10-25

La lista contiene 8 equipos/familias, con 14 perfiles técnicos en el selector de configuración y 8 vistas. Control UF parcial convive con UC-3500/ASTM dentro de la familia Sysmex. Comunicación presenta estadísticas generales, respuestas identificadas y operaciones con límites explícitos por separado; ver [Control UF y tiempos](UF-CONTROL-Y-TIEMPOS.md). Interpretación/grupos predeterminados, originales en detalle/modo técnico, detección por contenido, columnas automáticas/ajustables y seeds español/Lang.


UC-3500 directo: intérprete serial propio y captura sin respuestas; simulación activa deshabilitada para ese perfil. No confundirlo con U-WAM ASTM. Las etiquetas y celdas MAUI permiten seleccionar/copiar texto. Véanse códigos ER, resultados y límites en [UC3500-PROTOCOLO](UC3500-PROTOCOLO.md).


Seleccionar U-WAM y abrir/analizar resuelve el enlace por contenido dentro de Atsm.Plugin.Sysmex.Urinalysis, sin entrada UC-3500 separada. Interpretación manual disponible en configuración. Vista Calidad enfocada en mediana, DE, extremos, n y señales; material/lote literal y registros reales desplegables: [CALIDAD-DIAGNOSTICA](CALIDAD-DIAGNOSTICA.md).

## Revisión de detección y modelos (2026-10-25)

La importación nueva del log local UF-5000 recupera 425 mensajes y 5.006 observaciones, sin aviso de protocolo incompatible. Se corrigieron dos brechas: la clasificación `unsupportedProtocol` conservada en un caso antiguo ya no bloquea para siempre su reanálisis; y la cabecera puede estar dividida entre lecturas del monitor. Los fragmentos de una transmisión conservan su naturaleza de cable hasta EOT/FS, sin pasar por registros ASTM independientes. Los originales no se modifican ni se omite la validación de tramas.

El puerto opcional `IInstrumentHeaderProbe` mantiene las firmas de identidad en cada plugin. El catálogo consulta sólo cabeceras, no nombres de archivo, comentarios ni etiquetas del monitor; dos dialectos distintos identificados en una entrada generan ambigüedad. RX y TX se reconstruyen por separado. La identidad no cambia las opciones explícitas del mismo dialecto ni demuestra qué analizador químico está instalado.

| Familia | Reconocimiento automático y alcance |
|---|---|
| U-WAM / UN | H.5.1: U-WAM, UF-3000/3000x/4000/5000, UC-1000/3500, UD-10. Interpreta registros R/Q/O y QC S según R315168; es cobertura del enlace U-WAM. El UF-5000 directo del corpus local usa esos registros y está comprobado; no se atribuye ese mismo protocolo directo a todos los demás modelos. |
| UC-3500 directo | Reconocimiento RQ02/CM00/ER y resultados por su sintaxis propia dentro de la misma familia; nunca por el nombre del archivo. Conserva controles, códigos ER/errores de análisis y estados. |
| XN-L | H.5.1: XN-350, XN-450, XN-550, según 1273-MKT rev.3 pp.34–38. Recepción implementada, sin respuestas a consultas ni descarga de órdenes. |
| CS-2500 | H.5.1: CS-2500, manual rev.2.04 §5.3.3.1. Recepción implementada, sin respuestas a consultas ni descarga de órdenes. |
| ARCHITECT | H.5.1: ASI, según 91407-108 §6; identifica la interfaz, no el modelo físico. HL7 conserva su selección específica: MSH por sí solo no identifica fabricante. |
| CELL-DYN Ruby | H.5.2: CDRuby, evidencia de trazas locales. Siguen pendientes manual y homologación física. |
| QWALYS 3 | Recepción documentada disponible mediante selección. Las cabeceras de los ejemplos locales no aportan una identidad inequívoca del analizador: no se inventa autodetección. |
| CFX | CSV/RDML detectados por contenido. Son exportaciones de Maestro; no acreditan control directo ni identificación de cada hardware CFX. |

Fuentes locales: R315168 rev.22 (tabla de modelos, apéndices B/K y §5.3.3), guía 1273-MKT rev.3, CS-2500 rev.2.04, 91407-108 y manual QWALYS 3. Permanecen en `manuales/`, fuera de Git. Sin homologación por modelo/firmware en hardware; el corpus real verificado y las pruebas sintéticas se distinguen en ESTADO.

La siguiente ampliación documentada es SUIT v8: su tabla incluye XN-1000/2000/3000/9000, XE/XS/XT, UF-1000i/500i y CS-2000i **a través de SUIT** (pp.7 y 32 del PDF). No están implementados ni se agregan como si fueran el mismo dialecto XN-L/U-WAM. El manual usa H.2 con cuatro caracteres y registros OBR/OBX de E1238-94, además de S para QC; el parser LIS02 genérico de un carácter no puede interpretar esas estructuras correctamente. Requiere un intérprete SUIT propio y corpus revisado. S.4/7/11/12/13/16 permite obtener analizador, rol QC, lote, prueba, valor y fecha de medición (p.27 del PDF), reutilizando los puertos de calidad existentes; sin inventar unidad, nivel o valores objetivo ausentes. Seguimiento en EDC#205.

Presentación: lote ausente `-`; RAW se muestra como «Original» y MAINFORMAT como «Formateado». Los identificadores técnicos completos siguen en los originales/detalle y modo sin interpretación.

## Conocimiento y resúmenes semánticos (2026-10-25)

Las tablas usadas para interpretar se incorporan completas al plugin, con contexto y fuente; no se agrega únicamente el código observado. R315168 rev.22 apéndice K pp.88–89 está codificado para sus 38 códigos U-WAM distintos, incluido 0000 sin error y 8050/9010 exclusivos de la tabla UC-1000. El puerto IInstrumentKnowledge consulta por perfil, registro, campo, código y modelo opcional; SourceCode conserva la correspondencia con el analizador. 9901→00BB, 9902→00CC y 9903→00DD no son los códigos ER de respuesta serial.

El mismo código puede cambiar de significado por modelo: 1100 es fallo mecánico en la tabla común UC-3500 y tiempo de análisis agotado en UC-1000. Si no se identifica el modelo se muestran alternativas documentadas; no se aplica silenciosamente una de las tablas. Un código desconocido mantiene su valor y se presenta sin descripción documentada. El detalle conserva ambos códigos, referencia y original. Los hallazgos de cada código tienen identidad propia, de modo que agruparlos no mezcla causas diferentes.

También se exponen las tablas R.3.5 (origen A/M), R.3.7 (S/RC/RP/IF) y R.4.2 (RAW, MAINFORMAT, RAW_Q, RAW_C, RAW_R1, RAW_R2). IInstrumentMessageKnowledge explica la consulta con Q.3; IInstrumentFlagKnowledge distingue el indicador del analizador de LH y del rol QC. N = Normal no es una alarma ni requiere atención. H del analizador UF es juicio de error; H de química es positivo; H de LH es valor por encima de su intervalo. No se transforman esos estados en validación clínica ni se igualan sólo por compartir una letra.

La columna Estado prioriza el significado útil: «Normal» para N, sin «Final según el equipo» ni separador sobrante. Los estados preliminar/corrección/sin medir y los indicadores que sí requieren atención siguen visibles. Calidad usa esa interpretación para el contador y el resaltado; N no infla las alarmas.

IF se muestra como Imagen tanto en tipo como en valor/resultado de Calidad, incluidas las subfilas. La referencia o contenido original permanece en detalle y modo técnico; no se abre, descarga ni renderiza un archivo automáticamente. IsStructuredContent excluye una imagen de mínimo/mediana/máximo/DE incluso si su identificador es sólo numérico. RP se identifica como parámetro de sensibilidad y RC como comentario de revisión.

Mensajes conserva las explicaciones de identificación del paciente/muestra/solicitud y retira únicamente cabecera H y terminador L del resumen. Una consulta indica el número de muestra; el grupo conserva el propósito y el contador, y sus subfilas conservan las consultas individuales. «Con error» se añade por diagnósticos locales o indicadores de error documentados, sin marcar como error un N, un positivo químico o un LH por compartir un código. Estructura completa, evidencia y bytes permanecen en detalle.

### UF-5000Control: lectura parcial

El archivo local UF-5000Control se importaba parcialmente: el monitor reparte los bloques largos en lecturas con contador de tres dígitos, pero las llaves de decoración pueden abrirse y cerrarse en líneas distintas. Se recuperaron 315 lecturas; ahora son 34.373 entradas (34.371 lecturas y 2 eventos), 500.444 bytes y 399.939 NUL, sin diferencias con los contadores declarados. El adaptador usa esos contadores y estado por dirección para distinguir decoración de bytes literales; preserva espacios, NUL y llaves que son datos. Una ambigüedad sin estado previo conserva la interpretación anterior de la línea e informa la diferencia de longitud.

La ausencia de H/MSH no impide leer contenido útil. La revisión del 2026-10-26 reconstruye 2.193 mensajes completos y sus 500.444 bytes, con 349 avisos técnicos por estructura y campos de identificación. Incluye instrucciones de cierre, material de control/reactivo caducado, RBC alto y mantenimiento requerido. Los NUL pertenecen a payloads/encabezados; no prueban comunicaciones vacías ni ACK. No se confirma ejecución de mantenimiento. Campos numéricos restantes, integridad y roles físicos siguen pendientes; el usuario desconoce qué puerto captura. El nombre del archivo no es un modelo ni criterio de detección. [Layout observado, pruebas y métricas](UF-CONTROL-Y-TIEMPOS.md).

La revisión del manual de servicio del fabricante [UF-3000/4000/5000, capítulo 5, §5.4.10](https://www.scribd.com/document/906087031/UF-5000-E-05) distingue modos U-WAM/host y documenta la interfaz ASTM externa, pero no especifica el encuadre ni los campos de este contenido binario. El [capítulo 3 de electrónica](https://www.scribd.com/document/906087014/UF-5000-E-03) tampoco proporciona esa tabla de comandos. No se afirma haber identificado el canal interno preciso por ausencia de cabecera. Para P-15 se necesita la especificación de comunicación/control correspondiente y corpus conocido por comando; la captura queda preservada, sin intérprete ficticio ni órdenes activas.

## Tablas de conocimiento ampliadas (2026-10-25)

| Plugin | Conocimiento incorporado | Fuente primaria y contexto |
|---|---|---|
| U-WAM / UF | 11 valores de RBC-Info., UTI-Info. y BACT-Info.; 19 comentarios de revisión RC | R315168 rev.22, appendices C/D/F pp.74-76/80; tipo S/RC y nombre reservado; UF-3000/3000x/4000/5000 con restricciones por modelo |
| XN-L | 49 indicadores IP de anormalidad, sospecha, juicio y acción; 8 flags base, máscaras y estados de repetición | 1273-MKT rev.3, tablas 5/7-10 pp.24-25/29-33; R.3.5, R.4, R.7 y R.9 |
| CS-2500 | 68 códigos numéricos de evaluación; 34422 (reactivo insuficiente); tipos de emisión y flags HIL/volumen separados | [R316009 rev.2.04](https://doclib.siemens-healthineers.com/rest/v1/view?document-id=372255), pp.32-35 y appendix C tabla 19 pp.53-55; R.3.7 y componentes independientes R.7.1/R.7.2/R.7.3 |
| QWALYS 3 | 9 tipos de reacción/interpretación, 4 soportes, estados R/M y códigos CMP/INCMP/E según tipo | ASTM Connection Manual §4.6 p.10; R.3.4, R.4.7, R.5 y R.9 |
| ARCHITECT | F/P/I (concentración/respuesta/interpretación), ensayo primario/correlación y 7 flags con definición explícita, incluido EXP | ASI 91407-108, tablas 1.13/2.4, pp.1-60/2-16/2-17; R.3.7, R.3.11 y R.7 |

El lookup exige perfil/registro/campo/modelo; los puertos de presentación validan además el sistema de códigos y contexto de la observación. Desconocidos y mensajes originales se conservan. Toda etiqueta tiene clave de seed y evidencia; DisplayText traduce el significado sin traducir el código almacenado. No se copiaron los manuales al repositorio ni se añadieron dependencias.

Los indicadores IP son avisos para revisión. Un A sobre PLT_Clumps? significa sospecha de agregados plaquetarios; no equivale al A de un resultado enmascarado por fallo del analizador. Los índices Q-Flag no reciben umbrales inventados y sin A no se convierten en positivos por su valor numérico. Los códigos de investigación U-WAM permanecen cualitativos; no se presentan como diagnósticos confirmados. Un comentario RC genera atención por revisión, sin convertirse en error de análisis.

CS-2500 puede comunicar N junto con un código de cálculo, calibración o un valor enmascarado; el estado muestra el código específico. HIL y volumen tienen sus propios significados. 1100.4000.0000 se presenta como comprobación de volumen: no prueba volumen inadecuado por sí solo, pues N/A y el registro de muestra aportan ese contexto. El sentinel Unknown9999 no se trata como comodín para inventar interpretaciones. Los símbolos internos de la tabla 19 se resumen sin atribuirles una causa de hardware no documentada.

Se revisó ARCHITECT sin extender significados a indicadores que el manual sólo enumera (p. ej. A#1/A#2 o códigos específicos sin definición); sus originales continúan disponibles. CELL-DYN Ruby no incorpora tablas ajenas: aún requiere su manual propio. No se amplía por semejanza a otros modelos ni se declara homologación física.
