using System.Text;
using Atsm.Abstractions.Diagnostics;
using Atsm.Abstractions.Model;
using Atsm.Abstractions.Tracing;
using Atsm.Plugins;
using Atsm.Protocol.Hl7v2;
using Atsm.Protocol.Lis02;
using Atsm.Protocol.Mllp;
using Atsm.Runtime.Tracing;
using Atsm.Tracing;

namespace Atsm.Diagnostics;

public enum DiagnosticInputFormat { Export, AstmMessage, Hl7Message, SysmexLog, LegacyLog, WireBytes, AtsmTrace, ProtectedCase, Auto = -1 }
public sealed record DecodedMessage(long Sequence, string Direction, string Type, string Text);
public sealed record DiagnosticAnalysis(IReadOnlyList<TraceEntry> Entries, IReadOnlyList<StackEvent> Events,
    IReadOnlyList<DecodedMessage> Messages, IReadOnlyList<InstrumentObservation> Observations, IReadOnlyList<AtsmDiagnostic> Diagnostics)
{
    public IReadOnlyList<QualityResourceReference> QualityResources { get; init; } = [];
}
public sealed record DiagnosticCase(TraceHeader Header, IReadOnlyList<TraceEntry> Entries, IReadOnlyList<InstrumentObservationBatch>? Batches = null, bool IsComplete = true);

public static class DiagnosticReader
{
    public const int MaxInputBytes = 32 * 1024 * 1024;
    public static DiagnosticCase Read(byte[] bytes, DiagnosticInputFormat format, DiagnosticProfile profile, string sourceName, TimeSpan utcOffset, RecordedBy? recordedBy = null)
    {
        if (bytes.Length == 0 || bytes.Length > MaxInputBytes) throw new InvalidDataException("ATSM-DIAG-INPUT-LIMIT");
        if (format == DiagnosticInputFormat.Auto)
        {
            var catalog = new DiagnosticCatalog(); var detected = DiagnosticInputDetector.Detect(bytes, profile, catalog);
            format = detected.Format; if (detected.ProfileId is { } id) profile = catalog.Resolve(id);
            recordedBy ??= detected.Recorder;
        }
        if (format == DiagnosticInputFormat.ProtectedCase) throw new InvalidDataException("ATSM-DIAG-PROTECTED-READER-REQUIRED");
        if (!profile.IsHl7 && (format == DiagnosticInputFormat.Hl7Message || format == DiagnosticInputFormat.WireBytes && DiagnosticInputDetector.HasHl7Header(bytes)))
            throw new InvalidDataException("ATSM-DIAG-HL7-PROFILE-REQUIRED");
        if (format == DiagnosticInputFormat.AtsmTrace)
        {
            using var reader = new StringReader(new UTF8Encoding(false, true).GetString(bytes));
            var (header, entries) = TraceFile.Read(reader); return new(header, entries);
        }
        IReadOnlyList<TraceSegment> segments;
        var properties = new Dictionary<string, string> { ["inputFormat"] = format.ToString(), ["protocol"] = profile.Protocol };
        var limitations = new List<string> { "Diagnostic import; no external effects." };
        if (format is DiagnosticInputFormat.SysmexLog or DiagnosticInputFormat.LegacyLog)
        {
            var adapterId = format == DiagnosticInputFormat.SysmexLog ? "sysmex.port-monitor" : "legacy.interface-log";
            var adapter = new DiagnosticCatalog().LogAdapters.Single(a => a.Id == adapterId);
            var original = adapter.Extract(bytes, new(RecordedBy.Host) { UtcOffset = utcOffset });
            if (!profile.IsHl7 && DiagnosticInputDetector.HasHl7Header(original.Segments)) throw new InvalidDataException("ATSM-DIAG-HL7-PROFILE-REQUIRED");
            var inferred = format == DiagnosticInputFormat.LegacyLog ? RecordedBy.Host : DiagnosticInputDetector.InferRecorder(original.Segments);
            var recorder = recordedBy ?? inferred ?? RecordedBy.Instrument;
            var extracted = recorder == RecordedBy.Host ? original : adapter.Extract(bytes, new(recorder) { UtcOffset = utcOffset });
            if (extracted.Segments.Count == 0) throw new InvalidDataException("ATSM-DIAG-NO-LOG-ENTRIES");
            segments = extracted.Segments;
            properties["recordedBy"] = recorder.ToString();
            if (recordedBy is null && inferred is null) properties["directionUncertain"] = "true";
            if (!profile.IsHl7 && !DiagnosticInputDetector.HasAstmHeader(segments) && !DiagnosticInputDetector.HasHl7Header(segments) && segments.Any(s => s.Data.Length > 1)) properties["unsupportedProtocol"] = "true";
            limitations.AddRange(adapter.Limitations);
            limitations.Add($"Adapter {adapterId}; recorded by {recorder}; {extracted.Skipped} skipped lines.");
            limitations.AddRange(extracted.Findings);
        }
        else segments = [new(null, TraceDirection.Received, format == DiagnosticInputFormat.WireBytes ? SegmentContent.Wire : SegmentContent.Message, bytes)];
        var (newHeader, newEntries) = TraceReconstruction.Real(segments, sourceName, "diagnostic", limitations);
        return new(newHeader with { Profile = profile.Id, Properties = properties }, newEntries);
    }

    public static DiagnosticAnalysis Analyze(DiagnosticCase input, DiagnosticProfile profile, DiagnosticTiming timing)
    {
        var values = new List<InstrumentObservation>(); var findings = new List<AtsmDiagnostic>(); var messages = new List<DecodedMessage>();
        if (!input.IsComplete) findings.Add(new("ATSM-DIAG-INCOMPLETE-CASE", AtsmSeverity.Warning, "The case was interrupted or its final closure is missing; data may be incomplete."));
        findings.AddRange((input.Header.Limitations ?? []).Select(l => new AtsmDiagnostic("ATSM-DIAG-SOURCE-LIMITATION", AtsmSeverity.Information, l)));
        if (input.Header.Properties?.GetValueOrDefault("unsupportedProtocol") == "true")
        {
            findings.Add(new("ATSM-DIAG-UNSUPPORTED-PROTOCOL", AtsmSeverity.Warning, "Recorded bytes do not match a supported message protocol; raw input retained."));
            return new(input.Entries, [], [], [], findings);
        }
        if (input.Header.Properties?.GetValueOrDefault("directionUncertain") == "true")
            findings.Add(new("ATSM-DIAG-DIRECTION-UNCERTAIN", AtsmSeverity.Warning, "Recorder side could not be inferred; confirm instrument or host."));
        if (profile.ContentPlugin is { } contentPlugin)
        {
            var payload = input.Entries.Where(e => e.Direction == TraceDirection.Received).SelectMany(e => e.Data.ToArray()).ToArray();
            var interpreted = contentPlugin.Interpret(profile.ContentProfile!, payload, new("diagnostic"), new(input.Header.TraceId, 0, Math.Max(0, input.Entries.Count - 1)));
            values.AddRange(interpreted.Observations); findings.AddRange(interpreted.Diagnostics);
            messages.Add(new(0, "RX", profile.Protocol, $"{payload.Length} bytes · {values.Count} observaciones"));
            return new(input.Entries, [], messages, values, findings);
        }
        if (profile.IsHl7)
        {
            var encoding = new UTF8Encoding(false, true);
            var dialect = profile.Hl7Dialect(timing.InstrumentTimeZone);
            var decoders = new Dictionary<TraceDirection, MllpDecoder> { [TraceDirection.Received] = new(), [TraceDirection.Transmitted] = new() };
            foreach (var entry in input.Entries.Where(e => e.Direction != TraceDirection.Event && !e.Data.IsEmpty))
            {
                var payloads = new List<byte[]>();
                if (entry.Code == TraceReconstruction.MessageCode) payloads.Add(entry.Data.ToArray());
                else
                {
                    var events = new List<MllpEvent>(); decoders[entry.Direction].Feed(entry.Data.Span, events);
                    payloads.AddRange(events.Where(e => e.Kind == MllpEventKind.Message).Select(e => e.Payload!));
                    findings.AddRange(events.Where(e => e.Kind != MllpEventKind.Message).Select(e => new AtsmDiagnostic("ATSM-MLLP-" + e.Kind, AtsmSeverity.Warning, "Malformed framing or noise.")));
                }
                foreach (var payload in payloads)
                {
                    var text = encoding.GetString(payload); var parsed = Hl7Codec.Parse(text); findings.AddRange(parsed.Diagnostics);
                    if (parsed.Message is not { } message) continue;
                    messages.Add(new(entry.Sequence, entry.Direction.ToString(), message.Type + "^" + message.Trigger, text));
                    if (entry.Direction == TraceDirection.Received && message.Type is "OUL" or "QBP")
                    {
                        var interpreted = dialect.Interpret(message, new(input.Header.TraceId, entry.Sequence, entry.Sequence));
                        values.AddRange(interpreted.Observations); findings.AddRange(interpreted.Diagnostics);
                    }
                }
            }
            foreach (var decoder in decoders.Values.Where(d => d.HasIncompleteBlock)) findings.Add(new("ATSM-MLLP-INCOMPLETE-BLOCK", AtsmSeverity.Warning, "Capture ends inside an MLLP block."));
            return new(input.Entries, [], messages, values, findings);
        }
        var astm = profile.AstmPlugin!.CreateDialect(profile.AstmProfile!, new(new("diagnostic"), TimeProvider.System));
        var resources = new List<QualityResourceReference>();
        var stack = new TraceStackBuilder(profile.AstmProfile!.Encoding, astm).Build(input.Entries);
        findings.AddRange(stack.Events.Where(e => e.Code is "lis01.invalid-frame" or "lis01.frame-rejected" or "lis01.altered-by-recorder" or "lis01.implicit-acknowledge" or "lis01.retransmission")
            .Select(e => new AtsmDiagnostic("ATSM-DIAG-" + e.Code.ToUpperInvariant().Replace('.', '-'), AtsmSeverity.Warning, e.Detail ?? e.Code, $"entry:{e.Sequence}")));
        foreach (var message in stack.Messages)
        {
            var text = string.Concat(message.Records.Select(r => r + "\r"));
            messages.Add(new(message.LastSequence, message.Direction.ToString(), message.Complete ? "LIS02" : "LIS02 incompleto", text));
            if (!message.Complete) findings.Add(new("ATSM-DIAG-INCOMPLETE-MESSAGE", AtsmSeverity.Warning, "Incomplete message excluded from canonical observations.", $"entry:{message.LastSequence}"));
            if (!message.Complete || message.Direction != TraceDirection.Received) continue;
            var parsed = Lis02Codec.Parse(text); findings.AddRange(parsed.Diagnostics);
            foreach (var item in parsed.Messages)
            {
                var interpreted = astm.Interpret(item, new(input.Header.TraceId, message.FirstSequence, message.LastSequence));
                values.AddRange(interpreted.Observations); findings.AddRange(interpreted.Diagnostics);
                resources.AddRange(interpreted.QualityResources);
            }
        }
        return new(input.Entries, stack.Events, messages, values, findings) { QualityResources = resources };
    }

    public static string DescribeBytes(ReadOnlyMemory<byte> bytes, bool hex)
    {
        if (hex) return Convert.ToHexString(bytes.Span);
        var output = new StringBuilder();
        foreach (var value in bytes.Span)
            output.Append(value switch { 2 => "<STX>", 3 => "<ETX>", 4 => "<EOT>", 5 => "<ENQ>", 6 => "<ACK>",
                11 => "<VT>", 13 => "<CR>", 21 => "<NAK>", 23 => "<ETB>", 28 => "<FS>",
                < 32 or >= 127 => $"<{value:X2}>", _ => ((char)value).ToString() });
        return output.ToString();
    }
}
