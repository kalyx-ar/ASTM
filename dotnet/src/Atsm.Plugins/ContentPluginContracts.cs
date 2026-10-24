using Atsm.Abstractions.Diagnostics;
using Atsm.Abstractions.Model;
using Atsm.Abstractions.Tracing;

namespace Atsm.Plugins;

/// <summary>A vendor content format delivered by the host; never a directory watcher or device driver.</summary>
public sealed record InstrumentContentProfile(string Id, string Version, string Format, IReadOnlyList<string> Evidence);
public sealed record ContentInterpretation(IReadOnlyList<InstrumentObservation> Observations, IReadOnlyList<AtsmDiagnostic> Diagnostics);
public interface IInstrumentContentPlugin
{
    PluginDescriptor Describe();
    IReadOnlyList<InstrumentContentProfile> Profiles { get; }
    ContentInterpretation Interpret(InstrumentContentProfile profile, ReadOnlyMemory<byte> content, InstrumentRef instrument, TraceRef trace);
}
