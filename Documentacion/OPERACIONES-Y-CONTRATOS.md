# Operaciones y contratos médicos interoperables (#416)

Implementación 0.6.3, 2026-11-01. [Issue propietario](https://github.com/kalyx-ar/EDC/issues/416). ASTM es neutral por industria; medicina se incorpora mediante contratos/formatos médicos opcionales o plugins especializados. Operación incluye datos, validación, preparación/interpretación y lifecycle.

## Arquitectura implementada

1. Astm.Operations: catálogo/schema/negociación, envelopes, recibos y snapshots neutrales, sólo BCL.
2. Astm.Abstractions: transporte, diagnósticos y trazas; sin PatientContext, WorkOrder, InstrumentObservation ni ILabWorkflowHost.
3. Astm.Formats.Medical: modelos y host médicos opcionales, contratos Orders/Answer/Decoded, reglas por perfil y codecs JSON generados.
4. Astm.Plugins: IMedicalInstrumentPlugin aporta IMedicalOperationProvider a las facets ASTM, HL7, contenido y protocolos propietarios. MedicalOperationAdapters reutiliza cada dialecto; MedicalOperationObservers aplica protocolo/reglas.
5. Astm.Runtime: sesiones con Operations/SendOperationAsync, MedicalExchangeProcessor común y OperationTraceSink.
6. EDC LIMS/Instruments: LimsWorkflowHost autorizado, LimsMedicalExchange y registro LimsMedicalProviders. Todas las marcas/formatos entregan el mismo contrato.

Se eligió núcleo neutral con especialización opcional frente a DTOs EDC, mensajes HL7 o recursos FHIR completos como ABI. Sin SDK FHIR, servidor obligatorio ni dependencia de EDC. El formato CLSI médico genérico funciona con mensajes completos sin instrumento físico.

## Catálogo y datos

IMedicalOperationProvider enumera MedicalProfiles y crea IMedicalOperations (Describe/Resolve/Select/Validate/Prepare/Decode) y un IMedicalOperationObserver por traza/generación.

| Propósito | Capacidad y alcance |
|---|---|
| medical.lab.order.submit | Prepare/Execute/Observe sólo con descarga de órdenes; no inicia físicamente una medición. |
| medical.lab.worklist.query | Decode/Observe de consultas soportadas. |
| medical.lab.worklist.answer | Prepare/Execute/Observe de respuesta correlacionada. |
| medical.lab.observation.report | Decode/Observe de datos comunicados. |
| medical.lab.export.import | Decode de contenido y lifecycle ContentImport, distinto de corrida física. |
| medical.lab.communication.observe | Recibos de mensajes propietarios cuando sólo está probada esa frontera. |

MedicalDataContracts.Orders, Answer y Decoded describen DTOs compartidos. El envelope JSON sirve al borde dinámico; la llamada local usa DTOs tipados. Mapped conserva equivalencias explícitas además del código original. Observaciones preservan unidades, estados crudos/interpretados, calidad, vendor identity y TraceRef.

Patient es opcional en el contrato común; cada perfil declara campos admitidos. SuppressControlPatient rechaza paciente en controles/calibradores que el builder omitiría. Pacientes diferentes para una misma muestra fallan antes de agrupar. ARCHITECT HL7 exige una muestra/acción por mensaje. No se inventa un paciente para satisfacer la cabecera.

La cabecera técnica pertenece al dialecto. WireBindings muestra campos fijos/generados H/MSH y mapeos de datos P/PID/O/OBR. Campo no declarado/prohibido, control de texto, encoding no representable, fecha/enum inválidos o consulta incoherente fallan antes de preparar/enviar. No se afirman máximos de bytes ni unidades que el perfil no documenta.

HL7 requiere OriginalMessage para preservar QPD/contexto; otros formatos lo prohíben. OperationId fija MSH-10 durante el reintento; la fecha técnica puede avanzar. La selección exige contrato/revisión exactos; metadata y payload preparado tienen copia propia.

## Perfiles implementados

| Perfil | Operaciones y frontera |
|---|---|
| CLSI médico genérico | Órdenes/queries/resultados; paciente opcional, P.14 médico/P.26 ubicación. Negativa L.I sólo resuelve una consulta pendiente inequívoca. |
| ARCHITECT ASI | Órdenes/resultados/queries, Q.X/cancelación por muestra, controles sin paciente y variante EOT del receptor declarada. |
| ARCHITECT HL7 | ZOS, OUL R22, OML/ORL, ACK y control ID/dirección; respuesta requiere su propio acuse. Sin afirmar LAW completo. |
| CELL-DYN Ruby | Órdenes New/Cancel, resultados y ALL. Positiva parcial no termina ALL; negativa final L.I sí. Evidencia de trazas; manual faltante no se sustituye con conjeturas. |
| Sysmex U-WAM | Dos perfiles, órdenes/resultados/queries; Q.3, O.19/O.26 y negativa O vacía según dialecto. |
| XN-L / CS-2500 / QWALYS | Recepción/observación; no anuncian envío/cancelación por compartir el contrato. |
| UC-3500 directo | Pasivo RQ02/CM00/ER/resultados. Recibos por muestra/posición/ciclo; ER00 confirma aceptación de respuesta, no fin de medición ni disponibilidad del canal. |
| UF binario | Mensajes/campos/avisos; frontera de operación/canal Unknown. No deduce fin por contadores. |
| CFX CSV/RDML | Datos comunes y ContentImport; completar importación no certifica corrida física. |
| Sintético/pruebas | Conformidad común, sin acreditar hardware real. |

Todos los perfiles incluidos tienen catálogo y observador. Frontera no demostrada se informa Unknown con evidencia explícita.

## Operativa común

Crear operaciones con provider.CreateOperations(profileId, new(instrument, clock)); consultar Select y Resolve; construir MedicalRequests.Orders/Answer con OperationId estable; Validate antes de Prepare. La sesión ASTM/HL7 expone SendOperationAsync(envelope, token) y conserva su handshake. Un preview preparado todavía no transmite.

El host prepara ITraceSink antes de abrir transporte. MedicalExchangeProcessor recibe contenido completo y recepción durable; genera InstrumentObservationBatch y llama a ILabWorkflowHost. Identidades estables derivan de recepción/perfil/posición, nunca del valor medido. Respuestas usan el presupuesto real del perfil y se devuelven preparadas; el orquestador conserva envío/disposición/reintentos.

LimsMedicalProviders descubre facets registradas y CFX. HL7 requiere los tiempos de la instalación. LimsInstrumentConnection/Monitor publican OperationSnapshot y cierre. Custodia cifrada, equivalencias, permisos y materialización permanecen en el host; sin timers/permisos/bases/servicios nuevos por marca.

Consultas concurrentes conservan correlación propia. ACK, aceptación de orden y ejecución física no se confunden. Unknown, gaps/corrupción, cierre y acuses ambiguos nunca certifican libre. [Contrato del observador](OPERACION-OBSERVADA.md).

## Semántica y referencias del análisis

PatientContext, Specimen, Observation y Device conservan significado médico reconocible. WorkOrder agrega solicitudes, sin equivalencia 1:1 con ServiceRequest. DiagnosticReport corresponde a un informe, no a un mensaje recibido. Operation/Receipt/State son neutrales; diagnósticos van separados.

Referencias conceptuales: [FHIR R5 OperationDefinition](https://hl7.org/fhir/R5/operationdefinition.html), [Task](https://hl7.org/fhir/R5/task.html), [ServiceRequest](https://hl7.org/fhir/R5/servicerequest.html), [Observation](https://hl7.org/fhir/R5/observation.html), [Specimen](https://hl7.org/fhir/R5/specimen.html), [DiagnosticReport](https://hl7.org/fhir/R5/diagnosticreport.html), [OperationOutcome](https://hl7.org/fhir/R5/operationoutcome.html) e [IHE PaLM TF-2b rev.11](https://ihe.net/uploadedFiles/Documents/PaLM/IHE_PaLM_TF_Vol2b.pdf). Alineación semántica, sin dependencia binaria ni conformidad completa.

Estimación original: 18–32 días-persona (núcleo 2–4, medicina 2–4, pilotos 3–5, demás perfiles 6–10, EDC/ETL 3–5, conformidad/publicación 2–4). Era una previsión, no duración medida. El alcance se implementa en fuente/paquetes locales; adquisición de evidencia, hardware/homologación no estaban incluidos. #408 conserva firma/ensayo instalado VCOM; #404 permanece resuelto.
