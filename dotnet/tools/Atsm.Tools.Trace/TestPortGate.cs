using Atsm.Diagnostics;
using Atsm.Plugins;
using Atsm.Abstractions.Transport;

namespace Atsm.Tools.Trace;

internal static class TestPortGate
{
    internal static async Task<int> RunAsync(string file, string[] arguments)
    {
        string? Option(string key) { var i = Array.IndexOf(arguments, key); return i >= 0 && i + 1 < arguments.Length ? arguments[i + 1] : null; }
        var catalog = new DiagnosticCatalog(); var profile = catalog.Resolve(Option("--profile") ?? StandardAstmPlugin.ProfileId);
        var source = DiagnosticReader.Read(File.ReadAllBytes(file), DiagnosticInputFormat.Auto, profile, "private-local-scenario", TimeSpan.Zero);
        profile = catalog.Resolve(source.Header.Profile!);
        var scenario = CommunicationScenario.FromTrace(source, profile, int.Parse(Option("--max") ?? "5"));
        var executable = Option("--portgate") ?? Path.Combine(AppContext.BaseDirectory, "portgate.exe");
        var serial = profile.AstmProfile?.Transport?.Serial?.Default ?? new SerialSettings();
        if (Option("--baud") is { } baud) serial = serial with { BaudRate = int.Parse(baud) };
        if (serial.BaudRate < 50) throw new ArgumentOutOfRangeException("--baud");
        Console.WriteLine($"ATSM_TEST_PLAN messages={scenario.Messages.Count} payloadBytes={scenario.Messages.Sum(m => profile.AstmProfile!.Encoding.GetByteCount(m))} baud={serial.BaudRate} minimumWireTime={DiagnosticDuration.Format(scenario.MinimumTransmissionTime(profile, serial))}");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(int.Parse(Option("--timeout") ?? "60")));
        CommunicationScenarioResult report;
        try { report = await PortGateBench.RunAsync(executable, scenario, profile, timeout.Token, serial); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested) { Console.Error.WriteLine("ATSM_TEST_PORT_TIMEOUT"); return 124; }
        Console.WriteLine($"ATSM_TEST_PORT messages={report.Messages} hostCommands={report.HostCommands} observations={report.Observations} queries={report.Queries} queryResponses={report.ResponsesConfirmed} framesAcknowledged={report.FramesAcknowledged} protocolBudgetsMet={report.ProtocolBudgetsMet} elapsed={DiagnosticDuration.Format(report.Elapsed)}");
        foreach (var timing in CommunicationStatistics.Build(report.Analysis))
            Console.WriteLine($"ATSM_TEST_TIMING code={timing.Code} count={timing.Distribution.Count} min={DiagnosticDuration.Format(timing.Distribution.Minimum)} median={DiagnosticDuration.Format(timing.Distribution.Median)} p95={DiagnosticDuration.Format(timing.Distribution.P95)} max={DiagnosticDuration.Format(timing.Distribution.Maximum)}");
        return report.ProtocolBudgetsMet && report.HostCommands > 0 ? 0 : 2;
    }
}
