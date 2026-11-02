# Inspección de pacientes y resultados en Diagnostics

Incremento EDC #205, 2026-11-02, ASTM 0.6.6. En **Mensajes**, seleccionar una fila para ver **Estructura interpretada por el plugin** en el detalle. Cada campo muestra ubicación, significado, texto original entre comillas y valor decodificado. Se indican la fuente documental y los campos sin interpretación confirmada. El original completo continúa al final del detalle, con interpretación activada o desactivada.

El técnico puede cotejar identificador/nombre de paciente, muestra, ensayo, valor, unidad, indicadores y estado contra el mensaje original y el resumen de resultados. Esta inspección también funciona en mensajes transmitidos, órdenes sin resultados, mensajes incompletos y resultados sin proyección canónica. No incorpora pacientes ni valida coincidencias clínicas automáticamente.

## Contrato y estructura

`IInstrumentMessageInspection` es opcional. El plugin elige `MessageSchema(profileId)` y puede sobrescribir `InspectMessage`; la implementación predeterminada comparte sólo el lector de sintaxis `DelimitedMessageInspector`. Los mapas y sus fuentes están en cada plugin. Diagnostics compone los lectores y presenta su salida sin ramas de fabricante en el lector o el renderizador.

`InstrumentMessageInspection` conserva `Original`, registros originales, campos y hallazgos. En ASTM, P.1 es el tipo y P.2 la secuencia; en HL7, PID.1 es el primer campo después del nombre del segmento, con la excepción reglada MSH.1/MSH.2. Los componentes y subcomponentes son posicionales; `[n]` identifica una repetición. Se separa antes de decodificar escapes, usando los separadores declarados, sin alterar espacios ni vacíos. Un escape HL7 inválido conserva el texto y produce un hallazgo.

`P@2/O@3/R@4` relaciona registros por su posición en el mensaje, independiente de sus secuencias transmitidas, que pueden repetirse. Cambiar paciente elimina la asociación de solicitud anterior; cambiar muestra HL7 elimina la solicitud anterior. Los mensajes no comparten contexto. Un resultado sin O/OBR queda explícitamente sin asociación; no se inventa paciente cuando no llegó P/PID. Es una lectura estructural, no una resolución de identidad del LIS.

## Mapas incluidos

| Perfil | Particularidades verificables |
|---|---|
| CLSI LIS02 genérico | Posiciones estándar de paciente, solicitud y resultado; no certifica variantes del fabricante. |
| ARCHITECT ASI | Paciente P.3/P.6/P.8/P.9/P.14/P.26; muestra O.3; ensayo, dilución, lotes y tipo en R.3. |
| ARCHITECT HL7 | PID/SPM/OBR/OBX; numeración MSH, repeticiones y subcomponentes de ensayo/dilución/tipo. |
| CELL-DYN Ruby | Panel R.3.4/parámetro R.3.7 y resultado/alarma; evidencia explícita como hipótesis de trazas, sin manual homologado. |
| CS-2500 | P.6.2 nombre/P.6.3 apellido; resto no utilizado sin inferir; O.4.3 muestra, R.3 extendido y R.7 compuesto. |
| XN-L | O.4.3/O.3.3 muestra, R.3.5 parámetro; P sin mapa verificado queda desconocido. |
| U-WAM, ambas configuraciones | P.4 paciente, P.6 repetible; R.3.4–9 parámetro/método/dilución/tipo/rack/posición; R.4 valor/formato y R.9 celda UD-10. |
| QWALYS | P.3 número de muestra a cotejar con O.4; R.4.7 reacción; R.9 permite ver modificación por técnico. |

Son nueve perfiles contando el genérico. UC directo/UF binario conservan sus desgloses propios existentes; CFX CSV/RDML conserva la interpretación de exportaciones. No se les aplica un mapa P/O/R ficticio. Los campos no mapeados, extensiones y valores originales siguen visibles. El perfil sintético no publica un mapa propio.

## Verificación y límites

Corpus sintético por perfil: comparación con observaciones del mismo dialecto, varias jerarquías con secuencias repetidas, resultados huérfanos, ambos sentidos, mensaje incompleto, delimitadores alternativos, repeticiones, escapes, vacíos, subcomponentes HL7 y ausencia de PID. Lang registra las etiquetas por ensamblado y conserva los datos sin traducción. El self-test MAUI selecciona una fila real de Mensajes y verifica el detalle tanto con interpretación activada como desactivada.

Inspección local, sin nuevas dependencias, transporte, persistencia, permisos, cambios de derivación ni efectos sobre órdenes/aceptación de resultados. La interpretación documentada no reemplaza la homologación con el modelo/firmware real. Resultado final de las suites y publicaciones en el changelog del incremento.
