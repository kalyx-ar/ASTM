using System.Text;
using Astm.Abstractions.Diagnostics;
using Astm.Abstractions.Model;
using Astm.Abstractions.Tracing;
using Astm.Plugins;
using Astm.Plugins.Tracing;
using Astm.Protocol.Hl7v2;
using Astm.Protocol.Lis02;
using Astm.Protocol.Mllp;
using Astm.Runtime.Tracing;
using Astm.Tracing;

namespace Astm.Diagnostics;

public enum DiagnosticInputFormat { Export, AstmMessage, Hl7Message, SysmexLog, LegacyLog, WireBytes, AstmTrace, ProtectedCase, HexBytes, Auto = -1 }
public sealed record DecodedMessage(long Sequence, string Direction, string Type, string Text)
{
    public long FirstSequence { get; init; } = Sequence;
    public ProtocolExplanation? Explanation { get; init; }
    public int FirstByteOffset { get; init; }
    public IReadOnlyList<InstrumentTraceField> BinaryFields { get; init; } = [];
    public IReadOnlyDictionary<string, string> Fields { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<AstmDiagnostic> Diagnostics { get; init; } = [];
}
public sealed record DiagnosticAnalysis(IReadOnlyList<TraceEntry> Entries, IReadOnlyList<StackEvent> Events,
    IReadOnlyList<DecodedMessage> Messages, IReadOnlyList<InstrumentObservation> Observations, IReadOnlyList<AstmDiagnostic> Diagnostics)
{
    public IReadOnlyList<QualityResourceReference> QualityResources { get; init; } = [];
    public IReadOnlyList<InstrumentTraceTiming> Timings { get; init; } = [];
}
public sealed record DiagnosticCase(TraceHeader Header, IReadOnlyList<TraceEntry> Entries, IReadOnlyList<InstrumentObservationBatch>? Batches = null, bool IsComplete = true);

public static class DiagnosticReader
{
    public const int MaxInputBytes = 32 * 1024 * 1024;
    internal const string UnsupportedBinaryMessage = "Recorded binary payload has no documented interpreter; raw input retained.";
    public static DiagnosticCase Read(byte[] bytes, DiagnosticInputFormat format, DiagnosticProfile profile, string sourceName, TimeSpan utcOffset, RecordedBy? recordedBy = null)
    {
        if (bytes.Length == 0 || bytes.Length > MaxInputBytes) throw new InvalidDataException("ASTM-DIAG-INPUT-LIMIT");
        if (format == DiagnosticInputFormat.Auto)
        {
            var catalog = new DiagnosticCatalog(); var detected = DiagnosticInputDetector.Detect(bytes, profile, catalog);
            format = detected.Format; if (detected.ProfileId is { } id) profile = catalog.Resolve(id);
            recordedBy ??= detected.Recorder;
        }
        if (format == DiagnosticInputFormat.ProtectedCase) throw new InvalidDataException("ASTM-DIAG-PROTECTED-READER-REQUIRED");
        if (format == DiagnosticInputFormat.HexBytes)
        {
            var decoded = BinaryWireParser.ParseHex(bytes); var catalog = new DiagnosticCatalog();
            var detected = DiagnosticInputDetector.Detect(decoded, profile, catalog);
            if (detected.ProfileId is { } id) profile = catalog.Resolve(id);
            else if (profile.ContentPlugin is not null) profile = catalog.Resolve(StandardAstmPlugin.ProfileId);
            var inner = detected.Recognized && detected.Format != DiagnosticInputFormat.HexBytes ? detected.Format : DiagnosticInputFormat.WireBytes;
            var normalized = Read(decoded, inner, profile, sourceName, utcOffset, recordedBy);
            var metadata = new Dictionary<string, string>(normalized.Header.Properties ?? new Dictionary<string, string>())
                { ["inputFormat"] = nameof(DiagnosticInputFormat.HexBytes), ["decodedFormat"] = inner.ToString(), ["wireEncoding"] = "hex" };
            return normalized with { Header = normalized.Header with { Properties = metadata } };
        }
        if (!profile.IsHl7 && (format == DiagnosticInputFormat.Hl7Message || format == DiagnosticInputFormat.WireBytes && DiagnosticInputDetector.HasHl7Header(bytes)))
            throw new InvalidDataException("ASTM-DIAG-HL7-PROFILE-REQUIRED");
        if (format == DiagnosticInputFormat.AstmTrace)
        {
            using var reader = new StringReader(new UTF8Encoding(false, true).GetString(bytes));
            var (header, entries) = TraceFile.Read(reader); return new(header, entries);
        }
        IReadOnlyList<TraceSegment> segments;
        var properties = new Dictionary<string, string> { ["inputFormat"] = format.ToString(), ["protocol"] = profile.Protocol };
        var limitations = new List<string>();
        if (format is DiagnosticInputFormat.SysmexLog or DiagnosticInputFormat.LegacyLog)
        {
            var adapterId = format == DiagnosticInputFormat.SysmexLog ? "sysmex.port-monitor" : "legacy.interface-log";
            var adapter = new DiagnosticCatalog().LogAdapters.Single(a => a.Id == adapterId);
            var original = adapter.Extract(bytes, new(RecordedBy.Host) { UtcOffset = utcOffset });
            if (!profile.IsHl7 && DiagnosticInputDetector.HasHl7Header(original.Segments)) throw new InvalidDataException("ASTM-DIAG-HL7-PROFILE-REQUIRED");
            var inferred = profile.TracePlugin?.InferRecorder(profile.TraceProfile!, original.Segments) ?? (format == DiagnosticInputFormat.LegacyLog ? RecordedBy.Host : DiagnosticInputDetector.InferRecorder(original.Segments));
            var recorder = recordedBy ?? inferred ?? RecordedBy.Instrument;
            var extracted = recorder == RecordedBy.Host ? original : adapter.Extract(bytes, new(recorder) { UtcOffset = utcOffset });
            if (extracted.Segments.Count == 0) throw new InvalidDataException("ASTM-DIAG-NO-LOG-ENTRIES");
            segments = extracted.Segments;
            properties["recordedBy"] = recorder.ToString();
            if (recordedBy is null && inferred is null) properties["directionUncertain"] = "true";
            if (profile.TracePlugin is null && !profile.IsHl7 && !DiagnosticInputDetector.HasAstmHeader(segments) && !DiagnosticInputDetector.HasHl7Header(segments) && segments.Any(s => s.Data.Length > 1))
            {
                properties["unsupportedProtocol"] = "true";
                if (segments.Any(s => s.Data.Span.Contains((byte)0))) properties["unsupportedPayload"] = "binary";
            }
            properties["logAdapter"] = adapterId;
            properties["logUtcOffset"] = utcOffset.ToString("c", System.Globalization.CultureInfo.InvariantCulture);
            properties["skippedLines"] = extracted.Skipped.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (extracted.Skipped > 0) limitations.Add("ASTM-DIAG-SOURCE-SKIPPED: " + extracted.Skipped);
            limitations.AddRange(extracted.Findings);
        }
        else
        {
            segments = [new(null, TraceDirection.Received, format == DiagnosticInputFormat.WireBytes ? SegmentContent.Wire : SegmentContent.Message, bytes)];
            if (format == DiagnosticInputFormat.WireBytes && profile.TracePlugin is { } passive && recordedBy is null
                && passive.InferRecorder(profile.TraceProfile!, segments) is null)
            { properties["directionUncertain"] = "true"; properties["recordedBy"] = "Unknown"; }
        }
        properties["timingSource"] = segments.Any(s => s.Timestamp is not null) ? "recorder" : "none";
        var (newHeader, newEntries) = TraceReconstruction.Real(segments, sourceName, "diagnostic", limitations);
        return new(newHeader with { Profile = profile.Id, Properties = properties }, newEntries);
    }

    public static DiagnosticAnalysis Analyze(DiagnosticCase input, DiagnosticProfile profile, DiagnosticTiming timing)
    {
        input = RevalidateProtocol(input, profile);
        var values = new List<InstrumentObservation>(); var findings = new List<AstmDiagnostic>(); var messages = new List<DecodedMessage>();
        if (!input.IsComplete) findings.Add(new("ASTM-DIAG-INCOMPLETE-CASE", AstmSeverity.Warning, "The case was interrupted or its final closure is missing; data may be incomplete."));
        foreach (var limitation in input.Header.Limitations ?? [])
        {
            var colon = limitation.LastIndexOf(':');
            var count = colon >= 0 ? limitation[(colon + 1)..].Trim() : "";
            var code = limitation.StartsWith("ASTM-DIAG-SOURCE-SKIPPED: ", StringComparison.Ordinal) ? "ASTM-DIAG-SOURCE-SKIPPED"
                : limitation.StartsWith("declared length differs from decoded payload: ", StringComparison.Ordinal) ? "ASTM-DIAG-SOURCE-LENGTH-MISMATCH" : null;
            findings.Add(code is not null ? new(code, AstmSeverity.Warning, count) : new("ASTM-DIAG-SOURCE-LIMITATION", AstmSeverity.Information, limitation));
        }
        if (profile.TracePlugin is { } tracePlugin)
        {
            var interpreted = tracePlugin.Interpret(profile.TraceProfile!, input.Entries, new("diagnostic"), input.Header.TraceId, timing.InstrumentTimeZone);
            findings.AddRange(interpreted.Diagnostics);
            if (input.Header.Properties?.GetValueOrDefault("directionUncertain") == "true")
                findings.Add(new("ASTM-DIAG-DIRECTION-UNCERTAIN", AstmSeverity.Warning, "Recorder side could not be inferred; confirm instrument or host."));
            messages.AddRange(interpreted.Messages.Select(m => new DecodedMessage(m.LastSequence, m.Direction.ToString(), m.Code, m.Text)
            { FirstSequence = m.FirstSequence, FirstByteOffset = m.FirstByteOffset, BinaryFields = m.BinaryFields, Explanation = m.Explanation, Fields = m.Fields,
                Diagnostics = interpreted.Diagnostics.Where(d => InMessage(d, m.FirstSequence, m.LastSequence)).ToArray() }));
            return new(input.Entries, interpreted.Events.Select(e => new StackEvent(Enum.Parse<StackLevel>(e.Level.ToString()), e.LastSequence,
                e.Elapsed, e.Direction, e.Code, e.Code, e.FirstSequence) { Explanation = e.Explanation }).ToArray(), messages, interpreted.Observations, findings) { Timings = interpreted.Timings };
        }
        if (input.Header.Properties?.GetValueOrDefault("unsupportedProtocol") == "true")
        {
            findings.Add(new("ASTM-DIAG-UNSUPPORTED-PROTOCOL", AstmSeverity.Warning, input.Header.Properties.GetValueOrDefault("unsupportedPayload") == "binary"
                ? UnsupportedBinaryMessage : "Recorded bytes do not match a supported message protocol; raw input retained."));
            return new(input.Entries, [], [], [], findings);
        }
        if (input.Header.Properties?.GetValueOrDefault("directionUncertain") == "true")
            findings.Add(new("ASTM-DIAG-DIRECTION-UNCERTAIN", AstmSeverity.Warning, "Recorder side could not be inferred; confirm instrument or host."));
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
                    findings.AddRange(events.Where(e => e.Kind != MllpEventKind.Message).Select(e => new AstmDiagnostic("ASTM-MLLP-" + e.Kind, AstmSeverity.Warning, "Malformed framing or noise.")));
                }
                foreach (var payload in payloads)
                {
                    var text = encoding.GetString(payload); var parsed = Hl7Codec.Parse(text); findings.AddRange(parsed.Diagnostics);
                    if (parsed.Message is not { } message) continue;
                    var scoped = parsed.Diagnostics.ToList();
                    if (entry.Direction == TraceDirection.Received && message.Type is "OUL" or "QBP")
                    {
                        var interpreted = dialect.Interpret(message, new(input.Header.TraceId, entry.Sequence, entry.Sequence));
                        values.AddRange(interpreted.Observations); findings.AddRange(interpreted.Diagnostics); scoped.AddRange(interpreted.Diagnostics);
                    }
                    messages.Add(new(entry.Sequence, entry.Direction.ToString(), message.Type + "^" + message.Trigger, text) { Diagnostics = scoped });
                }
            }
            foreach (var decoder in decoders.Values.Where(d => d.HasIncompleteBlock)) findings.Add(new("ASTM-MLLP-INCOMPLETE-BLOCK", AstmSeverity.Warning, "Capture ends inside an MLLP block."));
            return new(input.Entries, [], messages, values, findings);
        }
        var astm = profile.AstmPlugin!.CreateDialect(profile.AstmProfile!, new(new("diagnostic"), TimeProvider.System));
        var resources = new List<QualityResourceReference>();
        var stack = new TraceStackBuilder(profile.AstmProfile!.Encoding, astm).Build(input.Entries);
        findings.AddRange(stack.Events.Where(e => e.Code is "lis01.invalid-frame" or "lis01.frame-rejected" or "lis01.altered-by-recorder" or "lis01.implicit-acknowledge" or "lis01.retransmission")
            .Select(e => new AstmDiagnostic("ASTM-DIAG-" + e.Code.ToUpperInvariant().Replace('.', '-'), AstmSeverity.Warning, e.Detail ?? e.Code, $"entry:{e.Sequence}")));
        foreach (var message in stack.Messages)
        {
            var text = string.Concat(message.Records.Select(r => r + "\r"));
            var scoped = findings.Where(d => InMessage(d, message.FirstSequence, message.LastSequence)).ToList();
            if (!message.Complete)
            {
                var incomplete = new AstmDiagnostic("ASTM-DIAG-INCOMPLETE-MESSAGE", AstmSeverity.Warning, "Incomplete message excluded from canonical observations.", $"entry:{message.LastSequence}");
                findings.Add(incomplete); scoped.Add(incomplete);
            }
            else
            {
                var parsed = Lis02Codec.Parse(text); findings.AddRange(parsed.Diagnostics); scoped.AddRange(parsed.Diagnostics);
                if (message.Direction == TraceDirection.Received)
                foreach (var item in parsed.Messages)
                {
                    var interpreted = astm.Interpret(item, new(input.Header.TraceId, message.FirstSequence, message.LastSequence));
                    values.AddRange(interpreted.Observations); findings.AddRange(interpreted.Diagnostics); scoped.AddRange(interpreted.Diagnostics);
                    resources.AddRange(interpreted.QualityResources);
                }
            }
            messages.Add(new(message.LastSequence, message.Direction.ToString(), message.Complete ? "LIS02" : "LIS02 incompleto", text)
            { FirstSequence = message.FirstSequence, Diagnostics = scoped });
        }
        return new(input.Entries, stack.Events, messages, values, findings) { QualityResources = resources };
    }
    private static bool InMessage(AstmDiagnostic finding, long first, long last) => finding.Location?.StartsWith("entry:", StringComparison.Ordinal) == true
        && long.TryParse(finding.Location.AsSpan(6), out var sequence) && sequence >= first && sequence <= last;

    /// <summary>Recompute a previous detection failure using today's interpreter. Raw entries are immutable evidence;
    /// an import-time classification must not permanently block their reanalysis.</summary>
    public static DiagnosticCase RevalidateProtocol(DiagnosticCase input, DiagnosticProfile profile)
    {
        if (input.Header.Properties?.GetValueOrDefault("unsupportedProtocol") != "true") return input;
        var segments = input.Entries.Select(e => new TraceSegment(e.Utc, e.Direction,
            e.Direction == TraceDirection.Event ? SegmentContent.Event : e.Code == TraceReconstruction.MessageCode ? SegmentContent.Message : SegmentContent.Wire, e.Data)).ToArray();
        var recognized = profile.TracePlugin?.Recognizes(profile.TraceProfile!, segments) == true
            || profile.AstmProfile is not null && DiagnosticInputDetector.HasAstmHeader(segments)
            || profile.IsHl7 && DiagnosticInputDetector.HasHl7Header(segments);
        if (!recognized) return input;
        var properties = new Dictionary<string, string>(input.Header.Properties); properties.Remove("unsupportedProtocol"); properties.Remove("unsupportedPayload");
        properties["protocol"] = profile.Protocol;
        return input with { Header = input.Header with { Properties = properties } };
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
