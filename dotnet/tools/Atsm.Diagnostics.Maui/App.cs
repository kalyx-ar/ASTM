using Microsoft.Maui.Controls;

namespace Atsm.Diagnostics.Maui;

public sealed class App(MainPage page, IDiagnosticPlatformServices platform) : Application
{
    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(page) { Title = "ATSM · Diagnóstico de mantenimiento", Width = 1440, Height = 920, MinimumWidth = 780, MinimumHeight = 650 };
        window.HandlerChanged += (_, _) => platform.AttachWindow(window, page);
        return window;
    }
}
