using Atsm.Abstractions.Diagnostics;
using Atsm.Abstractions.Model;
using Atsm.Abstractions.Tracing;

namespace Atsm.Plugins;

/// <summary>A passive vendor protocol. It cannot send commands, open channels or own storage.</summary>
public sealed record InstrumentTraceProfile(string Id, string Protocol, IReadOnlyList<string> Evidence);
public sealed record ProtocolExplanation(ProtocolText Text, IReadOnlyList<string> Arguments);
public sealed record InstrumentTraceMessage(long FirstSequence, long LastSequence, TraceDirection Direction,
    string Code, string Text, ProtocolExplanation Explanation, IReadOnlyDictionary<string, string> Fields);
public enum ProtocolEventLevel { Link, Message, Operation }
public sealed record InstrumentTraceEvent(ProtocolEventLevel Level, long FirstSequence, long LastSequence,
    TimeSpan Elapsed, TraceDirection Direction, string Code, ProtocolExplanation Explanation);
public sealed record TraceInterpretation(IReadOnlyList<InstrumentTraceMessage> Messages,
    IReadOnlyList<InstrumentTraceEvent> Events, IReadOnlyList<InstrumentObservation> Observations,
    IReadOnlyList<AtsmDiagnostic> Diagnostics);

/// <summary>Manufacturer-owned detection and reconstruction, independent of a recorder's file format.</summary>
public interface IInstrumentTracePlugin : IInstrumentDiagnosticVocabulary
{
    PluginDescriptor Describe();
    IReadOnlyList<InstrumentTraceProfile> Profiles { get; }
    bool Recognizes(IReadOnlyList<TraceSegment> segments);
    RecordedBy? InferRecorder(IReadOnlyList<TraceSegment> segments);
    TraceInterpretation Interpret(InstrumentTraceProfile profile, IReadOnlyList<TraceEntry> entries,
        InstrumentRef instrument, Guid traceId, TimeZoneInfo timeZone);
}
