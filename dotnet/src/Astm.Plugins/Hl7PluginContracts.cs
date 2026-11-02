using System.Text;
using Astm.Formats.Medical.Model;
using Astm.Abstractions.Tracing;
using Astm.Protocol.Hl7v2;
using Astm.Operations;

namespace Astm.Plugins;

/// <summary>Budgets are supplied from the deployed instrument configuration, not universal defaults.</summary>
public sealed record Hl7Profile(string Id, string Version, Encoding Encoding, TimeSpan QueryAnswerBudget,
    TimeSpan ResponseTimeout, IReadOnlyList<string> Evidence)
{
    public Astm.Formats.Medical.MedicalOperationRules OperationRules { get; init; } = new();
    public string Hl7Version { get; init; } = "2.5.1";
    public string CharacterSet { get; init; } = "UNICODE UTF-8";

    /// <summary>Theoretical transport of the instrument (network role, port, MLLP) with its evidence.</summary>
    public Astm.Abstractions.Transport.TransportKnowledge? Transport { get; init; }
}

public sealed record Hl7Interpretation(IReadOnlyList<InstrumentObservation> Observations, OrderQuery? Query,
    IReadOnlyList<Astm.Abstractions.Diagnostics.AstmDiagnostic> Diagnostics)
{
    public IReadOnlyList<CalibrationReport> CalibrationReports { get; init; } = [];
    public IReadOnlyList<InstrumentInventoryStatus> InventoryStatuses { get; init; } = [];
}

/// <summary>Evidence-backed optional operations owned by the dialect, without manufacturer branches in the runtime.</summary>
public sealed record Hl7OperationBinding(OperationDefinition Definition, OperationDataContract? Input = null,
    Func<OperationEnvelope, Hl7Message>? Build = null,
    Func<OperationEnvelope, IReadOnlyList<OperationValidationIssue>>? Validate = null);

public interface IHl7Dialect
{
    IReadOnlyList<Hl7OperationBinding> AdditionalOperations(Hl7Profile profile) => [];
    Hl7Interpretation Interpret(Hl7Message message, TraceRef trace);
    Hl7Message BuildQueryAnswer(Hl7Message request, OrderQueryAnswerContext answer);
    Hl7Message BuildOrders(IReadOnlyList<WorkOrder> orders, string controlId);
}

public sealed record OrderQueryAnswerContext(Astm.Formats.Medical.Hosting.OrderQueryAnswer Answer, string ControlId);

public interface IHl7InstrumentPlugin : IMedicalInstrumentPlugin
{
    PluginDescriptor Describe();
    IReadOnlyList<Hl7Profile> Profiles { get; }
    IHl7Dialect CreateDialect(Hl7Profile profile, DialectContext context);
}
