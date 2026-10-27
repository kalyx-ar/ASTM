using Astm.Plugins;
using static Astm.Diagnostics.Maui.Globals;

namespace Astm.Diagnostics.Maui;

public sealed partial class MainPage
{
    private async Task RunPortGateBenchAsync()
    {
        if (IsCapturing || _case is null || Profile.AstmProfile is null) return;
        var selected = Profile;
        using var stop = new CancellationTokenSource();
        try
        {
            var scenario = CommunicationScenario.FromTrace(_case, selected, 5);
            var serial = selected.AstmProfile.Transport?.Serial?.Default ?? new Astm.Abstractions.Transport.SerialSettings();
            stop.CancelAfter(TimeSpan.FromSeconds(60) + scenario.MinimumTransmissionTime(selected, serial));
            _liveStop = stop; _captureFinished = new(TaskCreationOptions.RunContinuationsAsynchronously); SetBusy(true);
            _status.Text = T("Scenario.Running");
            var report = await Task.Run(() => PortGateBench.RunAsync(Path.Combine(AppContext.BaseDirectory, "portgate.exe"), scenario, selected, stop.Token));
            _case = report.CapturedCase; _analysis = report.Analysis; _sourceBytes = null; _sourceName = null;
            _title.Text = T("Scenario.Title"); _summary.Text = F("View.Summary", _case!.Entries.Count, _analysis.Messages.Count, _analysis.Observations.Count, _analysis.Diagnostics.Count);
            _tab = 7; ShowTable();
            _status.Text = F(report.ProtocolBudgetsMet && report.HostCommands > 0 ? "Scenario.Passed" : "Scenario.Partial", report.HostCommands, report.Observations, DiagnosticDuration.Format(report.Elapsed));
        }
        catch (OperationCanceledException) { _status.Text = T("Scenario.Cancelled"); }
        catch (Exception error) { ShowError(error); }
        finally { _liveStop = null; SetBusy(false); _captureFinished?.TrySetResult(); }
    }
    private async Task VerifyVirtualSerialBenchAsync()
    {
        SelectProfile(StandardAstmPlugin.ProfileId);
        _case = DiagnosticReader.Read("H|\\^&\rP|1\rO|1|SYN-BENCH\rR|1|^^^GLU|5.4|mmol/L||N||F\rL|1|N\r"u8.ToArray(), DiagnosticInputFormat.AstmMessage, Profile, "synthetic", TimeSpan.Zero);
        await RunPortGateBenchAsync();
        if (_case.Header.Source != "communication-scenario" || _analysis?.Observations.Count != 1 || IsCapturing)
            throw new InvalidDataException("ASTM-MAUI-TEST-PORT-SELFTEST");
    }
}
