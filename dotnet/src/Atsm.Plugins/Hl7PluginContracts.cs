using System.Text;
using Atsm.Abstractions.Model;
using Atsm.Abstractions.Tracing;
using Atsm.Protocol.Hl7v2;

namespace Atsm.Plugins;

/// <summary>Budgets are supplied from the deployed instrument configuration, not universal defaults.</summary>
public sealed record Hl7Profile(string Id, string Version, Encoding Encoding, TimeSpan QueryAnswerBudget,
    TimeSpan ResponseTimeout, IReadOnlyList<string> Evidence)
{
    public string Hl7Version { get; init; } = "2.5.1";
    public string CharacterSet { get; init; } = "UNICODE UTF-8";

    /// <summary>Theoretical transport of the instrument (network role, port, MLLP) with its evidence.</summary>
    public Atsm.Abstractions.Transport.TransportKnowledge? Transport { get; init; }
}

public sealed record Hl7Interpretation(IReadOnlyList<InstrumentObservation> Observations, OrderQuery? Query,
    IReadOnlyList<Atsm.Abstractions.Diagnostics.AtsmDiagnostic> Diagnostics);

public interface IHl7Dialect
{
    Hl7Interpretation Interpret(Hl7Message message, TraceRef trace);
    Hl7Message BuildQueryAnswer(Hl7Message request, OrderQueryAnswerContext answer);
    Hl7Message BuildOrders(IReadOnlyList<WorkOrder> orders, string controlId);
}

public sealed record OrderQueryAnswerContext(Atsm.Abstractions.Hosting.OrderQueryAnswer Answer, string ControlId);

public interface IHl7InstrumentPlugin
{
    PluginDescriptor Describe();
    IReadOnlyList<Hl7Profile> Profiles { get; }
    IHl7Dialect CreateDialect(Hl7Profile profile, DialectContext context);
}
