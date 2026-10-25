using System.Security.Cryptography;
using Windows.Storage.Pickers;

namespace Atsm.Diagnostics.Maui;

public sealed class WindowsDiagnosticServices : IDiagnosticPlatformServices
{
    public void AttachColumnResize(Microsoft.Maui.Controls.View grip, Action<double> resizeBy, Action autoFit)
    {
        Microsoft.UI.Xaml.UIElement? attached = null; bool resizing = false; double previousX = 0;
        void Pressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args)
        {
            if (!args.GetCurrentPoint(attached).Properties.IsLeftButtonPressed) return;
            resizing = true; previousX = args.GetCurrentPoint(null).Position.X; attached!.CapturePointer(args.Pointer); args.Handled = true;
        }
        void Moved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args)
        {
            if (!resizing) return; var x = args.GetCurrentPoint(null).Position.X; resizeBy(x - previousX); previousX = x; args.Handled = true;
        }
        void Released(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs args) { resizing = false; attached?.ReleasePointerCapture(args.Pointer); }
        void DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs args) { autoFit(); args.Handled = true; }
        grip.HandlerChanged += (_, _) =>
        {
            if (attached is not null) { attached.PointerPressed -= Pressed; attached.PointerMoved -= Moved; attached.PointerReleased -= Released; attached.PointerCaptureLost -= Released; attached.DoubleTapped -= DoubleTapped; }
            attached = grip.Handler?.PlatformView as Microsoft.UI.Xaml.UIElement;
            if (attached is null) return;
            attached.PointerPressed += Pressed; attached.PointerMoved += Moved; attached.PointerReleased += Released; attached.PointerCaptureLost += Released; attached.DoubleTapped += DoubleTapped;
        };
    }
    private Microsoft.UI.Xaml.Window? _window;
    public IDiagnosticCaseProtection CaseProtection { get; } = new WindowsCaseProtection();
    public void AttachWindow(Microsoft.Maui.Controls.Window window, MainPage page)
    {
        if (window.Handler?.PlatformView is not Microsoft.UI.Xaml.Window native || ReferenceEquals(_window, native)) return;
        _window = native;
        native.AppWindow.Closing += async (_, args) =>
        {
            if (!page.IsCapturing) return;
            args.Cancel = true; await page.StopCaptureAsync(); native.Close();
        };
    }
    private nint Handle => WinRT.Interop.WindowNative.GetWindowHandle(_window ?? throw new InvalidOperationException("ATSM-WINDOW-NOT-READY"));
    public void EnsureWindowReady()
    {
        if (Handle == 0) throw new InvalidOperationException("ATSM-WINDOW-NOT-READY");
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var xaml = process.Modules.Cast<System.Diagnostics.ProcessModule>().Single(m => m.ModuleName.Equals("Microsoft.UI.Xaml.dll", StringComparison.OrdinalIgnoreCase));
        if (!Path.GetFullPath(xaml.FileName).StartsWith(Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("ATSM-EXTERNAL-WINUI-RUNTIME");
    }
    public async Task<DiagnosticInput?> PickInputAsync(bool certificate = false)
    {
        var picker = new FileOpenPicker();
        foreach (var extension in certificate ? new[] { ".pfx", ".p12" } : new[] { "*" }) picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Handle);
        var file = await picker.PickSingleFileAsync();
        return file is null ? null : new(file.Name, () => Task.FromResult<Stream>(File.OpenRead(file.Path)));
    }
    public async Task<DiagnosticDestination?> PickCaseDestinationAsync()
    {
        var picker = new FileSavePicker { SuggestedFileName = "ATSM_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture) };
        picker.FileTypeChoices.Add(Globals.T("Source.ProtectedCase"), new[] { ".atsmcase" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Handle);
        var file = await picker.PickSaveFileAsync();
        return file is null ? null : new(file.Name, () => File.Create(file.Path));
    }
    private sealed class WindowsCaseProtection : IDiagnosticCaseProtection
    {
        public byte[] Protect(byte[] plain) => ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
        public byte[] Unprotect(byte[] encrypted) => ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
    }
}
