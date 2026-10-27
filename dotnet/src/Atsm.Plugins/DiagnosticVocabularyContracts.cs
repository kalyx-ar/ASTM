using Atsm.Abstractions.Diagnostics;

namespace Atsm.Plugins;

/// <summary>Presentation metadata, never a protocol decision. Keys are stable; raw values remain unchanged.</summary>
public sealed record ProtocolText(string Key, string Fallback, string Evidence)
{
    public string? SourceAssembly { get; init; }
}

/// <summary>Optional expert vocabulary owned by the instrument plugin. Hosts may translate its seed by assembly.</summary>
public interface IInstrumentDiagnosticVocabulary
{
    IReadOnlyDictionary<string, ProtocolText> DiagnosticVocabulary { get; }
    ProtocolExplanation? ExplainFinding(AtsmDiagnostic finding) => DiagnosticVocabulary.TryGetValue("finding." + finding.Code, out var text) ? new(text, []) : null;
}

public static class StandardDiagnosticVocabulary
{
    public static IReadOnlyDictionary<string, ProtocolText> Shared { get; } = CreateShared();
    private static IReadOnlyDictionary<string, ProtocolText> CreateShared()
    {
        var source = typeof(StandardDiagnosticVocabulary).Assembly.GetName().Name!;
        var result = new Dictionary<string, ProtocolText>(StringComparer.Ordinal);
        foreach (var (code, key, text) in new (string, string, string)[]
        {
            ("record.H", "Record.Header", "Cabecera del mensaje"), ("record.P", "Record.Patient", "Identificación del paciente"),
            ("record.O", "Record.Order", "Muestra, tipo de análisis y estado de la solicitud"), ("record.R", "Record.Result", "Resultado de una determinación"),
            ("record.Q", "Record.Query", "Consulta de solicitudes"), ("record.C", "Record.Comment", "Comentario del instrumento"),
            ("record.M", "Record.Manufacturer", "Información del fabricante"), ("record.L", "Record.Terminator", "Fin del mensaje"),
            ("value.Image", "value.Image", "Imagen"), ("state.Normal", "state.Normal", "Normal"),
            ("state.AnalysisError", "state.AnalysisError", "Error de análisis"), ("state.LowReliability", "state.LowReliability", "Baja fiabilidad"),
            ("unit.None", "unit.None", "—"), ("unit.NotReported", "unit.NotReported", "No informada"),
            ("command.SendOrder", "command.SendOrder", "Enviar orden de análisis"), ("command.Sample", "command.Sample", "Número de muestra"),
            ("command.Tests", "command.Tests", "Códigos de pruebas, separados por coma"),
            ("Command.ENQ", "Command.ENQ", "Solicitud de inicio de transmisión (ENQ)"),
            ("Command.ACK", "Command.ACK", "Aceptación de la comunicación o trama (ACK)"),
            ("Command.NAK", "Command.NAK", "Rechazo; el emisor debe reintentar (NAK)"),
            ("Command.EOT", "Command.EOT", "Fin de transmisión (EOT)"),
            ("Command.STX", "Command.STX", "Inicio de trama (STX)"), ("Command.ETX", "Command.ETX", "Fin de mensaje (ETX)"),
            ("Command.ETB", "Command.ETB", "La trama continúa en otro bloque (ETB)"),
        }) result.Add(code, new(key, text, code.StartsWith("record.", StringComparison.Ordinal) ? "CLSI LIS02-A2" : "") { SourceAssembly = source });
        return result;
    }
    public static IReadOnlyDictionary<string, ProtocolText> Create(string evidence, params (string Code, string Meaning)[] additions)
    {
        var values = Shared.Where(p => p.Key.StartsWith("record.", StringComparison.Ordinal)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        foreach (var (code, meaning) in additions) values[code] = new(code, meaning, evidence);
        return values;
    }
}
