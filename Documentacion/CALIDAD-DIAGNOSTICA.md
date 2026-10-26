# Calidad en Diagnostics

Decisión humana de EDC#205 (2026-10-25): priorizar **mediana y desviación estándar** para diagnóstico, con material/lote sin ofuscar y los registros reales en subfilas.

La vista resume por material, lote/nivel, prueba, representación y unidad. También separa instrumento/serial, método, módulo, reactivos y referencia de calibración, para no mezclar series diferentes. El identificador recibido se muestra literalmente; si falta lote, se indica, sin inferirlo del nombre.

Columnas: material/lote, prueba, Tipo, Unidad, mínimo, mediana/resultado, máximo, DE, Mediciones, Sin medir y Estado. Tipo y Unidad son datos independientes, compartidos con Observaciones. Desplegar muestra cada observación original con su estado; la columna mediana/resultado contiene entonces su valor recibido. Fecha/secuencia, flags, identificación y estadísticas reportadas permanecen en detalle.

## Indicadores

- Mediana numérica: percentil 50 con interpolación lineal R7, calculada en decimal. En escalas ordinales se conserva la categoría central; si las dos categorías centrales difieren, se muestra su intervalo, sin inventar una categoría intermedia.
- DE: desviación estándar muestral con denominador n−1; Welford en double. No disponible con menos de dos mediciones numéricas.
- Mediciones: total de registros comunicados de la serie, incluidos cualitativos, duplicados, vacíos y no realizados. Cada registro permanece en una subfila; el contador no depende de que el valor sea numérico.
- Resultado compacto: mediana numérica, categoría ordinal central o categoría cualitativa más frecuente (p. ej., normal). Si hay empate cualitativo se indica Mixto. Las frecuencias completas quedan en detalle y los valores originales en subfilas; no se enumeran todos en la celda principal.
- Sin medir: columna con el total de registros comunicados cuyo estado es NotPerformed, incluso retransmitidos. No se repite en Mediana/resultado. Estado conserva ese dato y su error asociado cuando existe. Si todos están sin medir, el resultado queda vacío (—) y el conteo comunica la situación. Se distingue un valor vacío recibido de un análisis no realizado.
- Subconjunto numérico para las estadísticas: identidades de observación numéricas únicas. Comparadores/censurados, cualitativos y no realizados no entran en mediana/DE. Los duplicados cuentan como registros comunicados, pero no se convierten en réplicas numéricas nuevas. El detalle indica ambos conteos.
- Posibles atípicos: con al menos cuatro valores, se marcan los que están fuera de Q1−1,5·RIC y Q3+1,5·RIC. Cuartiles R7; los puntos siguen dentro del resumen y conservan sus alarmas. No es una regla de aceptación QC.
- Estado: Normal, Baja fiabilidad, Error de análisis u otro significado documentado. Un grupo homogéneo muestra sólo ese estado; si mezcla estados conserva sus frecuencias, con las alertas primero. Las subfilas muestran el estado del registro. Ya no se muestra Una medición ni un listado de avisos técnicos en esta columna; atípicos, duplicados, escalas sin orden y estadísticas reportadas permanecen en detalle/resaltado.
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


## Negativo y ausencia de medición (2026-10-25)

El manual UC-3500 distingue estado 0 (analizado) de estado 1 (no analizado, campos rellenados con espacios); la explicación de campos contempla además un valor cualitativo negativo con concentración en blanco (tablas 6-7 y 6-17). Por tanto, el guion negativo de una escala documentada no se reclasifica como Sin medir por carecer de concentración numérica.

En las escalas de cruces que declaran − como categoría negativa, el modo interpretado muestra **0** (cero cruces) en observaciones/subfilas y extremos/categoría central. Esto no crea una concentración cero, ni un valor numérico para calcular media/DE. El valor original − permanece en el caso, distribución/detalle y modo técnico. Normal de URO conserva su texto; un guion de concentración, de otro parámetro/protocolo o de un estado no realizado no recibe esta equivalencia. El cambio es de representación ordinal, solicitado por el usuario, no una afirmación de ausencia química absoluta.

Sin medir sigue dependiendo del estado de análisis explícito. Un campo vacío no se convierte en cero; tampoco se reconstruye una medición ausente. El puerto neutral expone DisplayValue como metadato de presentación del plugin, sin modificar RawText ni Numeric.

## Indicadores e imágenes (2026-10-25)

El contador y el resaltado usan IInstrumentFlagKnowledge del plugin. N en U-WAM se interpreta como Normal y no es una alarma. Los indicadores LH conservan su propio contexto, incluso con el componente del analizador vacío; H no se convierte por ello en un error del analizador. Se mantienen los indicadores originales en el caso y el detalle.

IF es una imagen: tipo y resultado compactos «Imagen» en resumen y subfilas, con referencia/contenido original en detalle y modo técnico. IsStructuredContent impide calcular estadísticas numéricas de sus identificadores. No se abre ningún recurso externo ni se lo convierte en una medición. Los parámetros RP se identifican como parámetros de sensibilidad; no se inventan límites u objetivos.

## Tipo y Unidad independientes (2026-10-25)

La representación usa ResultKind/DescribeValue para Tipo y ObservationValue.RawUnit/Unit/DescribeUnit para Unidad. DescribeUnit es un puerto opcional independiente: no se concatena la unidad al tipo. Los códigos técnicos desconocidos se conservan en el original, mientras la vista interpretada usa Numérico, Semicuantitativo, Texto, Cualitativo o Estadísticas según la representación disponible; no muestra Tipo informado por el equipo seguido de valores mezclados. El comportamiento compartido se verifica contra todos los perfiles del catálogo.

DisplayText permite presentar un valor codificado mediante el seed/Lang del plugin, independiente de DisplayValue (representación literal). Observaciones y Calidad lo usan sin reescribir RawText ni el código. Los indicadores de investigación U-WAM y los índices Q-Flag XN-L no son concentraciones ni réplicas: permanecen fuera de mediana/DE/extremos numéricos. Un comentario RC documentado deriva un indicador de revisión identificado como `:review-comment`, conservando los flags recibidos; no lo convierte en error de análisis. Ver [tablas incorporadas](DIAGNOSTICO-MANTENIMIENTO.md#tablas-de-conocimiento-ampliadas-2026-10-25).

En el corpus UF-5000, los 33 registros QC tienen R.4 compuesto y R.5 con unidades repetidas o componentes vacíos. R.4.2 no se considera formato salvo los códigos documentados RAW/MAINFORMAT/RAW_Q/RAW_C/RAW_R1/RAW_R2; los componentes originales se conservan sin atribuirles estadísticas no confirmadas. La identificación QC situada en O.4.3 cuando O.3 está vacío se conserva literalmente, sin inferir lote. R.3.5 sólo identifica método cuando contiene A/M; un código de prueba del formato directo UF no se convierte en método. Estos ajustes corrigen el tipo dependiente del valor, el material vacío y el método espurio; evidencia derivada de la traza, no homologación del protocolo directo.

La unidad ch^ se presenta como ch y /uL^/uL como /uL, conservando el campo original. No se elimina un exponente legítimo como 10^3/uL. Sólo se colapsan componentes vacíos o duplicados, sin conversiones ni equivalencias físicas inventadas.

Un resultado Coded conserva su carácter cualitativo aunque su código sea un dígito y exista un Numeric derivado. Ese código no participa en mediana, DE ni extremos numéricos; permanece en los originales y frecuencias. La regla compartida evita interpretar categorías codificadas de otros plugins como cantidades.

UC-3500: el manual v0.0.5 tabla 6-17 define reflectancias R%(1)/R%(2) en %, y pH/densidad relativa son adimensionales; los cualitativos/color no tienen unidad física. El registro no transmite un campo de unidad de concentración, por lo que se muestra No informada; no se deduce mg/dL, mmol/L u otra unidad por magnitud, nombre de archivo o valores típicos. Una unidad presente en el mensaje tiene prioridad sobre la presentación del plugin. 00CC se interpreta como Error de solicitud (00CC), sin Advertencia ni Código sin interpretar; estado 0 sin flags se presenta como Analizado, sin equipararlo a validación clínica.

En UF-5000 local los 33 registros de control llevan N. Los indicadores W (1), > (1) y A (26) pertenecen a otras observaciones de la captura; no se trasladan artificialmente a un grupo QC. Las mediciones recibidas con alertas conservan sus valores y participan en las estadísticas descriptivas salvo NotPerformed; no se filtran silenciosamente ni se aceptan clínicamente.
