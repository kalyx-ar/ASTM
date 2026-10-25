# Calidad en Diagnostics

Decisión humana de EDC#205 (2026-10-25): priorizar **mediana y desviación estándar** para diagnóstico, con material/lote sin ofuscar y los registros reales en subfilas.

La vista resume por material, lote/nivel, prueba, representación y unidad. También separa instrumento/serial, método, módulo, reactivos y referencia de calibración, para no mezclar series diferentes. El identificador recibido se muestra literalmente; si falta lote, se indica, sin inferirlo del nombre.

Columnas: material/lote, prueba, tipo/unidad, mínimo, mediana/valor, máximo, DE, n numéricas y señales. Desplegar muestra cada observación original; la columna mediana/valor contiene entonces su valor recibido. El detalle conserva estado, flags, fecha, identificación y estadísticas informadas por el equipo.

## Indicadores

- Mediana: percentil 50 con interpolación lineal R7, calculada en decimal.
- DE: desviación estándar muestral con denominador n−1; Welford en double. No disponible con menos de dos mediciones numéricas.
- n: identidades de observación numéricas únicas. Comparadores/censurados, no numéricos y no realizados no entran al cálculo; permanecen visibles con su contador. Duplicados de la misma identidad permanecen en subfilas, sin inflar n.
- Posibles atípicos: con al menos cuatro valores, se marcan los que están fuera de Q1−1,5·RIC y Q3+1,5·RIC. Cuartiles R7; los puntos siguen dentro del resumen y conservan sus alarmas. No es una regla de aceptación QC.
- Señales: posibles atípicos, alarmas/estados de advertencia del instrumento, valores no numéricos/censurados, no realizados, duplicados y existencia de estadísticas del equipo en detalle.
- Detalle del resumen: media, último registro y método de cálculo. La media no ocupa una columna principal. Las columnas muestran seis cifras significativas; el detalle conserva mayor precisión y las subfilas el texto original.

El resumen describe los datos de la captura seleccionada; no acredita un histórico completo. No se aplican límites de aceptación, objetivos ni reglas Westgard sin los datos y contexto correspondientes. Las estadísticas que el equipo ya envía en Quality.Statistics permanecen como metadatos de sus registros: no se convierten en réplicas adicionales.

Se evita un CV universal: su interpretación requiere una escala de razón y una media adecuada; no se aplica indiscriminadamente a Cq, pH u otras escalas. Referencias primarias: [NIST, coeficiente de variación](https://itl.nist.gov/div898/software/dataplot/refman2/auxillar/coefvari.htm), [NIST, box plot y cercas de 1,5 RIC](https://www.itl.nist.gov/div898/handbook/eda/section3/boxplot.htm). No se descartan valores automáticamente por su posición en la distribución.

## Conocimiento y selección Sysmex

Una entrada principal **Sysmex · U-WAM (UF-5000, UC-3500, UD-10)**, dentro del ensamblado Atsm.Plugin.Sysmex.Urinalysis. La configuración predeterminada es Interpretación automática; las configuraciones concretas quedan como elección manual. El perfil aplicado se resuelve por contenido también al reanalizar el caso, no por su extensión o nombre.

IInstrumentKnowledge consulta hechos por perfil/protocolo, registro, campo y código. ER04 en una respuesta de orden tiene una interpretación; el mismo texto en otro campo no hereda esa interpretación. Los códigos sin coincidencia documentada conservan el original. Se reutiliza la base de conocimiento, manteniendo decodificadores de encuadre separados. Identificar un formato compatible no acredita la identidad física de un modelo.

En captura sin respuestas puede resolverse el intérprete conforme llegan bytes. El modo automático no activa un anfitrión ASTM por suposición: para simularlo se elige expresamente una configuración compatible. No se agregan órdenes activas UC-3500. Capturas con protocolos incompatibles mezclados se declaran ambiguas.

Se retiraron los proyectos/DLL separados Uwam y Uc3500, sin aliases. Los identificadores de perfiles de los casos y los sistemas de códigos de datos conservan su significado: no son copias de ejecución antiguas. Lang usa el seed del nuevo ensamblado de familia. Contratos y persistencia siguen neutrales; la custodia corresponde al anfitrión.

Pruebas: medianas/DE conocidas, atípicos conservados, series separadas, censurados/no realizados/duplicados, estadísticas reportadas sin doble conteo, extremos decimales, resumen de una observación, contexto de códigos y selección U-WAM↔UC/ASTM. La GUI prueba la selección automática/manual y sus tablas; NativeAOT ejecuta el mismo cálculo.
