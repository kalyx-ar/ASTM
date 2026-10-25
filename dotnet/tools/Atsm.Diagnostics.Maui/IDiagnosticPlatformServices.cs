namespace Atsm.Diagnostics.Maui;

public sealed record DiagnosticInput(string Name, Func<Task<Stream>> OpenReadAsync);
public sealed record DiagnosticDestination(string DisplayName, Func<Stream> Create);

/// <summary>The portable MAUI page depends on platform file/custody/window ports, not WPF or EDC.
/// Windows is the first validated host; another platform must provide its own protected storage.</summary>
public interface IDiagnosticPlatformServices
{
    void AttachColumnResize(Microsoft.Maui.Controls.View grip, Action<double> resizeBy, Action autoFit);
    IDiagnosticCaseProtection CaseProtection { get; }
    void EnsureWindowReady();
    Task<DiagnosticInput?> PickInputAsync(bool certificate = false);
    Task<DiagnosticDestination?> PickCaseDestinationAsync();
    void AttachWindow(Microsoft.Maui.Controls.Window window, MainPage page);
}
