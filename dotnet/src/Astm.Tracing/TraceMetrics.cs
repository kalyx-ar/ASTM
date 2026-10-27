using Astm.Abstractions.Tracing;

namespace Astm.Tracing;

public sealed record DurationStats(int Count, TimeSpan P50, TimeSpan P95, TimeSpan Max)
{
    public static DurationStats Of(IEnumerable<TimeSpan> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0)
            return new DurationStats(0, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);
        TimeSpan At(double q) => sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(q * sorted.Length) - 1)];
        return new DurationStats(sorted.Length, At(0.50), At(0.95), sorted[^1]);
    }
}

/// <summary>Analytics over a stack: counts per event code and timing of messages and host answers.</summary>
public sealed record TraceMetrics(
    IReadOnlyDictionary<string, int> EventCounts,
    int MessagesReceived,
    int MessagesTransmitted,
    int IncompleteMessages,
    DurationStats ReceivedMessageDuration,
    DurationStats TransmittedMessageDuration,
    DurationStats HostAnswerLatency,
    int UnansweredQueries)
{
    public static TraceMetrics Of(TraceStack stack)
    {
        var counts = stack.Events
            .Where(e => e.Level != StackLevel.Technical || e.Direction == TraceDirection.Event)
            .GroupBy(e => e.Code)
            .ToDictionary(g => g.Key, g => g.Count());
        var complete = stack.Messages.Where(m => m.Complete).ToList();

        // Host answer latency: end of a received query message → start of the next transmitted message.
        var latencies = new List<TimeSpan>();
        var unanswered = 0;
        var ordered = complete.OrderBy(m => m.Start).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var message = ordered[i];
            if (message.Direction != TraceDirection.Received || !message.Records.Any(r => r.Length > 0 && char.ToUpperInvariant(r[0]) == 'Q'))
                continue;
            // The answer is the first host message before the instrument speaks again; otherwise the query went unanswered.
            var next = ordered.Skip(i + 1).FirstOrDefault();
            if (next is { Direction: TraceDirection.Transmitted })
                latencies.Add(next.Start - message.End);
            else
                unanswered++;
        }

        return new TraceMetrics(
            counts,
            complete.Count(m => m.Direction == TraceDirection.Received),
            complete.Count(m => m.Direction == TraceDirection.Transmitted),
            stack.Messages.Count(m => !m.Complete),
            DurationStats.Of(complete.Where(m => m.Direction == TraceDirection.Received).Select(m => m.End - m.Start)),
            DurationStats.Of(complete.Where(m => m.Direction == TraceDirection.Transmitted).Select(m => m.End - m.Start)),
            DurationStats.Of(latencies),
            unanswered);
    }
}

public sealed record TraceComparison(
    bool SameMessages,
    int OnlyInFirst,
    int OnlyInSecond,
    IReadOnlyList<(string Code, int First, int Second)> CountDifferences,
    TraceMetrics First,
    TraceMetrics Second)
{
    /// <summary>
    /// Compares two stacks (typically real vs theoretical): message content per direction and event counts.
    /// Differences in link events show what the real exchange did beyond the standard path (retransmissions, NAK, noise...).
    /// </summary>
    public static TraceComparison Of(TraceStack first, TraceStack second)
    {
        static List<string> Keys(TraceStack stack) => stack.Messages.Where(m => m.Complete)
            .Select(m => $"{m.Direction}|{string.Join('\r', m.Records)}").ToList();
        var a = Keys(first);
        var b = Keys(second);
        var onlyFirst = a.Except(b).Count();
        var onlySecond = b.Except(a).Count();
        var metricsA = TraceMetrics.Of(first);
        var metricsB = TraceMetrics.Of(second);
        var codes = metricsA.EventCounts.Keys.Union(metricsB.EventCounts.Keys).Order(StringComparer.Ordinal);
        var differences = codes
            .Select(c => (Code: c, First: metricsA.EventCounts.GetValueOrDefault(c), Second: metricsB.EventCounts.GetValueOrDefault(c)))
            .Where(d => d.First != d.Second)
            .ToList();
        return new TraceComparison(a.SequenceEqual(b), onlyFirst, onlySecond, differences, metricsA, metricsB);
    }
}
