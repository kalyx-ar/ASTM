using Atsm.Abstractions.Diagnostics;

namespace Atsm.Plugins;

/// <summary>Presentation metadata, never a protocol decision. Keys are stable; raw values remain unchanged.</summary>
public sealed record ProtocolText(string Key, string Fallback, string Evidence);

/// <summary>Optional expert vocabulary owned by the instrument plugin. Hosts may translate its seed by assembly.</summary>
public interface IInstrumentDiagnosticVocabulary
{
    IReadOnlyDictionary<string, ProtocolText> DiagnosticVocabulary { get; }
    ProtocolExplanation? ExplainFinding(AtsmDiagnostic finding) => DiagnosticVocabulary.TryGetValue("finding." + finding.Code, out var text) ? new(text, []) : null;
}

public static class StandardDiagnosticVocabulary
{
    public static IReadOnlyDictionary<string, ProtocolText> Create(string evidence, params (string Code, string Meaning)[] additions)
    {
        var values = new Dictionary<string, ProtocolText>(StringComparer.Ordinal)
        {
            ["record.H"] = new("Record.Header", "Cabecera del mensaje", evidence),
            ["record.P"] = new("Record.Patient", "Identificación del paciente", evidence),
            ["record.O"] = new("Record.Order", "Muestra y solicitudes de análisis", evidence),
            ["record.R"] = new("Record.Result", "Resultado de una determinación", evidence),
            ["record.Q"] = new("Record.Query", "Consulta de solicitudes al anfitrión", evidence),
            ["record.C"] = new("Record.Comment", "Comentario del instrumento", evidence),
            ["record.M"] = new("Record.Manufacturer", "Información del fabricante", evidence),
            ["record.L"] = new("Record.Terminator", "Fin del mensaje", evidence),
        };
        foreach (var (code, meaning) in additions) values[code] = new(code, meaning, evidence);
        return values;
    }
}
