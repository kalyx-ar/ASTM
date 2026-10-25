# Intérprete pasivo UC-3500

Entrega del 2026-10-25, [EDC#205](https://github.com/kalyx-ar/EDC/issues/205). El enlace directo UC-3500 ↔ anfitrión usa un formato serial propio. La salida U-WAM ↔ LIS conserva su plugin ASTM independiente. Diagnostics detecta el protocolo por contenido, aunque la extensión o el equipo seleccionado inicialmente sean otros.

Fuente: **Sysmex UC-3500 Host Communication Specifications (Except North America), v0.0.5, 2017-04-14**, §§6.1.1–6.1.2 y 7; [copia pública del documento del fabricante](https://pdfcoffee.com/uc-3500-host-manual-v005-pdf-free.html). Los manuales locales SUIT v8 y R315168 rev.22 no especifican este enlace. La copia pública permite comprobar los campos y códigos; el formato extraído de algunas tablas de errores pierde la alineación de columnas. No se asignan descripciones específicas a esas correspondencias ambiguas.

## Registros y diagnóstico

| Registro | Interpretación implementada |
|---|---|
| RQ02 | Consulta de orden: identificación de muestra de 14 caracteres y posición de dos caracteres. |
| CM00 | Respuesta del anfitrión con muestra/posición y nombre de tira de 10 caracteres; se conservan espacios y mayúsculas. Tira vacía significa que el anfitrión no tiene orden. |
| ER00 | Respuesta sin error; no prueba que se haya realizado una medición ni valida clínicamente un resultado. |
| ER01 / ER03 | Formato de orden incorrecto / identificación que no coincide con la consulta. |
| ER04 / ER05 | Tira solicitada ausente del rack de botellas / no registrada en el analizador. |
| Resultado completo | Identificación, rack, posición, serie N/#/C, número de análisis, código del equipo, tira utilizada y fecha/hora originales; bloques de determinaciones y flags. |
| ACK / NAK / EOT | Eventos de enlace: recepción, rechazo/retransmisión y fin o interrupción del intercambio. |

RQ02, CM00 y ER se correlacionan por secuencia y por identificación/posición. Faltantes, discrepancias y sentidos opuestos al documentado son hallazgos explícitos. Un ER no genera observaciones clínicas. Los registros/códigos desconocidos y fragmentos incompletos se conservan sin fabricar equivalencias.

Los códigos de **resultado** son distintos de las respuestas ER: `00BB` es error de lectura del código de barras; `00CC`, error de orden; `00DD`, vencimiento de las tiras. Las familias X0X0/YY00/0X0X se identifican según tabla 7-1; otros códigos permanecen sin descripción específica. En particular, 8010 sólo recibe la categoría de error de tira hasta verificar la alineación de la tabla original. Los flags y estados originales siempre acompañan al dato.

Con ORDER ERROR=MEASURE puede existir un resultado marcado 00CC medido con la tira configurada en el equipo, después de una orden rechazada. No se deduce la configuración efectiva a partir de un ER aislado, ni se convierten esas mediciones en resultados aceptados por el anfitrión.

## Encuadre e integridad

Reconstrucción por dirección, independiente del tamaño de las lecturas: TRANSPARENT 1 con CRLF y TRANSPARENT 2/HANDSHAKE con STX/ETX. Se comprueban BCC (XOR), SUM y SUM2 según §6.1.1.4, incluyendo ETX y excluyendo STX. Se admite checksum binario en otra entrada del monitor, incluso cuando su valor coincide con un carácter de control. El modo NONE comprueba delimitadores, no integridad.

El intérprete aprende la comprobación de la captura completa cuando hay una única modalidad demostrada; no transforma un checksum final ausente en NONE si las otras tramas prueban BCC/SUM/SUM2. La API del plugin permite fijar explícitamente `Uc3500CheckMode`. No distingue HANDSHAKE de TRANSPARENT 2 por STX/ETX solamente ni aplica timers de comunicación activos.

Existe una discrepancia documental: el texto indica 441 bytes máximos, mientras que la suma de los anchos de tabla 6-17 da **446 bytes** con reflectancias y código de color. La captura coincide con esos 446. Se valida la estructura completa de campos y se informa esta diferencia; no se recortan los últimos cinco bytes. También se admite el formato compacto sin reflectancias/código de color.

Checksum erróneo, encuadre incompleto o estructura desconocida impiden producir observaciones de ese registro. Se conservan bytes, texto y rango de secuencias. Las fechas de cuatro dígitos se interpretan con la zona de la estación; fechas de dos dígitos o inválidas conservan texto y aviso, sin inventar siglo ni orden día/mes. No se toma la fecha del nombre del archivo como fecha de medición.

## Mediciones y calidad

Se exponen valores cualitativos y concentraciones sin inventar unidades; reflectancias R%(1)/R%(2) con unidad %, densidad de tira/refractómetro, color, código de evaluación del color y turbidez. Cada representación conserva su tipo y valor original, sin mezclar concentración y reflectancia. Un parámetro no analizado conserva el estado `NotPerformed`.

Serie C identifica material de control; lote, nivel y vencimiento no se infieren. `AnalyticalQualityContext.SourceFields` conserva serie/número de análisis, tira utilizada, estados y campo original. Las observaciones pueden ser consultadas por el puerto de calidad del anfitrión una vez custodiadas en lotes. No hay medias/DE/CV calculados, reglas de aceptación QC ni calibración automática.

## Integración y uso

`IInstrumentTracePlugin` es un puerto neutral en Atsm.Plugins para detección, reconstrucción e interpretación pasiva. El fabricante implementa `Atsm.Plugin.Sysmex.Urinalysis`; devuelve mensajes, eventos, observaciones y diagnósticos. No abre conexiones ni administra archivos. Core compone el catálogo; MAUI y la CLI usan el mismo intérprete. Vocabulario y diagnósticos tienen seed español por ensamblado y adaptación Lang opcional.

Abrir el archivo con formato **Automático** resuelve el perfil UC dentro de U-WAM y el lado del registrador cuando hay evidencia. También se puede elegir U-WAM con interpretación automática para reanalizar un caso previamente marcado incompatible. La vista Mensajes explica los registros, Hallazgos agrupa códigos y el detalle conserva correlación, tiras y originales. Las etiquetas y celdas MAUI permiten seleccionar texto y copiarlo; los controles editables y el detalle mantienen su copia nativa.

Captura directa TCP/SerialTunnel disponible sin respuestas, con custodia previa del anfitrión. La simulación activa se rechaza antes de abrir el canal: no hay envío de órdenes ni ACK UC-3500 implementados. La captura directa no es un sniffer; un equipo en HANDSHAKE puede esperar ACK y detener su transmisión. No se acredita homologación en hardware.

## Comprobación local

La captura privada permanece ignorada. Agregados: 2.006 entradas, **501 mensajes** (128 RQ02, 128 CM00, 111 ER04, 17 ER00 y 117 resultados), **501 BCC correctos**, 2.251 observaciones con sus representaciones/estados, incluidas las de control. Ningún registro se pasa por LIS01/LIS02.

En las 111 órdenes que reciben ER04, el anfitrión solicita ` Strip  9A`; sus resultados informan ` Strip 11A` y 00CC. Es consistente con la tira pedida ausente y la medición con otra tira configurada. No prueba por sí solo la causa de configuración/carga. Las otras órdenes tienen tira vacía (15) o Strip 11A (2). También hay tres resultados 00BB, uno 8010 y dos 0000. Son códigos de equipo agregados, sin identificaciones de muestra.

Pruebas sintéticas: fragmentación en todos los puntos, checksum separado/control, tramas concatenadas, cuatro modalidades de comprobación, CRLF de longitud completa, códigos conocidos/desconocidos, correlación, sentidos, resultados/reflectancias/serie C, corrupción y truncamiento sin observaciones, captura sin escrituras y rechazo de simulación activa. Validación de entrega y límites restantes en ESTADO/CONTINUACION.


El intérprete serial convive con ASTM U-WAM en el mismo plugin; el selector principal ya no separa UC-3500. La base de conocimiento interpreta códigos según su contexto; un texto coincidente en otro campo no hereda el significado. Véase [CALIDAD-DIAGNOSTICA](CALIDAD-DIAGNOSTICA.md).
