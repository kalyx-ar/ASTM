using Astm.Abstractions.Diagnostics;
using Astm.Abstractions.Model;
using Astm.Abstractions.Tracing;

namespace Astm.Plugins;

/// <summary>A vendor content format delivered by the host; never a directory watcher or device driver.</summary>
public sealed record InstrumentContentProfile(string Id, string Version, string Format, IReadOnlyList<string> Evidence);
public sealed record ContentInterpretation(IReadOnlyList<InstrumentObservation> Observations, IReadOnlyList<AstmDiagnostic> Diagnostics);
public interface IInstrumentContentPlugin
{
    PluginDescriptor Describe();
    IReadOnlyList<InstrumentContentProfile> Profiles { get; }
    ContentInterpretation Interpret(InstrumentContentProfile profile, ReadOnlyMemory<byte> content, InstrumentRef instrument, TraceRef trace);
}
