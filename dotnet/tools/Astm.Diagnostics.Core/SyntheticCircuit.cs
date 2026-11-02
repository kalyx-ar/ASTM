using System.Diagnostics;
using Astm.Formats.Medical.Model;
using Astm.Abstractions.Tracing;
using Astm.Plugin.Testing.Synthetic;
using Astm.Plugins;
using Astm.Protocol.Lis01;
using Astm.Runtime.Sessions;
using Astm.Runtime.Tracing;
using Astm.Testing;

namespace Astm.Diagnostics;

public enum SimulationMode { Rigid, Randomized }

/// <summary>Run of the synthetic analyzer: rigid, or randomized with <paramref name="Seed"/>, over <paramref name="Specimens"/> specimens.</summary>
public sealed record SimulationOptions(SimulationMode Mode = SimulationMode.Rigid, int Seed = 1, int Specimens = 6)
{
    public ISimulationPolicy CreatePolicy() => Mode == SimulationMode.Rigid ? new RigidSimulation() : new RandomizedSimulation(Seed);
}

/// <summary>Outcome of a synthetic run. <see cref="Passed"/>: every result in the analyzer ledger was ingested once, unchanged.</summary>
public sealed record SimulationReport(string Policy, int Specimens, int Downloaded, int Queried, int Unsolicited, int Results, int Ingested,
    int Mismatches, int CorruptedFrames, int HostQueries, TimeSpan Elapsed)
{
    public bool Passed => Results > 0 && Mismatches == 0 && Ingested == Results;
    public DiagnosticCase? CapturedCase { get; init; }
    public DiagnosticAnalysis? Analysis { get; init; }
}

/// <summary>
/// Whole circuit with synthetic data: in-memory host → ASTM session with the synthetic test plugin → the given streams
/// (duplex pair, PortGate test serial port, a COM pair) → <see cref="SimulatedAnalyzer"/> and back. Shared by the tests,
/// <c>astm-trace simulate</c> and Diagnostics. Only synthetic specimens and values; the trace stays in memory.
/// </summary>
public sealed class SyntheticCircuit : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _run;
    private readonly Stream _hostSide;
    private readonly AstmInstrumentSession _session;
    private readonly DiagnosticTraceSink _trace;
    private readonly TraceHeader _header;

    public SyntheticCircuit(Stream hostSide, Stream instrumentSide, ISimulationPolicy policy)
    {
        var plugin = new SyntheticAstmPlugin();
        Profile = plugin.Profiles[0];
        var instrument = new InstrumentRef("synthetic-01");
        _hostSide = hostSide;
        _header = new TraceHeader(Guid.CreateVersion7(), "synthetic-analyzer", "simulation", Profile.Id, DateTimeOffset.UtcNow);
        _trace = new DiagnosticTraceSink(_header);
        _session = new AstmInstrumentSession(hostSide, plugin.CreateDialect(Profile, new DialectContext(instrument, TimeProvider.System)), Profile, Host,
            instrument, _trace, new AstmSessionOptions());
        Analyzer = new SimulatedAnalyzer(instrumentSide, policy, Profile);
        _run = _session.RunAsync(_stop.Token);
    }

    public AstmProfile Profile { get; }

    public InMemoryLabHost Host { get; } = new();

    public SimulatedAnalyzer Analyzer { get; }

    public int Downloaded { get; private set; }

    public int Queried { get; private set; }

    public int Unsolicited { get; private set; }

    /// <summary>Runs one synthetic session end to end and compares the ledger with what the host ingested.</summary>
    public static async Task<SimulationReport> RunAsync(Stream hostSide, Stream instrumentSide, SimulationOptions options, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Specimens, 1);
        var started = Stopwatch.GetTimestamp();
        await using var circuit = new SyntheticCircuit(hostSide, instrumentSide, options.CreatePolicy());
        await circuit.RunWorkloadAsync(options.Seed, options.Specimens, token);
        return await circuit.CompareAsync(started, token);
    }

    /// <summary>Downloads orders, lets the analyzer decode them and report.</summary>
    public async Task DownloadAsync(IReadOnlyList<WorkOrder> orders, CancellationToken token)
    {
        var sending = _session.SendOrdersAsync(orders, token);
        var received = await Analyzer.ReceiveOrdersAsync(token);
        if ((await sending).Result != Lis01SendResult.Delivered) throw new IOException("ASTM-SIMULATION-ORDER-NOT-DELIVERED");
        if (!orders.Select(o => (o.Specimen.SpecimenId, string.Join(',', o.Tests.Select(t => t.Vendor.Code))))
                .SequenceEqual(received.Select(o => (o.SpecimenId, string.Join(',', o.Tests)))))
            throw new InvalidDataException("ASTM-SIMULATION-ORDER-MISMATCH");
        Downloaded += orders.Count;
        await Analyzer.ReportAsync(received, token);
    }

    /// <summary>Registers the work in the host and lets the analyzer ask for it before reporting.</summary>
    public async Task QueryAsync(WorkOrder order, CancellationToken token)
    {
        Host.OrdersBySpecimen[order.Specimen.SpecimenId] = [order];
        var answered = await Analyzer.QueryAsync([order.Specimen.SpecimenId], token);
        if (answered.Count != 1 || !answered[0].Tests.SequenceEqual(order.Tests.Select(t => t.Vendor.Code)))
            throw new InvalidDataException("ASTM-SIMULATION-QUERY-MISMATCH");
        Queried++;
        await Analyzer.ReportAsync(answered, token);
    }

    public async Task UnsolicitedAsync(SimulatedOrder work, CancellationToken token)
    {
        Unsolicited++;
        await Analyzer.ReportAsync([work], token);
    }

    /// <summary>Synthetic specimens with tests drawn from the menu (plus an unknown code now and then), split among order
    /// download, host query and unsolicited results (all three from three specimens on); all decided by <paramref name="seed"/>.</summary>
    public async Task RunWorkloadAsync(int seed, int specimens, CancellationToken token)
    {
        var random = new Random(seed);
        var menu = SyntheticAstmPlugin.Menu.Select(t => t.Code).ToArray();
        for (var i = 1; i <= specimens; i++)
        {
            var tests = menu.OrderBy(_ => random.Next()).Take(random.Next(1, 5)).ToList();
            if (random.Next(8) == 0)
                tests.Add("ZZZ");
            var specimen = $"SYN-{seed}-{i:D3}";
            // The first three specimens cover the three workflows (order rotated by the seed); the rest are drawn.
            var flow = random.Next(3);
            if (i <= 3)
                flow = (i - 1 + (seed % 3 + 3) % 3) % 3;
            switch (flow)
            {
                case 0: await DownloadAsync([Order(specimen, [.. tests])], token); break;
                case 1: await QueryAsync(Order(specimen, [.. tests]), token); break;
                default: await UnsolicitedAsync(new(specimen, tests), token); break;
            }
        }
    }

    /// <summary>Waits until the host ingested as many results as the analyzer delivered and compares them field by field.</summary>
    public async Task<SimulationReport> CompareAsync(long started, CancellationToken token)
    {
        var expected = Analyzer.Sent.Select(Key).Order(StringComparer.Ordinal).ToArray();
        while (Host.Batches.Sum(b => b.Observations.Count) < expected.Length)
            await Task.Delay(5, token);
        var actual = Host.Batches.SelectMany(b => b.Observations).Select(Key).Order(StringComparer.Ordinal).ToArray();
        var remaining = actual.GroupBy(k => k, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var missing = 0;
        foreach (var key in expected)
            if (remaining.TryGetValue(key, out var count) && count > 0) remaining[key] = count - 1;
            else missing++;
        var captured = new DiagnosticCase(_header, _trace.Snapshot());
        var profile = new DiagnosticCatalog().Resolve(Profile.Id);
        return new(Analyzer.Policy.Description, Downloaded + Queried + Unsolicited, Downloaded, Queried, Unsolicited, expected.Length, actual.Length,
            missing + remaining.Values.Sum(), Analyzer.CorruptedFrames, Host.Queries.Count, Stopwatch.GetElapsedTime(started))
        {
            CapturedCase = captured,
            Analysis = DiagnosticReader.Analyze(captured, profile, new(TimeSpan.Zero, TimeSpan.Zero, TimeZoneInfo.Utc)),
        };
    }

    public static WorkOrder Order(string specimen, params string[] tests) =>
        new(Guid.CreateVersion7().ToString(), new(specimen), null,
            tests.Select(t => CodeSet.Of(new CodedValue(t, null, CodeSystems.Vendor(SyntheticAstmPlugin.PluginId)))).ToArray());

    public static string Key(SimulatedResult r) => string.Join('\u001F', r.SpecimenId, r.TestCode, r.Value, r.Unit, r.ReferenceRange,
        string.Join(',', r.Flags), r.Status, string.Join('\u001E', r.Comments), r.ResultTime.ToString("O"));

    public static string Key(InstrumentObservation o) => string.Join('\u001F', o.Specimen.SpecimenId, o.Test.Vendor.Code, o.Value.RawText,
        o.Value.RawUnit, o.ReferenceRangeText, string.Join(',', o.Flags.Select(f => f.Code)), o.InstrumentStatus, string.Join('\u001E', o.Comments),
        o.ResultTime?.DateTime.ToString("O"));

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { await _run; }
        catch (OperationCanceledException) { }
        await Analyzer.DisposeAsync();
        await _hostSide.DisposeAsync();
        _stop.Dispose();
    }
}
