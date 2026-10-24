using System.Text;
using Atsm.Abstractions.Tracing;
using Atsm.Protocol.Lis01;
using Atsm.Runtime.Tracing;

namespace Atsm.Tracing;

/// <summary>Messages to replay in a theoretical trace: direction (host's view), desired start and CR-less records.</summary>
/// <param name="GapAfterPrevious">Real idle time between the end of the previous message and this one. When set, the theoretical
/// timeline keeps the real reaction times and only replaces the link transfers; otherwise <paramref name="At"/> is used.</param>
public sealed record MessagePlan(TraceDirection Direction, TimeSpan At, IReadOnlyList<string> Records, TimeSpan? GapAfterPrevious = null);

public sealed record TheoreticalOptions
{
    public Lis01Options Link { get; init; } = Lis01Options.Default;

    public Encoding Encoding { get; init; } = Encoding.Latin1;

    /// <summary>Line speed used to compute byte transfer times; 0 transfers instantly.</summary>
    public int BaudRate { get; init; } = 9600;

    /// <summary>Start, data, parity and stop bits per character (8N1 = 10).</summary>
    public int BitsPerCharacter { get; init; } = 10;

    /// <summary>Time a side takes to react to the bytes it received.</summary>
    public TimeSpan ResponseDelay { get; init; } = TimeSpan.FromMilliseconds(5);

    public Atsm.Plugins.AstmFraming InstrumentFraming { get; init; } = Atsm.Plugins.AstmFraming.FramePerRecord;

    public Atsm.Plugins.AstmFraming HostFraming { get; init; } = Atsm.Plugins.AstmFraming.FramePerRecord;
}

/// <summary>
/// One method for real and theoretical traces. A real trace keeps the pure segments as they were captured; a theoretical
/// trace replays the same messages through two LIS01-A2 links with a virtual clock, which shows how the exchange should look
/// according to the standard and the profile. Both go through <see cref="TraceStackBuilder"/> for observability and analytics.
/// </summary>
public static class TraceReconstruction
{
    /// <summary>Entry code for message-level content (records without link framing).</summary>
    public const string MessageCode = "atsm.message";

    public static (TraceHeader Header, List<TraceEntry> Entries) Real(IEnumerable<TraceSegment> segments, string source, string? instrumentId, IReadOnlyList<string>? limitations = null)
    {
        var list = segments.ToList();
        var origin = list.Select(s => s.Timestamp).FirstOrDefault(t => t is not null) ?? DateTimeOffset.UnixEpoch;
        var entries = new List<TraceEntry>(list.Count);
        foreach (var segment in list)
        {
            var at = segment.Timestamp ?? origin;
            var elapsed = at - origin;
            entries.Add(segment.Content switch
            {
                SegmentContent.Event => new TraceEntry(entries.Count, elapsed, at, TraceDirection.Event, ReadOnlyMemory<byte>.Empty, segment.Code, segment.Detail),
                SegmentContent.Message => new TraceEntry(entries.Count, elapsed, at, segment.Direction, segment.Data, MessageCode),
                _ => new TraceEntry(entries.Count, elapsed, at, segment.Direction, segment.Data),
            });
        }
        var header = new TraceHeader(Guid.CreateVersion7(), "reconstructed", instrumentId, null, origin, ImportedFrom: source, Limitations: limitations);
        return (header, entries);
    }

    /// <summary>Messages of a stack, ready to be replayed as a theoretical trace.</summary>
    public static List<MessagePlan> Plan(TraceStack stack) =>
        PlanWithGaps(stack.Messages.Where(m => m.Complete).OrderBy(m => m.Start).ToList());

    private static List<MessagePlan> PlanWithGaps(List<StackMessage> messages) =>
        messages.Select((m, i) => new MessagePlan(m.Direction, m.Start, m.Records,
            i == 0 ? null : m.Start - messages[i - 1].End < TimeSpan.Zero ? TimeSpan.Zero : m.Start - messages[i - 1].End)).ToList();

    /// <summary>Messages from plain LIS02 text (records separated by CR or CR LF), all in one direction, back to back.</summary>
    public static List<MessagePlan> Plan(string text, TraceDirection direction)
    {
        var plans = new List<MessagePlan>();
        List<string>? current = null;
        foreach (var raw in text.Replace("\r\n", "\r", StringComparison.Ordinal).Replace('\n', '\r').Split('\r'))
        {
            if (raw.Length == 0)
                continue;
            if (char.ToUpperInvariant(raw[0]) == 'H' || current is null)
            {
                if (current is { Count: > 0 })
                    plans.Add(new MessagePlan(direction, TimeSpan.Zero, current));
                current = [];
            }
            current.Add(raw);
            if (char.ToUpperInvariant(raw[0]) == 'L')
            {
                plans.Add(new MessagePlan(direction, TimeSpan.Zero, current));
                current = null;
            }
        }
        if (current is { Count: > 0 })
            plans.Add(new MessagePlan(direction, TimeSpan.Zero, current));
        return plans;
    }

    public static (TraceHeader Header, List<TraceEntry> Entries) Theoretical(IEnumerable<MessagePlan> plans, TheoreticalOptions? options = null, string? instrumentId = null, string? source = null)
    {
        options ??= new TheoreticalOptions();
        var host = new Lis01Link(Lis01Role.Host, options.Link);
        var instrument = new Lis01Link(Lis01Role.Instrument, options.Link);
        var entries = new List<TraceEntry>();
        var origin = DateTimeOffset.UnixEpoch;
        var now = TimeSpan.Zero;

        foreach (var plan in plans.OrderBy(p => p.At))
        {
            var start = plan.GapAfterPrevious is { } gap ? now + gap : plan.At;
            if (start > now)
                now = start;
            var sender = plan.Direction == TraceDirection.Received ? instrument : host;
            var framing = plan.Direction == TraceDirection.Received ? options.InstrumentFraming : options.HostFraming;
            sender.Enqueue(framing == Atsm.Plugins.AstmFraming.FramePerRecord
                ? plan.Records.Select(r => (ReadOnlyMemory<byte>)options.Encoding.GetBytes(r + "\r")).ToList()
                : [options.Encoding.GetBytes(string.Concat(plan.Records.Select(r => r + "\r")))]);
            now = Pump(host, instrument, now, options, entries, origin);
        }

        var limitations = new List<string>
        {
            $"Theoretical trace: LIS01-A2 exchange simulated with the profile timers, {options.BaudRate} baud, {options.BitsPerCharacter} bits per character and {options.ResponseDelay.TotalMilliseconds} ms reaction time.",
            "No line errors, losses or retransmissions are simulated.",
        };
        var header = new TraceHeader(Guid.CreateVersion7(), "theoretical", instrumentId, null, origin, ImportedFrom: source, Limitations: limitations);
        return (header, entries);
    }

    private static TimeSpan Pump(Lis01Link host, Lis01Link instrument, TimeSpan now, TheoreticalOptions options, List<TraceEntry> entries, DateTimeOffset origin)
    {
        var wire = new Queue<(bool FromHost, byte[] Bytes)>();
        for (var guard = 0; guard < 1_000_000; guard++)
        {
            if (wire.Count == 0)
            {
                var hostActions = new List<Lis01Action>();
                var instrumentActions = new List<Lis01Action>();
                host.Poll(now, hostActions);
                instrument.Poll(now, instrumentActions);
                Collect(hostActions, fromHost: true, wire);
                Collect(instrumentActions, fromHost: false, wire);
                if (wire.Count == 0)
                {
                    // Waiting for a retry timer (busy or contention): jump to it, otherwise the exchange is over.
                    var next = new[] { host.NextDeadline, instrument.NextDeadline }.Where(d => d is not null).Min();
                    if (next is null || host.PendingTransmissions + instrument.PendingTransmissions == 0)
                        return now;
                    now = next.Value > now ? next.Value : now;
                    continue;
                }
            }

            var (fromHost, bytes) = wire.Dequeue();
            var transfer = options.BaudRate <= 0
                ? TimeSpan.Zero
                : TimeSpan.FromSeconds(bytes.Length * options.BitsPerCharacter / (double)options.BaudRate);
            now += transfer;
            entries.Add(new TraceEntry(entries.Count, now, origin + now, fromHost ? TraceDirection.Transmitted : TraceDirection.Received, bytes));
            now += options.ResponseDelay;
            var actions = new List<Lis01Action>();
            (fromHost ? instrument : host).OnBytes(bytes, now, actions);
            Collect(actions, fromHost: !fromHost, wire);
        }
        throw new InvalidOperationException("Theoretical exchange did not settle.");
    }

    private static void Collect(List<Lis01Action> actions, bool fromHost, Queue<(bool, byte[])> wire)
    {
        foreach (var action in actions)
        {
            if (action is Lis01Write write)
                wire.Enqueue((fromHost, write.Bytes));
        }
    }
}
