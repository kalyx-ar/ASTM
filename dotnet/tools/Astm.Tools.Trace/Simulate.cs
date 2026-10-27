using System.Globalization;
using Astm.Abstractions.Transport;
using Astm.Diagnostics;
using Astm.Testing;

namespace Astm.Tools.Trace;

/// <summary><c>astm-trace simulate</c>: the synthetic analyzer behind PortGate's test serial port (or a duplex pair with
/// <c>--direct</c>). Only synthetic specimens and values; nothing is written to disk.</summary>
internal static class Simulate
{
    internal static async Task<int> RunAsync(string[] arguments)
    {
        string? Option(string key) { var i = Array.IndexOf(arguments, key); return i >= 0 && i + 1 < arguments.Length ? arguments[i + 1] : null; }
        var mode = (Option("--mode") ?? "rigid") switch
        {
            "rigid" => SimulationMode.Rigid,
            "random" or "randomized" => SimulationMode.Randomized,
            var other => throw new ArgumentException("--mode rigid|random: " + other),
        };
        // A randomized run without --seed draws one and prints it, so any failure can be reproduced.
        var seed = Option("--seed") is { } given ? int.Parse(given, CultureInfo.InvariantCulture) : mode == SimulationMode.Rigid ? 1 : Random.Shared.Next(1, int.MaxValue);
        var specimens = int.Parse(Option("--specimens") ?? "6", CultureInfo.InvariantCulture);
        if (specimens is < 1 or > 1000) throw new ArgumentOutOfRangeException("--specimens");
        var options = new SimulationOptions(mode, seed, specimens);
        var direct = arguments.Contains("--direct");
        var serial = new Astm.Plugin.Testing.Synthetic.SyntheticAstmPlugin().Profiles[0].Transport!.Serial!.Default;
        if (Option("--baud") is { } baud) serial = serial with { BaudRate = int.Parse(baud, CultureInfo.InvariantCulture) };
        if (serial.BaudRate < 50) throw new ArgumentOutOfRangeException("--baud");
        var executable = Option("--portgate") ?? Path.Combine(AppContext.BaseDirectory, "portgate.exe");
        Console.WriteLine($"ASTM_SIMULATION_PLAN policy=\"{options.CreatePolicy().Description}\" specimens={specimens} transport={(direct ? "direct" : "portgate")}" + (direct ? "" : $" baud={serial.BaudRate}"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(int.Parse(Option("--timeout") ?? "60", CultureInfo.InvariantCulture)));
        SimulationReport report;
        try
        {
            if (direct)
            {
                var (host, instrument) = DuplexStream.CreatePair();
                report = await SyntheticCircuit.RunAsync(host, instrument, options, timeout.Token);
            }
            else
                report = await PortGateBench.RunAsync(executable, options, timeout.Token, serial);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { Console.Error.WriteLine("ASTM_SIMULATION_TIMEOUT"); return 124; }
        Console.WriteLine($"ASTM_SIMULATION policy=\"{report.Policy}\" specimens={report.Specimens} downloaded={report.Downloaded} queried={report.Queried} unsolicited={report.Unsolicited} results={report.Results} ingested={report.Ingested} mismatches={report.Mismatches} corruptedFrames={report.CorruptedFrames} hostQueries={report.HostQueries} passed={report.Passed} elapsed={DiagnosticDuration.Format(report.Elapsed)}");
        if (report.Analysis is { } analysis)
            foreach (var timing in CommunicationStatistics.Build(analysis))
                Console.WriteLine($"ASTM_SIMULATION_TIMING code={timing.Code} count={timing.Distribution.Count} min={DiagnosticDuration.Format(timing.Distribution.Minimum)} median={DiagnosticDuration.Format(timing.Distribution.Median)} p95={DiagnosticDuration.Format(timing.Distribution.P95)} max={DiagnosticDuration.Format(timing.Distribution.Maximum)}");
        return report.Passed ? 0 : 2;
    }
}
