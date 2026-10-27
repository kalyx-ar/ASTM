using System.Globalization;
using Astm.Plugin.Testing.Synthetic;
using static Astm.Diagnostics.Maui.Globals;

namespace Astm.Diagnostics.Maui;

public sealed partial class MainPage
{
    private readonly Picker _simulationMode = Choice(T("Simulation.Mode"));
    private readonly Entry _simulationSeed = Input(""), _simulationSpecimens = Input("6");
    private Button? _simulate;

    /// <summary>Synthetic analyzer, rigid or randomized, behind PortGate's test serial port: no driver, no patient data.</summary>
    private void InitializeSimulationControls()
    {
        foreach (var key in new[] { "Simulation.Rigid", "Simulation.Randomized" }) _simulationMode.Items.Add(T(key));
        _simulationMode.SelectedIndex = 0; _simulationSeed.Placeholder = T("Simulation.SeedHint");
        _simulate = Action(T("Simulation.Run"), RunSimulationAsync);
        var panel = new VerticalStackLayout { Spacing = 8, Children = { Text(T("Simulation.Title"), 12), Field(T("Simulation.Mode"), _simulationMode),
            Field(T("Simulation.Seed"), _simulationSeed), Field(T("Simulation.Specimens"), _simulationSpecimens), _simulate, Text(T("Simulation.Note"), 12) } };
        _source.Add(panel);
    }

    private async Task RunSimulationAsync()
    {
        if (IsCapturing) return;
        var randomized = _simulationMode.SelectedIndex == 1;
        if (!int.TryParse(_simulationSpecimens.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var specimens) || specimens is < 1 or > 200)
        { _status.Text = T("Validation.SimulationSpecimens"); return; }
        var seed = 1;
        if (randomized && !string.IsNullOrWhiteSpace(_simulationSeed.Text) && !int.TryParse(_simulationSeed.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out seed))
        { _status.Text = T("Validation.SimulationSeed"); return; }
        if (randomized && string.IsNullOrWhiteSpace(_simulationSeed.Text))
        {
            // A new seed for each run, shown in its field so the run can be repeated.
            seed = Random.Shared.Next(1, int.MaxValue); _simulationSeed.Text = seed.ToString(CultureInfo.InvariantCulture);
        }
        await RunSimulationAsync(new SimulationOptions(randomized ? SimulationMode.Randomized : SimulationMode.Rigid, seed, specimens));
    }

    private async Task<SimulationReport?> RunSimulationAsync(SimulationOptions options)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60) + TimeSpan.FromSeconds(options.Specimens));
        try
        {
            _liveStop = stop; _captureFinished = new(TaskCreationOptions.RunContinuationsAsynchronously); SetBusy(true);
            _status.Text = F("Simulation.Running", options.CreatePolicy().Description);
            var report = await Task.Run(() => PortGateBench.RunAsync(Path.Combine(AppContext.BaseDirectory, "portgate.exe"), options, stop.Token));
            SelectProfile(SyntheticAstmPlugin.ProfileId);
            _case = report.CapturedCase; _analysis = report.Analysis; _sourceBytes = null; _sourceName = null;
            _title.Text = F("Simulation.CaseTitle", report.Policy);
            _summary.Text = F("View.Summary", _case!.Entries.Count, _analysis!.Messages.Count, _analysis.Observations.Count, _analysis.Diagnostics.Count);
            _tab = 7; ShowTable();
            _status.Text = F(report.Passed ? "Simulation.Passed" : "Simulation.Failed", report.Policy, report.Results, report.Ingested, report.Mismatches,
                report.CorruptedFrames, DiagnosticDuration.Format(report.Elapsed));
            return report;
        }
        catch (OperationCanceledException) { _status.Text = T("Scenario.Cancelled"); return null; }
        catch (Exception error) { ShowError(error); return null; }
        finally { _liveStop = null; SetBusy(false); _captureFinished?.TrySetResult(); }
    }

    private async Task VerifySimulationAsync()
    {
        foreach (var options in new[] { new SimulationOptions(SimulationMode.Rigid, 1, 3), new SimulationOptions(SimulationMode.Randomized, 7, 3) })
            if (await RunSimulationAsync(options) is not { Passed: true } || _case?.Header.Source != "synthetic-analyzer" || IsCapturing)
                throw new InvalidDataException("ASTM-MAUI-SIMULATION-SELFTEST");
    }
}
