# Control UF y tiempos de comunicación

Incremento del 26 de octubre de 2026, kalyx-ar/EDC#205. Lectura pasiva y parcial dentro de `Atsm.Plugin.Sysmex.Urinalysis`, perfil `sysmex.uf.control.partial`. La selección principal conserva una sola familia U-WAM/UF/UC. La detección consulta el contenido, nunca el nombre del archivo; reconocer este formato compatible no acredita la identidad física de un equipo.

## Evidencia y registros reconocidos

La captura local UF-5000Control contiene etiquetas legibles y bloques repetidos. La ausencia de cabeceras ASTM/HL7 no impide extraer esos hechos. La implementación anterior importaba los bytes pero no examinaba estos registros; se sustituye ese límite por una interpretación parcial explícita. No se transmite ninguna orden UF.

Una clase de aviso observada ocupa 266 bytes:

| Offset, desde cero | Longitud | Comprobación / contenido |
|---|---|---|
| 0 | 3 | Prefijo observado `01 00 00` |
| 3 | 34 | Campo opaco; no se convierte en muestra ni fecha |
| 37 | 50 | Etiqueta ASCII terminada en NUL y relleno NUL |
| 87 | 64 | Recurso `DBS_LANG_#####` y texto localizado, terminación/relleno NUL |
| 151 | 114 | Recurso de contexto, terminación/relleno NUL |
| 265 | 1 | Marcador observado `03`; no es comprobación de integridad |

En la captura se verifican estas asociaciones:

| Recurso | Etiqueta y texto recibidos | Presentación |
|---|---|---|
| DBS_LANG_01746 | Chequeo fondo / Necesidad | Chequeo de fondo requerido |
| DBS_LANG_02215 | Limpieza / Necesidad | Limpieza requerida |

Ambas usan el contexto `DBS_LANG_01745`. Es conocimiento derivado de la traza, no una tabla de comandos homologada. La interpretación exige esa combinación de campos. Un recurso desconocido o un calificador/contexto diferente se conserva sin afirmar una necesidad conocida.

Estos avisos producen mensajes, eventos, observaciones técnicas y hallazgos informativos. Las observaciones tienen muestra vacía/rol desconocido, valor codificado, sin magnitud, unidad ni contexto QC. No se fabrican pacientes ni mediciones analíticas. Estado «Requiere atención», sin tratarlos como errores de análisis. El significado usa el seed/Lang; los campos y bytes recibidos permanecen disponibles.

«Necesidad» no acredita que se haya enviado una orden de mantenimiento, que la operación esté ejecutándose o que haya terminado. Por ello no genera una duración de operación.

También se reconocen bloques recurrentes de 13 bytes:

| Offset | Contenido observado |
|---|---|
| 0 | `02` |
| 1–5 | NUL |
| 6–7 | Campo variable; se expone una lectura candidata u16 little endian |
| 8–11 | Lectura u32 little endian 90 o 100 |
| 12 | `03` |

La secuencia que aportó el usuario tiene lectura u16le 2551 en offset 6 y u32le 100 en offset 8. No se les atribuye unidad, estado, contador, comando ni significado operativo confirmado. Tampoco se declara checksum o integridad verificados. «STX» y «ETX» son aquí nombres de bytes observados, no prueba de framing ASTM.

La reconstrucción trabaja sobre el stream por dirección, conserva referencias a las entradas de origen y soporta cortes arbitrarios entre lecturas. Los eventos del registrador separan los buffers. La detección automática requiere el registro con recursos; un bloque corto aislado no basta para adivinar fabricante/modelo. Una configuración manual permite examinarlo dentro del perfil parcial.

Resultado local: 34.373 entradas y 500.444 bytes conservados; 1.981 bloques recurrentes y 2 avisos, 1.983 mensajes/bloques y 2 observaciones técnicas. Se mantiene un hallazgo de interpretación parcial. Los demás campos, recursos como DBS_LANG_01682/01633/01712, integridad, encuadre completo, dirección física y propósito de las variantes 90/100 siguen pendientes. No se extraen resultados clínicos de esos campos desconocidos.

## Estadísticas generales

La vista **Comunicación** funciona independientemente de la interpretación de operaciones. Presenta mínimo, mediana, P95 (rango más próximo), máximo y cantidad de intervalos, con subfilas que remiten a las entradas originales. La mediana par es el punto medio temporal. Contadores de lecturas/bytes y límites se conservan en el detalle.

- Intervalos entre lecturas: globales y por dirección. Incluyen pausas, interrupciones y lecturas con el mismo timestamp.
- Cambios de sentido: tiempo desde una entrada hasta la siguiente de dirección contraria; no prueba que sea una respuesta.
- Recepción de mensajes/bloques: desde la primera hasta la última entrada que los contiene.
- ENQ→ACK, trama→ACK y sus respuestas NAK: sólo cuando el observador LIS01 establece la relación. Bytes 05/06/15 dentro de un contenido binario no generan estas métricas.
- Respuestas de comandos: límites explícitos aportados por el intérprete, por ejemplo CM00→ER en UC-3500. Una respuesta de rechazo también tiene tiempo de comunicación.

Un frame con checksum alterado por el registrador puede tener un tiempo de ACK medible si el observador comprueba que el receptor lo confirmó. Un acuse implícito no se convierte en un ACK observado. Los pendientes indican ausencia de cierre observado en el tramo considerado, no un timeout supuesto. Límites ausentes, secuencias invertidas o reloj regresivo no se convierten en cero: se excluyen del cálculo y se cuentan como intervalos no calculables.

Las fuentes sin reloj de captura, como un archivo de bytes o un mensaje completo importado, muestran duraciones no disponibles. Un cero ms registrado puede deberse a la resolución/coalescencia del monitor. Los casos y trazas conservan la procedencia temporal; una traza sintética describe su propio reloj, no una medición física nueva.

UF-5000Control contiene únicamente lecturas R desde el punto de vista del registrador. La aplicación muestra «Lectura del registrador» cuando no conoce los roles físicos, sin invertirlo visualmente en equipo/anfitrión. Sus 34.370 intervalos tienen mediana 0 ms, P95 521 ms y máximo 13.865.543 ms; el máximo es una pausa en la captura, no un RTT ni una duración de mantenimiento. La recepción de sus bloques aparece con 0 ms porque cada bloque está íntegramente contenido en una lectura fechada. No hay base suficiente para medir sus respuestas bidireccionales como RTT.

## Operaciones largas

`InstrumentTraceTiming` distingue `Response`, `OperationTotal` y `OperationProcessing`. El intérprete/anfitrión que conoce los límites aporta las secuencias de inicio y fin; un fin nulo queda pendiente. Una operación puede aportar:

1. Solicitud→aceptación/respuesta inicial: comunicación.
2. Solicitud→respuesta final: duración total.
3. Aceptación→final: tiempo de proceso.

Se muestran como distribuciones separadas por clase/código; una explicación del plugin identifica la operación cuando existe. No se completan límites por proximidad, cambio de etiqueta ni inicio de otra operación. Los avisos UF de necesidad sólo justifican los hechos informados; aún no delimitan una ejecución real de limpieza o chequeo.

## Contratos y comprobación

SDK `PluginApi.Version = 2`: los métodos `Recognizes` e `InferRecorder` reciben `InstrumentTraceProfile`, permitiendo discriminar varios protocolos dentro del mismo plugin. Catálogo y consumidores compilados se actualizaron juntos, sin firmas anteriores ni aliases de ejecución. Link v1 y el formato de traza no cambian. `TraceInterpretation`/`DiagnosticAnalysis` exponen los intervalos tipados; las estadísticas siguen fuera del conocimiento del fabricante.

Pruebas: avisos conocidos/desconocidos/contexto distinto, ausencia de muestra/QC, reconstrucción en todos los cortes, separación RX/TX, conservación del bloque aportado y de campos desconocidos, detección por contenido, reanálisis, reloj ausente/regresivo, percentiles/medianas, ACK/NAK reales, checksum alterado, CM00→ER y separación respuesta/operación/proceso. MAUI comprueba ocho vistas, selección automática, etiquetas/observaciones UF, intervalos desplegables y limpieza del aviso al cambiar de protocolo. CLI emite sólo agregados `ATSM_TIMING`; no publica identificadores ni payloads privados.
