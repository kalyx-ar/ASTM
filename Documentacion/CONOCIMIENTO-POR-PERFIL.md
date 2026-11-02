# Conocimiento instrumental consultable por perfil

Incremento #205, 2026-11-02, versión 0.6.5. [Autorización previa](https://github.com/kalyx-ar/EDC/issues/205#issuecomment-6586179676). Complementa [calidad/calibración](CALIDAD-Y-CALIBRACION.md) y [ARCHITECT HL7](CALIBRACION-ARCHITECT.md), sin sustituir el catálogo de operaciones.

## Contrato

Los plugins reales aportan `IInstrumentProfileKnowledge.DescribeKnowledge(profileId)`: hechos compilados con ID, perfil exacto, tema, canal, título/detalle, evidencia y estado. Un perfil ajeno devuelve una lista vacía. Colecciones de sólo lectura y textos `ProtocolText` con source del ensamblado propietario.

| Estado | Significado |
|---|---|
| Implemented | Existe la interpretación o tratamiento descrito. No acredita homologación física ni toda la interfaz. |
| Documented | El fabricante documenta el dato o servicio externo; falta su integración específica. |
| Unconfirmed | Evidencia insuficiente en esta interfaz. No significa que el equipo sea incapaz de calibrar. |
| Excluded | Exclusión expresa del fabricante para esa interfaz, como calibradores ARCHITECT ASI. |

`OperationPurpose` sólo se admite en hechos implementados y se contrasta con operaciones reales. SNCS, mantenimiento o una curva documentada no crean comandos. No hay conexión a servicios, apertura automática de rutas, polling ni decisiones clínicas nuevas.

Títulos/detalles tienen claves `knowledge.<id>.title/detail`, fallback español y evidencia. Diagnostics registra esos seeds por ensamblado (incluidos CFX/HL7/Ruby) mediante el adaptador Lang existente, sin cambiar datos originales ni la biblioteca Lang. La GUI los muestra en el detalle del catálogo, con estado y procedencia. Es metadata local consultable; no acredita una herramienta IA, proveedor remoto ni knowledge de uso registrado en otro sistema.

## Consulta

```text
astm-trace knowledge sysmex.cs2500.astm02.results
astm-trace knowledge all
```

El CLI devuelve JSON generado para NativeAOT, sin conexión al analizador ni datos de pacientes. EDC consulta `LimsMedicalProviders.Knowledge(profile)` sobre proveedores registrados; HL7 conserva los presupuestos reales obligatorios para abrir operaciones/sesiones.

## Conocimiento y datos reutilizables

| Familia/interfaz | Disponible | Límites y oportunidades |
|---|---|---|
| ARCHITECT ASI | Condiciones de envío QC, lote/módulo/rango, F/P/I para concentración/señal/interpretación. | 91407-108 §2-3 excluye calibradores. Rango no equivale a media objetivo/DE. |
| ARCHITECT HL7 | R24, INR/U06, INU/U05, ZCL, ensayos/lotes/seriales y campos originales de cantidad/unidades/vencimiento/estabilidad. | INV.19 puede ser estabilidad de inventario o calibración según el estado. ESR/U02, ESU/U01, EAN/U09 y servicio/configuración están documentados (§2-21/43), sin bindings/modelos/observadores propios implementados. |
| Sysmex CS-2500 | Errores de calibración R.7.2, máscaras R.4, QC/lote, tipo medición/promedio/reanálisis, dilución y repeticiones R.3.11. | Envío de errores/repeticiones configurable. Sin consulta de calibración confirmada. Archivos de reacción/tabla 15 necesitan adquisición autorizada e importador del host. |
| Sysmex XN-L | QC-lote frente a QC-archivo/XbarM, riesgo de doble representación, mensajes IP/acciones/flags contextuales. | 1273-MKT rev.3 p.40 y [SNCS oficial](https://www.sysmex.com/en-us/company/service-and-support): verificación remota por servicio propietario separado del LIS; no integrado. |
| U-WAM | Identificador QC compuesto, método/formato/serial, S.7, revisiones RC/RP e imágenes por modelo. | R315168 §5.3.3.7/apéndice J: CSV de información/objetivos/límites, referencia conservada pero importación/asociación pendientes. No abrir rutas automáticamente ni deducir DE de límites. |
| UC-3500 directo | Serie C de controles, reflectancias R%(1)/R%(2), tira y errores. | Pasivo, calibración no identificada; ER00 no confirma ejecución física. |
| UF binario | Avisos conocidos de preparación/limpieza y contadores/niveles reconstruidos. | Necesidad no equivale a ejecución. Unidades/asignación física/calibración sin confirmar; no convertir contadores en porcentajes de reactivo. |
| Sysmex SUIT | v8 §3.3.5/4.8/5.2.6 documenta protocolo y QC propios; advertencia incluida en conocimiento XN-L. | Sin plugin/perfil ficticio. No usar su estructura con XN-L/U-WAM. |
| Bio-Rad CFX | Cq/SQ y estadísticas con ámbito, material/exclusiones RDML y curva estándar con eficiencia/pendiente/ordenada/R². | [Maestro 2.3](https://www.bio-rad.com/sites/default/files/webroot/web/pdf/lsr/literature/10000126764.pdf) pp.206/212/213: series RFU/fusión por pozo/ciclo requieren corpus/ejes/unidades/contrato. Curva estándar y calibración de colorantes/hardware son distintas. |
| QWALYS | Reactivo/donante, ensayo, pocillo/nivel/soporte, operador y modificación. | ID de reactivo no se convierte en lote. Append (§2.3) exige cursor/checkpoint y mensajes completos del host; no hay consulta de calibración documentada. |
| CELL-DYN Ruby | Resultados, alarmas, órdenes y ALL con evidencia de trazas. | Falta manual: calibración y detalles no confirmados siguen como hipótesis, sin heredar funciones de ARCHITECT. |

Fuentes locales ignoradas: CS-2500 rev.2.04 pp.28/31-35/53/55 y tabla 15; XN-L 1273-MKT rev.3 pp.14/37-40; R315168 rev.22; SUIT v8; QWALYS Connection Manual; Abbott 91407-108/206797-101. No se incorporan manuales ni trazas reales al repositorio.

## Mejora CS-2500

R.4 puede usar las máscaras de p.33 sin error ampliado: X (sin curva validada), * (fallo de análisis), / (fallo de promedio), + (overflow) y - (fallo de cálculo). Se exige contexto de medición y máscara en la parte entera; números con signo, rutas, texto y mezclas ambiguas no se reclasifican.

La máscara genera un flag derivado `:measurement-mask` y diagnóstico propio, sin fabricar el código del fabricante `0000.4000.0030`. No duplica la causa ya explicada por información ampliada; un error ajeno no oculta una máscara específica conocida. Valor, flags originales y traza permanecen; no se reemplaza por cero ni se certifica calibración por ausencia de error. CS-2500 pasa a perfil 1.2.0.

`VendorIdentity["analysis.replications"]` conserva literalmente R.3.11 cuando se informa. No crea n, medias/desvíos desconocidos ni aumenta el conteo de observaciones; promedio no equivale a réplica individual y envío manual no implica medición manual.

## Verificación y continuidad

Pruebas por perfiles reales: procedencia/aislamiento, estados sin comandos ficticios, vínculos a operaciones existentes, colecciones/JSON/AOT, traducción por ensamblado y máscaras/contrapruebas/prioridad. GUI/CLI verificados por self-test de la salida real; EDC descubre conocimiento sin abrir un canal. Evidencia final en ESTADO/changelog.

Oportunidades documentadas trazadas en #205: CSV QC U-WAM; series RFU/fusión CFX; estado/notificaciones/servicio/configuración ARCHITECT; archivos de reacción CS-2500; SUIT con corpus y contratos. Se mantienen como documentadas, sin backlog paralelo ni afirmar implementación. Homologación física y #408/VCOM conservan sus límites.
