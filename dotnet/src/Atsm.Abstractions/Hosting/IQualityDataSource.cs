using Atsm.Abstractions.Model;
using Atsm.Abstractions.Tracing;

namespace Atsm.Abstractions.Hosting;

/// <summary>Read selection only: filters never assert suitability for a QC statistical series.
/// Null filters mean unspecified; date limits exclude observations without a result timestamp.</summary>
public sealed record QualityDataQuery(string? InstrumentId = null, CodedValue? Test = null,
    QualityMaterialKind? MaterialKind = null, string? MaterialLot = null, string? ModuleId = null,
    DateTimeOffset? FromInclusive = null, DateTimeOffset? UntilExclusive = null,
    Guid? TraceId = null, bool IncludePatientMeasurements = false);

public sealed record QualityDatum(Guid BatchId, InstrumentObservation Observation, AnalyticalQualityContext Context, TraceRef BatchTrace);
public sealed record QualityResourceQuery(string? InstrumentId = null, Guid? TraceId = null);

/// <summary>Host-owned read port over durably accepted observations and reported statistics.
/// Implementations enforce authorization/retention; this port does not query an analyzer or initiate calibration.</summary>
public interface IQualityDataSource
{
    IAsyncEnumerable<QualityDatum> ReadAsync(QualityDataQuery query, CancellationToken cancellationToken = default);
    IAsyncEnumerable<QualityResourceReference> ReadResourcesAsync(QualityResourceQuery query, CancellationToken cancellationToken = default);
}

/// <summary>Optional host/ETL port. Implementations authorize and resolve untrusted references, limit
/// size and retain source/hash/custody. Caller disposes the returned stream. ATSM never invokes this automatically.</summary>
public interface IQualityResourceContent
{
    ValueTask<Stream> OpenReadAsync(QualityResourceReference reference, CancellationToken cancellationToken = default);
}
