using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Hosting;

namespace Atsm.Diagnostics.Maui;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder().UseMauiApp<App>();
        builder.Services.AddSingleton<IDiagnosticPlatformServices, WindowsDiagnosticServices>();
        builder.Services.AddSingleton<MainPage>();
        return builder.Build();
    }
}
