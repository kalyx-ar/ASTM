using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Atsm.Abstractions.Channels;
using Atsm.Abstractions.Model;
using Atsm.Abstractions.Tracing;
using Atsm.Channels;
using Atsm.Channels.SerialTunnel;
using Atsm.Plugin.BioRad.Cfx;
using Atsm.Runtime.Tracing;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Dispatching;

namespace Atsm.Diagnostics.Maui;

public sealed class MainPage : ContentPage
{
    private static readonly Color Ink = Color.FromArgb("#1E293B"), Muted = Color.FromArgb("#52677E"), Teal = Color.FromArgb("#0F766E");
    private readonly IDiagnosticPlatformServices _platform;
    private readonly DiagnosticCatalog _catalog = new();
    private static readonly JsonSerializerOptions DetailJson = new(TraceJson.Options) { WriteIndented = true };
    private readonly Picker _profiles = Choice("Plugin / formato"), _formats = Choice("Entrada"), _transport = Choice("Transporte"), _mode = Choice("Modo");
    private readonly Entry _host = Input("127.0.0.1"), _port = Input(""), _agentPort = Input("p1"), _controller = Input(Environment.MachineName);
    private readonly Entry _query = Input(""), _response = Input(""), _offset = Input("-03:00"), _password = Input("");
    private readonly CheckBox _tls = new(), _hex = new();
    private readonly Label _evidence = Text("", 12), _certificate = Text("Sin certificado seleccionado", 12), _title = Text("Sin caso abierto", 21), _summary = Text("Elegí un perfil y abrí un archivo o conectá una interfaz.", 12), _status = Text("Listo · Diagnóstico sin publicación en LIMS.", 12);
    private readonly Button _open, _save, _connect, _stop;
    private readonly VerticalStackLayout _interface = new() { Spacing = 8 }, _source = new() { Spacing = 8 }, _tableHeader = new();
    private readonly CollectionView _table = new() { SelectionMode = SelectionMode.Single, ItemsLayout = new LinearItemsLayout(ItemsLayoutOrientation.Vertical) };
    private readonly Editor _detail = new() { IsReadOnly = true, Text = "Seleccioná una fila para inspeccionar su detalle.", TextColor = Color.FromArgb("#D7E7EF"), BackgroundColor = Color.FromArgb("#132632"), FontSize = 12, FontFamily = "Consolas" };
    private readonly Grid _body = new(), _workspace = new();
    private readonly ScrollView _sourceScroll;
    private readonly List<Button> _tabButtons = [];
    private readonly IDispatcherTimer _refresh;
    private readonly string[] _tabs = ["Trama", "Mensajes", "Observaciones", "Hallazgos", "Calidad", "Eventos", "Catálogo"];
    private readonly DiagnosticInputFormat[] _inputFormats = [DiagnosticInputFormat.Export, DiagnosticInputFormat.AstmMessage, DiagnosticInputFormat.Hl7Message, DiagnosticInputFormat.SysmexLog, DiagnosticInputFormat.LegacyLog, DiagnosticInputFormat.WireBytes, DiagnosticInputFormat.AtsmTrace];
    private DiagnosticCase? _case;
    private DiagnosticAnalysis? _analysis;
    private DiagnosticTraceSink? _capture;
    private CancellationTokenSource? _liveStop;
    private TaskCompletionSource? _captureFinished;
    private byte[]? _certificateBytes;
    private bool _ready, _settingsVisible, _selfTestStarted;
    private int _tab = 2, _lastCount = -1;
    public bool IsCapturing => _liveStop is not null;
    private DiagnosticProfile Profile => _catalog.Profiles[_profiles.SelectedIndex];

    public MainPage(IDiagnosticPlatformServices platform)
    {
        _platform = platform; BackgroundColor = Color.FromArgb("#F3F6FA");
        _open = Action("Abrir archivo", async () => await OpenAsync()); _save = Action("Guardar caso", async () => await SaveAsync());
        _connect = Action("Conectar", async () => await ConnectAsync()); _stop = Action("Detener", async () => await StopCaptureAsync()); _stop.IsEnabled = false;
        _password.IsPassword = true;
        foreach (var profile in _catalog.Profiles) _profiles.Items.Add(profile.DisplayName);
        foreach (var value in new[] { "Exportación del plugin (CSV / RDML)", "Mensaje ASTM completo", "Mensaje HL7 completo", "Log Sysmex · monitor de puertos", "Log legacy · interfaz", "Bytes de cable (.bin)", "Traza ATSM (.atsmtrace)" }) _formats.Items.Add(value);
        foreach (var value in new[] { "TCP · conectar al instrumento", "TCP · esperar al instrumento", "SerialTunnel · agente ATSM" }) _transport.Items.Add(value);
        foreach (var value in new[] { "Capturar sin responder", "Simular host: ACK, sin órdenes" }) _mode.Items.Add(value);
        _transport.SelectedIndex = _mode.SelectedIndex = 0;
        _profiles.SelectedIndexChanged += (_, _) => SetProfile(); _profiles.SelectedIndex = 0;
        _source.Add(Text("FUENTE Y PERFIL", 12)); _source.Add(_profiles); _source.Add(_evidence); _source.Add(_formats);
        _source.Add(new HorizontalStackLayout { Spacing = 8, Children = { _open, _save } });
        _source.Add(Text("Los casos se protegen para el usuario Windows actual. Los originales no se modifican.", 12));
        _interface.Add(Text("INTERFAZ EN VIVO", 12)); _interface.Add(_transport);
        _interface.Add(Field("Host / IP local al escuchar", _host));
        _interface.Add(Field("Puerto TCP", _port)); _interface.Add(Field("Puerto del agente", _agentPort));
        _interface.Add(Field("Identidad del controlador", _controller)); _interface.Add(new HorizontalStackLayout { Children = { _tls, Text("TLS · sin degradación automática", 12) } });
        _interface.Add(Action("Certificado PFX / mTLS", async () => await ChooseCertificateAsync())); _interface.Add(_certificate); _interface.Add(Field("Contraseña del PFX (no se guarda)", _password));
        _interface.Add(_mode); _interface.Add(Text("Conexión propia. SerialTunnel toma control exclusivo; no observa otra sesión.", 12));
        _interface.Add(Field("Presupuesto de consulta HL7 (segundos)", _query)); _interface.Add(Field("Presupuesto de respuesta HL7 (segundos)", _response));
        _interface.Add(new HorizontalStackLayout { Spacing = 8, Children = { _connect, _stop } });
        _source.Add(Field("Offset UTC de logs (±hh:mm)", _offset)); _source.Add(_interface);
        _sourceScroll = new ScrollView { Content = _source, Padding = 16, BackgroundColor = Colors.White };

        var nav = new HorizontalStackLayout { Spacing = 5 };
        for (var i = 0; i < _tabs.Length; i++) { var selected = i; var button = Action(_tabs[i], () => { _tab = selected; ShowTable(); return Task.CompletedTask; }); nav.Add(button); _tabButtons.Add(button); }
        var controls = new HorizontalStackLayout { Spacing = 8, Children = { Action("Fuente / conexión", () => { _settingsVisible = !_settingsVisible; LayoutPanels(); return Task.CompletedTask; }), Action("Ejemplo CFX", () => { if (!IsCapturing) LoadDemo(); return Task.CompletedTask; }), _hex, Text("Hexadecimal", 12) } };
        _hex.CheckedChanged += (_, _) => { if (_ready) ShowTable(); };
        var summary = new VerticalStackLayout { Spacing = 6, Padding = 14, BackgroundColor = Colors.White, Children = { _title, _summary, controls } };
        _workspace.RowDefinitions = new RowDefinitionCollection(new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star), new RowDefinition(150));
        Place(_workspace, summary, 0, 0); Place(_workspace, new ScrollView { Orientation = ScrollOrientation.Horizontal, Content = nav }, 0, 1);
        Place(_workspace, _tableHeader, 0, 2); Place(_workspace, _table, 0, 3); Place(_workspace, _detail, 0, 4);
        _table.BackgroundColor = Colors.White; _table.EmptyView = Text("Sin datos para esta vista.", 14);
        _table.SelectionChanged += (_, e) => { if (e.CurrentSelection.FirstOrDefault() is DiagnosticRow row) _detail.Text = row.Details.Length <= 65536 ? row.Details : row.Details[..65536] + "\n[Detalle visual limitado a 64 KiB; el caso conserva el contenido.]"; };
        _body.ColumnDefinitions = new ColumnDefinitionCollection(new ColumnDefinition(330), new ColumnDefinition(GridLength.Star)); _body.ColumnSpacing = 14; _body.Padding = 14;
        Place(_body, _sourceScroll, 0, 0); Place(_body, _workspace, 1, 0);
        var banner = new VerticalStackLayout { Padding = new Thickness(22, 12), BackgroundColor = Color.FromArgb("#12313E"), Children = { new Label { Text = "ATSM  /  Diagnóstico", FontSize = 25, TextColor = Colors.White }, new Label { Text = "MAUI · Interfaces, logs y datos de calidad · Motor independiente de EDC", FontSize = 12, TextColor = Color.FromArgb("#BBD0DA") } } };
        var root = new Grid { RowDefinitions = new RowDefinitionCollection(new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto)) };
        Place(root, banner, 0, 0); Place(root, _body, 0, 1); Place(root, new Border { Content = _status, Padding = new Thickness(20, 8), BackgroundColor = Color.FromArgb("#E3EBF2"), StrokeThickness = 0 }, 0, 2); Content = root;
        _refresh = Dispatcher.CreateTimer(); _refresh.Interval = TimeSpan.FromSeconds(1); _refresh.Tick += (_, _) => RefreshCapture();
        SizeChanged += (_, _) => LayoutPanels(); _ready = true; SetProfile(); ShowTable();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing(); var args = Environment.GetCommandLineArgs();
        if (args.Contains("--demo")) LoadDemo();
        var selfTest = Array.IndexOf(args, "--self-test");
        if (selfTest >= 0 && selfTest + 1 < args.Length && !_selfTestStarted)
        {
            _selfTestStarted = true;
            Dispatcher.DispatchDelayed(TimeSpan.FromSeconds(1), () =>
            {
                try { if (Handler?.PlatformView is null) throw new InvalidOperationException("Window not realized."); _platform.EnsureWindowReady(); SelfTest(); File.WriteAllText(args[selfTest + 1], "PASS: MAUI window, file-dialog-owner, private-runtime, catalog=12, CFX=3, protected-case-roundtrip, QC=6"); Environment.Exit(0); }
                catch (Exception error) { File.WriteAllText(args[selfTest + 1], "FAIL: " + error.GetType().Name); Environment.Exit(1); }
            });
        }
    }

    private void LayoutPanels()
    {
        var compact = Width < 1000;
        _body.ColumnDefinitions[0].Width = compact ? GridLength.Star : new GridLength(330);
        _body.ColumnDefinitions[1].Width = compact ? new GridLength(0) : GridLength.Star;
        Grid.SetColumn(_workspace, compact ? 0 : 1); _sourceScroll.IsVisible = !compact || _settingsVisible; _workspace.IsVisible = !compact || !_settingsVisible;
    }
    private void SetProfile()
    {
        if (!_ready || _profiles.SelectedIndex < 0) return;
        _evidence.Text = Profile.Evidence; _interface.IsVisible = Profile.CanConnect;
        _formats.SelectedIndex = Profile.ContentPlugin is not null ? 0 : Profile.IsHl7 ? 2 : 1;
        _connect.IsEnabled = Profile.CanConnect && !IsCapturing; if (_case is not null && !IsCapturing) Analyze();
    }
    private void SelectProfile(string id) => _profiles.SelectedIndex = Enumerable.Range(0, _catalog.Profiles.Count).Single(i => _catalog.Profiles[i].Id == id);
    private DiagnosticTiming Timing(bool live = false)
    {
        TimeSpan Budget(Entry input)
        {
            if (!live || !Profile.IsHl7) return TimeSpan.Zero;
            if (!double.TryParse(input.Text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0 || seconds > 86400) throw new ArgumentException("Ingresá los plazos HL7 configurados en el equipo, en segundos y con punto decimal.");
            return TimeSpan.FromSeconds(seconds);
        }
        return new(Budget(_query), Budget(_response), TimeZoneInfo.Local);
    }
    private async Task OpenAsync()
    {
        if (IsCapturing) return;
        try
        {
            var input = await _platform.PickInputAsync(); if (input is null) return;
            await using var stream = await input.OpenReadAsync();
            if (Path.GetExtension(input.Name).Equals(".atsmcase", StringComparison.OrdinalIgnoreCase))
            {
                var loaded = ProtectedCaseJournal.Read(stream, _platform.CaseProtection);
                if (loaded.Header.Profile is not { } id || !_catalog.Profiles.Any(p => p.Id == id)) throw new InvalidDataException("El perfil guardado no está disponible en este catálogo.");
                SelectProfile(id); _case = loaded;
            }
            else
            {
                var bytes = await ReadBoundedAsync(stream); var extension = Path.GetExtension(input.Name).ToLowerInvariant();
                if (Profile.ContentPlugin is not null && extension is ".rdml" or ".rdm" or ".xml") SelectProfile(CfxPlugin.RdmlProfileId);
                if (!TimeSpan.TryParse((_offset.Text ?? "").TrimStart('+'), CultureInfo.InvariantCulture, out var offset) || Math.Abs(offset.TotalHours) > 14) throw new ArgumentException("Offset UTC inválido (±hh:mm).");
                _case = DiagnosticReader.Read(bytes, extension == ".atsmtrace" ? DiagnosticInputFormat.AtsmTrace : _inputFormats[_formats.SelectedIndex], Profile, input.Name, offset);
            }
            Analyze(); _status.Text = _case.IsComplete ? "Archivo leído · Original conservado · Sin acciones sobre instrumentos ni LIMS." : "Caso incompleto: puede faltar el tramo final. Registros disponibles conservados.";
        }
        catch (Exception error) { ShowError(error); }
    }
    private static async Task<byte[]> ReadBoundedAsync(Stream stream)
    {
        using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer)) > 0) { if (output.Length + count > DiagnosticReader.MaxInputBytes) throw new InvalidDataException("Límite de entrada: 32 MiB."); output.Write(buffer, 0, count); }
        return output.ToArray();
    }
    private async Task SaveAsync()
    {
        if (_case is null || IsCapturing) return;
        try
        {
            var destination = await _platform.PickCaseDestinationAsync(); if (destination is null) return;
            using (var journal = new ProtectedCaseJournal(destination.Create(), _platform.CaseProtection, _case.Header with { Profile = Profile.Id }))
            {
                if (!_case.IsComplete) journal.MarkIncomplete();
                foreach (var entry in _case.Entries) journal.Append("entry", TraceLine.From(entry));
                foreach (var batch in _case.Batches ?? []) journal.Append("batch", batch);
            }
            _status.Text = "Caso protegido guardado: " + destination.DisplayName + " · Usuario Windows actual.";
        }
        catch (Exception error) { ShowError(error); }
    }
    private async Task ChooseCertificateAsync()
    {
        if (IsCapturing) return;
        try
        {
            var input = await _platform.PickInputAsync(certificate: true); if (input is null) return;
            await using var stream = await input.OpenReadAsync(); var bytes = await ReadBoundedAsync(stream);
            if (_certificateBytes is not null) CryptographicOperations.ZeroMemory(_certificateBytes);
            _certificateBytes = bytes; _certificate.Text = input.Name;
        }
        catch (Exception error) { ShowError(error); }
    }
    private async Task ConnectAsync()
    {
        if (IsCapturing || !Profile.CanConnect) return;
        ProtectedCaseJournal? journal = null; X509Certificate2? certificate = null;
        try
        {
            if (!int.TryParse(_port.Text, out var port) || port is < 1 or > 65535) throw new ArgumentException("Ingresá el puerto TCP configurado en el instrumento o agente.");
            var timing = Timing(live: true); var selected = Profile; var host = (_host.Text ?? "").Trim(); var controller = (_controller.Text ?? "").Trim(); var agentPort = (_agentPort.Text ?? "").Trim();
            if (host.Length == 0 || controller.Length == 0) throw new ArgumentException("Completá host e identidad del controlador.");
            var transport = _transport.SelectedIndex; var simulate = _mode.SelectedIndex == 1; var tls = _tls.IsChecked;
            if (_certificateBytes is not null) certificate = X509CertificateLoader.LoadPkcs12(_certificateBytes, _password.Text, X509KeyStorageFlags.EphemeralKeySet);
            if (tls && transport == 1 && certificate is null) throw new ArgumentException("El servidor TLS requiere un PFX con clave privada.");
            var endpoint = new TcpEndpointOptions(host, port, tls ? TlsMode.Tls : TlsMode.Plain) { ClientCertificates = certificate is null ? null : new X509CertificateCollection { certificate }, ServerCertificate = certificate };
            var destination = await _platform.PickCaseDestinationAsync(); if (destination is null) return;
            var header = new TraceHeader(Guid.CreateVersion7(), transport == 2 ? "SerialTunnel" : "TCP", "diagnostic", selected.Id, DateTimeOffset.UtcNow,
                new Dictionary<string, string> { ["protocol"] = selected.Protocol, ["mode"] = simulate ? "diagnostic-host" : "capture-no-responses", ["protection"] = "Windows DPAPI CurrentUser" });
            journal = new(destination.Create(), _platform.CaseProtection, header); _capture = new(header, journal); _case = new(header, []); _lastCount = -1;
            _capture.EnsureReady(); _liveStop = new(); _captureFinished = new(TaskCreationOptions.RunContinuationsAsynchronously); SetBusy(true); _refresh.Start();
            _status.Text = "Abriendo interfaz · Caso protegido preparado antes de conectar."; var token = _liveStop.Token;
            await Task.Run(() => LiveDiagnostic.RunAsync(selected, timing, _capture, async stop =>
            {
                IInstrumentChannel channel;
                if (transport == 2) channel = await SerialTunnelChannel.ConnectAsync("maintenance", endpoint, new(controller, agentPort), stop);
                else if (transport == 1)
                {
                    var listener = new TcpListener(IPAddress.Parse(host), port); listener.Start();
                    try { channel = await TcpNativeChannel.AcceptAsync("maintenance", listener, endpoint, "diagnostic", stop); } finally { listener.Stop(); }
                }
                else channel = await TcpNativeChannel.ConnectAsync("maintenance", endpoint, "diagnostic", stop);
                Dispatcher.Dispatch(() => _status.Text = simulate ? "Simulación de host · Custodia de diagnóstico, consultas sin órdenes." : "Captura conectada sin respuestas · Conexión propia.");
                return channel;
            }, simulate, controller, token));
            _status.Text = "Interfaz cerrada · Caso protegido conservado.";
        }
        catch (OperationCanceledException) { _status.Text = "Captura detenida · Caso protegido conservado."; }
        catch (Exception error) { journal?.MarkIncomplete(); ShowError(error); }
        finally
        {
            _refresh.Stop(); RefreshCapture(force: true);
            try { journal?.Dispose(); } catch (Exception error) { ShowError(error); }
            if (_case is not null && journal is not null) { _case = _case with { IsComplete = journal.IsComplete }; Analyze(); }
            certificate?.Dispose(); _liveStop?.Dispose(); _liveStop = null; _capture = null; SetBusy(false); _captureFinished?.TrySetResult();
        }
    }
    public async Task StopCaptureAsync() { var finished = _captureFinished?.Task; _liveStop?.Cancel(); if (finished is not null) await finished; }
    private void SetBusy(bool busy)
    {
        _profiles.IsEnabled = _formats.IsEnabled = _open.IsEnabled = _save.IsEnabled = !busy;
        _connect.IsEnabled = !busy && Profile.CanConnect; _stop.IsEnabled = busy;
    }
    private void RefreshCapture(bool force = false)
    {
        if (_capture is null || _case is null) return; var entries = _capture.Snapshot(); if (!force && entries.Count == _lastCount) return;
        _lastCount = entries.Count; _case = _case with { Entries = entries, Batches = _capture.Batches() }; Analyze();
    }
    private void Analyze()
    {
        if (!_ready || _case is null) return;
        try
        {
            _analysis = DiagnosticReader.Analyze(_case, Profile, Timing()); _title.Text = _case.Header.ImportedFrom ?? _case.Header.Source;
            _summary.Text = $"{_case.Entries.Count:N0} entradas · {_analysis.Messages.Count:N0} mensajes · {_analysis.Observations.Count:N0} observaciones · {_analysis.Diagnostics.Count:N0} hallazgos · Vista: últimas 2.000 filas"; ShowTable();
        }
        catch (Exception error) { ShowError(error); }
    }
    private string Json<T>(T value) => JsonSerializer.Serialize(value, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)DetailJson.GetTypeInfo(typeof(T)));
    private void ShowTable()
    {
        if (!_ready) return;
        foreach (var (button, index) in _tabButtons.Select((b, i) => (b, i))) button.BackgroundColor = index == _tab ? Teal : Color.FromArgb("#38576B");
        string[] headers; IEnumerable<DiagnosticRow> rows;
        switch (_tab)
        {
            case 0:
                headers = ["#", "ms", "Dirección", "Bytes", "Código / contenido"];
                rows = (_case?.Entries ?? []).Select(e => new DiagnosticRow([e.Sequence.ToString(), e.Elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture), e.Direction.ToString(), e.Data.Length.ToString(), e.Code ?? DiagnosticReader.DescribeBytes(e.Data[..Math.Min(e.Data.Length, 140)], _hex.IsChecked)], e.Code + "\n" + e.Detail + "\n" + DiagnosticReader.DescribeBytes(e.Data, _hex.IsChecked))); break;
            case 1:
                headers = ["#", "Dirección", "Tipo", "Contenido"];
                rows = (_analysis?.Messages ?? []).Select(m => new DiagnosticRow([m.Sequence.ToString(), m.Direction, m.Type, m.Text], m.Text.Replace("\r", "\r\n", StringComparison.Ordinal))); break;
            case 2:
                headers = ["Muestra", "Posición", "Prueba", "Tipo", "Valor original", "Unidad", "Estado / flags"];
                rows = (_analysis?.Observations ?? []).Select(o => new DiagnosticRow([o.Specimen.SpecimenId, o.Specimen.Position ?? "", o.Test.Vendor.Display ?? o.Test.Vendor.Code, o.ResultKind ?? "", o.Value.RawText, o.Value.RawUnit ?? "", o.InstrumentStatus + " " + string.Join(", ", o.Flags.Select(f => f.Code))], Json(o))); break;
            case 3:
                headers = ["Nivel", "Código", "Hallazgo"];
                rows = (_analysis?.Diagnostics ?? []).Select(d => new DiagnosticRow([d.Severity.ToString(), d.Code, d.Message], d.Code + "\n" + d.Location + "\n" + d.Message)); break;
            case 4:
                headers = ["Material / lote", "Prueba", "Dato reportado", "Valor original", "Ámbito", "Campo fuente"];
                rows = (_analysis?.Observations ?? []).Where(o => o.Specimen.Role != SpecimenRole.Patient && (o.Quality is not null || o.Specimen.Role is SpecimenRole.Control or SpecimenRole.Calibrator)).SelectMany(QualityRows)
                    .Concat((_analysis?.QualityResources ?? []).Select(r => new DiagnosticRow(["Referencia externa", "", "Objetivos / límites", "Requiere anfitrión / ETL", "Lote", r.SourceField], Json(r)))); break;
            case 5:
                headers = ["Capa", "#", "Código", "Detalle"];
                rows = (_analysis?.Events ?? []).Select(e => new DiagnosticRow([e.Level.ToString(), e.Sequence.ToString(), e.Code, e.Detail ?? ""], $"{e.Level}\n{e.Code}\n{e.Detail}")); break;
            default:
                headers = ["Marca", "Perfil", "Formato", "Evidencia / alcance"];
                rows = _catalog.Profiles.Select(p => new DiagnosticRow([p.Manufacturer, p.Name, p.Protocol, p.Evidence], p.Id + "\n" + p.DisplayName + "\n" + p.Evidence + "\n" + string.Join("\n", p.AstmProfile?.Evidence ?? p.ContentProfile?.Evidence ?? []))); break;
        }
        _tableHeader.Clear(); _tableHeader.Add(new DataRowView(headers, header: true));
        _table.ItemsSource = null; _table.ItemTemplate = new DataTemplate(() => new DataRowView(headers)); _table.ItemsSource = rows.TakeLast(2000).ToArray();
    }
    private IEnumerable<DiagnosticRow> QualityRows(InstrumentObservation observation)
    {
        var context = observation.Quality; var material = context?.Material;
        var label = (material?.Identifier ?? observation.Specimen.SpecimenId) + (material?.Lot is { } lot ? " / " + lot : ""); var detail = Json(observation);
        if (observation.Value.RawText.Length > 0) yield return new([label, observation.Test.Vendor.Code, "Medición · " + observation.ResultKind, observation.Value.RawText, "Individual", "Observación"], detail);
        foreach (var statistic in context?.Statistics ?? []) yield return new([label, observation.Test.Vendor.Code, statistic.Kind switch
        { ReportedStatisticKind.CqMean => "Cq · media", ReportedStatisticKind.CqStandardDeviation => "Cq · desvío estándar", ReportedStatisticKind.EfficiencyPercent => "Eficiencia (%)", ReportedStatisticKind.Slope => "Pendiente", ReportedStatisticKind.YIntercept => "Ordenada al origen", ReportedStatisticKind.RSquared => "R²",
            ReportedStatisticKind.StartingQuantity => "Cantidad inicial (SQ)", ReportedStatisticKind.LogStartingQuantity => "Log cantidad inicial", ReportedStatisticKind.StartingQuantityMean => "SQ · media", ReportedStatisticKind.StartingQuantityStandardDeviation => "SQ · desvío estándar", _ => statistic.Kind.ToString() }, statistic.Value.RawText,
            statistic.Scope switch { StatisticScope.ReplicateGroup => "Réplicas", StatisticScope.StandardCurve => "Curva", _ => "Pozo" }, statistic.SourceField], detail);
    }
    public void LoadDemo()
    {
        SelectProfile(CfxPlugin.CsvProfileId);
        var data = Encoding.UTF8.GetBytes("Well,Sample,Target,Cq,Fluor,Content,Cq Mean,Cq Std. Dev\r\nA01,CONTROL-SYN-1,TARGET-A,23.45,FAM,Pos-01,23.50,0.07\r\nA02,STANDARD-SYN-2,TARGET-A,28.12,FAM,Std-01,28.10,0.03\r\nA03,NTC-SYN-3,TARGET-B,Undetermined,HEX,NTC,Undetermined,\r\n");
        _case = DiagnosticReader.Read(data, DiagnosticInputFormat.Export, Profile, "Ejemplo sintético · Bio-Rad CFX", TimeSpan.FromHours(-3)); _tab = 4; Analyze();
        _status.Text = "Ejemplo sintético · Estadísticas reportadas por el software, sin decisión QC ni datos de pacientes.";
    }
    private void SelfTest()
    {
        var runtime = Path.GetFullPath(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
        if (!runtime.StartsWith(Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("External runtime used.");
        LoadDemo(); var path = Path.Combine(Path.GetTempPath(), "atsm-maui-selftest-" + Guid.NewGuid().ToString("N") + ".atsmcase");
        try
        {
            using (var journal = new ProtectedCaseJournal(File.Create(path), _platform.CaseProtection, _case!.Header)) foreach (var entry in _case.Entries) journal.Append("entry", TraceLine.From(entry));
            using var input = File.OpenRead(path); var loaded = ProtectedCaseJournal.Read(input, _platform.CaseProtection); var analyzed = DiagnosticReader.Analyze(loaded, Profile, Timing());
            if (_catalog.Profiles.Count != 12 || analyzed.Observations.Count != 3 || analyzed.Observations.Sum(o => o.Quality?.Statistics.Count ?? 0) != 6) throw new InvalidDataException("ATSM-MAUI-SELFTEST");
            if (Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains(Convert.ToBase64String(_case.Entries[0].Data.Span), StringComparison.Ordinal)) throw new InvalidDataException("ATSM-UNPROTECTED-CASE");
        }
        finally { File.Delete(path); }
    }
    private void ShowError(Exception error) => _status.Text = "No se completó la operación: " + error.Message;
    private static Entry Input(string text) => new() { Text = text, FontSize = 13, TextColor = Ink, BackgroundColor = Colors.White, MinimumHeightRequest = 36 };
    private static Picker Choice(string title) => new() { Title = title, FontSize = 13, TextColor = Ink, BackgroundColor = Colors.White, MinimumHeightRequest = 38 };
    private static Label Text(string value, double size) => new() { Text = value, FontSize = size, TextColor = size <= 12 ? Muted : Ink, VerticalTextAlignment = TextAlignment.Center };
    private static View Field(string label, View input) => new VerticalStackLayout { Spacing = 2, Children = { Text(label, 12), input } };
    private static Button Action(string title, Func<Task> action)
    {
        var button = new Button { Text = title, FontSize = 12, BackgroundColor = Teal, TextColor = Colors.White, Padding = new Thickness(10, 7), CornerRadius = 5, MinimumHeightRequest = 34 };
        button.Clicked += async (_, _) => await action(); return button;
    }
    private static void Place(Grid grid, View view, int column, int row) { Grid.SetColumn(view, column); Grid.SetRow(view, row); grid.Children.Add(view); }
    private sealed record DiagnosticRow(string[] Cells, string Details);
    private sealed class DataRowView : Grid
    {
        private readonly Label[] _labels;
        public DataRowView(string[] columns, bool header = false)
        {
            Padding = new Thickness(8, 5); ColumnSpacing = 8; MinimumHeightRequest = 34; BackgroundColor = Color.FromArgb(header ? "#EAF0F6" : "#FFFFFF");
            _labels = new Label[columns.Length];
            for (var i = 0; i < columns.Length; i++)
            {
                ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star)); var label = Text(header ? columns[i] : "", 12); label.LineBreakMode = LineBreakMode.TailTruncation; label.MaxLines = 1;
                if (header) label.FontAttributes = FontAttributes.Bold; _labels[i] = label; Place(this, label, i, 0);
            }
        }
        protected override void OnBindingContextChanged()
        {
            base.OnBindingContextChanged(); if (BindingContext is not DiagnosticRow row) return;
            for (var i = 0; i < _labels.Length; i++) _labels[i].Text = i < row.Cells.Length ? row.Cells[i] : "";
        }
    }
}
