using System.Text;
using Atsm.Abstractions.Channels;
using Atsm.Abstractions.Diagnostics;
using Atsm.Abstractions.Hosting;
using Atsm.Abstractions.Model;
using Atsm.Abstractions.Tracing;
using Atsm.Protocol.Lis01;
using Atsm.Protocol.Lis02;

namespace Atsm.Plugins;

/// <param name="EngineApi">Major version of this SDK the plugin was built against.</param>
public sealed record PluginDescriptor(
    string Id,
    string Version,
    int EngineApi,
    string Manufacturer,
    IReadOnlyList<string> Models,
    IReadOnlyList<string> Evidence);

public enum AstmWorkflow { UnsolicitedResults, HostQuery, OrderDownload }

/// <summary>
/// Declarative profile of an ASTM (LIS01-A2 + LIS02-A2) dialect. Every deviation from the standard
/// must cite its evidence in <see cref="Evidence"/>.
/// </summary>
public sealed record AstmProfile(
    string Id,
    string Version,
    Lis01Options Link,
    Encoding Encoding,
    IReadOnlySet<AstmWorkflow> Workflows,
    TimeSpan QueryAnswerBudget,
    OwnershipEnforcement RequiredOwnership,
    IReadOnlyList<string> Evidence);

public sealed record DialectContext(InstrumentRef Instrument, TimeProvider Clock);

/// <summary>What a dialect understood from one LIS02 message. Problems are data, never exceptions.</summary>
public sealed record AstmInterpretation(
    IReadOnlyList<InstrumentObservation> Observations,
    IReadOnlyList<OrderQuery> Queries,
    InstrumentRef? Instrument,
    IReadOnlyList<AtsmDiagnostic> Diagnostics)
{
    public static readonly AstmInterpretation Empty = new([], [], null, []);
}

public interface IAstmDialect
{
    AstmInterpretation Interpret(Lis02Message message, TraceRef trace);

    /// <summary>Records of one LIS02 message (H through L) that answer a host query.</summary>
    IReadOnlyList<Lis02Record> BuildQueryAnswer(OrderQuery query, OrderQueryAnswer answer);

    /// <summary>Records of one LIS02 message (H through L) that download orders without a prior query.</summary>
    IReadOnlyList<Lis02Record> BuildOrders(IReadOnlyList<WorkOrder> orders);
}

public interface IInstrumentPlugin
{
    PluginDescriptor Describe();

    IReadOnlyList<AstmProfile> Profiles { get; }

    IAstmDialect CreateDialect(AstmProfile profile, DialectContext context);
}

public static class PluginApi
{
    public const int Version = 1;
}
