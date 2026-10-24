using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Atsm.Abstractions.Channels;
using Atsm.Abstractions.Tracing;
using Atsm.Channels;
using Atsm.Channels.SerialTunnel;
using Atsm.Plugin.BioRad.Cfx;
using Atsm.Runtime.Tracing;
using Microsoft.Win32;

namespace Atsm.Diagnostics.Windows;

public partial class MainWindow : Window
{
    private readonly DiagnosticCatalog _catalog = new();
    private readonly WindowsCaseProtection _protection = new();
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(1) };
    private DiagnosticCase? _case;
    private DiagnosticTraceSink? _capture;
    private CancellationTokenSource? _liveStop;
    private bool _ready, _closeAfterStop;
    private int _lastCount = -1;
    private string? _certificatePath;
    private DiagnosticProfile Profile => (DiagnosticProfile)ProfileBox.SelectedItem;

    public MainWindow()
    {
        InitializeComponent();
        ProfileBox.ItemsSource = _catalog.Profiles; ProfileBox.SelectedIndex = 0;
        FormatBox.ItemsSource = new[] { new InputChoice(DiagnosticInputFormat.Export, "Exportación del plugin (CSV / RDML)"),
            new(DiagnosticInputFormat.AstmMessage, "Mensaje ASTM completo (texto)"), new(DiagnosticInputFormat.Hl7Message, "Mensaje HL7 completo (texto)"),
            new(DiagnosticInputFormat.SysmexLog, "Log Sysmex · monitor de puertos"), new(DiagnosticInputFormat.GlymsLog, "Log Glyms · interfaz"),
            new(DiagnosticInputFormat.WireBytes, "Bytes de cable (.bin)"), new(DiagnosticInputFormat.AtsmTrace, "Traza ATSM (.atsmtrace)") };
        CatalogGrid.ItemsSource = _catalog.Profiles; ControllerBox.Text = Environment.MachineName;
        _refresh.Tick += (_, _) => RefreshCapture();
        _ready = true; SetProfile();
    }

    private void SetProfile()
    {
        if (!_ready || ProfileBox.SelectedItem is null) return;
        EvidenceText.Text = Profile.Evidence;
        InterfacePanel.Visibility = Profile.CanConnect ? Visibility.Visible : Visibility.Collapsed;
        var selected = Profile.ContentProfile is not null ? DiagnosticInputFormat.Export : Profile.IsHl7 ? DiagnosticInputFormat.Hl7Message : DiagnosticInputFormat.AstmMessage;
        FormatBox.SelectedItem = ((IEnumerable<InputChoice>)FormatBox.ItemsSource).First(c => c.Value == selected);
        ConnectButton.IsEnabled = Profile.CanConnect && _liveStop is null;
        if (_case is not null && _liveStop is null) AnalyzeCurrent();
    }
    private void ProfileChanged(object sender, SelectionChangedEventArgs e) => SetProfile();

    private DiagnosticTiming Timing(bool forLive = false)
    {
        TimeSpan Budget(TextBox input)
        {
            if (!forLive || !Profile.IsHl7) return TimeSpan.Zero;
            if (!double.TryParse(input.Text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0 || seconds > 86400)
                throw new ArgumentException("Ingresá los presupuestos HL7 configurados en el equipo, en segundos (punto decimal).");
            return TimeSpan.FromSeconds(seconds);
        }
        return new(Budget(QueryBudgetBox), Budget(ResponseBudgetBox), TimeZoneInfo.Local);
    }
    private TimeSpan Offset()
    {
        if (!TimeSpan.TryParse(OffsetBox.Text.TrimStart('+'), CultureInfo.InvariantCulture, out var offset) || Math.Abs(offset.TotalHours) > 14)
            throw new ArgumentException("El offset UTC debe tener formato ±hh:mm, entre -14:00 y +14:00.");
        return offset;
    }

    private async void OpenFile(object sender, RoutedEventArgs e)
    {
        if (_liveStop is not null) return;
        var dialog = new OpenFileDialog { Filter = "Capturas y exportaciones|*.atsmcase;*.atsmtrace;*.csv;*.rdml;*.rdm;*.xml;*.log;*.txt;*.bin|Todos|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var path = dialog.FileName;
            if (Path.GetExtension(path).Equals(".atsmcase", StringComparison.OrdinalIgnoreCase))
            {
                using var stream = File.OpenRead(path); var loaded = ProtectedCaseJournal.Read(stream, _protection);
                if (loaded.Header.Profile is not { } id || !_catalog.Profiles.Any(p => p.Id == id)) throw new InvalidDataException("El perfil guardado no está disponible en este catálogo.");
                ProfileBox.SelectedItem = _catalog.Resolve(id); _case = loaded;
            }
            else
            {
                if (new FileInfo(path).Length > DiagnosticReader.MaxInputBytes) throw new InvalidDataException("El archivo supera el límite de entrada de 32 MiB.");
                var bytes = await File.ReadAllBytesAsync(path);
                var extension = Path.GetExtension(path).ToLowerInvariant();
                if (Profile.ContentPlugin is not null && extension is ".rdml" or ".rdm" or ".xml") ProfileBox.SelectedItem = _catalog.Resolve(CfxPlugin.RdmlProfileId);
                var format = extension == ".atsmtrace" ? DiagnosticInputFormat.AtsmTrace : ((InputChoice)FormatBox.SelectedItem).Value;
                _case = DiagnosticReader.Read(bytes, format, Profile, Path.GetFileName(path), Offset());
            }
            AnalyzeCurrent(); StatusText.Text = _case.IsComplete ? "Archivo leído en memoria · Original conservado · Sin acciones sobre equipos ni LIMS." : "Caso sin cierre confirmado: puede faltar el tramo final. Se conservan los registros completos disponibles.";
        }
        catch (Exception error) { ShowError(error); }
    }

    private void SaveCase(object sender, RoutedEventArgs e)
    {
        if (_case is null || _liveStop is not null) return;
        var path = ChooseCasePath(); if (path is null) return;
        try
        {
            using var journal = new ProtectedCaseJournal(File.Create(path), _protection, _case.Header with { Profile = Profile.Id });
            if (!_case.IsComplete) journal.MarkIncomplete();
            foreach (var entry in _case.Entries) journal.Append("entry", TraceLine.From(entry));
            foreach (var batch in _case.Batches ?? []) journal.Append("batch", batch);
            StatusText.Text = "Caso guardado con DPAPI para el usuario Windows actual · No es un paquete portable entre usuarios.";
        }
        catch (Exception error) { ShowError(error); }
    }
    private string? ChooseCasePath()
    {
        var dialog = new SaveFileDialog { Filter = "Caso ATSM protegido|*.atsmcase", DefaultExt = ".atsmcase", FileName = "ATSM_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }
    private void ChooseCertificate(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Certificado PKCS#12|*.pfx;*.p12" };
        if (dialog.ShowDialog(this) == true) { _certificatePath = dialog.FileName; CertificateText.Text = Path.GetFileName(dialog.FileName); }
    }

    private async void Connect(object sender, RoutedEventArgs e)
    {
        if (_liveStop is not null || !Profile.CanConnect) return;
        ProtectedCaseJournal? journal = null; X509Certificate2? certificate = null;
        try
        {
            if (!int.TryParse(PortBox.Text, out var port) || port is < 1 or > 65535) throw new ArgumentException("Ingresá el puerto TCP configurado en el instrumento o agente.");
            var timing = Timing(forLive: true); var selected = Profile;
            var host = HostBox.Text.Trim(); var controller = ControllerBox.Text.Trim(); var agentPort = AgentPortBox.Text.Trim();
            if (host.Length == 0 || controller.Length == 0) throw new ArgumentException("Completá host e identidad del controlador.");
            var transport = TransportBox.SelectedIndex; var simulate = ModeBox.SelectedIndex == 1; var tls = TlsBox.IsChecked == true;
            if (_certificatePath is not null) certificate = X509CertificateLoader.LoadPkcs12FromFile(_certificatePath, CertificatePassword.Password, X509KeyStorageFlags.EphemeralKeySet);
            if (tls && transport == 1 && certificate is null) throw new ArgumentException("El servidor TLS requiere un certificado PFX con clave privada.");
            var endpoint = new TcpEndpointOptions(host, port, tls ? TlsMode.Tls : TlsMode.Plain)
            { ClientCertificates = certificate is null ? null : new X509CertificateCollection { certificate }, ServerCertificate = certificate };
            var path = ChooseCasePath(); if (path is null) return;
            var header = new TraceHeader(Guid.CreateVersion7(), transport == 2 ? "SerialTunnel" : "TCP", "diagnostic", selected.Id, DateTimeOffset.UtcNow,
                new Dictionary<string, string> { ["protocol"] = selected.Protocol, ["mode"] = simulate ? "diagnostic-host" : "capture-no-responses", ["protection"] = "Windows DPAPI CurrentUser" });
            journal = new(File.Create(path), _protection, header);
            _capture = new(header, journal); _case = new(header, []); _lastCount = -1;
            _capture.EnsureReady(); _liveStop = new(); SetBusy(true); _refresh.Start();
            StatusText.Text = "Abriendo interfaz · Caso protegido preparado antes de conectar.";
            var liveToken = _liveStop.Token;
            await Task.Run(() => LiveDiagnostic.RunAsync(selected, timing, _capture, async stop =>
            {
                IInstrumentChannel channel;
                if (transport == 2)
                    channel = await SerialTunnelChannel.ConnectAsync("maintenance", endpoint, new(controller, agentPort), stop);
                else if (transport == 1)
                {
                    var listener = new TcpListener(IPAddress.Parse(host), port); listener.Start();
                    try { channel = await TcpNativeChannel.AcceptAsync("maintenance", listener, endpoint, "diagnostic", stop); }
                    finally { listener.Stop(); }
                }
                else channel = await TcpNativeChannel.ConnectAsync("maintenance", endpoint, "diagnostic", stop);
                _ = Dispatcher.InvokeAsync(() => StatusText.Text = simulate ? "Simulación de host · Resultados custodiados en el caso; consultas sin órdenes." : "Captura conectada sin respuestas de protocolo · No observa otra sesión.");
                return channel;
            }, simulate, controller, liveToken));
            StatusText.Text = "Interfaz cerrada · Captura conservada en el caso protegido.";
        }
        catch (OperationCanceledException) { StatusText.Text = "Captura detenida · Caso protegido conservado."; }
        catch (Exception error) { journal?.MarkIncomplete(); ShowError(error); }
        finally
        {
            _refresh.Stop(); RefreshCapture(force: true);
            try { journal?.Dispose(); } catch (Exception error) { ShowError(error); }
            finally
            {
                if (_case is not null && journal is not null) { _case = _case with { IsComplete = journal.IsComplete }; AnalyzeCurrent(); }
                certificate?.Dispose(); _liveStop?.Dispose(); _liveStop = null; _capture = null; SetBusy(false);
            }
            if (_closeAfterStop) Close();
        }
    }
    private void SetBusy(bool busy)
    {
        ProfileBox.IsEnabled = FormatBox.IsEnabled = OpenButton.IsEnabled = SaveButton.IsEnabled = !busy;
        ConnectButton.IsEnabled = !busy && Profile.CanConnect; StopButton.IsEnabled = busy;
    }
    private void Stop(object sender, RoutedEventArgs e) => _liveStop?.Cancel();
    private void OnClosing(object? sender, CancelEventArgs e)
    { if (_liveStop is not null) { e.Cancel = true; _closeAfterStop = true; _liveStop.Cancel(); } }

    private void RefreshCapture(bool force = false)
    {
        if (_capture is null || _case is null) return;
        var entries = _capture.Snapshot(); if (!force && entries.Count == _lastCount) return;
        _lastCount = entries.Count; _case = _case with { Entries = entries, Batches = _capture.Batches() }; AnalyzeCurrent();
    }
    private void AnalyzeCurrent()
    {
        if (!_ready || _case is null) return;
        try
        {
            var analyzed = DiagnosticReader.Analyze(_case, Profile, Timing());
            CaseTitle.Text = _case.Header.ImportedFrom ?? _case.Header.Source;
            SummaryText.Text = $"{_case.Entries.Count:N0} entradas · RX {_case.Entries.Where(e => e.Direction == TraceDirection.Received).Sum(e => e.Data.Length):N0} bytes · {analyzed.Messages.Count:N0} mensajes · {analyzed.Observations.Count:N0} observaciones · {analyzed.Diagnostics.Count:N0} hallazgos. Vista: últimas 2.000 filas; el caso conserva la captura.";
            TraceGrid.ItemsSource = _case.Entries.TakeLast(2000).Select(e => new TraceRow(e.Sequence, e.Elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture), e.Direction.ToString(), e.Data.Length,
                e.Code ?? DiagnosticReader.DescribeBytes(e.Data[..Math.Min(e.Data.Length, 180)], HexBox.IsChecked == true),
                e.Code is not null ? e.Code + "\n" + e.Detail + "\n" + DiagnosticReader.DescribeBytes(e.Data, HexBox.IsChecked == true) : DiagnosticReader.DescribeBytes(e.Data, HexBox.IsChecked == true))).ToArray();
            MessagesGrid.ItemsSource = analyzed.Messages.TakeLast(2000).ToArray();
            ObservationsGrid.ItemsSource = analyzed.Observations.TakeLast(2000).Select(o => new ObservationRow(o.Specimen.SpecimenId, o.Specimen.Position, o.Test.Vendor.Display ?? o.Test.Vendor.Code,
                o.ResultKind, o.Value.RawText, o.Value.RawUnit, o.InstrumentStatus + " " + string.Join(", ", o.Flags.Select(f => f.Code)), JsonSerializer.Serialize(o, new JsonSerializerOptions(TraceJson.Options) { WriteIndented = true }))).ToArray();
            FindingsGrid.ItemsSource = analyzed.Diagnostics.TakeLast(2000).ToArray(); EventsGrid.ItemsSource = analyzed.Events.TakeLast(2000).ToArray();
        }
        catch (Exception error) { ShowError(error); }
    }
    private void DetailSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || sender is not DataGrid { SelectedItem: { } item }) return;
        var text = item switch { TraceRow row => row.Details, ObservationRow row => row.Details, DiagnosticProfile profile => profile.Id + "\n" + profile.DisplayName + "\n" + profile.Evidence + "\n" + string.Join("\n", profile.AstmProfile?.Evidence ?? profile.ContentProfile?.Evidence ?? []), DecodedMessage message => message.Text.Replace("\r", "\r\n", StringComparison.Ordinal), _ => JsonSerializer.Serialize(item, new JsonSerializerOptions { WriteIndented = true }) };
        DetailBox.Text = text.Length <= 65536 ? text : text[..65536] + "\n[Detalle visual limitado a 64 KiB; el caso conserva el contenido.]";
    }
    private void HexChanged(object sender, RoutedEventArgs e) { if (_ready) AnalyzeCurrent(); }
    private void ShowError(Exception error) => StatusText.Text = "No se completó la operación: " + error.Message;
    private void Demo(object sender, RoutedEventArgs e) { if (_liveStop is null) LoadDemo(); }
    public void LoadDemo()
    {
        ProfileBox.SelectedItem = _catalog.Resolve(CfxPlugin.CsvProfileId);
        var data = Encoding.UTF8.GetBytes("Well,Sample,Target,Cq,Fluor\r\nA01,CONTROL-SYN-1,TARGET-A,23.45,FAM\r\nA02,SAMPLE-SYN-2,TARGET-A,28.12,FAM\r\nA03,SAMPLE-SYN-3,TARGET-B,Undetermined,HEX\r\n");
        _case = DiagnosticReader.Read(data, DiagnosticInputFormat.Export, Profile, "Ejemplo sintético · Bio-Rad CFX", TimeSpan.FromHours(-3));
        AnalyzeCurrent(); StatusText.Text = "Ejemplo sintético · Cq original, sin clasificación clínica ni datos de pacientes.";
    }
    public void SmokeCheckProtection(string directory)
    {
        var path = Path.Combine(directory, "atsm-smoke-" + Guid.NewGuid().ToString("N") + ".atsmcase");
        try
        {
            using (var journal = new ProtectedCaseJournal(File.Create(path), _protection, _case!.Header))
                foreach (var entry in _case.Entries) journal.Append("entry", TraceLine.From(entry));
            using var input = File.OpenRead(path); var read = ProtectedCaseJournal.Read(input, _protection);
            if (read.Entries.Count != _case.Entries.Count || DiagnosticReader.Analyze(read, Profile, Timing()).Observations.Count != 3) throw new InvalidDataException("Smoke round trip failed.");
            if (Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains("SAMPLE-SYN", StringComparison.Ordinal)) throw new InvalidDataException("Plaintext case content.");
        }
        finally { File.Delete(path); }
    }
    private sealed record InputChoice(DiagnosticInputFormat Value, string Label);
    private sealed record TraceRow(long Sequence, string Milliseconds, string Direction, int Length, string Preview, string Details);
    private sealed record ObservationRow(string Sample, string? Well, string Test, string? Kind, string Value, string? Unit, string Status, string Details);
}
