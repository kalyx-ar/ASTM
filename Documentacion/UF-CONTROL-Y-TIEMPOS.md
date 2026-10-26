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
| 2 | 102 bytes | 1.981 | Envoltura y datos binarios; significado de campos numéricos pendiente |
| 6 | 1.500 bytes | 105 | Diez posiciones de 150 bytes; campos de aviso en cada posición ocupada |
| 11 | 376 bytes | 105 | Texto del equipo y recurso DBS_LANG |
| 12 | 262 bytes | 2 | Etiqueta, recurso/calificador y contexto |

En tipo 6, cada posición tiene código u32le en offset 0 y texto con relleno NUL en offset 10, ancho 100. Se conservan todos los campos originales; no se fabrica el significado de los demás bytes. En tipo 11, el texto ocupa payload[62..262), y el recurso payload[262..376). En tipo 12, etiqueta [34..84), recurso/calificador [84..148) y contexto [148..262). El intérprete captura etiquetas nuevas: **no exige una lista cerrada de textos ni de códigos**. UTF-8 válido se decodifica como tal; en otro caso se conserva la lectura Latin-1 y siempre los bytes.

Las lecturas de 386 bytes corresponden al subencuadre tipo 11, no a un mensaje ASTM. El fragmento de 266 bytes observado comenzaba dentro del campo de longitud del tipo 12. Ambos pueden examinarse en capturas parciales sin exigir el encabezado externo completo. Los bloques de 13 bytes aportados por el usuario están dentro del payload de los mensajes tipo 2 completos: no se cuentan como otros mensajes cuando la envoltura está disponible. Sus lecturas numéricas candidatas siguen sin significado operativo confirmado.

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

Al seleccionar o desplegar un bloque, el panel inferior muestra su interpretación y los bytes del mensaje completo reconstruido, sin encadenar detalles de fragmentos. Las subfilas siguen disponibles para inspección técnica. Una lectura formada por cinco NUL muestra «Fragmento 5 bytes - NUL»; los fragmentos breves muestran nombres ASCII conocidos, como ACK (06), ENQ (05) y EOT (04). Ese nombre de byte no acredita una confirmación del enlace dentro de un payload binario. No se agrupan todas las cabeceras o todos los NUL dispersos en una vista cronológica. El modo sin agrupación conserva cada entrada.

La expansión/contracción actualiza una colección observable persistente en lugar de sustituir ItemsSource, y conserva las filas cuyo contenido no cambió. Windows configura el modo del panel nativo y restaura la posición tras el layout. El self-test de ventana real desplaza una lista de 1.200 registros, espera la finalización del scroll inicial, expande/contrae y comprueba offset, página y colección. También expande el último grupo de página para verificar que todos sus hijos siguen visibles: se paginan 300 grupos completos. [MAUI](https://learn.microsoft.com/en-us/dotnet/maui/user-interface/controls/collectionview/scrolling?view=net-maui-10.0), [panel nativo de Windows](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.itemsstackpanel.itemsupdatingscrollmode).

Las notas de desarrollo, enlaces a copias de manuales y advertencias genéricas de cobertura se retiraron del panel, del catálogo y de las cadenas que se incorporan a los ensamblados. La documentación técnica permanece fuera de los ejecutables. Los perfiles muestran configuraciones útiles.

Adaptador, registrador, zona horaria y cero líneas omitidas son metadatos de importación, no hallazgos. Se guardan en propiedades del caso. Las omisiones reales por fecha inválida o discrepancias de longitud generan advertencias específicas con su cantidad; una limitación desconocida muestra su texto concreto en lugar de encubrirla con una etiqueta genérica.

Las referencias a formatos de imagen se reconocen de forma general, además del tipo IF del plugin. Tipo y resultado muestran «Imagen», Unidad «—»; no entran en estadísticas aritméticas y no se abren archivos, URI ni referencias. El original permanece intacto.

## Tiempos y operaciones

Comunicación presenta mínimo, mediana, P95 (rango más próximo), máximo y cantidad, con subfilas de origen. Trama y Comunicación usan **HH:mm:ss.fff**, con horas acumuladas sin volver a cero a las 24 horas. No se usa notación científica. Sin reloj se muestra —; un cero fechado puede reflejar resolución/coalescencia del monitor.

Los intervalos entre lecturas, cambios de sentido y recepción de mensajes son independientes de operaciones. No se llaman RTT a pausas o cambios de sentido. ENQ/ACK, trama/ACK y NAK sólo se miden cuando el observador establece la relación; una coincidencia de bytes de control en un payload no basta. CM00→ER de UC-3500 aporta una respuesta explícita. Ausencia de cierre no equivale a un timeout supuesto; límites inválidos y regresiones de reloj no se convierten en cero.

En esta captura UF hay 34.370 intervalos: mediana 00:00:00.000, P95 00:00:00.521 y máximo 03:51:05.543. Es una pausa de captura, no mantenimiento. La recepción reconstruida de mensajes tiene mediana 0 ms, P95 1 ms y máximo 6 ms. Sólo se observan lecturas R; la UI dice «Lectura del registrador» mientras no se conozcan los roles físicos.

`InstrumentTraceTiming` separa Response, OperationTotal y OperationProcessing. El intérprete/anfitrión que conoce la solicitud, aceptación y respuesta final aporta sus secuencias; un fin nulo queda pendiente. Solicitud→respuesta inicial mide comunicación; solicitud→final, duración total; aceptación→final, proceso. La captura UF no delimita todavía estas operaciones largas.

SDK API 2: reconocimiento/inferencia por perfil, Timings tipados y consumidores compilados actualizados sin aliases anteriores. Link v1 y el formato de traza no cambian. Queda por documentar la semántica de los campos binarios tipo 2 y restantes, sus unidades, roles físicos y límites de operaciones; P-15 sigue abierto. La especificación local U-WAM R315168 describe la interfaz ASTM externa; no sustituye la especificación de esta envoltura binaria.
