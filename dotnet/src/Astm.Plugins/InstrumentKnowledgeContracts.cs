using Astm.Abstractions.Diagnostics;

namespace Astm.Plugins;

/// <summary>Context established by decoding, never by matching an isolated code in arbitrary text.</summary>
public readonly record struct InstrumentKnowledgeContext(string Profile, string Record, string Field, string? Model = null);
public sealed record InstrumentKnowledgeFact(ProtocolText Text, string? FindingCode = null,
    AstmSeverity Severity = AstmSeverity.Information, string? SourceCode = null);
public interface IInstrumentKnowledge
{
    InstrumentKnowledgeFact? Lookup(InstrumentKnowledgeContext context, string code);
}
