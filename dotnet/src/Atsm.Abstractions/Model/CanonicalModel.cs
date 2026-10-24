using Atsm.Abstractions.Tracing;

namespace Atsm.Abstractions.Model;

public static class CodeSystems
{
    public const string Loinc = "http://loinc.org";
    public const string Ucum = "http://unitsofmeasure.org";
    public const string SnomedCt = "http://snomed.info/sct";

    /// <summary>Code system for a vendor dialect, e.g. <c>urn:atsm:vendor:abbott.architect</c>.</summary>
    public static string Vendor(string dialectId) => $"urn:atsm:vendor:{dialectId}";
}

public sealed record CodedValue(string Code, string? Display, string System);

/// <summary>The vendor code always travels; mapped codes (LOINC, local catalogue...) are added by the runtime.</summary>
public sealed record CodeSet(CodedValue Vendor, IReadOnlyList<CodedValue> Mapped)
{
    public static CodeSet Of(CodedValue vendor) => new(vendor, []);
}

public sealed record InstrumentRef(
    string InstrumentId,
    string? Manufacturer = null,
    string? Model = null,
    string? SerialNumber = null,
    string? SoftwareVersion = null,
    string? Udi = null);

public enum SpecimenRole { Unknown, Patient, Control, Calibrator }

public sealed record SpecimenRef(
    string SpecimenId,
    string? ContainerId = null,
    string? Type = null,
    string? Position = null,
    SpecimenRole Role = SpecimenRole.Unknown);

/// <param name="RawText">Exactly as received; never replaced by the parsed value.</param>
public sealed record ObservationValue(
    string RawText,
    decimal? Numeric = null,
    string? Comparator = null,
    CodedValue? Coded = null,
    string? RawUnit = null,
    CodedValue? Unit = null);

public sealed record ReagentLot(string? Lot, string? Serial = null, string? Kind = null);

/// <summary>Identifiers assigned by the manufacturer (message, run, record sequence...), kept with their original meaning.</summary>
public sealed record VendorIdentity(IReadOnlyDictionary<string, string> Values)
{
    public static readonly VendorIdentity Empty = new(new Dictionary<string, string>());

    public string? this[string key] => Values.TryGetValue(key, out var value) ? value : null;
}

/// <summary>Meaning of the instrument status according to the dialect. It is not a clinical validation state.</summary>
public enum InstrumentStatusMeaning { Unknown, Final, Preliminary, Correction, Repeat, PreviouslyTransmitted, Warning, NotPerformed }

public sealed record InstrumentObservation(
    Guid ObservationId,
    InstrumentRef Instrument,
    SpecimenRef Specimen,
    CodeSet Test,
    ObservationValue Value,
    string? ReferenceRangeText,
    IReadOnlyList<CodedValue> Flags,
    string InstrumentStatus,
    InstrumentStatusMeaning InstrumentStatusMeaning,
    string? ResultKind,
    string? Dilution,
    DateTimeOffset? ResultTime,
    string? Operator,
    IReadOnlyList<ReagentLot> Reagents,
    IReadOnlyList<string> Comments,
    VendorIdentity VendorIdentity,
    TraceRef Trace);

public sealed record InstrumentObservationBatch(Guid BatchId, InstrumentRef Instrument, IReadOnlyList<InstrumentObservation> Observations, TraceRef Trace);

public sealed record OrderQuery(Guid QueryId, InstrumentRef Instrument, IReadOnlyList<SpecimenRef> Specimens, string Scope, TraceRef Trace);

/// <summary>Minimum patient data an instrument needs; the host decides what to send.</summary>
public sealed record PatientContext(
    string? Id = null,
    string? FamilyName = null,
    string? GivenName = null,
    DateOnly? BirthDate = null,
    string? AdministrativeSex = null,
    string? Physician = null,
    string? Location = null);

public enum OrderAction { New, Add, Cancel, Control }

public enum OrderPriority { Routine, Stat }

public sealed record WorkOrder(
    string OrderId,
    SpecimenRef Specimen,
    PatientContext? Patient,
    IReadOnlyList<CodeSet> Tests,
    OrderPriority Priority = OrderPriority.Routine,
    OrderAction Action = OrderAction.New,
    DateTimeOffset? CollectedAt = null,
    string? SpecimenType = null);
