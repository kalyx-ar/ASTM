using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Atsm.Abstractions.Diagnostics;
using Atsm.Abstractions.Model;
using Atsm.Abstractions.Tracing;
using Atsm.Plugins;

namespace Atsm.Plugin.Sysmex.Uc3500;

/// <summary>UC-3500 ↔ host, not the U-WAM ↔ LIS ASTM dialect. Read-only, with no active order generation.</summary>
public sealed partial class Uc3500Plugin(Uc3500CheckMode checkMode = Uc3500CheckMode.Auto) : IInstrumentTracePlugin
{
    public const string PluginId = "sysmex.uc3500";
    public const string ProfileId = "sysmex.uc3500.serial";
    public const string Manual = "Sysmex UC-3500 Host Communication Specifications (Except North America), v0.0.5 (2017-04-14)";
    public const string ManualUrl = "https://pdfcoffee.com/uc-3500-host-manual-v005-pdf-free.html";
    public PluginDescriptor Describe() => new(PluginId, "0.1.0", 1, "Sysmex", ["UC-3500"], [Manual, ManualUrl]);
    public IReadOnlyList<InstrumentTraceProfile> Profiles { get; } = [new(ProfileId, "UC-3500 · Serial (lectura pasiva)",
        [Manual + " §6.1.1: TRANSPARENT 1/2 y HANDSHAKE, comprobación BCC/SUM/SUM2/NONE; §6.1.2 tablas 6-14 a 6-18.",
         "El texto indica 441 bytes; la suma de anchos de tabla 6-17 y la captura local son 446. Se admite la estructura de campos de la tabla y se informa la discrepancia.",
         "Autodetección pasiva de comprobación por trama; NONE no acredita integridad. Sin timers, escrituras ni homologación en hardware.", ManualUrl])];

    private static Uc3500WireDecoder Decode(IEnumerable<TraceEntry> entries, Uc3500CheckMode mode)
    {
        var source = entries.ToArray();
        Uc3500WireDecoder Run(Uc3500CheckMode selected)
        { var decoder = new Uc3500WireDecoder(selected); foreach (var entry in source) decoder.Feed(entry); decoder.Complete(); return decoder; }
        var detected = Run(mode);
        if (mode != Uc3500CheckMode.Auto) return detected;
        // Learn from the capture as a whole. A missing last checksum must not silently become NONE
        // when the rest of the session proves that a checksum was configured.
        var checks = detected.Packets.Select(p => p.Check).Where(c => c is "BCC" or "SUM" or "SUM2").Distinct(StringComparer.Ordinal).ToArray();
        return checks.Length == 1 ? Run(checks[0] switch { "BCC" => Uc3500CheckMode.Bcc, "SUM" => Uc3500CheckMode.Sum, _ => Uc3500CheckMode.Sum2 }) : detected;
    }
    private static IEnumerable<TraceEntry> Entries(IReadOnlyList<TraceSegment> segments) => segments.Select((s, i) =>
        new TraceEntry(i, TimeSpan.Zero, DateTimeOffset.UnixEpoch, s.Direction, s.Data));
    public bool Recognizes(IReadOnlyList<TraceSegment> segments) => Decode(Entries(segments), checkMode).Packets.Any(p => Code(p.Text) != "UNKNOWN");
    public RecordedBy? InferRecorder(IReadOnlyList<TraceSegment> segments)
    {
        var host = 0; var instrument = 0;
        foreach (var packet in Decode(Entries(segments), checkMode).Packets)
        {
            var code = Code(packet.Text); if (code == "UNKNOWN") continue;
            var receivedFromInstrument = code != "CM00";
            if ((packet.Direction == TraceDirection.Received) == receivedFromInstrument) host++; else instrument++;
        }
        return host == instrument ? null : host > instrument ? RecordedBy.Host : RecordedBy.Instrument;
    }
    private static string Code(string text)
    {
        if (text.StartsWith("RQ02,", StringComparison.Ordinal)) return "RQ02";
        if (text.StartsWith("CM00,", StringComparison.Ordinal)) return "CM00";
        if (text.Length >= 5 && text.StartsWith("ER", StringComparison.Ordinal) && char.IsAsciiDigit(text[2]) && char.IsAsciiDigit(text[3]) && text[4] == ',') return text[..4];
        var fields = text.Split(',');
        return ResultLayout(fields) ? "RESULT" : "UNKNOWN";
    }
    private static bool ResultLayout(string[] f) => f.Length == 24 && f[0].Length == 14 && f[1].Length == 4 && f[2].Length == 2
        && f[3].Length == 9 && f[3][0] is 'N' or '#' or 'C' && f[3].AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0
        && f[4].Length == 4 && f[5].Length == 10 && f[6].Length == 10 && f[7].Length == 8
        && f.Skip(8).Take(12).All(v => v.Length is 25 or 14 && v.Length == f[8].Length && v[0] is '0' or '1')
        && f.Skip(20).Take(3).All(v => v.Length == 14 && v[0] is '0' or '1')
        && f[23].Length == (f[8].Length == 25 ? 20 : 16) && f[23][0] is '0' or '1';

    public TraceInterpretation Interpret(InstrumentTraceProfile profile, IReadOnlyList<TraceEntry> entries,
        InstrumentRef instrument, Guid traceId, TimeZoneInfo timeZone)
    {
        if (profile.Id != ProfileId) throw new ArgumentException("Unknown UC-3500 profile.", nameof(profile));
        var decoder = Decode(entries, checkMode);
        var messages = new List<InstrumentTraceMessage>(); var events = new List<InstrumentTraceEvent>();
        var observations = new List<InstrumentObservation>(); var findings = new List<AtsmDiagnostic>();
        void Finding(string code, long sequence, AtsmSeverity severity = AtsmSeverity.Warning) => findings.Add(new("ATSM-UC3500-" + code, severity,
            DiagnosticVocabulary.GetValueOrDefault("finding.ATSM-UC3500-" + code)?.Fallback ?? "Unknown UC-3500 record/code; original retained.", "entry:" + sequence));
        foreach (var (entry, value) in decoder.Controls)
        {
            var code = value switch { 6 => "ACK", 21 => "NAK", _ => "EOT" };
            events.Add(new(ProtocolEventLevel.Link, entry.Sequence, entry.Sequence, entry.Elapsed, entry.Direction, "uc3500." + code, Explain(code)));
        }
        foreach (var error in decoder.Errors) Finding(error.Code, error.Sequence);
        (string Id, string Position, long Sequence)? query = null;
        (string Id, string Position, string Strip, long Sequence)? order = null;
        foreach (var packet in decoder.Packets.OrderBy(p => p.Last))
        {
            var code = Code(packet.Text); var fields = new Dictionary<string, string>(StringComparer.Ordinal)
            { ["checksum"] = packet.Check, ["frame.valid"] = packet.Valid.ToString() };
            var explanation = Explain(code == "UNKNOWN" ? "UNKNOWN" : code.StartsWith("ER", StringComparison.Ordinal)
                ? DiagnosticVocabulary.ContainsKey(code) ? code : "ER.UNKNOWN" : code,
                code == "RESULT" ? packet.Text.Split(',')[0].Trim() : code);
            var layout = true;
            if (code == "RQ02")
            {
                layout = packet.Text.Length == 21;
                if (layout)
                {
                    fields["sample.id"] = packet.Text.Substring(5, 14).Trim(); fields["tube.position"] = packet.Text.Substring(19, 2);
                    explanation = Explain(code, fields["sample.id"], fields["tube.position"]);
                }
            }
            else if (code == "CM00")
            {
                layout = packet.Text.Length == 34 && packet.Text.StartsWith("CM00,01,", StringComparison.Ordinal);
                if (layout)
                {
                    fields["sample.id"] = packet.Text.Substring(8, 14).Trim(); fields["tube.position"] = packet.Text.Substring(22, 2);
                    fields["strip.requested"] = packet.Text.Substring(24, 10); // Leading spaces belong to the strip name.
                    explanation = string.IsNullOrWhiteSpace(fields["strip.requested"])
                        ? Explain("CM00.EMPTY", fields["sample.id"], fields["tube.position"])
                        : Explain(code, fields["sample.id"], fields["tube.position"], fields["strip.requested"]);
                }
            }
            else if (code.StartsWith("ER", StringComparison.Ordinal))
            {
                layout = packet.Text.Length == 11 && packet.Text.AsSpan(5).SequenceEqual("000000");
                fields["order.response"] = code;
                if (order is { } sent)
                {
                    fields["order.sequence"] = sent.Sequence.ToString(CultureInfo.InvariantCulture);
                    fields["sample.id"] = sent.Id; fields["tube.position"] = sent.Position; fields["strip.requested"] = sent.Strip;
                }
                if (query is { } asked) fields["query.sequence"] = asked.Sequence.ToString(CultureInfo.InvariantCulture);
            }
            else if (code == "RESULT")
            {
                var f = packet.Text.Split(',');
                fields["sample.id"] = f[0].Trim(); fields["rack"] = f[1]; fields["tube.position"] = f[2];
                fields["analysis.series"] = f[3][..1]; fields["analysis.number"] = f[3][1..]; fields["analysis.error"] = f[4];
                fields["strip.used"] = f[5]; fields["analysis.date"] = f[6]; fields["analysis.time"] = f[7];
                explanation = Explain(code, fields["sample.id"], fields["analysis.series"], fields["analysis.number"], f[4]);
                if (packet.Text.Length > 441) Finding("LENGTH-DISCREPANCY", packet.Last, AtsmSeverity.Information);
            }
            if (!packet.Valid) Finding(packet.Check == "incomplete" ? "INCOMPLETE" : "CHECKSUM", packet.Last);
            if (!layout) Finding("LAYOUT", packet.Last);
            if (code == "UNKNOWN") Finding("UNKNOWN", packet.Last);
            var valid = packet.Valid && layout && code != "UNKNOWN";
            if (!valid) explanation = Explain("INVALID");
            fields["record.valid"] = valid.ToString();
            messages.Add(new(packet.First, packet.Last, packet.Direction, code, packet.Text, explanation, fields));
            events.Add(new(ProtocolEventLevel.Message, packet.First, packet.Last, packet.Elapsed, packet.Direction, "uc3500." + code, explanation));
            if (!valid) continue;
            if ((packet.Direction == TraceDirection.Transmitted) != (code == "CM00"))
            { Finding("DIRECTION", packet.Last); continue; }
            if (code == "RQ02")
            {
                if (query is not null || order is not null) Finding("PENDING", packet.Last);
                query = (fields["sample.id"], fields["tube.position"], packet.Last); order = null;
            }
            else if (code == "CM00")
            {
                if (query is not { } requested || requested.Id != fields["sample.id"] || requested.Position != fields["tube.position"]) Finding("CORRELATION", packet.Last);
                else fields["query.sequence"] = requested.Sequence.ToString(CultureInfo.InvariantCulture);
                order = (fields["sample.id"], fields["tube.position"], fields["strip.requested"], packet.Last);
            }
            else if (code.StartsWith("ER", StringComparison.Ordinal))
            {
                if (order is null) Finding("CORRELATION", packet.Last);
                if (code != "ER00") Finding(DiagnosticVocabulary.ContainsKey(code) ? code : "UNKNOWN", packet.Last);
                query = null; order = null;
            }
            else if (code == "RESULT")
            {
                var f = packet.Text.Split(',');
                if (f[4] != "0000") Finding(AnalysisFinding(f[4]), packet.Last);
                var parsedTime = ParseTime(f[6], f[7], timeZone);
                if (parsedTime is null) Finding("TIMESTAMP", packet.Last);
                observations.AddRange(Results(f, instrument, new(traceId, packet.First, packet.Last), fields, parsedTime));
            }
        }
        if (query is not null || order is not null) Finding("PENDING", order?.Sequence ?? query!.Value.Sequence);
        return new(messages, events.OrderBy(e => e.LastSequence).ToArray(), observations, findings);
    }
    private static DateTimeOffset? ParseTime(string date, string time, TimeZoneInfo zone)
    {
        if (!DateTime.TryParseExact(date.Trim() + " " + time, ["yyyy/MM/dd HH:mm:ss", "yyyyMMdd HH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            || zone.IsInvalidTime(parsed) || zone.IsAmbiguousTime(parsed)) return null;
        return new(parsed, zone.GetUtcOffset(parsed));
    }
    private static string AnalysisFinding(string code) => code switch
    {
        "00BB" or "00CC" or "00DD" => "ANALYSIS-" + code,
        _ when code.Length == 4 && code[1] == '0' && code[3] == '0' && code[0] is >= '1' and <= '9' && code[2] is >= '1' and <= '9' => "ANALYSIS-STRIP",
        _ when code.Length == 4 && code[0] == '0' && code[2] == '0' && code[1] is >= '1' and <= '9' && code[3] is >= '1' and <= '9' => "ANALYSIS-LIGHT",
        _ when code.Length == 4 && code.EndsWith("00", StringComparison.Ordinal) => "ANALYSIS-MEASUREMENT",
        _ => "ANALYSIS-ERROR",
    };
    private static readonly string[] Parameters = ["URO", "BLD", "BIL", "KET", "GLU", "PRO", "pH", "NIT", "LEU", "SG.strip", "CRE", "ALB", "P/C", "A/C", "SG.refractometer", "COLOR"];
    private static IEnumerable<InstrumentObservation> Results(string[] f, InstrumentRef instrument, TraceRef trace,
        Dictionary<string, string> identity, DateTimeOffset? time)
    {
        var role = f[3][0] == 'C' ? SpecimenRole.Control : SpecimenRole.Patient;
        var specimen = new SpecimenRef(f[0].Trim(), Position: f[1] + "/" + f[2], Role: role);
        for (var i = 0; i < Parameters.Length; i++)
        {
            var raw = f[8 + i]; var analyzed = raw[0] == '0';
            var values = new List<(string Kind, string Raw, string? Unit)>();
            if (i < 15)
            {
                values.Add(("QUALITATIVE", raw.Substring(2, 6), null));
                if (analyzed && !string.IsNullOrWhiteSpace(raw.Substring(8, 6))) values.Add(("CONCENTRATION", raw.Substring(8, 6), null));
                if (analyzed && raw.Length == 25)
                {
                    if (!string.IsNullOrWhiteSpace(raw.Substring(14, 5))) values.Add(("REFLECTANCE1", raw.Substring(14, 5), "%"));
                    if (!string.IsNullOrWhiteSpace(raw.Substring(20, 5))) values.Add(("REFLECTANCE2", raw.Substring(20, 5), "%"));
                }
            }
            else
            {
                var offset = raw.Length == 20 ? 6 : 2;
                values.Add(("COLOR", raw.Substring(offset, 12), null));
                if (analyzed && raw.Length == 20) values.Add(("COLOR-RANK", raw.Substring(2, 4), null));
                if (analyzed) values.Add(("TURBIDITY", raw[^2..], null));
            }
            foreach (var (kind, value, unit) in values)
            {
                var test = kind == "TURBIDITY" ? "TURBIDITY" : Parameters[i];
                var source = new Dictionary<string, string>(identity, StringComparer.Ordinal) { ["result.parameter"] = Parameters[i], ["analysis.state"] = raw[..1], ["comment.mark"] = raw.Substring(1, 1), ["raw.parameter"] = raw };
                var flags = new List<CodedValue>();
                if (raw[1] != ' ') flags.Add(new(raw.Substring(1, 1), null, CodeSystems.Vendor(PluginId)));
                if (f[4] != "0000") flags.Add(new(f[4], null, CodeSystems.Vendor(PluginId)));
                var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"{trace.TraceId:N}/{trace.FirstSequence}/{test}/{kind}"))[..16]);
                var numeric = decimal.TryParse(value.Trim(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number) ? number : (decimal?)null;
                yield return new InstrumentObservation(id, instrument, specimen, CodeSet.Of(new(test, null, CodeSystems.Vendor(PluginId))), new(value, numeric, RawUnit: unit), null,
                    flags, raw[..1], !analyzed ? InstrumentStatusMeaning.NotPerformed : flags.Count > 0 ? InstrumentStatusMeaning.Warning : InstrumentStatusMeaning.Unknown,
                    kind, null, time, null, [], [], new(source), trace)
                { Quality = new(role == SpecimenRole.Control ? new(QualityMaterialKind.Control, specimen.SpecimenId) : null,
                    RunId: f[3], Method: "UC-3500") { SourceFields = source } };
            }
        }
    }
}
