using Atsm.Abstractions.Tracing;
using Atsm.Plugins;

namespace Atsm.Diagnostics;

public sealed record CommunicationInterval(long FirstSequence, long LastSequence, TimeSpan Duration);
public sealed record CommunicationDistribution(int Count, TimeSpan? Minimum, TimeSpan? Median, TimeSpan? P95, TimeSpan? Maximum)
{
    public static CommunicationDistribution Of(IEnumerable<CommunicationInterval> intervals)
    {
        var sorted = intervals.Select(i => i.Duration).Order().ToArray();
        if (sorted.Length == 0) return new(0, null, null, null, null);
        var middle = sorted.Length / 2;
        var median = sorted.Length % 2 == 1 ? sorted[middle] : TimeSpan.FromTicks(sorted[middle - 1].Ticks + (sorted[middle].Ticks - sorted[middle - 1].Ticks) / 2);
        return new(sorted.Length, sorted[0], median, sorted[(int)Math.Ceiling(0.95 * sorted.Length) - 1], sorted[^1]);
    }
}
public sealed record CommunicationStatistic(string Code, string Direction, IReadOnlyList<CommunicationInterval> Intervals, int Pending = 0,
    int InvalidIntervals = 0, long Bytes = 0, int Reads = 0)
{
    public CommunicationDistribution Distribution => CommunicationDistribution.Of(Intervals);
    public string? ExchangeCode { get; init; }
    public bool ClockAvailable { get; init; } = true;
}

/// <summary>Capture intervals are independent of device operations. A response interval requires identified boundaries.</summary>
public static class CommunicationStatistics
{
    public static bool HasCaptureClock(DiagnosticCase input) => input.Header.Properties?.GetValueOrDefault("timingSource") != "none"
        && !(input.Header.Properties?.GetValueOrDefault("timingSource") is null
            && input.Header.Properties?.GetValueOrDefault("inputFormat") is "WireBytes" or "AstmMessage" or "Hl7Message" or "Export");
    public static IReadOnlyList<CommunicationStatistic> Build(DiagnosticAnalysis analysis, bool clockAvailable = true)
    {
        var result = new List<CommunicationStatistic>();
        var data = analysis.Entries.Where(e => e.Direction != TraceDirection.Event && e.Data.Length > 0).ToArray();
        var bySequence = analysis.Entries.ToDictionary(e => e.Sequence);
        void Add(string code, string direction, IEnumerable<(long First, long? Last)> boundaries, long bytes = 0, int reads = 0, string? exchangeCode = null)
        {
            var intervals = new List<CommunicationInterval>(); var pending = 0; var invalid = 0;
            foreach (var (first, last) in boundaries)
            {
                if (last is null) { pending++; continue; }
                if (!clockAvailable) continue;
                if (!bySequence.TryGetValue(first, out var start) || !bySequence.TryGetValue(last.Value, out var end) || end.Elapsed < start.Elapsed || end.Sequence < start.Sequence)
                { invalid++; continue; }
                intervals.Add(new(first, last.Value, end.Elapsed - start.Elapsed));
            }
            result.Add(new(code, direction, intervals, pending, invalid, bytes, reads) { ExchangeCode = exchangeCode, ClockAvailable = clockAvailable });
        }
        var directions = data.Select(e => e.Direction.ToString()).Distinct().ToArray();
        foreach (var direction in directions.Length > 1 ? new[] { "All" }.Concat(directions) : directions)
        {
            var stream = data.Where(e => direction == "All" || e.Direction.ToString() == direction).ToArray();
            if (stream.Length == 0) continue;
            Add("ReadInterval", direction, stream.Zip(stream.Skip(1), (a, b) => (a.Sequence, (long?)b.Sequence)), stream.Sum(e => (long)e.Data.Length), stream.Length);
        }
        foreach (var from in new[] { TraceDirection.Received, TraceDirection.Transmitted })
        {
            var turns = data.Zip(data.Skip(1)).Where(p => p.First.Direction == from && p.Second.Direction != from).ToArray();
            if (turns.Length > 0) Add("DirectionTurn", from.ToString(), turns.Select(p => (p.First.Sequence, (long?)p.Second.Sequence)));
        }
        if (analysis.Messages.Count > 0)
            Add("MessageReception", "All", analysis.Messages.Select(m => (m.FirstSequence, (long?)m.Sequence)));

        // LIS01 observer events already establish framing, sender and acknowledgement. Binary byte coincidences are never ACKs here.
        var enquiries = new Dictionary<TraceDirection, long>(); var frames = new Dictionary<TraceDirection, long>();
        var enquiryPairs = new List<(long, long?)>(); var framePairs = new List<(long, long?)>();
        var enquiryNak = new List<(long, long?)>(); var frameNak = new List<(long, long?)>();
        foreach (var item in analysis.Events)
        {
            if (item.Code == "lis01.enquiry")
            {
                if (enquiries.Remove(item.Direction, out var previous)) enquiryPairs.Add((previous, null));
                enquiries[item.Direction] = item.Sequence;
            }
            else if (item.Code == "lis01.established" && enquiries.Remove(item.Direction, out var enquiry)) enquiryPairs.Add((enquiry, item.Sequence));
            else if (item.Code == "lis01.busy")
            {
                var sender = item.Direction == TraceDirection.Received ? TraceDirection.Transmitted : TraceDirection.Received;
                if (enquiries.Remove(sender, out var refused)) enquiryNak.Add((refused, item.Sequence));
            }
            else if (item.Code == "lis01.frame" || item.Code == "lis01.invalid-frame" && item.Detail?.EndsWith(": Checksum", StringComparison.Ordinal) == true)
                frames[item.Direction] = item.Sequence;
            else if (item.Code == "lis01.frame-accepted" && frames.Remove(item.Direction, out var frame)) framePairs.Add((frame, item.Sequence));
            else if (item.Code == "lis01.frame-rejected")
            {
                var sender = item.Direction == TraceDirection.Received ? TraceDirection.Transmitted : TraceDirection.Received;
                if (frames.Remove(sender, out var refused)) frameNak.Add((refused, item.Sequence));
            }
            else if (item.Code == "lis01.transmission-end") frames.Remove(item.Direction);
        }
        enquiryPairs.AddRange(enquiries.Values.Select(s => (s, (long?)null)));
        framePairs.AddRange(frames.Values.Select(s => (s, (long?)null)));
        if (enquiryPairs.Count > 0) Add("EnquiryAck", "All", enquiryPairs);
        if (framePairs.Count > 0) Add("FrameAck", "All", framePairs);
        if (enquiryNak.Count > 0) Add("EnquiryNak", "All", enquiryNak);
        if (frameNak.Count > 0) Add("FrameNak", "All", frameNak);
        foreach (var group in analysis.Timings.GroupBy(t => (t.Kind, t.Code)))
            Add(group.Key.Kind.ToString(), "All", group.Select(t => (t.FirstSequence, t.LastSequence)), exchangeCode: group.Key.Code);
        return result;
    }
}
