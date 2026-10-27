using Astm.Plugins;
using Astm.Protocol.Lis01;

namespace Astm.Plugin.Testing.Synthetic;

/// <summary>Work the analyzer holds for a specimen: from a downloaded order, a query answer or loaded unsolicited.</summary>
public sealed record SimulatedOrder(string SpecimenId, IReadOnlyList<string> Tests, bool Control = false);

/// <summary>What the analyzer transmitted for one test: the ledger the host side must reproduce exactly.</summary>
public sealed record SimulatedResult(string SpecimenId, string TestCode, string Value, string? Unit, string? ReferenceRange,
    IReadOnlyList<string> Flags, string Status, IReadOnlyList<string> Comments, DateTime ResultTime);

/// <summary>Decides values, framing, batching, delays and link faults of a <see cref="SimulatedAnalyzer"/>.</summary>
public interface ISimulationPolicy
{
    /// <summary>Text that reproduces the run (mode and seed).</summary>
    string Description { get; }

    Lis01Options Link(Lis01Options profile);

    AstmFraming Framing(AstmFraming profile);

    /// <summary>Specimens reported in the next results message (1..<paramref name="pending"/>).</summary>
    int Batch(int pending);

    TimeSpan Delay();

    /// <summary>Asked for every frame write; see <see cref="Astm.Testing.InstrumentSimulator.FrameFault"/>.</summary>
    bool CorruptFrame();

    SimulatedResult Result(SimulatedOrder order, SyntheticTest? test, string code, int sequence);
}

/// <summary>
/// Rigid analyzer: the same input always gives the same bytes. Every value is the midpoint of its reference interval,
/// status final, one specimen per message, the profile framing and no faults. Golden values for regression tests.
/// </summary>
public sealed class RigidSimulation : ISimulationPolicy
{
    public static readonly DateTime Clock = new(2026, 1, 1, 8, 0, 0, DateTimeKind.Unspecified);

    public string Description => "rigid";

    public Lis01Options Link(Lis01Options profile) => profile;

    public AstmFraming Framing(AstmFraming profile) => profile;

    public int Batch(int pending) => 1;

    public TimeSpan Delay() => TimeSpan.Zero;

    public bool CorruptFrame() => false;

    public SimulatedResult Result(SimulatedOrder order, SyntheticTest? test, string code, int sequence) => test is null
        ? NotOnMenu(order, code, sequence)
        : new(order.SpecimenId, code, test.Format((test.Low + test.High) / 2), test.Unit, test.ReferenceRange, [], "F", [], Clock.AddMinutes(sequence));

    internal static SimulatedResult NotOnMenu(SimulatedOrder order, string code, int sequence) =>
        new(order.SpecimenId, code, "", null, null, [], "X", ["test not on menu"], Clock.AddMinutes(sequence));
}

/// <summary>
/// Randomized analyzer, reproducible by its seed: values inside and outside the interval with H/L flags, values below the
/// detection limit, tests not performed, preliminary results, comments that need LIS02 escapes, several specimens per
/// message, frame per record or per message with short frames (ETB), processing delays and frames sent once with a wrong
/// checksum (NAK and retransmission). Faults use their own generator so link timing never changes the values.
/// </summary>
public sealed class RandomizedSimulation(int seed) : ISimulationPolicy
{
    private static readonly int[] FrameSizes = [32, 64, 128, Lis01Frame.MaxTextLength];
    private readonly Random _values = new(seed);
    private readonly Random _faults = new(unchecked(seed * 7919 + 1));
    private readonly object _faultLock = new();
    private bool _lastCorrupted;

    public int Seed { get; } = seed;

    public string Description => $"randomized seed {Seed}";

    /// <summary>Share of frames written once with a wrong checksum.</summary>
    public double FaultRate { get; init; } = 0.15;

    public Lis01Options Link(Lis01Options profile) => profile with { MaxTextLength = FrameSizes[_values.Next(FrameSizes.Length)] };

    public AstmFraming Framing(AstmFraming profile) => _values.Next(2) == 0 ? AstmFraming.FramePerRecord : AstmFraming.FramePerMessage;

    public int Batch(int pending) => _values.Next(1, Math.Min(pending, 3) + 1);

    public TimeSpan Delay() => TimeSpan.FromMilliseconds(_values.Next(0, 25));

    public bool CorruptFrame()
    {
        lock (_faultLock)
        {
            // Never two in a row: the retransmission of a corrupted frame is always correct.
            _lastCorrupted = !_lastCorrupted && _faults.NextDouble() < FaultRate;
            return _lastCorrupted;
        }
    }

    public SimulatedResult Result(SimulatedOrder order, SyntheticTest? test, string code, int sequence)
    {
        var time = RigidSimulation.Clock.AddMinutes(sequence).AddSeconds(_values.Next(60));
        if (test is null)
            return RigidSimulation.NotOnMenu(order, code, sequence) with { ResultTime = time };
        var roll = _values.NextDouble();
        var span = test.High - test.Low;
        string value; string[] flags = []; var status = "F"; var comments = new List<string>();
        if (roll < 0.55)
            value = test.Format(test.Low + span * (decimal)_values.NextDouble());
        else if (roll < 0.70)
        {
            value = test.Format(test.High + span * (0.05m + (decimal)_values.NextDouble()));
            flags = ["H"];
        }
        else if (roll < 0.82)
        {
            if (test.Low == 0)
                value = "<" + test.Format(0.3m);
            else
                value = test.Format(test.Low * (0.5m + 0.45m * (decimal)_values.NextDouble()));
            flags = ["L"];
        }
        else if (roll < 0.88)
        {
            value = "";
            status = "X";
            comments.Add("insufficient sample");
        }
        else if (roll < 0.93)
        {
            value = test.Format(test.Low + span * (decimal)_values.NextDouble());
            status = "P";
        }
        else
        {
            value = test.Format(test.Low + span * (decimal)_values.NextDouble());
            // Delimiters and the escape character must survive LIS02 escaping both ways.
            comments.Add($"check {sequence}: A|B^C&D\\E~F");
        }
        if (_values.Next(10) == 0)
            comments.Add("rerun " + _values.Next(1, 4));
        return new(order.SpecimenId, code, value, test.Unit, test.ReferenceRange, flags, status, comments, time);
    }
}
