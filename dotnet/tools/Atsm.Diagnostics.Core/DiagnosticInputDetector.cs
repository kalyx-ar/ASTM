using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using Atsm.Abstractions.Tracing;
using Atsm.Plugin.BioRad.Cfx;
using Atsm.Plugin.Sysmex.Uwam;
using Atsm.Plugins;

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
            var profile = hl7 ? Hl7Profile(selected)
                : selected.CanConnect ? selected.Id : StandardAstmPlugin.ProfileId;
            // A recorder brand alone does not identify the protocol. Only a declared message header is a model hint.
            var headers = extraction.Segments.Where(s => s.Data.Span.IndexOf("H|"u8) >= 0).Select(s => Encoding.Latin1.GetString(s.Data.Span));
            if (!hl7 && headers.Any(h => h.Contains("U-WAM", StringComparison.OrdinalIgnoreCase) || h.Contains("UF-5000", StringComparison.OrdinalIgnoreCase))
                && selected.AstmPlugin is not UwamPlugin) profile = UwamPlugin.WithChemistryProfile; // Conservative 6 s budget; a file cannot prove the installed chemistry option.
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
            return new([DiagnosticInputFormat.AstmMessage], selected.AstmProfile is not null ? selected.Id : StandardAstmPlugin.ProfileId);
        if (bytes.Contains((byte)2) && IsAstmHeader(text))
            return new([DiagnosticInputFormat.WireBytes], selected.AstmProfile is not null ? selected.Id : StandardAstmPlugin.ProfileId);
        var wire = new TraceSegment(null, TraceDirection.Received, SegmentContent.Wire, bytes);
        var protocols = catalog.Profiles.Where(p => p.TracePlugin?.Recognizes([wire]) == true).ToArray();
        if (protocols.Length == 1) return new([DiagnosticInputFormat.WireBytes], protocols[0].Id);
        return new(formats);
    }

    public static bool HasAstmHeader(IEnumerable<TraceSegment> segments) => segments.Any(s => IsAstmHeader(Encoding.Latin1.GetString(s.Data.Span)));
    public static bool HasHl7Header(IEnumerable<TraceSegment> segments) => segments.Any(s => Hl7Header().IsMatch(Encoding.Latin1.GetString(s.Data.Span)));
    public static bool HasHl7Header(ReadOnlySpan<byte> bytes) => Hl7Header().IsMatch(Encoding.Latin1.GetString(bytes));
    private static string? Hl7Profile(DiagnosticProfile selected) => selected.IsHl7 ? selected.Id
        : selected.InstrumentId == "abbott.architect" ? Atsm.Plugin.Abbott.Architect.ArchitectHl7Plugin.ProfileId : null;
    private static bool IsAstmHeader(string text) => Header().IsMatch(text);
    public static RecordedBy? InferRecorder(IEnumerable<TraceSegment> segments)
    {
        var received = 0; var sent = 0;
        foreach (var segment in segments.Where(s => s.Direction != TraceDirection.Event))
        {
            var text = Encoding.Latin1.GetString(segment.Data.Span);
            var results = ResultOrQuery().Matches(text).Count;
            results += Hl7Header().Matches(text).Count(header => header.Value.TrimStart('\v', '\r', '\n').Split('|').ElementAtOrDefault(8)?.Split('^')[0] is "OUL" or "QBP");
            if (segment.Direction == TraceDirection.Received) received += results; else sent += results;
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
    [GeneratedRegex(@"(?:^|[\r\n]|\x02[0-7])[RQ]\|", RegexOptions.CultureInvariant)]
    private static partial Regex ResultOrQuery();
    [GeneratedRegex(@"(?:^|[\v\r\n])MSH\|[^\r\n]*", RegexOptions.CultureInvariant)]
    private static partial Regex Hl7Header();
}
