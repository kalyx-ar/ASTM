namespace Atsm.Abstractions.Model;

public enum QualityMaterialKind { Unknown, Control, Calibrator, Standard, NoTemplateControl, NoReverseTranscriptaseControl, PositiveControl, NegativeControl }
public enum StatisticScope { Well, ReplicateGroup, StandardCurve }
public enum ReportedStatisticKind { CqMean, CqStandardDeviation, StartingQuantity, LogStartingQuantity, StartingQuantityMean, StartingQuantityStandardDeviation, EfficiencyPercent, Slope, YIntercept, RSquared }

/// <summary>Only explicitly transmitted identity. An absent lot, level or expiry stays absent.</summary>
public sealed record QualityMaterial(QualityMaterialKind Kind, string Identifier, string? Lot = null,
    string? Level = null, string? ExpirationRaw = null, string? TypeRaw = null);

/// <summary>A reference to a calibration, not a successful calibration certificate or a curve.</summary>
public sealed record CalibrationReference(string AssayCode, string? ReagentLot, string PerformedAtRaw);

/// <summary>A statistic reported by instrument software, not recomputed by ATSM. Scope and original
/// field travel with the value. Replicate statistics must not be counted once per replicated row.</summary>
public sealed record ReportedStatistic(ReportedStatisticKind Kind, ObservationValue Value, StatisticScope Scope,
    string SourceField, string? GroupId = null);

public sealed record AnalyticalQualityContext(QualityMaterial? Material = null, string? ModuleId = null,
    string? RunId = null, string? Method = null, CalibrationReference? Calibration = null)
{
    public IReadOnlyList<ReportedStatistic> Statistics { get; init; } = [];
    public IReadOnlyDictionary<string, string> SourceFields { get; init; } = new Dictionary<string, string>();
}

public enum QualityResourceKind { ControlTargetLimits }

/// <summary>An untrusted, out-of-band reference transmitted by the instrument, never opened by ATSM.</summary>
public sealed record QualityResourceReference(Guid ResourceId, InstrumentRef Instrument, QualityResourceKind Kind,
    string RawLocator, string SourceField, Atsm.Abstractions.Tracing.TraceRef Trace);
