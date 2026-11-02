# Calibración ARCHITECT: informes y consulta de estado

Implementación 0.6.4, 2026-11-02, EDC #205. Autorización humana previa: [alcance registrado](https://github.com/kalyx-ar/EDC/issues/205#issuecomment-6583895923). Propietario: plugin Abbott ARCHITECT; el núcleo de transporte/operaciones conserva neutralidad.

## Interfaces y evidencia

- ASI RS-232 (`abbott.architect.asi-rs232`): Abbott 91407-108 §2-3 excluye resultados y excepciones de calibradores. No anuncia informes ni consultas de calibración. QC continúa siendo una función distinta.
- HL7 (`abbott.architect.hl7-2.5.1`), perfil 1.2.0: Abbott 206797-101 §2-17/19 y §3-57/63 define `OUL^R24` espontáneo al completar una calibración, exitosa o fallida; un ensayo/módulo/lote de reactivo por mensaje. El informe ED contiene texto plano en Base64, variable según modelo matemático y tipo de calibración. No es una descarga periódica ni una consulta histórica.
- §2-27/30 y §3-12/22: `INR^U06` pide inventario y estado de calibración de **todos los elementos a bordo de un módulo** (`INV|ALL|OK`). `ACK^U06` confirma recepción, no disponibilidad de resultados ni ejecución física. `INU^U05` informa elementos y ensayos asociados; también puede llegar espontáneamente. No contiene una correlación inequívoca con una consulta anterior: se conserva como informe independiente.
- §3-97: `ZCL` de `OUL^R22` continúa identificando la calibración usada; es una referencia ensayo/lote/fecha, distinta del informe de curva.

Los manuales locales están ignorados bajo `manuales/`. Se conservan las restricciones de cada interfaz; compartir la familia I1000/C4000 no acredita disponibilidad de HL7 para todo firmware/configuración.

## Contratos y custodia

`Astm.Formats.Medical.Model.CalibrationReport` conserva identidad, ensayo, dilución, equipo/módulo/sistema, fecha original, estado F/X y éxito/fallo, materiales calibradores, reactivos, fuente y traza. `ContentBase64` conserva los bytes exactos y `ContentSha256` su hash. `Text` contiene el texto sólo si es UTF-8 válido; otra codificación produce aviso y conserva bytes, sin caracteres de reemplazo ni adivinar una página de códigos. El modelo matemático y sus coeficientes quedan en el documento original: no se supone un formato fijo ni se recalculan curvas.

`InstrumentInventoryStatus` conserva cada INV, incluidos consumibles, todos los ensayos de INV.15, lote/serial, módulo/fecha, códigos originales y campos fuente (cantidades, unidades, estabilidad). Traduce exclusivamente CN/CE/CX/AX/CI/CO/CV/OL/CQ a estados de calibración; códigos desconocidos permanecen crudos, sin inventar disponibilidad ni aprobación clínica. Un INV no mezcla códigos conocidos de inventario y calibración.

`MedicalDecoded` y `InstrumentObservationBatch` incluyen `CalibrationReports` e `InventoryStatuses`; `medical.lab.decoded` pasa a revisión 1.1. No se fabrican observaciones de paciente ni valores numéricos para alojar estos documentos. Las colecciones se entregan en la misma frontera `ILabWorkflowHost.IngestAsync`, incluso en lotes sin observaciones. El host debe custodiar **todo** el lote antes de Accepted/Duplicate; AA sólo sigue a esa aceptación. Rechazo emite AR y aplazamiento/fallo AE. Cancelación/fallo de traza no producen AA.

Los IDs de informe/estado se derivan de recepción/perfil/ordinal en sesión y ETL. El decodificador aislado no acredita identidad de recepción durable. Repetir recepción conserva identidades, pero no fusiona hechos distintos por valor/hash.

`IQualityDataSource.ReadCalibrationReportsAsync` y `ReadInventoryStatusesAsync` leen lotes ya custodiados del host mediante `ObservationQualityDataSource`. Filtran por equipo, ensayo **y sistema**, lote, módulo y traza. No conectan al instrumento ni reemplazan los permisos del anfitrión; no calculan aceptación QC ni borran repeticiones.

## Operaciones y uso

El catálogo HL7 añade `medical.lab.calibration.report`, `medical.lab.inventory.report` (Decode/Observe) y `medical.lab.inventory.query` (Prepare/Execute/Observe). El schema de consulta exige serial de módulo no vacío (máximo 11 caracteres documentados) e ID 1..5. Rechaza campos adicionales, controles de texto y perfiles/revisiones incompatibles. No existe campo para restringir el pedido a un analito: se filtra el informe recibido.

```csharp
IMedicalOperationProvider provider = new ArchitectHl7Plugin(queryBudget, responseTimeout, instrumentZone);
var operations = provider.CreateOperations(ArchitectHl7Plugin.ProfileId, new(instrument, clock));
var request = MedicalRequests.Inventory(operations, new(moduleSerial, moduleId), stableOperationId);
var preview = operations.Prepare(request); // Aún no transmite.
var receipt = await session.SendOperationAsync(request, token);
// receipt confirma el intercambio/ACK. INU se entrega independientemente a IngestAsync.
```

Las extensiones de operaciones HL7 pertenecen al dialecto mediante `Hl7OperationBinding`, schema/validación, constructor y metadatos de cable/ACK. Adaptador y sesión no contienen ramas Abbott. El observador usa el propósito declarado y el ACK exacto; pérdida de cobertura o acuse incompatible mantiene Unknown, sin certificar ejecución física. Los presupuestos siguen viniendo de la configuración real; no hay polling ni reenvío automático nuevos.

## Verificación y límites

Pruebas sintéticas: éxito/fallo R24, dos calibradores, lote/módulo/hash, texto y codificación desconocida, delimitadores personalizados, Base64/tipo/mensaje incompleto/ensayo/equipo discordantes, rechazo sin custodia parcial, estados conocidos/desconocidos, INV mixto, schema/catálogo, identidad estable sesión/ETL, lecturas filtradas/cancelación, ACK demorado por custodia, U06 correlacionado y U05 independiente, observación y ausencia de capacidades en ASI. Suites completas y firma de paquetes: evidencia final en ESTADO/changelog.

No se inicia físicamente una calibración, no se pide una curva histórica, no se declara LAW completo ni homologación de equipo. Validación real requiere equipo con HL7 habilitado y firmware compatible. UI de informes, persistencia/proyección QC del host y decodificadores matemáticos por modelo son capacidades independientes; este cambio entrega los contratos y datos al host, sin añadir almacén, permisos o dependencias.
