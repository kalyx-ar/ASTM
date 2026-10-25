using Microsoft.Maui.Controls;
using Microsoft.Extensions.DependencyInjection;
using static Atsm.Diagnostics.Maui.Globals;

namespace Atsm.Diagnostics.Maui;

public sealed class App(IServiceProvider services, IDiagnosticPlatformServices platform) : Application
{
    protected override Window CreateWindow(IActivationState? activationState)
    {
        UserAppTheme = AppTheme.Light;
        MainPage? page = null;
        var loading = new ContentPage { Content = new Label { Text = T("App.Loading"), Margin = 24 } };
        var window = new Window(loading) { Title = T("App.Title"), Width = 1440, Height = 920, MinimumWidth = 780, MinimumHeight = 650 };
        window.HandlerChanged += (_, _) => { if (page is not null) platform.AttachWindow(window, page); };
        window.Created += async (_, _) =>
        {
            try
            {
                await Globals.InitializeAsync();
                page = services.GetRequiredService<MainPage>(); window.Title = T("App.Title"); window.Page = page;
                platform.AttachWindow(window, page);
            }
            catch (Exception error)
            {
                var args = Environment.GetCommandLineArgs(); var selfTest = Array.IndexOf(args, "--self-test");
                if (selfTest >= 0 && selfTest + 1 < args.Length) { File.WriteAllText(args[selfTest + 1], "FAIL: initialization " + error.GetType().Name); Environment.Exit(1); }
                await loading.DisplayAlertAsync(T("App.ErrorTitle"), T("App.StartError"), T("Close"));
            }
        };
        return window;
    }
}
