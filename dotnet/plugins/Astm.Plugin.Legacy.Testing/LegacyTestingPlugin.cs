using System.Text;
using System.Text.RegularExpressions;
using Astm.Abstractions.Tracing;
using Astm.Plugins.Tracing;

namespace Astm.Plugin.Legacy.Testing;

/// <summary>
/// Test plugin for the proprietary logs of the legacy LIMS (<c>…Interface.log</c>). It only extracts segments;
/// the instrument dialect (for example ARCHITECT) interprets them.
/// </summary>
public sealed class LegacyTestingPlugin : ITraceLogAdapterProvider
{
    public IReadOnlyList<ITraceLogAdapter> LogAdapters { get; } = [new LegacyInterfaceLogAdapter()];
}

/// <summary>
/// Entry: <c>LEVEL yyyy-MM-dd h:mm:ss.fff AM|PM – Instrument TAG &gt;content&lt;count</c> in Windows-1252.
/// <c>&lt;&lt;&lt;</c> is data received from the instrument; <c>Host.EC</c> and <c>Host ECACK</c> are data sent by the legacy host;
/// other tags are host events. The CR LF that ends a frame is also the log line break, so the payload runs until the next entry.
/// </summary>
public sealed partial class LegacyInterfaceLogAdapter() : RegexLogAdapter(LogFormat)
{
    public static readonly RegexLogFormat LogFormat = new(
        "legacy.interface-log",
        "Legacy LIMS interface log (host side)",
        EntryPattern(),
        "yyyy-MM-dd h:mm:ss.fff tt",
        Encoding.Latin1,
        new Dictionary<string, TraceDirection>
        {
            ["<<<"] = TraceDirection.Received,
            ["Host.EC"] = TraceDirection.Transmitted,
            ["Host ECACK"] = TraceDirection.Transmitted,
        },
        [
            "Timestamps are log write times in the host's local time without offset.",
            "Timing inside an entry and the exact split of reads are unknown.",
            "Bytes the legacy host did not log cannot be recovered.",
        ])
    {
        PayloadUntilNextEntry = true,
        PayloadTail = TailPattern(),
        Content = SegmentContent.Wire,
    };

    [GeneratedRegex(@"(?:^|\n)(?<level>INFO|WARN|ERROR|DEBUG|FATAL) (?<time>\d{4}-\d{2}-\d{2} \d{1,2}:\d{2}:\d{2}\.\d{3} [AP]M) \x96 (?<label>[^>\r\n]*?)\s*(?<dir><<<|Host\.EC|Host ECACK)?\s*>", RegexOptions.CultureInvariant)]
    private static partial Regex EntryPattern();

    [GeneratedRegex(@"<\d*\r\n?$|<\d*$", RegexOptions.CultureInvariant)]
    private static partial Regex TailPattern();

    protected override bool Accept(Match match, LogAdapterOptions options)
    {
        if (options.Filter is null)
            return true;
        var label = match.Groups["label"].Value.Trim();
        return match.Groups["dir"].Success
            ? string.Equals(label, options.Filter, StringComparison.OrdinalIgnoreCase)
            : label.StartsWith(options.Filter + " ", StringComparison.OrdinalIgnoreCase);
    }

    protected override string? InstrumentHint(MatchCollection matches) =>
        matches.FirstOrDefault(m => m.Groups["dir"].Success)?.Groups["label"].Value.Trim();
}
