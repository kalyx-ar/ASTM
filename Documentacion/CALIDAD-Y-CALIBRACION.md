# Datos analíticos para control interno y calibración

Investigación e implementación del 2026-10-24, [EDC#205](https://github.com/kalyx-ar/EDC/issues/205), alcance previo [registrado aquí](https://github.com/kalyx-ar/EDC/issues/205#issuecomment-6391823820). Se refiere al proyecto ATSM y a las interfaces ASTM/HL7/exportaciones específicas de sus plugins; compartir un protocolo no garantiza que todos los modelos transmitan los mismos datos.

## Hallazgo y separación de responsabilidades

Hay tres clases distintas de información aprovechable: **mediciones individuales** sobre controles/calibradores (incluidas señales previas a conversión), **estadísticas reportadas por el software del fabricante** y **valores asignados/metadatos** del material, reactivo y calibración. Un rango de control no identifica por sí solo una media objetivo ni un desvío estándar. Un resultado con estado final tampoco acredita aceptación QC o validación clínica.

ATSM conserva datos y procedencia, interpreta el dialecto y expone puertos de lectura. El anfitrión custodia, autoriza y proporciona historia, catálogos y políticas del laboratorio. No se agregan comandos de calibración, descarga universal de históricos, aceptación automática de controles ni reglas clínicas universales. Una captura puede ser parcial: no se infiere que contenga todas las mediciones realizadas por el instrumento.

## Matriz de evidencia por interfaz

| Interfaz y fuente primaria | Datos comprobados en su documentación | Implementado en este incremento / límite |
|---|---|---|
| Abbott ARCHITECT ASI, manual local 91407-108, §2, tabla de registro R pp.2-16/17 | O.12=Q; R.3.10 lote de control, R.3.11 tipo F/P/I; P puede ser señal RLU/Abs/mV, R.6 rango de control, R.7 flags incluidos Westgard, R.14 módulo | Contexto QC tipado de lote/módulo; se conservan señales, tipo, rango y flags existentes. No se convierte rango en media/DE ni se recalculan flags. No se acredita un protocolo universal de calibración ASTM. |
| ARCHITECT HL7, 206797-101 §2-13 y §3-23/26, §3-97 | OUL/R22 distingue SPM.11 Q/C/P. INV identifica material, lote y vencimiento; SID identifica reactivos; ZCL relaciona ensayo/lote/fecha de calibración | Material/lote/vencimiento, módulo y referencia ZCL por OBR; sin arrastrar calibración de otra orden. Referencia discordante genera aviso. SID y los resultados P/F/I permanecen disponibles. Nivel no separado en la interfaz queda ausente. |
| ARCHITECT HL7, 206797-101 §2-17, §3-57/64 y apéndice A | **OUL/R24** transmite resultado de calibración exitosa o fallida; OBX.5 usa ED con texto de archivo de calibración codificado en Base64, variable según modelo matemático | Documentado para siguiente incremento; el perfil actual R22 **rechaza R24**. No se declaran sus coeficientes/estadísticas accesibles mediante el decodificador implementado. Ver puerto propuesto abajo. |
| Sysmex XN-L, guía local 1273-MKT rev.3 §5 p.14 y ejemplos pp.37/38 | Dos salidas QC configurables: identificación QC-lote desde Sample Explorer o identificador de archivo QC desde QC Chart; O.12=Q | Reconoce control por O.12, conserva identificador y serial. Extrae lote sólo del formato documentado `QC-...`; un número de archivo/XbarM no se convierte en lote o control comercial. Nivel/objetivo/DE no se inventan. |
| Sysmex CS-2500, ASTM Host Interface rev.2.04 pp.16,28,30,32/33 | QC por O.12=Q; ID de ancho fijo QC + nombre de control(7) + lote(6). R.3.7 distingue medición, promedio, reanálisis y promedio de reanálisis | Material/lote tipados sólo si coincide el formato. Tipo/dilución/flags crudos permanecen; un promedio del equipo no se usa como una réplica individual y no revela su n/DE. No se dereferencian imágenes/rutas. |
| Sysmex U-WAM R315168 rev.22 pp.39/40,43,47, §5.3.3.7 y apéndice J | QC RAW o varios formatos en química; identificador combina lote/nivel/método según equipo/configuración; R.14 identifica serial para QC. S.7 anuncia CSV externo de objetivos/límites y metadatos del lote | Conserva identificador compuesto, método, formato y serial; no separa ambiguamente lote/nivel. S.7 se entrega como `QualityResourceReference` en el mismo lote custodiado, incluso si no hay observaciones. **No abre el CSV** ni interpreta sus límites todavía. |
| Sysmex SUIT v8.0, manual local §3.3.5 y §5.2.6 | QC por registros S propios; puede informar archivo QC o identificador QC-lote, parámetro, módulo y modo | Investigación documentada; no hay plugin SUIT. No pasar sus registros S al plugin U-WAM: las posiciones y el significado son distintos. |
| Bio-Rad CFX Maestro 2.3, manual 10000126764B pp.202–204, tablas 19/20 | Exporta cuantificación, estadísticas de réplicas y parámetros de curva | CSV de cuantificación conserva estadísticas opcionales y tipo de material. **Nuevo perfil CSV de curva estándar** conserva eficiencia, pendiente, ordenada y R². Ámbitos pozo/réplicas/curva explícitos; no se recalculan los valores. RDML conserva tipo de control/estándar y corrida. |
| DIAGAST QWALYS 3, Connection Manual local §§4.5–4.7 | Reacciones, identificación de ensayo/reactivo, niveles y modificación del resultado | Permanece la interpretación de resultados existente. El manual revisado no acredita una exportación de medias/DE/CV o curva de calibración: no se presenta como un canal de estadísticas QC. |

Manuales locales bajo `manuales/` ignorado: no se distribuyen ni se copian sus ejemplos reales al código. Fuentes públicas primarias: [CFX Maestro 2.3](https://www.bio-rad.com/sites/default/files/webroot/web/pdf/lsr/literature/10000126764.pdf), [esquemas RDML del consorcio](https://github.com/RDML-consortium/rdml-schema), [IHE PaLM TF-2b rev.11](https://www.ihe.net/uploadedFiles/Documents/PaLM/IHE_PaLM_TF_Vol2b.pdf). LAW incluye datos de pacientes y QC y conserva identidad de equipo/observación y flags (apéndices B/C); no demuestra compatibilidad de cualquier extensión de calibración del fabricante. ARCHITECT ZOS/R24 y LAW siguen siendo perfiles distintos.

## Contratos implementados

Disponibilidad del dato: en ARCHITECT ASI (§2-3/4) la transmisión QC depende de liberación, opciones de envío y conexión habilitada; las excepciones también tienen condiciones de liberación. En XN-L (§5) puede habilitarse una, ambas o ninguna salida QC, con riesgo de recibir dos representaciones de una misma medición. Registrar esta configuración y el período cubierto antes de interpretar tendencias, faltantes o recuentos; el lector no transforma la captura en un histórico completo.

`Atsm.Abstractions.Model.AnalyticalQualityContext`, opcional en `InstrumentObservation.Quality`, conserva:

- `QualityMaterial`: tipo explícito, identificador, lote, nivel y vencimiento **crudo** si se transmiten. Un estándar qPCR tiene tipo `Standard`; no se etiqueta como calibrador instrumental por inferencia.
- Módulo, corrida y método cuando hay evidencia de su significado. El nombre de middleware no sustituye al serial del analizador que midió.
- `CalibrationReference`: ensayo/lote/fecha cruda de la calibración referida; no es el informe de su curva ni un certificado de éxito.
- `ReportedStatistic`: valor original y valor decimal opcional, tipo, ámbito y campo fuente. Se trata de salida del software instrumental, no de un cálculo del anfitrión. No se inventan unidades, n, valores asignados ni grupo de réplicas desconocido.
- `SourceFields`: campos adicionales necesarios para inspección y futura reinterpretación. `TraceRef`, ObservationId, BatchId, flags, tipo de resultado, rango, reactivos y tiempo existentes siguen viajando.

`InstrumentObservationBatch.QualityResources` contiene referencias externas tipadas. `AstmMessageProcessor` asigna identidades estables por recepción y custodia estas referencias mediante **el mismo `ILabWorkflowHost.IngestAsync`** que las observaciones; un lote puede contener sólo recursos. El anfitrión debe almacenar el lote completo antes de `Accepted`. Aceptar una referencia no acredita que el archivo externo exista, esté completo o haya sido adquirido. No se crea una segunda escritura no atómica ni un almacén propio de ATSM.

Los perfiles modificados ASTM/HL7/CFX pasan a versión 1.1.0; el nuevo perfil `biorad.cfx.maestro.standard-curve-csv` empieza en 1.0.0. Los constructores canónicos existentes siguen siendo válidos (propiedades opcionales inicializadas); los consumidores deben actualizarse para custodiar el contexto y los lotes que sólo contienen referencias. La clave de deduplicación del anfitrión continúa siendo la identidad de recepción/lote, no la igualdad del valor numérico.

### Puertos de aplicación de sólo lectura

`IQualityDataSource` ofrece dos lecturas asíncronas:

```csharp
IAsyncEnumerable<QualityDatum> ReadAsync(QualityDataQuery query, CancellationToken token);
IAsyncEnumerable<QualityResourceReference> ReadResourcesAsync(QualityResourceQuery query, CancellationToken token);
```

`ObservationQualityDataSource` es el adaptador implementado en `Atsm.Runtime.Quality`. Recibe una función que lee los lotes custodiados por el anfitrión; no conecta al equipo ni guarda copias. Ejemplo de composición (el lector durable pertenece al anfitrión):

```csharp
IQualityDataSource source = new ObservationQualityDataSource(
    token => host.ReadCommittedBatchesAsync(token));

var query = new QualityDataQuery(
    InstrumentId: "XN-L-01",
    Test: new CodedValue("WBC", null, CodeSystems.Vendor("sysmex.xnl")),
    MaterialKind: QualityMaterialKind.Control,
    MaterialLot: "LOTE-CONFIRMADO",
    ModuleId: "SERIAL-CONFIRMADO",
    FromInclusive: inicio, UntilExclusive: fin);

await foreach (var item in source.ReadAsync(query, token))
    await consumidor.RecibirAsync(item, token);
```

La selección usa igualdad exacta de instrumento, **código y sistema** del ensayo, tipo/lote/módulo y traza; ventana temporal `[inicio, fin)`. Una fecha desconocida no cumple un filtro temporal, y un lote desconocido no cumple un filtro por lote. No se infiere por nombre o posición. Las observaciones marcadas `Patient` se excluyen por defecto y requieren opción explícita; esa opción **no es una autorización** y los controles también pueden contener identificadores sensibles. El anfitrión aplica permisos antes de suministrar datos.

Se conservan repeticiones, correcciones, flags, resultados no numéricos y excluidos: el lector no los filtra silenciosamente ni fusiona filas. Un `Unknown` no se vuelve paciente ni control por el nombre de muestra. Un filtro no certifica que los datos formen una serie estadística comparable. En producción el anfitrión debe filtrar/paginar desde su almacén; el adaptador genérico recorre los lotes que se le suministran.

`IQualityResourceContent.OpenReadAsync(reference, token)` define el puerto opcional que implementa **el anfitrión/ETL**, retornando un Stream que el consumidor debe cerrar. No tiene implementación de red/disco en ATSM ni se invoca automáticamente. Debe autorizar el localizador no confiable contra recursos configurados, comprobar formato/límites, identificar la versión/hash y tomar custodia del contenido. El localizador de S.7 se preserva crudo; no es permiso para abrir una ruta UNC o URL arbitraria. El importador tipado de CSV U-WAM y su asociación de lote/objetivos/límites siguen pendientes; Scheduler/ETL puede resolver la referencia sin incorporar filedrop al motor.

## CFX y mantenimiento

El CSV de cuantificación acepta opcionalmente `Content`, `Cq Mean`, `Cq Std. Dev`, `Starting Quantity (SQ)`, `Log Starting Quantity`, `SQ Mean` y `SQ Std. Dev`. Se mantienen sus valores originales y ámbito; media/DE repetidas por cada fila no son nuevas réplicas ni valores objetivo del control. Sin identificación completa de grupos/n no se combinan estadísticas de réplicas entre archivos. La cultura/separador siguen siendo explícitos mediante `CfxCsvOptions`; la GUI usa el formato de exportación predeterminado en inglés.

Para curva estándar seleccionar el nuevo perfil y abrir un CSV con `Target` (o `Fluor`/`Fluor (or Target)`), `Efficiency %`, `Slope`, `Y-intercept`, `R^2`. La observación contenedora tiene tipo `standard-curve`, sin fabricar muestra o medición escalar; sus cuatro estadísticas pertenecen a esa fila/curva. Eficiencia porcentual conserva su escala original. Datos ausentes/no numéricos no se sustituyen por cero; error estructural rechaza el archivo completo. No se asignan umbrales de aceptación, no se ajusta la curva ni se identifica calibración de hardware a partir de esa exportación.

La herramienta Windows incorpora la pestaña **Calidad**, con medición, estadística reportada, ámbito y campo de origen; el detalle incluye lote/módulo/reactivos/calibración/flags. También muestra referencias externas U-WAM sin abrirlas. Se reutiliza la custodia DPAPI existente y el caso conserva la captura original para reanalizar. La vista es de diagnóstico, no una implementación del programa de control interno del laboratorio.

## Puerto documentado para el informe de calibración y próximas extensiones

El siguiente incremento R24 debe agregar un `CalibrationReport` tipado, custodiado de forma atómica con el lote, y una lectura `ReadCalibrationReportsAsync` sobre el almacén del anfitrión. Campos mínimos: identidad estable y traza, equipo/módulo, ensayo, lote/serial de reactivos y materiales calibradores, fecha original, estado del fabricante, tipo/modelo matemático si está informado, documento ED original/decodificado y su formato/hash. No se deben extraer coeficientes suponiendo un formato fijo: el fabricante declara que el texto varía con el modelo y tipo de calibración. Primero conservar documento y procedencia; luego decodificadores versionados con corpus por modelo. **Es una especificación de extensión, no un puerto ya implementado ni conformidad LAW.**

También quedan las curvas de reacción/series RFU y señales extendidas con su eje/unidad/ciclo/temperatura, importador de valores asignados/límites U-WAM, adaptación SUIT y metadatos faltantes de otros modelos. Los límites establecidos en el instrumento o tablas de valores asignados no siempre viajan por el protocolo; obtenerlos exige su exportación documentada o catálogo del anfitrión. No usar el ancho de límite U-WAM como DE sin confirmar su definición.

Para un servicio estadístico del anfitrión hacen falta series homogéneas por equipo/módulo, ensayo/sistema, tipo de señal, método/dilución, material/lote/nivel, unidad y cambio de reactivos/calibración; política explícita de correcciones/reenvíos/exclusiones y tiempo/corrida. Levey–Jennings, media/DE/CV, sesgo y reglas Westgard requieren distinguir lo **reportado**, lo **asignado** y lo **calculado**, conservando versión/selección. Esta entrega habilita sus entradas; no implementa esas decisiones ni sustituye su validación.

En EDC corresponde implementar los puertos en Application/Infrastructure sobre SQLCipher, transacción del lote completo, autorización UI y exposición deliberada a ETL/DataSets/API según alcance. Un contrato CLR no crea automáticamente una API remota, herramienta IA ni sincronización. No se crea un módulo EDC nuevo, ni se alteran Identidades, derivación, firma o baselines de confianza. No hay dependencias nuevas.

Validación: casos sintéticos de CFX/roles/estadísticas, mapeos QC por fabricante, INV/ZCL por orden, referencias U-WAM sin observaciones e identidad al reintentar, serialización, filtros y cancelación del lector; build y regresión .NET/nativa repetidos, DPAPI/arranque MAUI (WPF retirado el 2026-10-25). Conteos/commits en ESTADO y changelog. Sin equipo físico, históricos completos ni corpus real QC validado.
