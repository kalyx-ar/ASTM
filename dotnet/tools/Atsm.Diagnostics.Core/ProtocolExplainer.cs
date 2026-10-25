using System.Text;
using Atsm.Abstractions.Model;
using Atsm.Abstractions.Tracing;
using Atsm.Plugins;
using Atsm.Protocol.Lis01;
using Atsm.Runtime.Tracing;
using Atsm.Tracing;

namespace Atsm.Diagnostics;

public static class ProtocolExplainer
{
    public static string SingleLine(string text) => text.Replace("\r\n", " ⏐ ", StringComparison.Ordinal).Replace("\r", " ⏐ ", StringComparison.Ordinal).Replace("\n", " ⏐ ", StringComparison.Ordinal);
    public static IReadOnlyList<(char Code, ProtocolText? Meaning)> Records(string text, DiagnosticProfile profile)
    {
        var vocabulary = (profile.AstmPlugin as IInstrumentDiagnosticVocabulary)?.DiagnosticVocabulary;
        return text.Replace("\r\n", "\r", StringComparison.Ordinal).Replace('\n', '\r').Split('\r', StringSplitOptions.RemoveEmptyEntries)
            .Select(r => (Code: r[0], Meaning: profile.AstmPlugin is IInstrumentKnowledge knowledge
                ? knowledge.Lookup(new(profile.Id, "record", "type"), r[0].ToString())?.Text
                : vocabulary?.GetValueOrDefault("record." + r[0]))).ToArray();
    }
    public static string RecordNames(string text, DiagnosticProfile profile) => string.Join(" · ", Records(text, profile).Select(record =>
        record.Meaning is { } meaning ? DiagnosticText.Plugin(profile.AstmPlugin!, meaning) : DiagnosticText.Format("Unknown.Command", record.Code)));
    public static string RecordEvidence(string text, DiagnosticProfile profile) => string.Join("\n", Records(text, profile)
        .Where(r => r.Meaning is not null).Select(r => r.Code + ": " + r.Meaning!.Evidence).Distinct(StringComparer.Ordinal));
    public static string MessageKind(DecodedMessage message, DiagnosticProfile profile)
    {
        if (profile.TracePlugin is { } plugin)
        {
            var code = message.Type.StartsWith("ER", StringComparison.Ordinal) ? "ER" : message.Type;
            return plugin.DiagnosticVocabulary.TryGetValue("kind." + code, out var kind) ? DiagnosticText.Plugin(plugin, kind) : DiagnosticText.Format("Unknown.Command", message.Type);
        }
        if (profile.ContentPlugin is not null) return DiagnosticText.Text("Message.Export");
        if (message.Type.Contains("incompleto", StringComparison.Ordinal)) return DiagnosticText.Text("Message.Incomplete");
        if (profile.IsHl7)
            return message.Type switch
            {
                "OUL^R22" => DiagnosticText.Text("Message.Results"), "QBP^ZOS" => DiagnosticText.Text("Message.Query"),
                "OML^O33" => DiagnosticText.Text("Message.Orders"), "RSP^ZOS" => DiagnosticText.Text("Message.QueryAnswer"),
                "ORL^O34" or "ACK^R22" => DiagnosticText.Text("Message.Acknowledgement"),
                _ => DiagnosticText.Format("Message.Hl7Unknown", message.Type),
            };
        var types = Records(message.Text, profile).Select(r => r.Code).ToHashSet();
        return DiagnosticText.Text(types.Contains('R') ? "Message.Results" : types.Contains('Q') ? "Message.Query" : types.Contains('O') ? "Message.Orders" : "Message.Empty");
    }
    public static string MessageContent(DecodedMessage message, DiagnosticProfile profile, IReadOnlyList<InstrumentObservation> observations)
    {
        if (message.Explanation is { } explanation && profile.TracePlugin is { } plugin) return DiagnosticText.Explanation(plugin, explanation);
        var results = observations.Where(o => o.Trace.LastSequence == message.Sequence).ToArray();
        if (results.Length > 0) return string.Join(" · ", results.Select(o => DiagnosticText.Format("Message.Result", o.Specimen.SpecimenId,
            o.Test.Vendor.Display ?? o.Test.Vendor.Code, o.Value.RawText, o.Value.RawUnit ?? "")));
        return profile.IsHl7 ? MessageKind(message, profile) : RecordNames(message.Text, profile);
    }
    public static string Entry(TraceEntry entry, DiagnosticProfile profile)
    {
        if (entry.Code == TraceReconstruction.MessageCode)
            return profile.IsHl7 ? DiagnosticText.Text("Event.lis02.message") : RecordNames((profile.AstmProfile?.Encoding ?? Encoding.UTF8).GetString(entry.Data.Span), profile);
        if (entry.Data.Length == 1)
        {
            var control = entry.Data.Span[0] switch { 2 => "STX", 3 => "ETX", 4 => "EOT", 5 => "ENQ", 6 => "ACK", 21 => "NAK", 23 => "ETB", _ => null };
            if (control is not null) return DiagnosticText.Text("Command." + control);
        }
        if (entry.Data.Length > 1 && entry.Data.Span[0] == 2 && profile.AstmProfile is { } astm)
        {
            var parsed = Lis01Frame.Parse(entry.Data[1..]);
            if (parsed.IsValid) return RecordNames(astm.Encoding.GetString(parsed.Text.Span), profile);
        }
        return entry.Code is { } code ? DiagnosticText.Event(code, entry.Detail) : DiagnosticText.Format("Message.Wire", entry.Data.Length);
    }
    public static string ResultKind(InstrumentObservation observation, DiagnosticProfile profile)
    {
        var raw = observation.ResultKind ?? "";
        if (raw.Length == 0) return "";
        if (profile.TracePlugin is { } trace && trace.DiagnosticVocabulary.TryGetValue("resultKind." + raw, out var traceKind)) return DiagnosticText.Plugin(trace, traceKind);
        if ((profile.AstmPlugin as IInstrumentDiagnosticVocabulary)?.DiagnosticVocabulary is { } vocabulary)
        {
            if (vocabulary.TryGetValue("resultKind." + raw, out var kind)) return DiagnosticText.Plugin(profile.AstmPlugin!, kind);
            if (raw.Contains('/', StringComparison.Ordinal) && vocabulary.TryGetValue("resultFormat." + raw.Split('/')[^1], out var format))
                return DiagnosticText.Plugin(profile.AstmPlugin!, format) + " (" + raw + ")";
        }
        return DiagnosticText.Format("Raw.Kind", raw);
    }
    public static string Status(InstrumentObservation observation) => DiagnosticText.Text("State." + observation.InstrumentStatusMeaning)
        + (observation.Flags.Count == 0 ? "" : " · " + DiagnosticText.Format("Raw.Flags", string.Join(", ", observation.Flags.Select(f => f.Code))));
}
