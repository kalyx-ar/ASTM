using System.Security.Cryptography;
using Windows.Storage.Pickers;

namespace Atsm.Diagnostics.Maui;

public sealed class WindowsDiagnosticServices : IDiagnosticPlatformServices
{
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
        picker.FileTypeChoices.Add("Caso ATSM protegido", new[] { ".atsmcase" });
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
