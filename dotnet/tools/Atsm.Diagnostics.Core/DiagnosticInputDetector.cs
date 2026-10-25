using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using Atsm.Abstractions.Tracing;
using Atsm.Plugin.BioRad.Cfx;
using Atsm.Plugins;
using Atsm.Protocol.Lis02;

namespace Atsm.Diagnostics;

public sealed record DiagnosticInputDetection(IReadOnlyList<DiagnosticInputFormat> Candidates, string? ProfileId = null,
    RecordedBy? Recorder = null, bool UnsupportedProtocol = false)
{
    public bool Recognized => Candidates.Count == 1;
    public DiagnosticInputFormat Format => Recognized ? Candidates[0] : throw new InvalidDataException(Candidates.Count == 0 ? "ATSM-DIAG-UNKNOWN-INPUT" : "ATSM-DIAG-AMBIGUOUS-INPUT");
}

/// <summary>Read-only detection by content. It does not choose live communication deadlines or invent an instrument dialect.</summary>
public static partial class DiagnosticInputDetector
{
    public static DiagnosticInputDetection Detect(byte[] bytes, DiagnosticProfile selected, DiagnosticCatalog catalog)
    {
        if (bytes.Length == 0 || bytes.Length > DiagnosticReader.MaxInputBytes) throw new InvalidDataException("ATSM-DIAG-INPUT-LIMIT");
        if (bytes.AsSpan().StartsWith("ATSMCASE1"u8)) return new([DiagnosticInputFormat.ProtectedCase]);
        var formats = new List<DiagnosticInputFormat>();
        var matchingLogs = catalog.LogAdapters.Where(a => a is ITraceLogProbe probe && probe.Recognizes(bytes)).ToArray();
        foreach (var log in matchingLogs)
            formats.Add(log.Id switch { "sysmex.port-monitor" => DiagnosticInputFormat.SysmexLog, "legacy.interface-log" => DiagnosticInputFormat.LegacyLog, _ => throw new NotSupportedException(log.Id) });
        var text = Encoding.Latin1.GetString(bytes.AsSpan(bytes.AsSpan().StartsWith(new byte[] { 239, 187, 191 }) ? 3 : 0));
        if (matchingLogs.Length > 0)
        {
            if (matchingLogs.Length > 1) return new(formats);
            var extraction = matchingLogs[0].Extract(bytes, new(RecordedBy.Host));
            var traceProfiles = catalog.Profiles.Where(p => p.TracePlugin?.Recognizes(extraction.Segments) == true).ToArray();
            if (traceProfiles.Length == 1)
                return new(formats, traceProfiles[0].Id, traceProfiles[0].TracePlugin!.InferRecorder(extraction.Segments));
            if (traceProfiles.Length > 1) throw new InvalidDataException("ATSM-DIAG-AMBIGUOUS-INPUT");
            var hl7 = HasHl7Header(extraction.Segments);
            var compatible = HasAstmHeader(extraction.Segments) || hl7 || selected.IsHl7;
            var controlOnly = extraction.Segments.Where(s => s.Direction != TraceDirection.Event).All(s => s.Data.Span.IndexOfAnyExcept((byte)4, (byte)5, (byte)6) < 0 || s.Data.Span.SequenceEqual(new byte[] { 21 }));
            var profile = hl7 ? Hl7Profile(selected) : catalog.ResolveAstmInterpretation(extraction.Segments, selected).Id;
            return new(formats, profile, matchingLogs[0].Id == "legacy.interface-log" ? RecordedBy.Host : InferRecorder(extraction.Segments), !compatible && !controlOnly);
        }
        var first = text.TrimStart('\uFEFF', ' ', '\r', '\n', '\t').AsSpan();
        if (first.StartsWith("{", StringComparison.Ordinal))
        {
            try
            {
                var line = text.Split('\n', 2)[0].TrimStart('\uFEFF'); using var document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("format", out var format) && format.GetString() == "atsm-trace/1") formats.Add(DiagnosticInputFormat.AtsmTrace);
            }
            catch (JsonException) { }
        }
        var xml = IsRdml(bytes);
        if (xml) return new([DiagnosticInputFormat.Export], CfxPlugin.RdmlProfileId);
        var header = text.Split('\n', 2)[0];
        if (header.Contains("Well", StringComparison.Ordinal) && header.Contains("Sample", StringComparison.Ordinal) && header.Contains("Cq", StringComparison.Ordinal))
            return new([DiagnosticInputFormat.Export], CfxPlugin.CsvProfileId);
        if (header.Contains("Slope", StringComparison.Ordinal) && header.Contains("Y-intercept", StringComparison.Ordinal) && header.Contains("R^2", StringComparison.Ordinal))
            return new([DiagnosticInputFormat.Export], CfxPlugin.CurveProfileId);
        if (first.StartsWith("MSH", StringComparison.Ordinal)) return new([DiagnosticInputFormat.Hl7Message], Hl7Profile(selected));
        if (bytes.AsSpan().IndexOf("\vMSH"u8) >= 0) return new([DiagnosticInputFormat.WireBytes], Hl7Profile(selected));
        if (first.StartsWith("H|", StringComparison.Ordinal) && IsAstmHeader(first.ToString()))
            return new([DiagnosticInputFormat.AstmMessage], catalog.ResolveAstmInterpretation([new(null, TraceDirection.Received, SegmentContent.Message, bytes)], selected).Id);
        if (bytes.Contains((byte)2) && IsAstmHeader(text))
            return new([DiagnosticInputFormat.WireBytes], catalog.ResolveAstmInterpretation([new(null, TraceDirection.Received, SegmentContent.Wire, bytes)], selected).Id);
        var wire = new TraceSegment(null, TraceDirection.Received, SegmentContent.Wire, bytes);
        var protocols = catalog.Profiles.Where(p => p.TracePlugin?.Recognizes([wire]) == true).ToArray();
        if (protocols.Length == 1) return new([DiagnosticInputFormat.WireBytes], protocols[0].Id);
        return new(formats);
    }

    public static bool HasAstmHeader(IEnumerable<TraceSegment> segments) => ProbeLines(segments).Any(s => IsAstmHeader(s.Text));
    public static bool HasHl7Header(IEnumerable<TraceSegment> segments) => ProbeLines(segments).Any(s => Hl7Header().IsMatch(s.Text));
    public static bool HasHl7Header(ReadOnlySpan<byte> bytes) => Hl7Header().IsMatch(Encoding.Latin1.GetString(bytes));
    private static string? Hl7Profile(DiagnosticProfile selected) => selected.IsHl7 ? selected.Id
        : selected.InstrumentId == "abbott.architect" ? Atsm.Plugin.Abbott.Architect.ArchitectHl7Plugin.ProfileId : null;
    private static bool IsAstmHeader(string text) => Header().IsMatch(text);
    internal static IReadOnlyList<Lis02Record> AstmHeaders(IEnumerable<TraceSegment> segments) => ProbeLines(segments)
        .SelectMany(s => HeaderRecord().Matches(s.Text).Select(m => m.Groups[1].Value))
        .Where(h => Lis02Delimiters.TryParseHeader(h, out _))
        .Select(h => { Lis02Delimiters.TryParseHeader(h, out var delimiters); return new Lis02Record(h, delimiters!); }).ToArray();

    // A monitor entry is a read/write, not a frame or record. Reassemble only for probing;
    // interpretation still uses the original entries and validates framing/checksums.
    // RX and TX never share a buffer; event labels and file names are not protocol evidence.
    private static IEnumerable<(TraceDirection Direction, string Text)> ProbeLines(IEnumerable<TraceSegment> segments)
    {
        var buffers = new Dictionary<TraceDirection, StringBuilder>();
        foreach (var segment in segments.Where(s => s.Direction != TraceDirection.Event && s.Content != SegmentContent.Event))
        {
            if (!buffers.TryGetValue(segment.Direction, out var buffer)) buffers[segment.Direction] = buffer = new();
            foreach (var value in segment.Data.ToArray())
            {
                if (value is 13 or 10)
                {
                    if (buffer.Length > 0) { yield return (segment.Direction, buffer.ToString()); buffer.Clear(); }
                }
                else buffer.Append((char)value);
            }
        }
        foreach (var (direction, buffer) in buffers)
            if (buffer.Length > 0) yield return (direction, buffer.ToString());
    }
    public static RecordedBy? InferRecorder(IEnumerable<TraceSegment> segments)
    {
        var received = 0; var sent = 0;
        foreach (var (direction, text) in ProbeLines(segments))
        {
            var results = ResultOrQuery().Matches(text).Count;
            results += Hl7Header().Matches(text).Count(header => header.Value.TrimStart('\v', '\r', '\n').Split('|').ElementAtOrDefault(8)?.Split('^')[0] is "OUL" or "QBP");
            if (direction == TraceDirection.Received) received += results; else sent += results;
        }
        // R/Q records originate at the instrument for these ASTM interfaces. No inference from ACK/O alone.
        return received == sent ? null : received > sent ? RecordedBy.Host : RecordedBy.Instrument;
    }
    private static bool IsRdml(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            if (bytes.AsSpan().StartsWith("PK"u8))
            {
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
                if (archive.Entries.Count > 64) return false;
                var entries = archive.Entries.Where(e => e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (entries.Length != 1 || entries[0].Length > 16 * 1024 * 1024) return false;
                using var content = entries[0].Open(); return RdmlRoot(content);
            }
            return RdmlRoot(stream);
        }
        catch (Exception error) when (error is InvalidDataException or XmlException or IOException) { return false; }
    }
    private static bool RdmlRoot(Stream stream)
    {
        using var reader = XmlReader.Create(stream, new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 });
        reader.MoveToContent(); return reader.LocalName.Equals("rdml", StringComparison.OrdinalIgnoreCase);
    }
    [GeneratedRegex(@"(?:^|[\r\n]|\x02[0-7])H\|[^|\r\n]{3}(?=\||\r|\n|$)", RegexOptions.CultureInvariant)]
    private static partial Regex Header();
    [GeneratedRegex(@"(?:^|[\r\n]|\x02[0-7])(H\|[^|\r\n]{3}(?:\|[^\x02\x03\x17\r\n]*)?)(?=[\x03\x17\r\n]|$)", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderRecord();
    [GeneratedRegex(@"(?:^|[\r\n]|\x02[0-7])[RQ]\|", RegexOptions.CultureInvariant)]
    private static partial Regex ResultOrQuery();
    [GeneratedRegex(@"(?:^|[\v\r\n])MSH\|[^\r\n]*", RegexOptions.CultureInvariant)]
    private static partial Regex Hl7Header();
}
