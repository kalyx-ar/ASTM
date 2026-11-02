using System.Text.Json;
using System.Text.Json.Serialization;
using Astm.Formats.Medical.Model;
using Astm.Formats.Medical.Hosting;
using Astm.Abstractions.Diagnostics;
using Astm.Abstractions.Tracing;
using Astm.Operations;

namespace Astm.Formats.Medical;

public static class MedicalPurposes
{
    public const string SubmitOrders = "medical.lab.order.submit", AnswerWorklist = "medical.lab.worklist.answer",
        ReceiveObservations = "medical.lab.observation.report", ReceiveQuery = "medical.lab.worklist.query",
        ImportContent = "medical.lab.export.import", ObserveMessages = "medical.lab.communication.observe";
}
public sealed record MedicalOrderSubmission(IReadOnlyList<WorkOrder> Orders);
public sealed record MedicalWorklistAnswer(OrderQuery Query, OrderQueryAnswer Answer, string? OriginalMessage = null);
public sealed record MedicalOperationContext(InstrumentRef Instrument, TimeProvider Clock);
public sealed record MedicalDecoded(IReadOnlyList<InstrumentObservation> Observations, IReadOnlyList<OrderQuery> Queries,
    IReadOnlyList<AstmDiagnostic> Diagnostics, IReadOnlyList<QualityResourceReference> QualityResources);
public sealed record MedicalOperationRules
{
    public IReadOnlyList<string> PatientFields { get; init; } = ["id", "familyName", "givenName", "birthDate", "administrativeSex"];
    public bool SuppressControlPatient { get; init; }
    public bool AllSpecimensQuery { get; init; }
    public string QuerySpecimen { get; init; } = "Q.3.2";
    public string NegativeAnswer { get; init; } = "L.I";
    public bool ReceiverEotAcknowledges { get; init; }
    public bool SingleSpecimenPerMessage { get; init; }
    public bool SingleActionPerMessage { get; init; }
    public IReadOnlyList<OrderAction> OrderActions { get; init; } = [OrderAction.New, OrderAction.Add, OrderAction.Cancel, OrderAction.Control];
    public IReadOnlyDictionary<string, string> WireBindings { get; init; } = new Dictionary<string, string>();
}
public sealed record MedicalOperationProfile(string Id, string Revision, string Format, string EncodingName,
    MedicalOperationRules Rules)
{
    public InstrumentRef? Instrument { get; init; }
    public TimeSpan QueryAnswerBudget { get; init; }
}

public interface IMedicalOperationProvider
{
    IReadOnlyList<string> MedicalProfiles { get; }
    IMedicalOperations CreateOperations(string profileId, MedicalOperationContext context);
    IMedicalOperationObserver CreateOperationObserver(string profileId, MedicalOperationContext context, Guid traceId);
}
public interface IMedicalOperationObserver
{
    OperationSnapshot Current { get; }
    OperationSnapshot Feed(TraceEntry entry);
    void Invalidate();
    void Resynchronize(long? nextSequence = null);
    void Complete();
}
public interface IMedicalOperations : IOperationCatalog
{
    MedicalOperationProfile Profile { get; }
    OperationDefinition Select(string purpose);
    IReadOnlyList<OperationValidationIssue> Validate(OperationEnvelope request);
    PreparedOperationMessage Prepare(OperationEnvelope request);
    MedicalDecoded Decode(ReadOnlyMemory<byte> bytes, TraceRef trace, TimeZoneInfo timeZone);
}
public static class MedicalRequests
{
    public static OperationEnvelope Orders(IMedicalOperations operations, IReadOnlyList<WorkOrder> orders, Guid operationId) =>
        Envelope(operations, MedicalPurposes.SubmitOrders, operationId,
            JsonSerializer.SerializeToElement(new MedicalOrderSubmission(orders), MedicalJsonContext.Default.MedicalOrderSubmission));
    public static OperationEnvelope Answer(IMedicalOperations operations, OrderQuery query, OrderQueryAnswer answer, Guid operationId, string? originalMessage = null) =>
        Envelope(operations, MedicalPurposes.AnswerWorklist, operationId,
            JsonSerializer.SerializeToElement(new MedicalWorklistAnswer(query, answer, originalMessage), MedicalJsonContext.Default.MedicalWorklistAnswer));
    private static OperationEnvelope Envelope(IMedicalOperations operations, string purpose, Guid id, JsonElement data)
    {
        var definition = operations.Select(purpose);
        return new(id, purpose, definition.Major, definition.ProfileId, definition.ProfileRevision, definition.Input!, data);
    }
}
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(MedicalOrderSubmission))]
[JsonSerializable(typeof(MedicalWorklistAnswer))]
[JsonSerializable(typeof(MedicalDecoded))]
[JsonSerializable(typeof(InstrumentObservationBatch))]
public partial class MedicalJsonContext : JsonSerializerContext;
