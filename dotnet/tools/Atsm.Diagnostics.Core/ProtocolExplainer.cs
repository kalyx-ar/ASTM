using System.Text;
using Atsm.Abstractions.Model;
using Atsm.Abstractions.Diagnostics;
using Atsm.Abstractions.Tracing;
using Atsm.Plugins;
using Atsm.Protocol.Lis01;
using Atsm.Runtime.Tracing;
using Atsm.Tracing;
using Atsm.Protocol.Lis02;

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
        string content;
        var results = observations.Where(o => o.Trace.LastSequence == message.Sequence).ToArray();
        if (message.Explanation is { } explanation && profile.TracePlugin is { } plugin) content = DiagnosticText.Explanation(plugin, explanation);
        else if (profile.AstmPlugin is IInstrumentMessageKnowledge knowledge && Lis02Codec.Parse(message.Text).Messages
            .Select(knowledge.ExplainMessage).FirstOrDefault(e => e is not null) is { } semantic) content = DiagnosticText.Explanation(profile.AstmPlugin, semantic);
        else if (results.Length > 0) content = string.Join(" · ", results.Select(o => DiagnosticText.Format("Message.Result", o.Specimen.SpecimenId,
            o.Test.Vendor.Display ?? o.Test.Vendor.Code, ResultValue(o, profile), o.Value.RawUnit ?? "")));
        else content = MessageSummary(message, profile);
        return MessageHasError(message, profile, results) ? DiagnosticText.Format("Message.WithError", content) : content;
    }
    public static string MessageSummary(DecodedMessage message, DiagnosticProfile profile)
    {
        if (profile.IsHl7 || profile.TracePlugin is not null || profile.ContentPlugin is not null) return MessageKind(message, profile);
        var names = Records(message.Text, profile).Where(r => r.Code is not ('H' or 'L')).Select(r => r.Meaning is { } meaning
            ? DiagnosticText.Plugin(profile.AstmPlugin!, meaning) : DiagnosticText.Format("Unknown.Command", r.Code)).Distinct(StringComparer.Ordinal).ToArray();
        return names.Length == 0 ? DiagnosticText.Text("Message.Empty") : string.Join(" · ", names);
    }
    public static bool MessageHasError(DecodedMessage message, DiagnosticProfile profile, IReadOnlyList<InstrumentObservation> observations) =>
        message.Type.Contains("incompleto", StringComparison.Ordinal) || message.Diagnostics.Any(d => d.Severity is AtsmSeverity.Warning or AtsmSeverity.Error)
        || observations.Where(o => o.Trace.LastSequence == message.Sequence).Any(o => o.InstrumentStatusMeaning == InstrumentStatusMeaning.Warning
            || o.Flags.Any(f => FlagKnowledge(profile)?.DescribeFlag(o, f)?.IsError == true));
    private static IInstrumentFlagKnowledge? FlagKnowledge(DiagnosticProfile? profile) => ((object?)profile?.TracePlugin ?? profile?.AstmPlugin) as IInstrumentFlagKnowledge;
    public static bool RequiresAttention(InstrumentObservation observation, IInstrumentFlagKnowledge? knowledge = null) =>
        observation.InstrumentStatusMeaning == InstrumentStatusMeaning.Warning || observation.Flags.Any(f => knowledge?.DescribeFlag(observation, f)?.RequiresAttention != false);
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
        var plugin = (object?)profile.TracePlugin ?? profile.AstmPlugin ?? (object?)profile.ContentPlugin;
        if (plugin is IInstrumentValueKnowledge knowledge && knowledge.DescribeValue(observation) is { } valueMeaning)
            return DiagnosticText.Plugin(plugin, valueMeaning.Kind);
        var raw = observation.ResultKind ?? "";
        if (raw.Length == 0) return DataKind(observation);
        if (profile.TracePlugin is { } trace && trace.DiagnosticVocabulary.TryGetValue("resultKind." + raw, out var traceKind)) return DiagnosticText.Plugin(trace, traceKind);
        if ((profile.AstmPlugin as IInstrumentDiagnosticVocabulary)?.DiagnosticVocabulary is { } vocabulary)
        {
            if (vocabulary.TryGetValue("resultKind." + raw, out var kind)) return DiagnosticText.Plugin(profile.AstmPlugin!, kind);
            if (raw.Contains('/', StringComparison.Ordinal) && raw.Split('/')[^1] is not ("RAW" or "MAINFORMAT") && vocabulary.TryGetValue("resultFormat." + raw.Split('/')[^1], out var format))
                return DiagnosticText.Plugin(profile.AstmPlugin!, format);
        }
        return DataKind(observation);
    }
    private static string DataKind(InstrumentObservation observation) => DiagnosticText.Text(observation.Value.Coded is not null ? "Value.Coded" : observation.Value.Numeric is not null
        ? string.IsNullOrEmpty(observation.Value.Comparator) ? "Value.Numeric" : "Value.Bounded" : string.IsNullOrWhiteSpace(observation.Value.RawText)
            ? observation.Quality?.Statistics.Count > 0 ? "Value.Statistics" : "Value.Empty" : "Value.Text");
    public static string Unit(InstrumentObservation observation, DiagnosticProfile profile)
    {
        if (InstrumentUnits.Symbol(observation.Value) is { } symbol) return symbol;
        var plugin = (object?)profile.TracePlugin ?? profile.AstmPlugin ?? (object?)profile.ContentPlugin;
        if (plugin is IInstrumentValueKnowledge knowledge && knowledge.DescribeUnit(observation) is { } unit) return DiagnosticText.Plugin(plugin, unit);
        return DiagnosticText.Text(observation.Value.Numeric is null || observation.Value.Coded is not null ? "Unit.None" : "Unit.NotReported");
    }
    public static string ResultValue(InstrumentObservation observation, DiagnosticProfile profile)
    {
        var plugin = (object?)profile.TracePlugin ?? profile.AstmPlugin ?? (object?)profile.ContentPlugin;
        var meaning = (plugin as IInstrumentValueKnowledge)?.DescribeValue(observation);
        if (meaning?.DisplayText is { } text) return DiagnosticText.Plugin(plugin!, text);
        return meaning?.DisplayValue is { } display ? display == meaning.Kind.Fallback ? DiagnosticText.Plugin(plugin!, meaning.Kind) : display : observation.Value.RawText;
    }
    public static string Status(InstrumentObservation observation, DiagnosticProfile? profile = null)
    {
        var knowledge = FlagKnowledge(profile);
        var meaningfulFlags = observation.Flags.Any(f => knowledge?.DescribeFlag(observation, f)?.RequiresAttention != false)
            ? observation.Flags.Where(f => knowledge?.DescribeFlag(observation, f)?.RequiresAttention != false) : observation.Flags;
        var flags = meaningfulFlags.Select(f => knowledge?.DescribeFlag(observation, f) is { } meaning
            ? DiagnosticText.Plugin(((object?)profile!.TracePlugin ?? profile.AstmPlugin)!, meaning.Text) : DiagnosticText.Format("Flag.Unknown", f.Code)).Distinct(StringComparer.Ordinal).ToList();
        if (observation.InstrumentStatusMeaning == InstrumentStatusMeaning.NotPerformed)
            return DiagnosticText.Text("State.NotPerformed") + (observation.Flags.Any(f => knowledge?.DescribeFlag(observation, f)?.RequiresAttention != false) && flags.Count > 0
                ? " · " + string.Join(" · ", flags) : "");
        // Final/Unknown add no information to a documented analyzer flag such as N = Normal.
        if (flags.Count > 0 && observation.InstrumentStatusMeaning is InstrumentStatusMeaning.Final or InstrumentStatusMeaning.Unknown or InstrumentStatusMeaning.Warning) return string.Join(" · ", flags);
        var state = knowledge?.DescribeState(observation) is { } stateMeaning
            ? DiagnosticText.Plugin(((object?)profile!.TracePlugin ?? profile.AstmPlugin)!, stateMeaning) : DiagnosticText.Text("State." + observation.InstrumentStatusMeaning);
        return flags.Count == 0 ? state : state + " · " + string.Join(" · ", flags);
    }
}
