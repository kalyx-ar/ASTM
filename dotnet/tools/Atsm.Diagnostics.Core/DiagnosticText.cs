using System.Data.Common;
using System.Globalization;
using Atsm.Abstractions.Diagnostics;
using Atsm.Abstractions.Tracing;
using Atsm.Plugins;

namespace Atsm.Diagnostics;

/// <summary>Spanish presentation seed. Codes, vendor data, stored traces and original exceptions are never translated.</summary>
public static class DiagnosticText
{
    private static readonly Dictionary<string, string> lang = new(StringComparer.Ordinal)
    {
        ["Configuration.WithChemistry"] = "ASTM · Con analizador químico (consulta: 8 s)",
        ["Configuration.WithoutChemistry"] = "ASTM · Sin analizador químico (consulta: 18 s)",
        ["Catalog.Generic"] = "Instrumento ASTM genérico",
        ["Group.Details"] = "{0} filas agrupadas. Despliegue el grupo para consultar cada registro y su detalle original.",
        ["Group.Values"] = "{0} valores distintos", ["Unknown.Command"] = "Comando sin interpretación documentada ({0})",
        ["Message.Results"] = "Resultados de análisis", ["Message.Query"] = "Consulta de solicitudes",
        ["Message.Orders"] = "Solicitudes de análisis", ["Message.Empty"] = "Mensaje sin registros interpretables",
        ["Message.QueryAnswer"] = "Respuesta a consulta de solicitudes", ["Message.Acknowledgement"] = "Confirmación de recepción del mensaje",
        ["Message.Result"] = "Muestra {0} · {1}: {2} {3}",
        ["Message.Incomplete"] = "Mensaje incompleto", ["Message.Hl7Unknown"] = "Mensaje HL7 sin interpretación documentada ({0})",
        ["Message.Export"] = "Exportación de resultados", ["Message.Wire"] = "Fragmento {0} bytes", ["Message.Nul"] = "Fragmento {0} bytes - NUL",
        ["Message.BinaryFragment"] = "Fragmento {0} bytes - {1}",
        ["Message.WithError"] = "{0} - Con error", ["Flag.Unknown"] = "Código sin interpretar: {0}",
        ["Finding.UnsupportedBinary"] = "Protocolo binario sin intérprete documentado; bytes disponibles en Trama.",
        ["Direction.Received"] = "Equipo → anfitrión", ["Direction.Transmitted"] = "Anfitrión → equipo", ["Direction.Event"] = "Evento",
        ["Direction.RX"] = "Equipo → anfitrión", ["Severity.Information"] = "Información", ["Severity.Warning"] = "Advertencia", ["Severity.Error"] = "Error",
        ["Level.Technical"] = "Captura", ["Level.Link"] = "Enlace", ["Level.Message"] = "Mensaje", ["Level.Operation"] = "Interpretación",
        ["State.Unknown"] = "—", ["State.Final"] = "Final",
        ["State.Preliminary"] = "Preliminar", ["State.Correction"] = "Corrección", ["State.PreviouslyTransmitted"] = "Transmitido anteriormente",
        ["State.NotPerformed"] = "Sin medir", ["State.Canceled"] = "Cancelado",
        ["State.Repeat"] = "Repetición", ["State.Warning"] = "Advertencia",
        ["Value.Numeric"] = "Numérico", ["Value.Bounded"] = "Semicuantitativo", ["Value.Coded"] = "Cualitativo",
        ["Value.Text"] = "Texto", ["Value.Statistics"] = "Estadísticas", ["Value.Empty"] = "—",
        ["Unit.None"] = "—", ["Unit.NotReported"] = "No informada", ["Raw.Flags"] = "Indicadores del equipo: {0}",
        ["Quality.StateCount"] = "{0} ({1})",
        ["Original"] = "Contenido original", ["Evidence"] = "Referencia de interpretación", ["Sequence"] = "Entrada {0}",
        ["Finding.Unknown"] = "Diagnóstico del sistema ({0}); consulte el detalle original.",
        ["Finding.ATSM-DIAG-INCOMPLETE-CASE"] = "El caso fue interrumpido o no tiene cierre; los datos pueden estar incompletos.",
        ["Finding.ATSM-DIAG-UNSUPPORTED-PROTOCOL"] = "El log utiliza un protocolo sin intérprete compatible. Se conservan los bytes; no se infieren resultados.",
        ["Finding.ATSM-DIAG-DIRECTION-UNCERTAIN"] = "No se pudo inferir la dirección del log. Confirme si lo registró el anfitrión o el instrumento.",
        ["Source.Skipped"] = "Registros omitidos por fecha inválida: {0}",
        ["Source.LengthMismatch"] = "Registros con longitud informada distinta de los bytes capturados: {0}",
        ["Finding.ATSM-DIAG-INCOMPLETE-MESSAGE"] = "Mensaje incompleto; se excluyó de las observaciones interpretadas.",
        ["Finding.ATSM-DIAG-LIS01-INVALID-FRAME"] = "Trama inválida; consulte la causa y los bytes originales en el detalle.",
        ["Finding.STXInside"] = "Se recibió un inicio de trama (STX) dentro de otra trama abierta.",
        ["Finding.ATSM-DIAG-LIS01-FRAME-REJECTED"] = "El receptor rechazó una trama.",
        ["Finding.ATSM-DIAG-LIS01-ALTERED-BY-RECORDER"] = "El monitor alteró bytes de la captura; el checksum registrado no representa lo recibido por el equipo.",
        ["Finding.ATSM-DIAG-LIS01-IMPLICIT-ACKNOWLEDGE"] = "La siguiente trama permite inferir una aceptación que no figura en la captura.",
        ["Finding.ATSM-DIAG-LIS01-RETRANSMISSION"] = "El emisor retransmitió una trama.",
        ["Finding.ATSM-L02-INVALID-DELIMITERS"] = "La cabecera no declara separadores válidos; se conservaron los anteriores.",
        ["Finding.ATSM-L02-RECORD-OUTSIDE-MESSAGE"] = "Se encontró un registro fuera de un mensaje con cabecera.",
        ["Finding.ATSM-L02-UNEXPECTED-RECORD-ORDER"] = "El orden de los registros no coincide con el esperado por el perfil.",
        ["Finding.ATSM-L02-MISSING-TERMINATOR"] = "Falta el registro de fin de mensaje.",
        ["Finding.UnknownRecord"] = "El perfil no tiene una interpretación documentada para este registro; se conserva el original.",
        ["Finding.WithoutOrder"] = "Se recibió un resultado sin una solicitud/muestra previa.",
        ["Finding.Timestamp"] = "La fecha u hora informada no es válida para el perfil.",
        ["Finding.QueryUnsupported"] = "Se preservó la consulta; este perfil sólo recibe resultados y no genera una respuesta.",
        ["Finding.ATSM-ARC-QUERY-CANCELLED"] = "El equipo canceló la consulta de solicitudes.",
        ["Finding.ATSM-RUBY-ALARM"] = "CELL-DYN Ruby informó una alarma.",
        ["Finding.ATSM-RUBY-REJECTED"] = "CELL-DYN Ruby rechazó el mensaje del anfitrión.",
        ["Finding.ATSM-CFX-INVALID-EXPORT"] = "La exportación CFX no es válida o no coincide con el perfil CSV/RDML seleccionado.",
        ["Finding.ATSM-CFX-MISSING-SAMPLE"] = "La exportación no identifica la muestra; no se la reemplazó por la posición del pozo.",
        ["Finding.ATSM-CFX-NONNUMERIC-CQ"] = "El Cq no es numérico; se conservó el valor original del equipo.",
        ["Finding.ATSM-CFX-NONNUMERIC-STATISTIC"] = "La estadística no es numérica o está ausente; se conservó el texto original.",
        ["Finding.ATSM-CFX-DUPLICATE-ID"] = "La exportación repite un identificador de reacción.",
        ["Finding.ATSM-CFX-INVALID-REFERENCE"] = "Una referencia interna de RDML no es válida.",
        ["Finding.ATSM-QWALYS-MISSING-ORDER"] = "El resultado QWALYS no tiene identificador de muestra del instrumento.",
        ["Finding.HL7"] = "El mensaje HL7 no cumple el perfil seleccionado; consulte el código y el detalle original.",
        ["Finding.MLLP"] = "Se detectó ruido o un bloque MLLP incompleto o inválido.",
        ["Event.bytes"] = "Bytes capturados", ["Event.record-bytes"] = "Registros capturados sin encuadre de enlace",
        ["Event.OPEN"] = "Inicio de captura", ["Event.CLOSE"] = "Fin de captura",
        ["Event.lis01.enquiry"] = "Solicitud de inicio de transmisión", ["Event.lis01.established"] = "Enlace establecido",
        ["Event.lis01.busy"] = "Receptor ocupado", ["Event.lis01.contention"] = "Ambos extremos intentaron transmitir",
        ["Event.lis01.frame"] = "Trama recibida", ["Event.lis01.invalid-frame"] = "Trama inválida",
        ["Event.lis01.frame-accepted"] = "Trama aceptada", ["Event.lis01.frame-rejected"] = "Trama rechazada",
        ["Event.lis01.retransmission"] = "Retransmisión de trama", ["Event.lis01.implicit-acknowledge"] = "Aceptación inferida por la siguiente trama",
        ["Event.lis01.altered-by-recorder"] = "Bytes alterados por el monitor de captura", ["Event.lis01.receiver-interrupt"] = "El receptor interrumpió la transmisión",
        ["Event.lis01.transmission-end"] = "Fin de transmisión", ["Event.lis01.noise"] = "Bytes fuera del encuadre esperado",
        ["Event.lis02.message"] = "Registros del mensaje", ["Event.lis02.incomplete"] = "Mensaje incompleto",
        ["Event.operation.observation"] = "Resultado interpretado", ["Event.operation.query"] = "Consulta de solicitudes interpretada",
        ["Detection.Unknown"] = "No se pudo reconocer el contenido. Seleccione un formato manualmente y confirme el perfil del equipo.",
        ["Detection.Ambiguous"] = "El archivo coincide con más de un formato. Seleccione el formato manualmente.",
        ["Detection.Unsupported"] = "Se reconoció el log, pero su contenido no tiene registros ASTM/HL7 compatibles. Puede ser un enlace interno del equipo que requiere otro plugin. Se conservan los bytes en Trama; no se infieren resultados.",
        ["Detection.DirectionUnknown"] = "No se pudo inferir quién registró el log. Seleccione anfitrión o instrumento para interpretar su dirección.",
        ["Detection.Success"] = "Formato detectado: {0}. Perfil: {1}.", ["Detection.Selected"] = "Formato seleccionado: {0}.",
        ["Format.Auto"] = "Detectar automáticamente", ["Format.Export"] = "Exportación CSV / RDML",
        ["Format.AstmMessage"] = "Mensaje ASTM completo", ["Format.Hl7Message"] = "Mensaje HL7 completo",
        ["Format.SysmexLog"] = "Log Sysmex · monitor de puertos", ["Format.LegacyLog"] = "Log legacy · interfaz",
        ["Format.WireBytes"] = "Bytes de cable", ["Format.HexBytes"] = "Bytes en hexadecimal / controles", ["Format.AtsmTrace"] = "Traza ATSM", ["Format.ProtectedCase"] = "Caso protegido",
    };
    private static readonly Dictionary<string, Dictionary<string, string>> pluginSeeds = new(StringComparer.Ordinal);
    public static string Text(string key) => lang.GetValueOrDefault(key) ?? (StandardDiagnosticVocabulary.Shared.ContainsKey(key) ? Common(key) : key);
    public static string Localize(string key, string fallback) => lang.GetValueOrDefault(key, fallback);
    public static bool IsLocalized(string text) => lang.Values.Contains(text, StringComparer.Ordinal);
    public static string Format(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, Text(key), args);
    public static string Finding(AtsmDiagnostic finding, DiagnosticProfile? profile = null)
    {
        if (finding.Code == "ATSM-DIAG-SOURCE-LIMITATION") return finding.Message;
        if (finding.Code == "ATSM-DIAG-SOURCE-SKIPPED") return Format("Source.Skipped", finding.Message);
        if (finding.Code == "ATSM-DIAG-SOURCE-LENGTH-MISMATCH") return Format("Source.LengthMismatch", finding.Message);
        var plugin = (object?)profile?.TracePlugin ?? profile?.AstmPlugin;
        if (plugin is IInstrumentDiagnosticVocabulary vocabulary && vocabulary.ExplainFinding(finding) is { } explanation) return Explanation(plugin, explanation);
        if (finding.Code == "ATSM-DIAG-UNSUPPORTED-PROTOCOL" && finding.Message == DiagnosticReader.UnsupportedBinaryMessage) return Text("Finding.UnsupportedBinary");
        if (finding.Code == "ATSM-DIAG-LIS01-INVALID-FRAME" && finding.Message == "STX inside a frame") return Text("Finding.STXInside");
        if (lang.TryGetValue("Finding." + finding.Code, out var text)) return text;
        var key = finding.Code switch
        {
            var code when code.EndsWith("UNKNOWN-RECORD", StringComparison.Ordinal) => "Finding.UnknownRecord",
            var code when code.EndsWith("RESULT-WITHOUT-ORDER", StringComparison.Ordinal) => "Finding.WithoutOrder",
            var code when code.EndsWith("INVALID-TIMESTAMP", StringComparison.Ordinal) => "Finding.Timestamp",
            var code when code.EndsWith("QUERY-UNSUPPORTED", StringComparison.Ordinal) => "Finding.QueryUnsupported",
            var code when code.StartsWith("ATSM-HL7-", StringComparison.Ordinal) => "Finding.HL7",
            var code when code.StartsWith("ATSM-MLLP-", StringComparison.Ordinal) => "Finding.MLLP",
            _ => "Finding.Unknown",
        };
        return Format(key, finding.Code);
    }
    public static string FindingEvidence(AtsmDiagnostic finding, DiagnosticProfile profile) =>
        (((object?)profile.TracePlugin ?? profile.AstmPlugin) as IInstrumentDiagnosticVocabulary)?.ExplainFinding(finding)?.Text.Evidence ?? "";
    public static string Direction(string code) => lang.GetValueOrDefault("Direction." + code, code);
    public static string Event(string code, string? original = null) => lang.GetValueOrDefault("Event." + code)
        ?? Finding(new(code, AtsmSeverity.Information, original ?? ""));
    public static string Plugin(object plugin, ProtocolText value)
    {
        var source = value.SourceAssembly ?? plugin.GetType().Assembly.GetName().Name!;
        return pluginSeeds.TryGetValue(source, out var seed) ? seed.GetValueOrDefault(value.Key, value.Fallback) : value.Fallback;
    }
    public static string Explanation(object plugin, ProtocolExplanation value) => string.Format(CultureInfo.CurrentCulture, Plugin(plugin, value.Text), value.Arguments.Cast<object>().ToArray());
    public static string Common(string key) => Plugin(typeof(StandardDiagnosticVocabulary), StandardDiagnosticVocabulary.Shared[key]);
    public static async Task InitializeAsync(DbConnection database, DiagnosticCatalog catalog, string target = "es-AR", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
#if KALYX_LANG
        await InitializeSeedAsync(lang, typeof(DiagnosticText).Assembly.GetName().Name!, database, target, cancellationToken);
        var common = StandardDiagnosticVocabulary.Shared.Values.ToDictionary(v => v.Key, v => v.Fallback, StringComparer.Ordinal);
        var commonSource = typeof(StandardDiagnosticVocabulary).Assembly.GetName().Name!;
        await InitializeSeedAsync(common, commonSource, database, target, cancellationToken); pluginSeeds[commonSource] = common;
        foreach (var assembly in catalog.AstmPlugins.OfType<IInstrumentDiagnosticVocabulary>().Concat(catalog.TracePlugins).GroupBy(p => p.GetType().Assembly.GetName().Name!, StringComparer.Ordinal))
        {
            var seed = assembly.SelectMany(p => p.DiagnosticVocabulary.Values).Where(v => v.SourceAssembly is null).GroupBy(v => v.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Fallback, StringComparer.Ordinal);
            if (seed.Count == 0) continue;
            await InitializeSeedAsync(seed, assembly.Key, database, target, cancellationToken);
            pluginSeeds[assembly.Key] = seed;
        }
#else
        await Task.CompletedTask;
        throw new NotSupportedException("EnableKalyxLang");
#endif
    }
#if KALYX_LANG
    private static async Task InitializeSeedAsync(Dictionary<string, string> seed, string source, DbConnection database, string target, CancellationToken cancellationToken)
    {
        var runtime = await Kalyx.Globalization.Lang.InitializeAsync(seed, database, source: source, target: target, cancellationToken: cancellationToken);
        await runtime.FlushMissingAsync(cancellationToken);
    }
#endif
}
