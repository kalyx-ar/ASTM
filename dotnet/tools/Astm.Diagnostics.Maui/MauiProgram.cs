using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Hosting;

namespace Astm.Diagnostics.Maui;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        Microsoft.Maui.Handlers.LabelHandler.Mapper.AppendToMapping("SelectableDiagnosticText", (handler, _) =>
        {
            handler.PlatformView.IsTextSelectionEnabled = true;
        });
        Microsoft.Maui.Handlers.CheckBoxHandler.Mapper.AppendToMapping("CompactDiagnosticOptions", (handler, _) =>
        {
            handler.PlatformView.MinWidth = 0; handler.PlatformView.MinHeight = 0;
            handler.PlatformView.Padding = new Microsoft.UI.Xaml.Thickness(0);
        });
        var builder = MauiApp.CreateBuilder().UseMauiApp<App>();
        builder.Services.AddSingleton<IDiagnosticPlatformServices, WindowsDiagnosticServices>();
        builder.Services.AddSingleton<MainPage>();
        return builder.Build();
    }
}
