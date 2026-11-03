using Astm.Abstractions.Diagnostics;
using Astm.Formats.Medical.Model;
using Astm.Abstractions.Tracing;

namespace Astm.Plugins;

/// <summary>A passive vendor protocol. It cannot send commands, open channels or own storage.</summary>
public sealed record InstrumentTraceProfile(string Id, string Protocol, IReadOnlyList<string> Evidence);
public sealed record ProtocolExplanation(ProtocolText Text, IReadOnlyList<string> Arguments);
/// <summary>A field of a reconstructed binary message, including its original byte range. Offsets are zero based.</summary>
public sealed record InstrumentTraceField(int Offset, int Length, string Code, string RawValue, ProtocolExplanation Explanation);
public sealed record InstrumentTraceMessage(long FirstSequence, long LastSequence, TraceDirection Direction,
    string Code, string Text, ProtocolExplanation Explanation, IReadOnlyDictionary<string, string> Fields)
{
    /// <summary>Offset in the first source entry; a read may contain more than one message.</summary>
    public int FirstByteOffset { get; init; }
    public IReadOnlyList<InstrumentTraceField> BinaryFields { get; init; } = [];
}
public enum ProtocolEventLevel { Link, Message, Operation }
public sealed record InstrumentTraceEvent(ProtocolEventLevel Level, long FirstSequence, long LastSequence,
    TimeSpan Elapsed, TraceDirection Direction, string Code, ProtocolExplanation Explanation);
public sealed record TraceInterpretation(IReadOnlyList<InstrumentTraceMessage> Messages,
    IReadOnlyList<InstrumentTraceEvent> Events, IReadOnlyList<InstrumentObservation> Observations,
    IReadOnlyList<AstmDiagnostic> Diagnostics)
{
    public IReadOnlyList<InstrumentTraceTiming> Timings { get; init; } = [];
    public IReadOnlyList<Astm.Operations.OperationReceipt> OperationReceipts { get; init; } = [];
}

public enum InstrumentTimingKind { Response, OperationTotal, OperationProcessing }
/// <summary>Only a decoder/host that knows both boundaries may supply a completed interval. Null end means pending.</summary>
public sealed record InstrumentTraceTiming(InstrumentTimingKind Kind, string Code, long FirstSequence, long? LastSequence,
    ProtocolExplanation? Explanation = null);

/// <summary>Manufacturer-owned detection and reconstruction, independent of a recorder's file format.</summary>
public interface IInstrumentTracePlugin : IInstrumentDiagnosticVocabulary, IMedicalInstrumentPlugin
{
    PluginDescriptor Describe();
    IReadOnlyList<InstrumentTraceProfile> Profiles { get; }
    bool Recognizes(InstrumentTraceProfile profile, IReadOnlyList<TraceSegment> segments);
    RecordedBy? InferRecorder(InstrumentTraceProfile profile, IReadOnlyList<TraceSegment> segments);
    TraceInterpretation Interpret(InstrumentTraceProfile profile, IReadOnlyList<TraceEntry> entries,
        InstrumentRef instrument, Guid traceId, TimeZoneInfo timeZone);
}

/// <summary>Optional bounded incremental decoder. Feed never treats a read boundary as EOF.
/// Complete is called only at the end of the actual source. Output contains new facts only.</summary>
public interface IInstrumentTraceReader
{
    TraceInterpretation Feed(TraceEntry entry);
    TraceInterpretation Complete();
}
public interface IInstrumentTraceStreamPlugin : IInstrumentTracePlugin
{
    IInstrumentTraceReader CreateReader(InstrumentTraceProfile profile, InstrumentRef instrument, Guid traceId,
        TimeZoneInfo timeZone, int maxBufferedBytes);
}
