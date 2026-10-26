# UF binario y tiempos de comunicación

Incremento del 26 de octubre de 2026, kalyx-ar/EDC#205. `Atsm.Plugin.Sysmex.Urinalysis` interpreta resultados ASTM, UC-3500 y el formato binario UF dentro de la misma familia. El perfil binario es `sysmex.uf.binary`. Los dos archivos UF aportados corresponden al mismo equipo; su nombre no participa en la detección ni se usa como modelo.

## Reconstrucción del stream

La normalización reside en el SDK neutral: `BinaryWireParser` convierte hexadecimal/prefijos/notación de controles a los mismos bytes y `BinaryFrameLayout` describe encuadres por longitud, orden de bytes y patrones. El plugin UF declara su layout y la interpretación del payload; no contiene otro parser genérico de encuadres. El formato HexBytes de Diagnostics utiliza ese puerto antes del codec ASTM o del perfil binario correspondiente. No se transforma un protocolo propietario en ASTM por cambiar su representación: se normalizan los datos al mismo modelo canónico.

Los nombres ASTM comunes y sus traducciones pertenecen a `StandardDiagnosticVocabulary.Shared`, source `Atsm.Plugins`. Se retiraron reformulaciones redundantes por marca para H/P/O/R/Q/C/M/L; Imagen, Normal y otros conceptos generales se reutilizan. La disposición de registros/campos y los códigos con significado específico siguen en el plugin.

La primera aproximación encontraba dos avisos y bloques de 13 bytes dentro de las lecturas. Era insuficiente: confundía cortes del monitor con límites de mensaje. La revisión reconstruye las envolturas por longitud, por dirección y conservando las entradas de origen. Una división arbitraria en lecturas no cambia el resultado; un evento de captura separa los buffers.

| Offset, desde cero | Longitud | Disposición observada |
|---|---|---|
| 0 | 1 | 02 |
| 1 | 20 | Primer identificador, texto con terminación/relleno NUL |
| 21 | 12 | Segundo identificador, texto con terminación/relleno NUL |
| 33 | 3 | Tipo de mensaje, lectura little endian |
| 36 | 1 | 02 |
| 37 | 4 | Campo u32le observado igual a 1; no se le asigna otra semántica |
| 41 | 4 | Longitud del payload, u32le |
| 45 | N | Payload, que puede incluir 02/03 y NUL como datos |
| 45 + N | 1 | 03 final |

El tamaño total es N + 46. El encuadre se verificó sobre los 500.444 bytes de la captura: **2.193 mensajes completos, sin restos ni doble conteo**. No se presume checksum, integridad criptográfica ni identidad física autenticada. La disposición es evidencia de traza, todavía no una especificación del fabricante homologada. Los identificadores recibidos se presentan como identificadores, sin inventar si cada uno es serie, módulo u otro dato.

| Tipo observado | Payload | Mensajes | Lectura implementada |
|---|---:|---:|---|
| 2 | 102 bytes | 1.981 | Estado, indicadores, nivel general y siete registros de contadores/niveles; unidades y asignación física pendientes |
| 6 | 1.500 bytes | 105 | Diez posiciones de 150 bytes; campos de aviso en cada posición ocupada |
| 11 | 376 bytes | 105 | Texto del equipo y recurso DBS_LANG |
| 12 | 262 bytes | 2 | Etiqueta, recurso/calificador y contexto |

En tipo 6, cada posición tiene código u32le en offset 0 y texto con relleno NUL en offset 10, ancho 100. Se conservan todos los campos originales; no se fabrica el significado de los demás bytes. En tipo 11, el texto ocupa payload[62..262), y el recurso payload[262..376). En tipo 12, etiqueta [34..84), recurso/calificador [84..148) y contexto [148..262). El intérprete captura etiquetas nuevas: **no exige una lista cerrada de textos ni de códigos**. UTF-8 válido se decodifica como tal; en otro caso se conserva la lectura Latin-1 y siempre los bytes.

Las lecturas de 386 bytes corresponden al subencuadre tipo 11, no a un mensaje ASTM. El fragmento de 266 bytes observado comenzaba dentro del campo de longitud del tipo 12. Ambos pueden examinarse en capturas parciales sin exigir el encabezado externo completo. Los bloques de 13 bytes aportados por el usuario están dentro del payload de los mensajes tipo 2. Se retiró el reconocimiento aislado por ese patrón: sin envoltura no acredita otro mensaje ni una confirmación.

## Campos del tipo 2

La disposición se identifica por longitud de 102 bytes y siete identificadores correlativos, del 1 al 7. No depende del nombre del archivo ni de valores de ejemplo. Si cambia esa estructura, se conserva como datos sin aplicar este layout.

| Offset del payload | Ancho | Lectura expuesta |
|---|---:|---|
| 0 | 2 | `status.code`, código u16le; observado 2/4/7/8/9/10, sin nombres operativos inventados |
| 2, 4 | 2 cada uno | `status.indicator.1/2`, indicadores u16le, observados 0/1 |
| 6 | 8 | Campo desconocido conservado en hexadecimal, observado en cero |
| 14 | 4 | `status.level`, nivel u32le, observado 80→70→60→50 |
| 18 + 12 × n | 4 | `counter.{n+1}.id`, identificadores 1..7 |
| 22 + 12 × n | 2 | Campo desconocido de cada registro, observado en cero |
| 24 + 12 × n | 2 | `counter.{n+1}.value`, contador u16le |
| 26 + 12 × n | 4 | `counter.{n+1}.level`, nivel u32le |

Las 1.981 instantáneas presentan cuatro contadores poblados y tres en cero. Los primeros bajan 196 unidades, con saltos consecutivos de 0 o −1. Rangos: 1.489–1.685, 2.358–2.554, 1.511–1.707 y 2.145–2.341. Sus niveles son 80/90, 90/100, 80/90 y 80/90. Que los registros 5–7 estén en cero **no demuestra** que sean reservados, reactivos agotados o recursos ausentes.

**Hipótesis:** disponibilidad restante de reactivos. El fabricante describe seguimiento automático del remanente y contadores de consumo en UF. El manual de servicio, capítulo 6, §§6-108/109, fija 2.077 mediciones para los colorantes SF/CR. Los contadores 1 y 3 cambian de nivel 90 a 80 al pasar de 1.662 a 1.661, compatible con ese denominador y niveles por decenas. Esto refuerza la hipótesis, pero no aporta el contrato binario que asigna los registros a nombres concretos ni confirma unidades o porcentajes. Por eso la UI presenta **Contador / Nivel / Código de estado**, sin llamarlos mediciones clínicas, mililitros, porcentajes ni reactivos específicos. Referencias de fabricante: [Sysmex Journal 28(1), UF-5000](https://www.sysmex.co.jp/pdf/journal/en/vol28_1_04.pdf), manual de servicio UF-3000/4000/5000, edición 3-4, capítulos 1 y 6, julio/agosto de 2016. No se incorporan copias ni enlaces de agregadores a los ejecutables.

El fragmento `02 00 00 00 00 00 F7 09 64 00 00 00 03` es el registro del contador 2 (valor 2.551 y nivel 100) seguido del identificador 3. Ese último 03 pertenece al registro siguiente; no es el terminador de la envoltura. Del mismo modo, un 06 que forma parte de un contador no es un ACK.

El puerto neutral `InstrumentTraceMessage.BinaryFields` expone código, valor original, explicación localizada y rango de bytes. `FirstByteOffset` conserva el inicio cuando una lectura contiene varios mensajes. `BinaryWireParser.TryReadUnsigned` centraliza anchos de 1–4 bytes y ambos órdenes. Diagnostics relaciona esos campos con las lecturas de origen sin lógica Sysmex: los plugins declaran el layout y la semántica conocida. El detalle del bloque completo incluye todos los campos; los bytes originales permanecen intactos. En la captura, las 34.371 lecturas con datos quedan cubiertas por campos del mensaje; las otras dos entradas son eventos de captura.

## Contenido extraído

Se obtienen **349 observaciones técnicas** y sus hallazgos/eventos, sin muestra, rol clínico, medición numérica, unidad ni contexto QC fabricados:

| Texto recibido | Registros |
|---|---:|
| Ejecute cierre | 205 |
| UF-CELLPACK SF caducado | 105 |
| Err mater. de control caducado | 31 |
| RBC alto inusual | 6 |
| Necesidad: chequeo de fondo / limpieza | 2 |

Los conteos incluyen avisos repetidos en instantáneas del equipo, no 349 incidentes distintos. Tipo 11 incluye 100 instrucciones de cierre, 4 avisos de material caducado y 1 de RBC alto; las listas tipo 6 aportan otros 242 registros. El texto viene del equipo. El knowledge sólo añade la forma breve «Chequeo de fondo requerido» / «Limpieza requerida» a los recursos 01746/02215 con calificador Necesidad y contexto 01745; un código o texto nuevo se captura igualmente. Las traducciones afectan la presentación de interpretaciones propias, no los textos recibidos.

Una instrucción de cierre o una necesidad de mantenimiento no acreditan ejecución, aceptación, inicio ni final de una operación. No generan una duración inventada. «RBC alto inusual» es un aviso comunicado, no un nuevo resultado clínico de RBC.

## Presentación y cronología

El identificador recibido se muestra una sola vez en el encabezado del caso. Trama identifica las cabeceras sin repetir el mismo ID en todas las filas y marca un cambio si aparece otro identificador. Cuando se agrupa, presenta el bloque completo con las lecturas originales como subfilas. Las repeticiones sólo se unen si son consecutivas e idénticas; cualquier comunicación diferente corta el grupo. Mensajes y Eventos también preservan el orden. Hallazgos puede agrupar por diagnóstico, y Calidad conserva sus agrupaciones estadísticas.

Al seleccionar o desplegar un bloque, el panel inferior muestra su interpretación, los campos decodificados y los bytes del mensaje completo reconstruido, sin encadenar detalles de fragmentos. Las subfilas muestran los campos a los que pertenecen sus bytes, aunque contengan sólo NUL o un byte que coincide con ASCII de control. Sólo fuera de un mensaje reconstruido se usa el fallback de nombres de bytes. No se agrupan todas las cabeceras o todos los NUL dispersos en una vista cronológica. El modo sin agrupación conserva cada entrada.

La expansión/contracción actualiza una colección observable persistente en lugar de sustituir ItemsSource, y conserva las filas cuyo contenido no cambió. Windows configura el modo del panel nativo y restaura la posición tras el layout. El self-test de ventana real desplaza una lista de 1.200 registros, espera la finalización del scroll inicial, expande/contrae y comprueba offset, página y colección. También expande el último grupo de página para verificar que todos sus hijos siguen visibles: se paginan 300 grupos completos. [MAUI](https://learn.microsoft.com/en-us/dotnet/maui/user-interface/controls/collectionview/scrolling?view=net-maui-10.0), [panel nativo de Windows](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.itemsstackpanel.itemsupdatingscrollmode).

Las notas de desarrollo, enlaces a copias de manuales y advertencias genéricas de cobertura se retiraron del panel, del catálogo y de las cadenas que se incorporan a los ensamblados. La documentación técnica permanece fuera de los ejecutables. Los perfiles muestran configuraciones útiles.

Adaptador, registrador, zona horaria y cero líneas omitidas son metadatos de importación, no hallazgos. Se guardan en propiedades del caso. Las omisiones reales por fecha inválida o discrepancias de longitud generan advertencias específicas con su cantidad; una limitación desconocida muestra su texto concreto en lugar de encubrirla con una etiqueta genérica.

Las referencias a formatos de imagen se reconocen de forma general, además del tipo IF del plugin. Tipo y resultado muestran «Imagen», Unidad «—»; no entran en estadísticas aritméticas y no se abren archivos, URI ni referencias. El original permanece intacto.

## Tiempos y operaciones

Comunicación presenta mínimo, mediana, P95 (rango más próximo), máximo y cantidad, con subfilas de origen. Trama y Comunicación usan **HH:mm:ss.fff**, con horas acumuladas sin volver a cero a las 24 horas. No se usa notación científica. Sin reloj se muestra —; un cero fechado puede reflejar resolución/coalescencia del monitor.

Los intervalos entre lecturas, cambios de sentido y recepción de mensajes son independientes de operaciones. No se llaman RTT a pausas o cambios de sentido. ENQ/ACK, trama/ACK y NAK sólo se miden cuando el observador establece la relación; una coincidencia de bytes de control en un payload no basta. CM00→ER de UC-3500 aporta una respuesta explícita. Ausencia de cierre no equivale a un timeout supuesto; límites inválidos y regresiones de reloj no se convierten en cero.

En esta captura UF hay 34.370 intervalos: mediana 00:00:00.000, P95 00:00:00.521 y máximo 03:51:05.543. Es una pausa de captura, no mantenimiento. La recepción reconstruida de mensajes tiene mediana 0 ms, P95 1 ms y máximo 6 ms. Sólo se observan lecturas R; la UI dice «Lectura del registrador» mientras no se conozcan los roles físicos.

`InstrumentTraceTiming` separa Response, OperationTotal y OperationProcessing. El intérprete/anfitrión que conoce la solicitud, aceptación y respuesta final aporta sus secuencias; un fin nulo queda pendiente. Solicitud→respuesta inicial mide comunicación; solicitud→final, duración total; aceptación→final, proceso. La captura UF no delimita todavía estas operaciones largas.

SDK API 2: reconocimiento/inferencia por perfil, Timings tipados y campos binarios con rangos de origen. Link v1 y el formato de traza no cambian. P-15 mantiene pendientes la asignación física y unidades de contadores/niveles, el significado operativo de estados/indicadores, integridad, roles físicos y límites de operaciones. La especificación local U-WAM R315168 describe la interfaz ASTM externa; no sustituye la especificación de esta envoltura binaria.
