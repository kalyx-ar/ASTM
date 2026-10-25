using System.Text;
using Atsm.Abstractions.Model;
using Atsm.Abstractions.Tracing;
using Atsm.Plugins;
using Atsm.Protocol.Lis01;
using Atsm.Protocol.Lis02;

namespace Atsm.Tracing;

public enum StackLevel { Technical, Link, Message, Operation }

/// <summary>One event of the layered stack. <see cref="Sequence"/> points at the trace entry that completed it.</summary>
public sealed record StackEvent(
    StackLevel Level,
    long Sequence,
    TimeSpan Elapsed,
    TraceDirection Direction,
    string Code,
    string? Detail = null,
    long FirstSequence = -1)
{
    public ProtocolExplanation? Explanation { get; init; }
}

/// <summary>A complete LIS02 message as reassembled from the trace, with the range of entries that carried it.</summary>
public sealed record StackMessage(TraceDirection Direction, long FirstSequence, long LastSequence, TimeSpan Start, TimeSpan End, IReadOnlyList<string> Records, bool Complete);

public sealed record TraceStack(IReadOnlyList<StackEvent> Events, IReadOnlyList<StackMessage> Messages);

/// <summary>
/// Builds the layered event stack of a trace: technical (bytes), link (LIS01 events from a passive observer), message
/// (LIS02 records and diagnostics) and, when a dialect is given, operation (canonical facts). The same builder serves real
/// and theoretical traces, so both can be compared.
/// </summary>
public sealed class TraceStackBuilder(Encoding encoding, IAstmDialect? dialect = null)
{
    public TraceStack Build(IReadOnlyList<TraceEntry> entries)
    {
        var events = new List<StackEvent>();
        var messages = new List<StackMessage>();
        var observer = new Lis01Observer();
        var observations = new List<Lis01Observation>();
        var elapsed = entries.ToDictionary(e => e.Sequence, e => e.Elapsed);
        var pendingRecords = new Dictionary<TraceDirection, (long First, TimeSpan Start, List<string> Records)>();

        foreach (var entry in entries)
        {
            switch (entry.Direction)
            {
                case TraceDirection.Event:
                    events.Add(new StackEvent(StackLevel.Technical, entry.Sequence, entry.Elapsed, TraceDirection.Event, entry.Code ?? "event", entry.Detail));
                    continue;
                case var direction when entry.Code == TraceReconstruction.MessageCode:
                    // Message-level content (no link framing): records accumulate until the terminator.
                    events.Add(new StackEvent(StackLevel.Technical, entry.Sequence, entry.Elapsed, direction, "record-bytes", $"{entry.Data.Length} bytes"));
                    foreach (var record in encoding.GetString(entry.Data.Span).Split('\r', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var line = record.Trim('\n');
                        if (line.Length == 0)
                            continue;
                        if (char.ToUpperInvariant(line[0]) == 'H' && pendingRecords.Remove(direction, out var open))
                            Complete(direction, open.First, entry.Sequence, open.Start, entry.Elapsed, open.Records, complete: false, events, messages);
                        if (!pendingRecords.TryGetValue(direction, out var current))
                            pendingRecords[direction] = current = (entry.Sequence, entry.Elapsed, []);
                        current.Records.Add(line);
                        if (char.ToUpperInvariant(line[0]) == 'L')
                        {
                            pendingRecords.Remove(direction);
                            Complete(direction, current.First, entry.Sequence, current.Start, entry.Elapsed, current.Records, complete: true, events, messages);
                        }
                    }
                    continue;
                default:
                    events.Add(new StackEvent(StackLevel.Technical, entry.Sequence, entry.Elapsed, entry.Direction, "bytes", $"{entry.Data.Length} bytes"));
                    observations.Clear();
                    observer.Feed(entry.Direction == TraceDirection.Received ? Lis01Role.Instrument : Lis01Role.Host, entry.Data.Span, entry.Sequence, observations);
                    foreach (var observation in observations)
                        OnObservation(observation, elapsed, events, messages);
                    continue;
            }
        }

        var lastSequence = entries.Count == 0 ? 0 : entries[^1].Sequence;
        observations.Clear();
        observer.Flush(lastSequence, observations);
        foreach (var observation in observations)
            OnObservation(observation, elapsed, events, messages);
        foreach (var (direction, open) in pendingRecords)
            Complete(direction, open.First, lastSequence, open.Start, elapsed.GetValueOrDefault(lastSequence), open.Records, complete: false, events, messages);

        return new TraceStack(events, messages);
    }

    private void OnObservation(Lis01Observation observation, Dictionary<long, TimeSpan> elapsed, List<StackEvent> events, List<StackMessage> messages)
    {
        var direction = observation.Side == Lis01Role.Instrument ? TraceDirection.Received : TraceDirection.Transmitted;
        var at = elapsed.GetValueOrDefault(observation.Sequence);
        var frame = observation.FrameNumber >= 0 ? $"frame {observation.FrameNumber}{(observation.Final ? " ETX" : string.Empty)}" : null;
        var detail = frame is null ? observation.Detail : observation.Detail is null ? frame : $"{frame}: {observation.Detail}";
        events.Add(new StackEvent(StackLevel.Link, observation.Sequence, at, direction, "lis01." + Kebab(observation.Kind.ToString()), detail, observation.FirstSequence));
        // Empty transmissions (ENQ followed by EOT) are link checks: they stay as link events, not messages.
        if (observation.Kind != Lis01ObservationKind.TransmissionEnd || observation.Messages is not { Count: > 0 })
            return;
        // A LIS01 message may carry one record or a whole LIS02 message (CR-separated records).
        var records = observation.Messages.SelectMany(m => encoding.GetString(m).Split('\r', StringSplitOptions.RemoveEmptyEntries)).ToList();
        var start = elapsed.GetValueOrDefault(observation.FirstSequence);
        Complete(direction, observation.FirstSequence, observation.Sequence, start, at, records, observation.Complete, events, messages);
    }

    private void Complete(TraceDirection direction, long first, long last, TimeSpan start, TimeSpan end, List<string> records, bool complete,
        List<StackEvent> events, List<StackMessage> messages)
    {
        var message = new StackMessage(direction, first, last, start, end, records, complete);
        messages.Add(message);
        if (!complete)
        {
            events.Add(new StackEvent(StackLevel.Message, last, end, direction, "lis02.incomplete", $"{records.Count} record(s)", first));
            return;
        }
        var parsed = Lis02Codec.Parse(string.Concat(records.Select(r => r + "\r")));
        foreach (var diagnostic in parsed.Diagnostics)
            events.Add(new StackEvent(StackLevel.Message, last, end, direction, diagnostic.Code, diagnostic.Message, first));
        foreach (var lis02 in parsed.Messages)
        {
            events.Add(new StackEvent(StackLevel.Message, last, end, direction, "lis02.message",
                string.Concat(lis02.Records.Select(r => r.Type)), first));
            if (dialect is null)
                continue;
            var interpretation = dialect.Interpret(lis02, new TraceRef(Guid.Empty, first, last));
            foreach (var observation in interpretation.Observations)
                events.Add(new StackEvent(StackLevel.Operation, last, end, direction, "operation.observation", Describe(observation), first));
            foreach (var query in interpretation.Queries)
                events.Add(new StackEvent(StackLevel.Operation, last, end, direction, "operation.query", $"{query.Specimens.Count} specimen(s), scope {query.Scope}", first));
            foreach (var diagnostic in interpretation.Diagnostics)
                events.Add(new StackEvent(StackLevel.Operation, last, end, direction, diagnostic.Code, diagnostic.Message, first));
        }
    }

    private static string Describe(InstrumentObservation observation) =>
        $"{observation.Specimen.Role} {observation.Test.Vendor.Code} {observation.ResultKind ?? "-"} {observation.InstrumentStatusMeaning}";

    private static string Kebab(string name) =>
        string.Concat(name.Select((c, i) => char.IsUpper(c) && i > 0 ? "-" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
}
