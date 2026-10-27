using System.Runtime.CompilerServices;
using Astm.Abstractions.Hosting;
using Astm.Abstractions.Model;

namespace Astm.Runtime.Quality;

/// <summary>Projects batches supplied by the host's committed store. No second write, own database,
/// transport or automatic statistics. Repeated receptions/corrections remain visible for host reconciliation.</summary>
public sealed class ObservationQualityDataSource(Func<CancellationToken, IAsyncEnumerable<InstrumentObservationBatch>> readCommitted) : IQualityDataSource
{
    public async IAsyncEnumerable<QualityDatum> ReadAsync(QualityDataQuery query, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (query.FromInclusive is { } from && query.UntilExclusive is { } until && from >= until)
            throw new ArgumentException("ASTM-QUALITY-INVALID-TIME-RANGE", nameof(query));
        await foreach (var batch in readCommitted(cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        foreach (var observation in batch.Observations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (observation.Specimen.Role == SpecimenRole.Patient && !query.IncludePatientMeasurements) continue;
            var context = observation.Quality;
            if (context is null && observation.Specimen.Role is SpecimenRole.Control or SpecimenRole.Calibrator)
                context = new(new(observation.Specimen.Role == SpecimenRole.Control ? QualityMaterialKind.Control : QualityMaterialKind.Calibrator, observation.Specimen.SpecimenId));
            if (context is null) continue;
            if (query.InstrumentId is { } id && observation.Instrument.InstrumentId != id
                || query.Test is { } test && (observation.Test.Vendor.Code != test.Code || observation.Test.Vendor.System != test.System)
                || query.MaterialKind is { } kind && context.Material?.Kind != kind
                || query.MaterialLot is { } lot && context.Material?.Lot != lot
                || query.ModuleId is { } module && context.ModuleId != module
                || query.TraceId is { } traceId && observation.Trace.TraceId != traceId
                || query.FromInclusive is { } start && (observation.ResultTime is null || observation.ResultTime < start)
                || query.UntilExclusive is { } end && (observation.ResultTime is null || observation.ResultTime >= end)) continue;
            yield return new(batch.BatchId, observation, context, batch.Trace);
        }
    }

    public async IAsyncEnumerable<QualityResourceReference> ReadResourcesAsync(QualityResourceQuery query, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await foreach (var batch in readCommitted(cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        foreach (var reference in batch.QualityResources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (query.InstrumentId is { } id && reference.Instrument.InstrumentId != id
                || query.TraceId is { } traceId && reference.Trace.TraceId != traceId) continue;
            yield return reference;
        }
    }
}
