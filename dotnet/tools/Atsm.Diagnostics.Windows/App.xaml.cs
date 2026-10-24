using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Atsm.Diagnostics.Windows;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window = new MainWindow();
        if (e.Args.Length == 2 && e.Args[0] == "--smoke-test")
        {
            try
            {
                window.LoadDemo(); window.SmokeCheckProtection(Path.GetDirectoryName(Path.GetFullPath(e.Args[1]))!);
                var surface = (FrameworkElement)window.Content;
                surface.Measure(new Size(1420, 850)); surface.Arrange(new Rect(0, 0, 1420, 850)); surface.UpdateLayout();
                var bitmap = new RenderTargetBitmap(1420, 850, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(e.Args[1]); encoder.Save(output);
                Shutdown(0);
            }
            catch (Exception error) { Console.Error.WriteLine(error); Shutdown(1); }
        }
        else { if (e.Args.Contains("--demo")) window.LoadDemo(); MainWindow = window; window.Show(); }
    }
}
