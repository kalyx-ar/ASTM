# Modelo canónico y contrato con el host

Extensión implementada 2026-10-24: `InstrumentObservation.Quality` conserva material/lote, módulo/corrida/método, referencia de calibración y estadísticas reportadas con ámbito/campo original. `InstrumentObservationBatch.QualityResources` conserva recursos externos, incluso en lotes sin observaciones. Puertos opcionales `IQualityDataSource` (adaptador de lectura implementado) e `IQualityResourceContent` (lo implementa el anfitrión/ETL); el único puerto obligatorio de flujo sigue siendo `ILabWorkflowHost`. Contratos reales, mapeos y límites en [CALIDAD-Y-CALIBRACION](CALIDAD-Y-CALIBRACION.md); los ejemplos indicativos históricos siguientes no sustituyen a las clases de `dotnet/src/Astm.Abstractions`.

Estado: propuesta. Es la frontera que hace a ASTM neutral: los plugins producen y consumen este modelo; cada LIS/LIMS (EDC u otro) implementa un único adaptador.

## Principios

- **Alineado con IHE LAW y FHIR, sin depender de ninguno.** Los nombres siguen los conceptos de LAW (orden de trabajo analítico, especimen, resultado); existe un proyector opcional a FHIR.
- **Doble codificación siempre:** el código del fabricante viaja intacto; LOINC/UCUM/SNOMED se agregan cuando hay mapeo versionado.
- **Valor original preservado.** El valor numérico interpretado es un campo derivado; el texto recibido nunca se pierde.
- **Sin decisiones clínicas.** El estado del equipo se informa con su significado; el host decide validación, rangos de referencia del laboratorio y publicación.
- **Identidad e idempotencia explícitas.** Cada hecho trae identidad del fabricante y referencia de traza; el host define la clave de deduplicación por proceso.

## Tipos principales

```csharp
namespace Astm.Abstractions.Model;

public sealed record InstrumentRef(string InstrumentId, string? Manufacturer, string? Model,
    string? SerialNumber, string? SoftwareVersion, string? Udi);

public sealed record CodedValue(string Code, string? Display, string System);   // system: "urn:astm:vendor:abbott.architect", "http://loinc.org", "http://unitsofmeasure.org", …
public sealed record CodeSet(CodedValue Vendor, IReadOnlyList<CodedValue> Mapped); // Mapped puede estar vacío

public sealed record SpecimenRef(string SpecimenId, string? ContainerId, string? Type,
    string? Position /* rack^posición */, string? Role /* paciente, control, calibrador */);

public sealed record ObservationValue(string RawText, decimal? Numeric, string? Comparator /* <, >, <=, >= */,
    CodedValue? Coded, string? RawUnit, CodedValue? Unit /* UCUM */);

public sealed record InstrumentObservation(
    Guid ObservationId,                 // UUIDv7 en dialecto aislado; UUIDv8 estable por recepción en runtime
    InstrumentRef Instrument,
    SpecimenRef Specimen,
    CodeSet Test,
    ObservationValue Value,
    string? ReferenceRangeText,         // el informado por el equipo, sin interpretar
    IReadOnlyList<CodedValue> Flags,    // anormalidad/alarma del equipo
    string InstrumentStatus,            // F, R, C, W, I, … tal como llega
    string InstrumentStatusMeaning,     // significado según el dialecto: Final, Repeat, Correction, Warning…
    string? Dilution,
    DateTimeOffset? ResultTime,
    string? Operator,
    IReadOnlyList<ReagentLot> Reagents,
    VendorIdentity VendorIdentity,      // ids de mensaje/ejecución/test del fabricante
    TraceRef Trace);                    // traceId + rangos de bytes/mensaje

public sealed record OrderQuery(Guid QueryId, InstrumentRef Instrument,
    IReadOnlyList<SpecimenRef> Specimens, string Scope /* ALL, tests concretos */, TraceRef Trace);

public sealed record WorkOrder(string OrderId, SpecimenRef Specimen, PatientContext? Patient,
    IReadOnlyList<CodeSet> Tests, string Priority, string Action /* nuevo, agregar, cancelar */,
    DateTimeOffset? CollectedAt);

public sealed record PatientContext(string? Id, string? FamilyName, string? GivenName,
    DateOnly? BirthDate, string? AdministrativeSex);  // mínimo necesario; cada campo opcional
```

`ReagentLot`, `VendorIdentity`, `TraceRef` y los estados de operación se detallan en `spec/model-v1`. Las firmas son indicativas y se cerrarán con el primer plugin.

**Datos de paciente mínimos:** el modelo sólo transporta lo que el instrumento necesita o envía. El host decide qué enviar en `PatientContext`; la decisión de Architect de forzar sexo `U` (ver [REFERENCIAS-LEGACY](REFERENCIAS-LEGACY.md)) se resuelve allí, no en el plugin.

## Puertos que implementa el host

```csharp
namespace Astm.Abstractions.Hosting;

public interface ILabWorkflowHost
{
    // Sincrónico y con plazo: el motor pasa un token con el presupuesto del host query.
    ValueTask<OrderQueryAnswer> AnswerQueryAsync(OrderQuery query, CancellationToken deadline);

    // Durable e idempotente: devolver Accepted sólo cuando el hecho quedó persistido en el host.
    ValueTask<IngestOutcome> IngestAsync(InstrumentObservationBatch batch, CancellationToken ct);

    // Órdenes pendientes para descarga (broadcast), por instrumento.
    IAsyncEnumerable<WorkOrder> PendingOrdersAsync(InstrumentRef instrument, CancellationToken ct);
    ValueTask AcknowledgeOrderAsync(string orderId, OrderDeliveryOutcome outcome, CancellationToken ct);

    // Mapeos versionados (LIVD/equivalencias locales) que el host publica para un instrumento.
    ValueTask<CodeMappingSet?> MappingsAsync(InstrumentRef instrument, CancellationToken ct);
}
```

- `IngestOutcome`: `Accepted`, `Duplicate` (con referencia al hecho anterior), `Rejected` (motivo estable), `Deferred` (pendiente de decisión). El motor confirma al instrumento en capa baja según el perfil; **la confirmación de capa baja no depende de que el host haya aceptado**, salvo que el perfil lo exija y el plazo lo permita.
- Si el host no está disponible, el runtime escribe el lote en un **buzón durable local** (outbox) y lo reenvía; el buzón conserva orden e identidad.

## Mapeo de códigos

- `CodeMappingSet` es un artefacto versionado: `{instrumentModel, vendorCode, loinc?, ucum?, localCode?, validFrom, source}`.
- Se puede importar desde archivos LIVD (JSON/CSV del fabricante) o desde las equivalencias del host (en el sistema legacy, `lims.interface_analito`).
- El mapeo se aplica en el runtime, después del plugin: el plugin sólo entiende el dialecto, no los catálogos del laboratorio.

## Proyección FHIR (opcional)

`Astm.Fhir` proyecta `InstrumentObservation` → `Observation` (con `device`, `specimen`, `code` con ambos sistemas, `valueQuantity` o `valueString`, `interpretation`, `status` derivado del significado), y `WorkOrder` ↔ `ServiceRequest`. Útil para hosts FHIR; no es requisito de EDC.

## Equivalencia con LAW

| LAW (HL7 v2.5.1) | Modelo ASTM |
|---|---|
| OML^O33 (Analyzer → Order) | `WorkOrder` saliente |
| QBP^Q11 / RSP^K11 | `OrderQuery` / `OrderQueryAnswer` |
| OUL^R22 | `InstrumentObservationBatch` |
| SPM / SAC | `SpecimenRef` |
| OBX-3, OBX-5, OBX-6, OBX-8, OBX-11 | `Test`, `Value`, `Unit`, `Flags`, `InstrumentStatus` |
| INV | `ReagentLot` |

Un plugin ASTM y uno HL7 LAW del mismo fabricante producen el mismo modelo, lo que permite migrar un equipo de ASTM a HL7 sin tocar el host.
