using System.Globalization;
using Astm.Plugins;
using Astm.Protocol.Lis01;
using Astm.Protocol.Lis02;
using Astm.Testing;

namespace Astm.Plugin.Testing.Synthetic;

/// <summary>
/// Synthetic analyzer on the instrument side of a stream (a duplex pair, the PortGate test serial port or a real COM pair).
/// It decodes the host orders and query answers, "runs" the synthetic menu with its <see cref="ISimulationPolicy"/> and
/// reports the results as LIS02 messages over LIS01. Every result it transmits is kept in <see cref="Sent"/> so a test
/// compares what the host ingested with what the instrument said. No patient data: specimens and values are synthetic.
/// </summary>
public sealed class SimulatedAnalyzer : IAsyncDisposable
{
    public const string Model = "SYNTH";
    public const string SerialNumber = "SN-0001";

    private static readonly Lis02Delimiters Delimiters = Lis02Delimiters.Default;
    private readonly InstrumentSimulator _link;
    private readonly AstmProfile _profile;
    private readonly IReadOnlyDictionary<string, SyntheticTest> _menu;
    private readonly List<SimulatedResult> _sent = [];
    private int _sequence;

    public SimulatedAnalyzer(Stream stream, ISimulationPolicy policy, AstmProfile? profile = null, IReadOnlyList<SyntheticTest>? menu = null)
    {
        Policy = policy;
        _profile = profile ?? new SyntheticAstmPlugin().Profiles[0];
        _menu = (menu ?? SyntheticAstmPlugin.Menu).ToDictionary(t => t.Code, StringComparer.Ordinal);
        _link = new InstrumentSimulator(stream, policy.Link(_profile.Link), _profile.Encoding) { FrameFault = policy.CorruptFrame };
    }

    public ISimulationPolicy Policy { get; }

    /// <summary>Every result transmitted and delivered, in order.</summary>
    public IReadOnlyList<SimulatedResult> Sent
    {
        get { lock (_sent) return _sent.ToArray(); }
    }

    public int CorruptedFrames => _link.CorruptedFrames;

    /// <summary>Waits for the next host transmission and returns the orders it carries (none for a "no information" answer).</summary>
    public async Task<IReadOnlyList<SimulatedOrder>> ReceiveOrdersAsync(CancellationToken cancellationToken)
    {
        var text = await _link.Received.ReadAsync(cancellationToken).ConfigureAwait(false);
        return ReadOrders(text);
    }

    /// <summary>Asks the host for the work of <paramref name="specimens"/> (LIS02 Q record) and returns its answer.</summary>
    public async Task<IReadOnlyList<SimulatedOrder>> QueryAsync(IReadOnlyList<string> specimens, CancellationToken cancellationToken)
    {
        var records = new List<Lis02Record>
        {
            Header(),
            new Lis02RecordBuilder('Q', Delimiters).SetText(2, "1").SetRepeats(3, specimens.Select(s => new[] { null, s }))
                .SetComponents(5, null, null, null, "ALL").SetText(13, "O").Build(),
            Terminator(),
        };
        await SendAsync(records, cancellationToken).ConfigureAwait(false);
        return await ReceiveOrdersAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs <paramref name="work"/> and reports it, batching specimens per message as the policy decides.</summary>
    public async Task<IReadOnlyList<SimulatedResult>> ReportAsync(IReadOnlyList<SimulatedOrder> work, CancellationToken cancellationToken)
    {
        var reported = new List<SimulatedResult>();
        for (var next = 0; next < work.Count;)
        {
            var batch = work.Skip(next).Take(Policy.Batch(work.Count - next)).ToArray();
            next += batch.Length;
            var delay = Policy.Delay();
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            var records = new List<Lis02Record> { Header() };
            var results = new List<SimulatedResult>();
            var patient = 1;
            foreach (var order in batch)
            {
                records.Add(new Lis02RecordBuilder('P', Delimiters).SetText(2, (patient++).ToString(CultureInfo.InvariantCulture)).Build());
                records.Add(new Lis02RecordBuilder('O', Delimiters).SetText(2, "1").SetText(3, order.SpecimenId)
                    .SetRepeats(5, order.Tests.Select(t => new[] { null, null, null, t }))
                    .SetText(6, "R").SetText(12, order.Control ? "Q" : "N").SetText(26, "F").Build());
                var index = 1;
                foreach (var code in order.Tests)
                {
                    var result = Policy.Result(order, _menu.GetValueOrDefault(code), code, Interlocked.Increment(ref _sequence));
                    results.Add(result);
                    records.Add(new Lis02RecordBuilder('R', Delimiters).SetText(2, (index++).ToString(CultureInfo.InvariantCulture))
                        .SetComponents(3, null, null, null, code)
                        .SetText(4, result.Value).SetText(5, result.Unit).SetText(6, result.ReferenceRange)
                        .SetRepeats(7, result.Flags.Select(f => new[] { f }))
                        .SetText(9, result.Status)
                        .SetText(13, result.ResultTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture))
                        .SetComponents(14, Model, SerialNumber).Build());
                    var comment = 1;
                    foreach (var text in result.Comments)
                        records.Add(new Lis02RecordBuilder('C', Delimiters).SetText(2, (comment++).ToString(CultureInfo.InvariantCulture))
                            .SetText(3, "I").SetText(4, text).SetText(5, "G").Build());
                }
            }
            records.Add(Terminator());
            await SendAsync(records, cancellationToken).ConfigureAwait(false);
            lock (_sent)
                _sent.AddRange(results);
            reported.AddRange(results);
        }
        return reported;
    }

    public ValueTask DisposeAsync() => _link.DisposeAsync();

    private async Task SendAsync(List<Lis02Record> records, CancellationToken cancellationToken)
    {
        var receipt = await _link.SendMessagesAsync(_profile.Frame(records, Policy.Framing(_profile.InstrumentFraming))).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (receipt.Result != Lis01SendResult.Delivered)
            throw new IOException($"SYNTH-NOT-DELIVERED: {receipt.Result} ({Policy.Description})");
    }

    private static Lis02Record Header() =>
        Lis02RecordBuilder.Header(Delimiters).SetComponents(5, Model, "1.0", SerialNumber).SetText(12, "P").Build();

    private static Lis02Record Terminator() => new Lis02RecordBuilder('L', Delimiters).SetText(2, "1").SetText(3, "N").Build();

    private static List<SimulatedOrder> ReadOrders(string text)
    {
        var orders = new List<SimulatedOrder>();
        foreach (var message in Lis02Codec.Parse(text).Messages)
        foreach (var order in message.OfType('O'))
        {
            var tests = order.Repeats(5)
                .Select(r => message.Delimiters.Decode(r.Split(message.Delimiters.Component).ElementAtOrDefault(3) ?? ""))
                .Where(t => t.Length > 0).ToArray();
            if (order.Field(12) == "C")
                continue;  // cancellation: nothing to run
            orders.Add(new(order.Text(3, 1).Trim(), tests, order.Field(12) == "Q"));
        }
        return orders;
    }
}
