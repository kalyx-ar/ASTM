using System.Text;
using Atsm.Abstractions.Transport;
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
    IReadOnlyList<string> Evidence)
{
    /// <summary>How the host frames the messages it sends. Most instruments expect one record per ETX-terminated frame chain.</summary>
    /// <summary>Theoretical transport of the instrument (serial settings, network role) with its evidence; never an open connection.</summary>
    public TransportKnowledge? Transport { get; init; }

    public AstmFraming HostFraming { get; init; } = AstmFraming.FramePerRecord;

    /// <summary>How the instrument frames what it sends; used to build theoretical traces that match the device.</summary>
    public AstmFraming InstrumentFraming { get; init; } = AstmFraming.FramePerRecord;

    /// <summary>LIS01 messages to transmit for one LIS02 message, according to <paramref name="framing"/>.</summary>
    public List<ReadOnlyMemory<byte>> Frame(IEnumerable<Lis02Record> records, AstmFraming framing) =>
        framing == AstmFraming.FramePerRecord
            ? Lis02Codec.ToRecordBytes(records, Encoding)
            : [Encoding.GetBytes(Lis02Codec.Serialize(records))];
}

/// <summary>
/// <see cref="FramePerRecord"/>: each record ends a frame chain with ETX. <see cref="FramePerMessage"/>: the whole LIS02 message
/// (all records, CR-separated) is one frame chain, split with ETB only when it exceeds the frame size.
/// </summary>
public enum AstmFraming { FramePerRecord, FramePerMessage }

public sealed record DialectContext(InstrumentRef Instrument, TimeProvider Clock);

/// <summary>What a dialect understood from one LIS02 message. Problems are data, never exceptions.</summary>
public sealed record AstmInterpretation(
    IReadOnlyList<InstrumentObservation> Observations,
    IReadOnlyList<OrderQuery> Queries,
    InstrumentRef? Instrument,
    IReadOnlyList<AtsmDiagnostic> Diagnostics)
{
    public static readonly AstmInterpretation Empty = new([], [], null, []);
    public IReadOnlyList<QualityResourceReference> QualityResources { get; init; } = [];
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
    public const int Version = 2;
}
