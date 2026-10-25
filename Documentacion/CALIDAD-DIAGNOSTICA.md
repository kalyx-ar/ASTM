# Calidad en Diagnostics

Decisión humana de EDC#205 (2026-10-25): priorizar **mediana y desviación estándar** para diagnóstico, con material/lote sin ofuscar y los registros reales en subfilas.

La vista resume por material, lote/nivel, prueba, representación y unidad. También separa instrumento/serial, método, módulo, reactivos y referencia de calibración, para no mezclar series diferentes. El identificador recibido se muestra literalmente; si falta lote, se indica, sin inferirlo del nombre.

Columnas: material/lote, prueba, tipo/unidad, mínimo, mediana/resultado, máximo, DE, mediciones, Sin medir y señales. Desplegar muestra cada observación original; la columna mediana/resultado contiene entonces su valor recibido. El detalle conserva estado, flags, fecha, identificación y estadísticas informadas por el equipo.

## Indicadores

- Mediana numérica: percentil 50 con interpolación lineal R7, calculada en decimal. En escalas ordinales se conserva la categoría central; si las dos categorías centrales difieren, se muestra su intervalo, sin inventar una categoría intermedia.
- DE: desviación estándar muestral con denominador n−1; Welford en double. No disponible con menos de dos mediciones numéricas.
- Mediciones: total de registros comunicados de la serie, incluidos cualitativos, duplicados, vacíos y no realizados. Cada registro permanece en una subfila; el contador no depende de que el valor sea numérico.
- Resultado compacto: mediana numérica, categoría ordinal central o categoría cualitativa más frecuente (p. ej., normal). Si hay empate cualitativo se indica Mixto. Las frecuencias completas quedan en detalle y los valores originales en subfilas; no se enumeran todos en la celda principal.
- Sin medir: columna con el total de registros comunicados cuyo estado es NotPerformed, incluso retransmitidos. No se repite en Mediana/resultado ni en Señales. Si todos están sin medir, el resultado queda vacío (—) y el conteo comunica la situación. Se distingue un valor vacío recibido de un análisis no realizado.
- Subconjunto numérico para las estadísticas: identidades de observación numéricas únicas. Comparadores/censurados, cualitativos y no realizados no entran en mediana/DE. Los duplicados cuentan como registros comunicados, pero no se convierten en réplicas numéricas nuevas. El detalle indica ambos conteos.
- Posibles atípicos: con al menos cuatro valores, se marcan los que están fuera de Q1−1,5·RIC y Q3+1,5·RIC. Cuartiles R7; los puntos siguen dentro del resumen y conservan sus alarmas. No es una regla de aceptación QC.
- Señales: posibles atípicos, alarmas/estados de advertencia del instrumento, escalas de orden no definido, duplicados y existencia de estadísticas del equipo en detalle.
- Detalle del resumen: media, último registro y método de cálculo. La media no ocupa una columna principal. La mediana numérica y DE muestran seis cifras significativas; extremos ordinales/límites mantienen su representación. El detalle conserva mayor precisión y las subfilas el texto original.

El resumen describe los datos de la captura seleccionada; no acredita un histórico completo. No se aplican límites de aceptación, objetivos ni reglas Westgard sin los datos y contexto correspondientes. Las estadísticas que el equipo ya envía en Quality.Statistics permanecen como metadatos de sus registros: no se convierten en réplicas adicionales.

Se evita un CV universal: su interpretación requiere una escala de razón y una media adecuada; no se aplica indiscriminadamente a Cq, pH u otras escalas. Referencias primarias: [NIST, coeficiente de variación](https://itl.nist.gov/div898/software/dataplot/refman2/auxillar/coefvari.htm), [NIST, box plot y cercas de 1,5 RIC](https://www.itl.nist.gov/div898/handbook/eda/section3/boxplot.htm). No se descartan valores automáticamente por su posición en la distribución.

## Conocimiento y selección Sysmex

Una entrada principal **Sysmex · U-WAM (UF-5000, UC-3500, UD-10)**, dentro del ensamblado Atsm.Plugin.Sysmex.Urinalysis. La configuración predeterminada es Interpretación automática; las configuraciones concretas quedan como elección manual. El perfil aplicado se resuelve por contenido también al reanalizar el caso, no por su extensión o nombre.

IInstrumentKnowledge consulta hechos por perfil/protocolo, registro, campo y código. ER04 en una respuesta de orden tiene una interpretación; el mismo texto en otro campo no hereda esa interpretación. Los códigos sin coincidencia documentada conservan el original. Se reutiliza la base de conocimiento, manteniendo decodificadores de encuadre separados. Identificar un formato compatible no acredita la identidad física de un modelo.

En captura sin respuestas puede resolverse el intérprete conforme llegan bytes. El modo automático no activa un anfitrión ASTM por suposición: para simularlo se elige expresamente una configuración compatible. No se agregan órdenes activas UC-3500. Capturas con protocolos incompatibles mezclados se declaran ambiguas.

Se retiraron los proyectos/DLL separados Uwam y Uc3500, sin aliases. Los identificadores de perfiles de los casos y los sistemas de códigos de datos conservan su significado: no son copias de ejecución antiguas. Lang usa el seed del nuevo ensamblado de familia. Contratos y persistencia siguen neutrales; la custodia corresponde al anfitrión.

Pruebas: medianas/DE conocidas, resultados cualitativos y frecuencias, conteo de registros comunicados, valores codificados, atípicos conservados, series separadas, censurados/no realizados/duplicados, estadísticas reportadas sin doble conteo, extremos decimales, resumen de una observación, contexto de códigos y selección U-WAM↔UC/ASTM. La GUI prueba la selección automática/manual y sus tablas; NativeAOT ejecuta el mismo cálculo.


En la captura local del UC-3500, los identificadores de los cuatro registros de control ya llegan como asteriscos. El diagnosticador los conserva literalmente: no son una máscara de la UI y no se reconstruye un material/lote que el equipo no comunicó.


## Semicuantitativos y límites (2026-10-25)

El plugin Sysmex usa IInstrumentValueKnowledge para describir la escala según el parámetro y el campo. En los campos de tiras documentados por UC-3500, valores como 2+ se muestran como Semicuantitativo (cruces). El rango de cruces puede mostrarse de − a 2+ y la mediana corresponde a una categoría real. No se convierte 2+ en concentración 2 ni se calcula media/DE con rangos ordinales. + y 1+ no se igualan cuando el formato los distingue. El orden se comprueba por parámetro; NIT conserva la etiqueta Cualitativo (cruces) para su salida binaria −/+. Categorías desconocidas se conservan, sin suponer su orden.

>=50 se interpreta como límite inferior y se muestra en Mínimo; <=50 se muestra en Máximo. Se conserva el comparador y el otro extremo queda no acotado (—). El Numeric=50 que acompaña a Comparator=>= es el umbral comunicado, no una medición exacta para las estadísticas. También se admiten >, < y símbolos equivalentes al analizar valores ya entregados por el anfitrión; el cable UC mantiene su especificación ASCII.

En una serie con resultados acotados en sentidos opuestos puede no existir un extremo finito común: se deja — en lugar de fabricar el intervalo [50,20]. Todos los límites siguen en el detalle. Los valores exactos se mantienen en el subconjunto estadístico separado. La fuente para órdenes y categorías del UC es su manual v0.0.5, tablas 6-17/6-18, enlazado en UC3500-PROTOCOLO.
