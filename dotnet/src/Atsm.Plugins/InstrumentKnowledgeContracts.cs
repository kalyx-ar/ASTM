using Atsm.Abstractions.Diagnostics;

namespace Atsm.Plugins;

/// <summary>Context established by decoding, never by matching an isolated code in arbitrary text.</summary>
public readonly record struct InstrumentKnowledgeContext(string Profile, string Record, string Field);
public sealed record InstrumentKnowledgeFact(ProtocolText Text, string? FindingCode = null,
    AtsmSeverity Severity = AtsmSeverity.Information);
public interface IInstrumentKnowledge
{
    InstrumentKnowledgeFact? Lookup(InstrumentKnowledgeContext context, string code);
}
