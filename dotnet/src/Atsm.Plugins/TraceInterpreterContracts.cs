using Atsm.Abstractions.Diagnostics;
using Atsm.Abstractions.Model;
using Atsm.Abstractions.Tracing;

namespace Atsm.Plugins;

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
    IReadOnlyList<AtsmDiagnostic> Diagnostics)
{
    public IReadOnlyList<InstrumentTraceTiming> Timings { get; init; } = [];
}

public enum InstrumentTimingKind { Response, OperationTotal, OperationProcessing }
/// <summary>Only a decoder/host that knows both boundaries may supply a completed interval. Null end means pending.</summary>
public sealed record InstrumentTraceTiming(InstrumentTimingKind Kind, string Code, long FirstSequence, long? LastSequence,
    ProtocolExplanation? Explanation = null);

/// <summary>Manufacturer-owned detection and reconstruction, independent of a recorder's file format.</summary>
public interface IInstrumentTracePlugin : IInstrumentDiagnosticVocabulary
{
    PluginDescriptor Describe();
    IReadOnlyList<InstrumentTraceProfile> Profiles { get; }
    bool Recognizes(InstrumentTraceProfile profile, IReadOnlyList<TraceSegment> segments);
    RecordedBy? InferRecorder(InstrumentTraceProfile profile, IReadOnlyList<TraceSegment> segments);
    TraceInterpretation Interpret(InstrumentTraceProfile profile, IReadOnlyList<TraceEntry> entries,
        InstrumentRef instrument, Guid traceId, TimeZoneInfo timeZone);
}
