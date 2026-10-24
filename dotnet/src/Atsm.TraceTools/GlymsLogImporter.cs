using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Atsm.Abstractions.Tracing;
using Atsm.Runtime.Tracing;

namespace Atsm.TraceTools;

/// <summary>
/// Converts a Glyms interface log (<c>…Interface.log</c>) to an ATSM trace.
/// Entry layout: <c>LEVEL yyyy-MM-dd hh:mm:ss.fff AM|PM – Instrument TAG &gt;content&lt;count</c>, where
/// <c>&lt;&lt;&lt;</c> is data received from the instrument, <c>Host.EC</c> and <c>Host ECACK</c> are data sent by the host,
/// and any other tag is a host event. The CR LF ending a frame is also the log line break.
/// </summary>
public static partial class GlymsLogImporter
{
    public const string ReceivedTag = "<<<";

    private static readonly Encoding LogEncoding = Encoding.Latin1;

    public static readonly IReadOnlyList<string> Limitations =
    [
        "Timestamps are log write times in the host's local time, without offset; they are stored as UTC+00:00.",
        "Timing inside an entry and the exact split of reads are unknown.",
        "Bytes the legacy host did not log cannot be recovered.",
    ];

    [GeneratedRegex(@"(?:^|\n)(?<level>INFO|WARN|ERROR|DEBUG|FATAL) (?<date>\d{4}-\d{2}-\d{2}) (?<time>\d{1,2}:\d{2}:\d{2}\.\d{3}) (?<ampm>AM|PM) \x96 (?<label>[^>\r\n]*?)\s*>", RegexOptions.CultureInvariant)]
    private static partial Regex EntryHeader();

    [GeneratedRegex(@"<\d*\r\n?$|<\d*$", RegexOptions.CultureInvariant)]
    private static partial Regex EntryTail();

    public sealed record ImportResult(TraceHeader Header, IReadOnlyList<TraceEntry> Entries, int Skipped);

    public static ImportResult Import(byte[] log, string source, string? instrumentFilter = null)
    {
        var text = LogEncoding.GetString(log);
        var matches = EntryHeader().Matches(text);
        var entries = new List<TraceEntry>();
        DateTime? first = null;
        var skipped = 0;
        string? instrument = null;

        for (var i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            var bodyStart = match.Index + match.Length;
            var bodyEnd = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            var body = EntryTail().Replace(text[bodyStart..bodyEnd], string.Empty, 1);

            var (entryInstrument, tag) = SplitLabel(match.Groups["label"].Value);
            var matchesFilter = instrumentFilter is null
                || (entryInstrument.Length > 0
                    ? string.Equals(entryInstrument, instrumentFilter, StringComparison.OrdinalIgnoreCase)
                    : tag.StartsWith(instrumentFilter + " ", StringComparison.OrdinalIgnoreCase));
            if (!matchesFilter)
                continue;
            instrument ??= entryInstrument;

            if (!DateTime.TryParseExact($"{match.Groups["date"].Value} {match.Groups["time"].Value} {match.Groups["ampm"].Value}",
                    "yyyy-MM-dd h:mm:ss.fff tt", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            {
                skipped++;
                continue;
            }
            first ??= local;
            var elapsed = local - first.Value;
            var utc = new DateTimeOffset(local, TimeSpan.Zero);
            var data = LogEncoding.GetBytes(body);

            var direction = tag switch
            {
                ReceivedTag => TraceDirection.Received,
                "Host.EC" or "Host ECACK" => TraceDirection.Transmitted,
                _ => TraceDirection.Event,
            };
            entries.Add(direction == TraceDirection.Event
                ? new TraceEntry(entries.Count, elapsed, utc, direction, ReadOnlyMemory<byte>.Empty, "GLYMS-" + match.Groups["level"].Value, $"{tag}: {body}")
                : new TraceEntry(entries.Count, elapsed, utc, direction, data));
        }

        var header = new TraceHeader(
            Guid.CreateVersion7(),
            "glyms-log",
            instrument,
            null,
            first is { } start ? new DateTimeOffset(start, TimeSpan.Zero) : DateTimeOffset.UnixEpoch,
            ImportedFrom: source,
            Limitations: Limitations);
        return new ImportResult(header, entries, skipped);
    }

    /// <summary>Splits "Architect &lt;&lt;&lt;" or "CELL-DYN Ruby Host.EC" into instrument name and tag.</summary>
    private static (string Instrument, string Tag) SplitLabel(string label)
    {
        foreach (var tag in (ReadOnlySpan<string>)[ReceivedTag, "Host ECACK", "Host.EC"])
        {
            var index = label.LastIndexOf(tag, StringComparison.Ordinal);
            if (index >= 0)
                return (label[..index].Trim(), tag);
        }
        // Events: the instrument name is not separable from the event text; keep the whole label as tag.
        return (string.Empty, label.Trim());
    }
}
