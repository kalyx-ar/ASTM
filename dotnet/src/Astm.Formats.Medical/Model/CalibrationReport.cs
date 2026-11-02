using Astm.Abstractions.Tracing;

namespace Astm.Formats.Medical.Model;

/// <summary>A manufacturer report, separate from a patient observation or a host QC decision.
/// ContentBase64 preserves the exact reported bytes; Text is available only for valid UTF-8.</summary>
public sealed record CalibrationReport(Guid ReportId, InstrumentRef Instrument, CodedValue Assay,
    string? Dilution, string ModuleId, string SystemSerial, string PerformedAtRaw, string InstrumentStatus,
    bool Successful, string ContentBase64, string ContentSha256, string? Text, IReadOnlyList<QualityMaterial> Materials,
    IReadOnlyList<ReagentLot> Reagents, TraceRef Trace)
{
    public IReadOnlyDictionary<string, string> SourceFields { get; init; } = new Dictionary<string, string>();
}

public enum CalibrationStatusMeaning { Unknown, None, Failed, Expired, AdjustmentExpired, InProgress,
    Valid, Overridden, CalibratorLotOverridden, PendingQualityControl }

/// <summary>One inventory segment as reported, including consumables and calibration states.
/// Empty assays stay empty; calibration status is never inferred from inventory availability.</summary>
public sealed record InstrumentInventoryStatus(Guid StatusId, InstrumentRef Instrument, string ModuleId,
    string ReportedAtRaw, string SubstanceIdentifier, string SubstanceType,
    IReadOnlyList<CodedValue> Statuses, IReadOnlyList<CalibrationStatusMeaning> CalibrationStates,
    IReadOnlyList<CodedValue> Assays, string? ReagentLot, string? ReagentSerial, TraceRef Trace)
{
    public IReadOnlyDictionary<string, string> SourceFields { get; init; } = new Dictionary<string, string>();
}
