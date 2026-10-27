using System.Security.Cryptography;
using System.Text;
using Atsm.Abstractions.Model;
using Atsm.Abstractions.Tracing;
using Atsm.Plugins;
using Atsm.Protocol.Lis01;
using Atsm.Protocol.Lis02;
using Atsm.Runtime.Sessions;
using Atsm.Runtime.Tracing;
using Atsm.Testing;

namespace Atsm.Diagnostics;

public sealed record CommunicationScenario(IReadOnlyList<string> Messages, IReadOnlyList<string> ExpectedObservations, int ExpectedQueries)
{
    public TimeSpan MinimumTransmissionTime(DiagnosticProfile profile, Atsm.Abstractions.Transport.SerialSettings serial)
    {
        var bits = 1 + serial.DataBits + (serial.Parity == Atsm.Abstractions.Transport.SerialParity.None ? 0 : 1)
            + (serial.StopBits == Atsm.Abstractions.Transport.SerialStopBits.Two ? 2 : 1);
        return TimeSpan.FromSeconds(Messages.Sum(m => (double)profile.AstmProfile!.Encoding.GetByteCount(m)) * bits / serial.BaudRate);
    }
    /// <summary>Private source bytes and values remain in memory. Selects complete instrument messages, not captured ACKs from both sides.</summary>
    public static CommunicationScenario FromTrace(DiagnosticCase source, DiagnosticProfile profile, int limit = 20)
    {
        if (profile.AstmPlugin is null || profile.AstmProfile is null) throw new NotSupportedException("ATSM-SCENARIO-ASTM-REQUIRED");
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        var analysis = DiagnosticReader.Analyze(source, profile, new(TimeSpan.Zero, TimeSpan.Zero, TimeZoneInfo.Utc));
        var candidates = analysis.Messages.Where(m => m.Direction == nameof(TraceDirection.Received)
            && !m.Type.Contains("incompleto", StringComparison.Ordinal)
            && Lis02Codec.Parse(m.Text).Messages.Any(p => p.Header is not null && p.IsTerminated)).Select(m => m.Text).ToArray();
        var messages = candidates.Take(limit).ToArray();
        var dialect = profile.AstmPlugin.CreateDialect(profile.AstmProfile, new(new("scenario"), TimeProvider.System));
        // Include a sample result so the bench can exercise a host order and its response, preserving relative source order.
        if (!messages.Any(m => CommandData(m, dialect) is not null) && candidates.FirstOrDefault(m => CommandData(m, dialect) is not null) is { } response && messages.Length > 0) messages[^1] = response;
        if (messages.Length == 0) throw new InvalidDataException("ATSM-SCENARIO-NO-INSTRUMENT-MESSAGES");
        var expected = new List<string>(); var queries = 0;
        foreach (var text in messages)
        foreach (var message in Lis02Codec.Parse(text).Messages)
        {
            var result = dialect.Interpret(message, new(Guid.Empty, 0, 0));
            expected.AddRange(result.Observations.Select(Signature)); queries += result.Queries.Count;
        }
        return new(messages, expected, queries);
    }
    private static (string Sample, string[] Tests)? CommandData(string text, IAstmDialect dialect)
    {
        // A result transmission may carry many O records. Let the plugin resolve specimen and test fields.
        var observations = Lis02Codec.Parse(text).Messages.SelectMany(m => dialect.Interpret(m, new(Guid.Empty, 0, 0)).Observations).ToArray();
        var sample = observations.FirstOrDefault(o => !string.IsNullOrWhiteSpace(o.Specimen.SpecimenId) && !string.IsNullOrWhiteSpace(o.Test.Vendor.Code))?.Specimen.SpecimenId;
        return sample is null ? null : (sample, observations.Where(o => o.Specimen.SpecimenId == sample)
            .Select(o => o.Test.Vendor.Code).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.Ordinal).ToArray());
    }
    private static string Signature(InstrumentObservation o) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join('\u001F', o.Specimen.SpecimenId, o.Test.Vendor.Code, o.Value.RawText, o.Value.RawUnit, o.InstrumentStatus,
            string.Join('\u001E', o.Flags.Select(f => f.Code))))));

    /// <summary>Runs real LIS01 sessions over the supplied endpoints, including the driver's-free serial test port of PortGate.</summary>
    public async Task<CommunicationScenarioResult> RunAsync(DiagnosticProfile profile, Stream hostStream, Stream instrumentStream,
        bool rejectBadChecksum, CancellationToken token)
    {
        var selected = profile.AstmProfile ?? throw new NotSupportedException("ATSM-SCENARIO-ASTM-REQUIRED");
        var header = new TraceHeader(Guid.CreateVersion7(), "communication-scenario", "scenario", profile.Id, DateTimeOffset.UtcNow);
        var trace = new DiagnosticTraceSink(header); var host = new InMemoryLabHost();
        var session = new AstmInstrumentSession(hostStream, profile.AstmPlugin!.CreateDialect(selected, new(new("scenario"), TimeProvider.System)),
            selected, host, new("scenario"), trace, new());
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var running = session.RunAsync(stop.Token);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        await using var instrument = new InstrumentSimulator(instrumentStream, selected.Link, selected.Encoding);
        try
        {
            if (rejectBadChecksum)
            {
                await instrument.WriteRawAsync([5]);
                await WaitAsync(() => trace.Snapshot().Any(e => e.Direction == TraceDirection.Transmitted && e.Data.Span.SequenceEqual(new byte[] { 6 })), token);
                var bad = Lis01Frame.Build(1, "H|\\^&\r"u8, true); bad[^3] = bad[^3] == (byte)'0' ? (byte)'1' : (byte)'0';
                await instrument.WriteRawAsync(bad);
                await WaitAsync(() => trace.Snapshot().Any(e => e.Direction == TraceDirection.Transmitted && e.Data.Span.SequenceEqual(new byte[] { 21 })), token);
                // Retry the rejected frame with its correct checksum in the same transmission.
                await instrument.WriteRawAsync(Lis01Frame.Build(1, "H|\\^&\r"u8, true));
                await WaitAsync(() => trace.Snapshot().Count(e => e.Direction == TraceDirection.Transmitted && e.Data.Span.SequenceEqual(new byte[] { 6 })) >= 2, token);
                await instrument.WriteRawAsync(Lis01Frame.Build(2, "L|1|N\r"u8, true));
                await WaitAsync(() => trace.Snapshot().Count(e => e.Direction == TraceDirection.Transmitted && e.Data.Span.SequenceEqual(new byte[] { 6 })) >= 3, token);
                await instrument.WriteRawAsync([4]);
                await WaitAsync(() => session.LinkState == Lis01State.Idle, token);
            }
            var acknowledged = 0;
            var commandResponse = -1; var commandsExercised = 0;
            if (profile.AstmPlugin is IInstrumentCommandPlugin commandPlugin && commandPlugin.Commands(profile.Id).Any(c => c.Id == AstmOrderCommands.SendOrder))
            {
                var dialect = profile.AstmPlugin.CreateDialect(selected, new(new("scenario"), TimeProvider.System));
                commandResponse = Enumerable.Range(0, Messages.Count).FirstOrDefault(i => CommandData(Messages[i], dialect) is not null, -1);
                if (commandResponse >= 0)
                {
                    var reply = Lis02Codec.Parse(Messages[commandResponse]).Messages.Single();
                    var (sample, tests) = CommandData(Messages[commandResponse], dialect)!.Value;
                    var sending = session.SendCommandAsync(commandPlugin, new(AstmOrderCommands.SendOrder,
                        new Dictionary<string, string> { ["sample"] = sample, ["tests"] = string.Join(',', tests) }), token);
                    // The simulated instrument decodes the order before deciding which captured result to return.
                    var received = Lis02Codec.Parse(await instrument.Received.ReadAsync(token)).Messages.Single();
                    var order = received.OfType('O').Single();
                    var requested = order.Repeats(5).Select(r => received.Delimiters.Decode(r.Split(received.Delimiters.Component).ElementAtOrDefault(3) ?? "")).ToHashSet(StringComparer.Ordinal);
                    if (order.Text(3) != sample || !requested.SetEquals(tests)) throw new InvalidDataException("ATSM-SCENARIO-COMMAND-MISMATCH");
                    var receipt = await sending;
                    if (receipt.Result != Lis01SendResult.Delivered) throw new IOException("ATSM-SCENARIO-COMMAND-NOT-DELIVERED");
                    acknowledged += receipt.FramesAcknowledged;
                    var response = await instrument.SendMessagesAsync(selected.Frame(reply.Records, selected.InstrumentFraming)).WaitAsync(token);
                    if (response.Result != Lis01SendResult.Delivered) throw new IOException("ATSM-SCENARIO-COMMAND-RESPONSE-FAILED");
                    acknowledged += response.FramesAcknowledged; commandsExercised++;
                }
            }
            foreach (var (message, index) in Messages.Select((m, i) => (m, i)))
            {
                if (index == commandResponse) continue;
                var records = Lis02Codec.Parse(message).Messages.SelectMany(m => m.Records).ToArray();
                var receipt = await instrument.SendMessagesAsync(selected.Frame(records, selected.InstrumentFraming)).WaitAsync(token);
                if (receipt.Result != Lis01SendResult.Delivered) throw new IOException("ATSM-SCENARIO-NOT-DELIVERED");
                acknowledged += receipt.FramesAcknowledged;
                // Let the host finish its query answer before the next instrument ENQ.
                await WaitAsync(() => session.LinkState == Lis01State.Idle, token);
            }
            await WaitAsync(() => host.Batches.Sum(b => b.Observations.Count) >= ExpectedObservations.Count && host.Queries.Count >= ExpectedQueries, token);
            for (var i = 0; i < ExpectedQueries; i++)
            {
                var response = await instrument.Received.ReadAsync(token);
                if (!Lis02Codec.Parse(response).Messages.Any(m => m.Header is not null && m.IsTerminated)) throw new InvalidDataException("ATSM-SCENARIO-QUERY-RESPONSE");
            }
            var actual = host.Batches.SelectMany(b => b.Observations).Select(Signature).Order(StringComparer.Ordinal).ToArray();
            if (!ExpectedObservations.Order(StringComparer.Ordinal).SequenceEqual(actual)) throw new InvalidDataException("ATSM-SCENARIO-OBSERVATION-MISMATCH");
            var captured = new DiagnosticCase(header, trace.Snapshot());
            var interpreted = DiagnosticReader.Analyze(captured, profile, new(TimeSpan.Zero, TimeSpan.Zero, TimeZoneInfo.Utc));
            var timings = CommunicationStatistics.Build(interpreted);
            var acknowledgements = timings.Where(t => t.Code is "EnquiryAck" or "FrameAck").ToArray();
            var withinBudgets = acknowledgements.Length > 0 && acknowledgements.All(t => t.Distribution.Maximum is { } maximum
                && maximum <= (t.Code == "EnquiryAck" ? selected.Link.EstablishmentTimeout : selected.Link.FrameAckTimeout));
            return new(Messages.Count, actual.Length, host.Queries.Count, acknowledged, interpreted) { ResponsesConfirmed = ExpectedQueries, ProtocolBudgetsMet = withinBudgets,
                HostCommands = commandsExercised, CapturedCase = captured, Elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started) };
        }
        finally
        {
            stop.Cancel();
            try { await running; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }
    private static async Task WaitAsync(Func<bool> ready, CancellationToken token)
    { while (!ready()) await Task.Delay(5, token); }
}
public sealed record CommunicationScenarioResult(int Messages, int Observations, int Queries, int FramesAcknowledged, DiagnosticAnalysis Analysis)
{
    public int ResponsesConfirmed { get; init; }
    public bool ProtocolBudgetsMet { get; init; }
    public int HostCommands { get; init; }
    public DiagnosticCase? CapturedCase { get; init; }
    public TimeSpan Elapsed { get; init; }
}
