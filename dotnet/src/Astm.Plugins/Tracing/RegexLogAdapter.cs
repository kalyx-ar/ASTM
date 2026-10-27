using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Astm.Abstractions.Tracing;

namespace Astm.Plugins.Tracing;

/// <summary>
/// Declarative description of a line- or entry-oriented log. The <see cref="Entry"/> pattern uses named groups:
/// <c>time</c> (required), <c>dir</c> (direction token as written by the recorder), <c>payload</c>, optional
/// <c>length</c> (declared byte count, checked after decoding) and <c>label</c> (event text when there is no direction).
/// </summary>
public sealed record RegexLogFormat(
    string Id,
    string Description,
    Regex Entry,
    string TimeFormat,
    Encoding Encoding,
    IReadOnlyDictionary<string, TraceDirection> DirectionsFromRecorder,
    IReadOnlyList<string> Limitations)
{
    /// <summary>Textual tokens replaced by bytes inside the payload, e.g. <c>[CR]</c>.</summary>
    public IReadOnlyDictionary<string, byte> Tokens { get; init; } = new Dictionary<string, byte>();

    /// <summary>When true the payload runs from the end of one entry match to the start of the next (multi-line entries).</summary>
    public bool PayloadUntilNextEntry { get; init; }

    /// <summary>Stripped from the end of every payload (log decoration such as a byte counter).</summary>
    public Regex? PayloadTail { get; init; }

    /// <summary>Forces the content kind; when null it is inferred (<see cref="SegmentClassifier"/>).</summary>
    public SegmentContent? Content { get; init; }
}

/// <summary>Generic adapter driven by a <see cref="RegexLogFormat"/>; plugins configure it instead of writing parsers.</summary>
public class RegexLogAdapter(RegexLogFormat format) : ITraceLogAdapter, ITraceLogProbe
{
    public RegexLogFormat Format { get; } = format;

    public string Id => Format.Id;

    public string Description => Format.Description;

    public IReadOnlyList<string> Limitations => Format.Limitations;

    public bool Recognizes(ReadOnlySpan<byte> content) => Format.Entry.IsMatch(Format.Encoding.GetString(content));

    public virtual LogExtraction Extract(ReadOnlySpan<byte> log, LogAdapterOptions options)
    {
        var text = Format.Encoding.GetString(log);
        var matches = Format.Entry.Matches(text);
        var segments = new List<TraceSegment>();
        var findings = new Dictionary<string, int>();
        var skipped = 0;

        for (var i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            if (!DateTime.TryParseExact(match.Groups["time"].Value, Format.TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            {
                skipped++;
                continue;
            }
            var timestamp = new DateTimeOffset(local, options.UtcOffset);

            var payload = Format.PayloadUntilNextEntry
                ? text[(match.Index + match.Length)..(i + 1 < matches.Count ? matches[i + 1].Index : text.Length)]
                : match.Groups["payload"].Value;
            if (Format.PayloadTail is { } tail)
                payload = tail.Replace(payload, string.Empty, 1);

            if (!Accept(match, options))
                continue;

            var dir = match.Groups["dir"];
            if (!dir.Success || !Format.DirectionsFromRecorder.TryGetValue(dir.Value, out var fromRecorder))
            {
                var label = match.Groups["label"].Success ? match.Groups["label"].Value.Trim() : dir.Value;
                segments.Add(new TraceSegment(timestamp, TraceDirection.Event, SegmentContent.Event, ReadOnlyMemory<byte>.Empty,
                    Format.Id, payload.Length == 0 ? label : $"{label}: {payload}"));
                continue;
            }

            var bytes = Decode(payload);
            if (match.Groups["length"].Success && int.TryParse(match.Groups["length"].Value, out var declared) && declared != bytes.Length)
                Count(findings, "declared length differs from decoded payload");
            if (bytes.Length == 0)
                continue;
            var direction = options.RecordedBy == RecordedBy.Host
                ? fromRecorder
                : fromRecorder == TraceDirection.Received ? TraceDirection.Transmitted : TraceDirection.Received;
            segments.Add(new TraceSegment(timestamp, direction, Format.Content ?? SegmentClassifier.Classify(bytes), bytes));
        }

        return new LogExtraction(segments, skipped, findings.Select(f => $"{f.Key}: {f.Value}").ToList(), InstrumentHint(matches));
    }

    /// <summary>Hook for formats that interleave several instruments or channels.</summary>
    protected virtual bool Accept(Match match, LogAdapterOptions options) => true;

    protected virtual string? InstrumentHint(MatchCollection matches) => null;

    protected byte[] Decode(string payload)
    {
        if (Format.Tokens.Count == 0)
            return Format.Encoding.GetBytes(payload);
        var output = new List<byte>(payload.Length);
        var i = 0;
        while (i < payload.Length)
        {
            if (payload[i] == '[')
            {
                var end = payload.IndexOf(']', i + 1);
                if (end > i && Format.Tokens.TryGetValue(payload[i..(end + 1)], out var value))
                {
                    output.Add(value);
                    i = end + 1;
                    continue;
                }
            }
            output.AddRange(Format.Encoding.GetBytes(payload[i].ToString()));
            i++;
        }
        return [.. output];
    }

    private static void Count(Dictionary<string, int> counts, string key) => counts[key] = counts.GetValueOrDefault(key) + 1;
}

public static class SegmentClassifier
{
    /// <summary>Bytes containing LIS01 / MLLP link control characters are wire content; plain records are message content.</summary>
    public static SegmentContent Classify(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            if (value is 0x02 or 0x04 or 0x05 or 0x06 or 0x15 or 0x0B or 0x1C)
                return SegmentContent.Wire;
        }
        return SegmentContent.Message;
    }
}

/// <summary>ASCII control character names as used by many vendor monitors: <c>[NUL]</c> … <c>[US]</c>, <c>[DEL]</c>.</summary>
public static class AsciiControlTokens
{
    private static readonly string[] Names =
    [
        "NUL", "SOH", "STX", "ETX", "EOT", "ENQ", "ACK", "BEL", "BS", "HT", "LF", "VT", "FF", "CR", "SO", "SI",
        "DLE", "DC1", "DC2", "DC3", "DC4", "NAK", "SYN", "ETB", "CAN", "EM", "SUB", "ESC", "FS", "GS", "RS", "US",
    ];

    public static IReadOnlyDictionary<string, byte> Bracketed { get; } = Build();

    private static Dictionary<string, byte> Build()
    {
        var tokens = Names.Select((name, i) => (Key: $"[{name}]", Value: (byte)i)).ToDictionary(p => p.Key, p => p.Value);
        tokens["[DEL]"] = 0x7F;
        return tokens;
    }
}
