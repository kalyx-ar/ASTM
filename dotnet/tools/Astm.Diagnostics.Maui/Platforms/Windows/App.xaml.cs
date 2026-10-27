using System.Runtime.InteropServices;
using Microsoft.Maui;
using Microsoft.Maui.Hosting;

namespace Astm.Diagnostics.Maui.WinUI;

public partial class App : MauiWinUIApplication
{
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => { e.Handled = true; Fail(e.Exception); };
    }
    protected override MauiApp CreateMauiApp()
    {
        try { return MauiProgram.CreateMauiApp(); }
        catch (Exception error) { Fail(error); throw; }
    }
    private static void Fail(Exception error)
    {
        // Never dump a stack, file content or credentials to a plaintext fallback log.
        var code = error is FileNotFoundException or FileLoadException ? "ASTM-STARTUP-MISSING-COMPONENT" : "ASTM-STARTUP-FAILED";
        MessageBoxW(0, "No se pudo iniciar ASTM Diagnóstico.\nVolvé a extraer el paquete completo y conservá sus componentes juntos.\n\nCódigo: " + code,
            "ASTM · Error de inicio", 0x10);
        Environment.Exit(1);
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint parent, string text, string caption, uint type);
}
