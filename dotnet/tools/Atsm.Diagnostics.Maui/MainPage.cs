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
using Atsm.Plugin.Sysmex.Urinalysis;
using Atsm.Runtime.Tracing;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Dispatching;
using Microsoft.Maui.Layouts;
using static Atsm.Diagnostics.Maui.Globals;

namespace Atsm.Diagnostics.Maui;

public sealed class MainPage : ContentPage
{
    private static readonly Color Ink = Color.FromArgb("#1E293B"), Muted = Color.FromArgb("#52677E"), Teal = Color.FromArgb("#0F766E");
    private readonly IDiagnosticPlatformServices _platform;
    private readonly DiagnosticCatalog _catalog = new();
    private static readonly JsonSerializerOptions DetailJson = new(TraceJson.Options) { WriteIndented = true };
    private readonly Picker _configurations = Choice(T("Source.Configuration")), _recorder = Choice(T("Source.Recorder")), _profiles = Choice(T("Source.Plugin")), _formats = Choice(T("Source.Format")), _transport = Choice(T("Live.Transport")), _mode = Choice(T("Live.Mode"));
    private readonly Entry _host = Input("127.0.0.1"), _port = Input(""), _agentPort = Input("p1"), _controller = Input(Environment.MachineName);
    private readonly Entry _query = Input(""), _response = Input(""), _offset = Input("-03:00"), _password = Input("");
    private readonly CheckBox _tls = new() { WidthRequest = 24, HeightRequest = 28 }, _hex = new() { WidthRequest = 24, HeightRequest = 28 },
        _interpret = new() { IsChecked = true, WidthRequest = 24, HeightRequest = 28 }, _group = new() { IsChecked = true, WidthRequest = 24, HeightRequest = 28 };
    private readonly Label _certificate = Text(T("Live.NoCertificate"), 12), _title = Text(T("View.NoCase"), 21), _summary = Text(T("View.Prompt"), 12), _status = Text(T("View.Ready"), 12);
    private readonly Button _open, _save, _connect, _stop;
    private readonly VerticalStackLayout _interface = new() { Spacing = 8 }, _source = new() { Spacing = 8 };
    private readonly DiagnosticTable _table;
    private readonly Editor _detail = new() { IsReadOnly = true, Text = T("View.DetailPrompt"), TextColor = Color.FromArgb("#D7E7EF"), BackgroundColor = Color.FromArgb("#132632"), FontSize = 12, FontFamily = "Consolas" };
    private readonly Grid _body = new(), _workspace = new();
    private readonly ScrollView _sourceScroll;
    private readonly List<Button> _tabButtons = [];
    private readonly IDispatcherTimer _refresh;
    private readonly string[] _tabs = [T("View.Wire"), T("View.Messages"), T("View.Observations"), T("View.Findings"), T("View.Quality"), T("View.Events"), T("View.Catalog"), T("View.Communication")];
    private readonly DiagnosticInputFormat[] _inputFormats = [DiagnosticInputFormat.Auto, DiagnosticInputFormat.Export, DiagnosticInputFormat.AstmMessage, DiagnosticInputFormat.Hl7Message, DiagnosticInputFormat.SysmexLog, DiagnosticInputFormat.LegacyLog, DiagnosticInputFormat.WireBytes, DiagnosticInputFormat.HexBytes, DiagnosticInputFormat.AtsmTrace];
    private DiagnosticCase? _case;
    private DiagnosticAnalysis? _analysis;
    private DiagnosticTraceSink? _capture;
    private CancellationTokenSource? _liveStop;
    private TaskCompletionSource? _captureFinished;
    private byte[]? _certificateBytes;
    private bool _ready, _settingsVisible, _selfTestStarted, _selecting;
    private byte[]? _sourceBytes;
    private string? _sourceName;
    private string? _detectedProfile;
    private string? _protocolNotice;
    private int _tab = 2, _lastCount = -1;
    public bool IsCapturing => _liveStop is not null;
    private DiagnosticInstrument SelectedInstrument => _catalog.Instruments[_profiles.SelectedIndex];
    private bool HasAutomaticInterpretation => SelectedInstrument.Id == SysmexUrinalysisPlugin.PluginId;
    private bool AutomaticInterpretation => HasAutomaticInterpretation && _configurations.SelectedIndex == 0;
    private DiagnosticProfile Profile => AutomaticInterpretation ? _catalog.Resolve(_detectedProfile ?? SysmexUrinalysisPlugin.WithChemistryProfile)
        : SelectedInstrument.Profiles[_configurations.SelectedIndex - (HasAutomaticInterpretation ? 1 : 0)];

    public MainPage(IDiagnosticPlatformServices platform)
    {
        _platform = platform; _table = new(platform); BackgroundColor = Color.FromArgb("#F3F6FA");
        _open = Action(T("Source.Open"), async () => await OpenAsync()); _save = Action(T("Source.Save"), async () => await SaveAsync());
        _connect = Action(T("Live.Connect"), async () => await ConnectAsync()); _stop = Action(T("Live.Stop"), async () => await StopCaptureAsync()); _stop.IsEnabled = false;
        _password.IsPassword = true;
        foreach (var instrument in _catalog.Instruments) _profiles.Items.Add(instrument.DisplayName);
        foreach (var format in _inputFormats) _formats.Items.Add(DiagnosticText.Text("Format." + format)); _formats.SelectedIndex = 0;
        foreach (var key in new[] { "Source.RecorderAuto", "Source.RecorderHost", "Source.RecorderInstrument" }) _recorder.Items.Add(T(key)); _recorder.SelectedIndex = 0;
        _recorder.SelectedIndexChanged += (_, _) => ReopenLog();
        foreach (var value in new[] { T("Live.TcpClient"), T("Live.TcpServer"), T("Live.Tunnel") }) _transport.Items.Add(value);
        foreach (var value in new[] { T("Live.Capture"), T("Live.Simulate") }) _mode.Items.Add(value);
        _transport.SelectedIndex = _mode.SelectedIndex = 0;
        _profiles.SelectedIndexChanged += (_, _) => ConfigureInstrument(); _configurations.SelectedIndexChanged += (_, _) => SetProfile(); _profiles.SelectedIndex = 0;
        _source.Add(Text(T("Source.Title"), 12)); _source.Add(Field(T("Source.Plugin"), _profiles)); _source.Add(Field(T("Source.Configuration"), _configurations));
        _source.Add(Field(T("Source.Format"), _formats)); _source.Add(Field(T("Source.Recorder"), _recorder));
        _source.Add(new HorizontalStackLayout { Spacing = 8, Children = { _open, _save } });
        _source.Add(Text(T("Source.Custody"), 12));
        _interface.Add(Text(T("Live.Title"), 12)); _interface.Add(Field(T("Live.Transport"), _transport));
        _interface.Add(Field(T("Live.Host"), _host));
        _interface.Add(Field(T("Live.Port"), _port)); _interface.Add(Field(T("Live.AgentPort"), _agentPort));
        _interface.Add(Field(T("Live.Controller"), _controller)); _interface.Add(Option(T("Live.Tls"), _tls));
        _interface.Add(Action(T("Live.Certificate"), async () => await ChooseCertificateAsync())); _interface.Add(_certificate); _interface.Add(Field(T("Live.Password"), _password));
        _interface.Add(Field(T("Live.Mode"), _mode)); _interface.Add(Text(T("Live.Ownership"), 12));
        _interface.Add(Field(T("Live.QueryBudget"), _query)); _interface.Add(Field(T("Live.ResponseBudget"), _response));
        _interface.Add(new HorizontalStackLayout { Spacing = 8, Children = { _connect, _stop } });
        _source.Add(Action(T("View.Source"), () => { _settingsVisible = false; LayoutPanels(); return Task.CompletedTask; })); _source.Add(Field(T("Source.Offset"), _offset)); _source.Add(_interface);
        _sourceScroll = new ScrollView { Content = _source, Padding = 16, BackgroundColor = Colors.White };

        var nav = new HorizontalStackLayout { Spacing = 5 };
        for (var i = 0; i < _tabs.Length; i++) { var selected = i; var button = Action(_tabs[i], () => { _tab = selected; ShowTable(); return Task.CompletedTask; }); nav.Add(button); _tabButtons.Add(button); }
        var controls = new FlexLayout { Direction = FlexDirection.Row, Wrap = FlexWrap.Wrap, AlignItems = FlexAlignItems.Center,
            Children = { Action(T("View.Source"), () => { _settingsVisible = !_settingsVisible; LayoutPanels(); return Task.CompletedTask; }),
                Action(T("View.Demo"), () => { if (!IsCapturing) LoadDemo(); return Task.CompletedTask; }),
                Option(T("View.Interpret"), _interpret),
                Option(T("View.Group"), _group),
                Option(T("View.Hex"), _hex) } };
        _hex.CheckedChanged += (_, _) => { if (_ready) ShowTable(); };
        _interpret.CheckedChanged += (_, _) => { if (_ready) ShowTable(); };
        _group.CheckedChanged += (_, _) => { if (_ready) ShowTable(); };
        var summary = new VerticalStackLayout { Spacing = 6, Padding = 14, BackgroundColor = Colors.White, Children = { _title, _summary, controls } };
        _workspace.RowDefinitions = new RowDefinitionCollection(new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star), new RowDefinition(150));
        Place(_workspace, summary, 0, 0); Place(_workspace, new ScrollView { Orientation = ScrollOrientation.Horizontal, Content = nav }, 0, 1);
        Place(_workspace, _table, 0, 2); Place(_workspace, _detail, 0, 3);
        _table.DetailSelected += detail => _detail.Text = detail.Length <= 65536 ? detail : detail[..65536] + "\n" + T("View.DetailLimit");
        _body.ColumnDefinitions = new ColumnDefinitionCollection(new ColumnDefinition(330), new ColumnDefinition(GridLength.Star)); _body.ColumnSpacing = 14; _body.Padding = 14;
        Place(_body, _sourceScroll, 0, 0); Place(_body, _workspace, 1, 0);
        var banner = new VerticalStackLayout { Padding = new Thickness(22, 12), BackgroundColor = Color.FromArgb("#12313E"), Children = { new Label { Text = T("App.Banner"), FontSize = 25, TextColor = Colors.White }, new Label { Text = T("App.Subtitle"), FontSize = 12, TextColor = Color.FromArgb("#BBD0DA") } } };
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
            Dispatcher.DispatchDelayed(TimeSpan.FromSeconds(1), async () =>
            {
                try { if (Handler?.PlatformView is null) throw new InvalidOperationException("Window not realized."); _platform.EnsureWindowReady(); await SelfTestAsync(); File.WriteAllText(args[selfTest + 1], "PASS: MAUI window, file-dialog-owner, private-runtime, instruments=8, profiles=14, family-auto-and-manual, quality-median-sd-with-originals, qualitative-values-and-communicated-count, ordinal-crosses-and-bounds, unperformed-column, selectable-title-and-table, interpreted-default, grouped-default, auto-columns-left-aligned, eight-views-raw-and-interpreted, uf-binary-framing, chronological-blocks, duration-format, communication-statistics, CFX=3, protected-case-roundtrip, QC=6"); Environment.Exit(0); }
                catch (Exception error) { File.WriteAllText(args[selfTest + 1], "FAIL: " + error.GetType().Name + (error.Message.StartsWith("ATSM-", StringComparison.Ordinal) ? " " + error.Message : "")); Environment.Exit(1); }
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
    private void ConfigureInstrument()
    {
        if (_profiles.SelectedIndex < 0 || _selecting) return;
        _selecting = true; _configurations.Items.Clear();
        _detectedProfile = null;
        if (HasAutomaticInterpretation) _configurations.Items.Add(T("Source.AutomaticInterpretation"));
        foreach (var profile in _catalog.Instruments[_profiles.SelectedIndex].Profiles) _configurations.Items.Add(profile.Configuration);
        _configurations.SelectedIndex = 0; _selecting = false; SetProfile();
    }
    private void SetProfile()
    {
        if (!_ready || _selecting || _profiles.SelectedIndex < 0 || _configurations.SelectedIndex < 0) return;
        _interface.IsVisible = Profile.CanConnect;
        _mode.IsEnabled = Profile.CanSimulateHost && !AutomaticInterpretation; if (!_mode.IsEnabled) _mode.SelectedIndex = 0;
        _connect.IsEnabled = Profile.CanConnect && !IsCapturing;
        if (_case is not null && !IsCapturing) Analyze();
    }
    private void SelectProfile(string id)
    {
        var model = _catalog.Instruments.Single(instrument => instrument.Profiles.Any(p => p.Id == id));
        _selecting = true; _profiles.SelectedIndex = _catalog.Instruments.ToList().IndexOf(model);
        _configurations.Items.Clear(); if (HasAutomaticInterpretation) _configurations.Items.Add(T("Source.AutomaticInterpretation"));
        foreach (var profile in model.Profiles) _configurations.Items.Add(profile.Configuration);
        _detectedProfile = id;
        _configurations.SelectedIndex = HasAutomaticInterpretation ? 0 : model.Profiles.ToList().FindIndex(p => p.Id == id); _selecting = false;
        _interface.IsVisible = Profile.CanConnect; _connect.IsEnabled = Profile.CanConnect && !IsCapturing;
        _mode.IsEnabled = Profile.CanSimulateHost && !AutomaticInterpretation; if (!_mode.IsEnabled) _mode.SelectedIndex = 0;
    }
    private RecordedBy? Recorder => _recorder.SelectedIndex switch { 1 => RecordedBy.Host, 2 => RecordedBy.Instrument, _ => null };
    private TimeSpan LogOffset()
    {
        if (!TimeSpan.TryParse((_offset.Text ?? "").TrimStart('+'), CultureInfo.InvariantCulture, out var offset) || Math.Abs(offset.TotalHours) > 14) throw new ArgumentException(T("Validation.Offset"));
        return offset;
    }
    private void ReopenLog()
    {
        if (!_ready || IsCapturing || _sourceBytes is null || _case?.Header.Properties?.GetValueOrDefault("inputFormat") is not ("SysmexLog" or "LegacyLog")) return;
        try { _case = DiagnosticReader.Read(_sourceBytes, Enum.Parse<DiagnosticInputFormat>(_case.Header.Properties["inputFormat"]), Profile, _sourceName!, LogOffset(), Recorder); Analyze(); }
        catch (Exception error) { ShowError(error); }
    }
    private DiagnosticTiming Timing(bool live = false)
    {
        TimeSpan Budget(Entry input)
        {
            if (!live || !Profile.IsHl7) return TimeSpan.Zero;
            if (!double.TryParse(input.Text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0 || seconds > 86400) throw new ArgumentException(T("Validation.Hl7Timing"));
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
            _sourceBytes = null; _sourceName = null;
            await using var stream = await input.OpenReadAsync();
            if (Path.GetExtension(input.Name).Equals(".atsmcase", StringComparison.OrdinalIgnoreCase))
            {
                var loaded = ProtectedCaseJournal.Read(stream, _platform.CaseProtection);
                if (loaded.Header.Profile is not { } id || !_catalog.Profiles.Any(p => p.Id == id)) throw new InvalidDataException(T("Validation.Profile"));
                SelectProfile(id); _case = loaded;
                _status.Text = T("View.FileRead");
            }
            else
            {
                var bytes = await ReadBoundedAsync(stream); _sourceBytes = bytes; _sourceName = input.Name;
                var format = _inputFormats[_formats.SelectedIndex];
                var detected = DiagnosticInputDetector.Detect(bytes, Profile, _catalog);
                if (format == DiagnosticInputFormat.Auto)
                {
                    if (!detected.Recognized) throw new InvalidDataException(DiagnosticText.Text(detected.Candidates.Count == 0 ? "Detection.Unknown" : "Detection.Ambiguous"));
                    format = detected.Format;
                    if (detected.ProfileId is { } profileId && !(HasAutomaticInterpretation && !AutomaticInterpretation)) SelectProfile(profileId);
                }
                if (format == DiagnosticInputFormat.ProtectedCase)
                {
                    using var protectedInput = new MemoryStream(bytes); var loaded = ProtectedCaseJournal.Read(protectedInput, _platform.CaseProtection);
                    if (loaded.Header.Profile is not { } id || !_catalog.Profiles.Any(p => p.Id == id)) throw new InvalidDataException(T("Validation.Profile"));
                    SelectProfile(id); _case = loaded;
                }
                else _case = DiagnosticReader.Read(bytes, format, Profile, input.Name, LogOffset(), Recorder);
                _status.Text = DiagnosticText.Format(_inputFormats[_formats.SelectedIndex] == DiagnosticInputFormat.Auto ? "Detection.Success" : "Detection.Selected", DiagnosticText.Text("Format." + format), Profile.DisplayName);
            }
            Analyze();
            if (!_case.IsComplete) _status.Text = T("View.Incomplete");
        }
        catch (Exception error) { ShowError(error); }
    }
    private static async Task<byte[]> ReadBoundedAsync(Stream stream)
    {
        using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer)) > 0) { if (output.Length + count > DiagnosticReader.MaxInputBytes) throw new InvalidDataException(T("Validation.InputLimit")); output.Write(buffer, 0, count); }
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
            _status.Text = F("View.Saved", destination.DisplayName);
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
            if (!int.TryParse(_port.Text, out var port) || port is < 1 or > 65535) throw new ArgumentException(T("Validation.Port"));
            var timing = Timing(live: true); var selected = Profile; var host = (_host.Text ?? "").Trim(); var controller = (_controller.Text ?? "").Trim(); var agentPort = (_agentPort.Text ?? "").Trim();
            if (host.Length == 0 || controller.Length == 0) throw new ArgumentException(T("Validation.Host"));
            var transport = _transport.SelectedIndex; var simulate = _mode.SelectedIndex == 1; var tls = _tls.IsChecked;
            if (_certificateBytes is not null) certificate = X509CertificateLoader.LoadPkcs12(_certificateBytes, _password.Text, X509KeyStorageFlags.EphemeralKeySet);
            if (tls && transport == 1 && certificate is null) throw new ArgumentException(T("Validation.Certificate"));
            var endpoint = new TcpEndpointOptions(host, port, tls ? TlsMode.Tls : TlsMode.Plain) { ClientCertificates = certificate is null ? null : new X509CertificateCollection { certificate }, ServerCertificate = certificate };
            var destination = await _platform.PickCaseDestinationAsync(); if (destination is null) return;
            var header = new TraceHeader(Guid.CreateVersion7(), transport == 2 ? "SerialTunnel" : "TCP", "diagnostic", selected.Id, DateTimeOffset.UtcNow,
                new Dictionary<string, string> { ["protocol"] = selected.Protocol, ["mode"] = simulate ? "diagnostic-host" : "capture-no-responses", ["protection"] = "Windows DPAPI CurrentUser" });
            journal = new(destination.Create(), _platform.CaseProtection, header); _capture = new(header, journal); _case = new(header, []); _lastCount = -1;
            _capture.EnsureReady(); _liveStop = new(); _captureFinished = new(TaskCreationOptions.RunContinuationsAsynchronously); SetBusy(true); _refresh.Start();
            _status.Text = T("Live.Opening"); var token = _liveStop.Token;
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
                Dispatcher.Dispatch(() => _status.Text = simulate ? T("Live.Simulating") : T("Live.Connected"));
                return channel;
            }, simulate, controller, token));
            _status.Text = T("Live.Closed");
        }
        catch (OperationCanceledException) { _status.Text = T("Live.Stopped"); }
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
        _profiles.IsEnabled = _configurations.IsEnabled = _recorder.IsEnabled = _formats.IsEnabled = _open.IsEnabled = _save.IsEnabled = !busy;
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
            if (AutomaticInterpretation)
            {
                var detected = _catalog.ResolveInterpretation(_case, Profile); _detectedProfile = detected.Id;
                _case = _case with { Header = _case.Header with { Profile = detected.Id } };
            }
            _case = DiagnosticReader.RevalidateProtocol(_case, Profile);
            _analysis = DiagnosticReader.Analyze(_case, Profile, Timing()); _title.Text = _case.Header.ImportedFrom ?? _case.Header.Source;
            if (_analysis.Diagnostics.FirstOrDefault(d => d.Code == "ATSM-DIAG-UNSUPPORTED-PROTOCOL") is { } protocolNotice)
                _status.Text = _protocolNotice = DiagnosticText.Finding(protocolNotice, Profile);
            else if (_status.Text == DiagnosticText.Text("Detection.Unsupported") || _status.Text == DiagnosticText.Text("Finding.UnsupportedBinary")
                || _status.Text == DiagnosticText.Text("Finding.ATSM-DIAG-UNSUPPORTED-PROTOCOL") || _status.Text == _protocolNotice)
            { _status.Text = T("View.FileRead"); _protocolNotice = null; }
            _summary.Text = F("View.Summary", _case.Entries.Count, _analysis.Messages.Count, _analysis.Observations.Count, _analysis.Diagnostics.Count);
            if (_analysis.Messages.OrderBy(m => m.FirstSequence).Select(m => m.Fields.GetValueOrDefault("device.identifier")).FirstOrDefault(id => !string.IsNullOrWhiteSpace(id)) is { } deviceIdentifier)
                _summary.Text += " · " + F("Source.DeviceIdentifier", deviceIdentifier);
            ShowTable();
        }
        catch (Exception error) { ShowError(error); }
    }
    private string Json<T>(T value) => JsonSerializer.Serialize(value, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)DetailJson.GetTypeInfo(typeof(T)));
    private static DiagnosticRow Row(string[] cells, string details, params string?[] key) => new(cells, details, DiagnosticGrouping.Key(key));
    private void ShowTable()
    {
        if (!_ready) return;
        var interpreted = _interpret.IsChecked;
        foreach (var (button, index) in _tabButtons.Select((b, i) => (b, i))) button.BackgroundColor = index == _tab ? Teal : Color.FromArgb("#38576B");
        string[] headers; IEnumerable<DiagnosticRow> rows;
        string D(string raw) => interpreted ? CaptureDirection(raw) : raw;
        string Detail(string meaning, string raw) => meaning + "\n\n" + DiagnosticText.Text("Original") + "\n" + raw;
        var unsupported = _analysis?.Diagnostics.Any(d => d.Code == "ATSM-DIAG-UNSUPPORTED-PROTOCOL") == true;
        switch (_tab)
        {
            case 0:
                headers = [T("Column.Sequence"), T("Column.Time"), T("Column.Direction"), T("Column.Bytes"), T("Column.Command")];
                var protocolMessages = (_analysis?.Messages ?? []).Where(m => m.Explanation is not null).ToLookup(m => m.FirstSequence);
                var protocolEvents = (_analysis?.Events ?? []).Where(e => e.Explanation is not null && e.Level == Atsm.Tracing.StackLevel.Link).ToLookup(e => e.Sequence);
                var binaryEntries = BinaryTracePresentation.DescribeEntries(_case?.Entries ?? [], _analysis?.Messages ?? [], Profile);
                var captureClock = _case is not null && CommunicationStatistics.HasCaptureClock(_case);
                rows = (_case?.Entries ?? []).Select(entry =>
                {
                    var meaning = unsupported && entry.Direction != TraceDirection.Event && (entry.Data.Length == 0 || entry.Data.Span.IndexOfAnyExcept((byte)0) >= 0)
                        ? DiagnosticText.Format("Message.Wire", entry.Data.Length) : ProtocolExplainer.Entry(entry, Profile);
                    if (binaryEntries.TryGetValue(entry.Sequence, out var binaryEntry)) meaning = binaryEntry.Meaning;
                    else if (Profile.TracePlugin is { } plugin)
                    {
                        var explanations = protocolMessages[entry.Sequence].Select(m => m.Explanation!).Concat(protocolEvents[entry.Sequence].Select(e => e.Explanation!)).ToArray();
                        if (explanations.Length > 0) meaning = string.Join(" · ", explanations.Select(e => DiagnosticText.Explanation(plugin, e)));
                    }
                    var raw = DiagnosticReader.DescribeBytes(entry.Data, _hex.IsChecked);
                    return Row([entry.Sequence.ToString(), DiagnosticDuration.Format(captureClock ? entry.Elapsed : null), D(entry.Direction.ToString()), entry.Data.Length.ToString(), interpreted ? meaning : entry.Code ?? raw],
                        Detail(meaning + (binaryEntry is not null ? "\n" + binaryEntry.Details : ""), "#" + entry.Sequence + "\n" + entry.Code + "\n" + entry.Detail + "\n" + raw), entry.Direction.ToString(), entry.Code, Convert.ToHexString(entry.Data.Span));
                }).ToArray();
                if (_group.IsChecked && Profile.TracePlugin is not null)
                { _table.SetGroups("wire:" + interpreted + ":blocks", headers, WireGroups(rows.ToArray()), 4); return; }
                break;
            case 1:
                headers = [T("Column.Sequence"), T("Column.Direction"), T("Column.Type"), T("Column.Content")];
                var observations = (_analysis?.Observations ?? []).ToLookup(o => o.Trace.LastSequence);
                rows = (_analysis?.Messages ?? []).Select(message =>
                {
                    var kind = ProtocolExplainer.MessageKind(message, Profile);
                    var meaning = ProtocolExplainer.MessageContent(message, Profile, observations[message.Sequence].ToArray());
                    return Row([message.Sequence.ToString(), D(message.Direction), interpreted ? kind : message.Type, ProtocolExplainer.SingleLine(interpreted ? meaning : message.Text)],
                        Detail(kind + "\n" + meaning + "\n" + DiagnosticText.Text("Evidence") + "\n" + (message.Explanation?.Text.Evidence ?? ProtocolExplainer.RecordEvidence(message.Text, Profile))
                            + "\n" + BinaryTracePresentation.DescribeMessage(message, Profile) + "\n" + string.Join("\n", message.Fields.Select(f => f.Key + ": " + f.Value)), message.Text), message.Direction, message.Type, message.Text);
                }).ToArray();
                var messageGroups = DiagnosticGrouping.Build(rows, _group.IsChecked, consecutive: true).Select(group =>
                {
                    if (!interpreted || group.Count == 1 || group.Children.Select(r => r.Cells[3]).Distinct(StringComparer.Ordinal).Count() == 1) return group;
                    var first = (_analysis?.Messages ?? []).First(m => m.Sequence.ToString() == group.Children[0].Cells[0]);
                    var summary = group.Summary(n => DiagnosticText.Format("Group.Values", n));
                    var content = ProtocolExplainer.MessageSummary(first, Profile);
                    if (ProtocolExplainer.MessageHasError(first, Profile, observations[first.Sequence].ToArray())) content = DiagnosticText.Format("Message.WithError", content);
                    var cells = summary.Cells.ToArray(); cells[3] = content;
                    return group with { SummaryRow = summary with { Cells = cells } };
                }).ToArray();
                _table.SetGroups("messages:" + interpreted + ":" + _group.IsChecked, headers, messageGroups, 3);
                return;
            case 2:
                headers = [T("Column.Specimen"), T("Column.Position"), T("Column.Test"), T("Column.Type"), T("Column.Value"), T("Column.Unit"), T("Column.Status")];
                rows = (_analysis?.Observations ?? []).Select(o => Row([o.Specimen.SpecimenId, o.Specimen.Position ?? "", o.Test.Vendor.Display ?? o.Test.Vendor.Code,
                    interpreted ? ProtocolExplainer.ResultKind(o, Profile) : o.ResultKind ?? "", interpreted ? ProtocolExplainer.ResultValue(o, Profile) : o.Value.RawText,
                    interpreted ? ProtocolExplainer.Unit(o, Profile) : o.Value.RawUnit ?? "",
                    interpreted ? ProtocolExplainer.Status(o, Profile) : o.InstrumentStatus + " " + string.Join(", ", o.Flags.Select(f => f.Code))], Json(o),
                    o.Specimen.SpecimenId, o.Test.Vendor.Code, o.ResultKind)); break;
            case 3:
                headers = interpreted ? [T("Column.Severity"), T("Column.Finding"), T("Column.Sequence")] : [T("Column.Severity"), T("Column.Code"), T("Column.Finding"), T("Column.Sequence")];
                rows = (_analysis?.Diagnostics ?? []).Select(finding =>
                {
                    var meaning = DiagnosticText.Finding(finding, Profile);
                    return Row(interpreted ? [DiagnosticText.Text("Severity." + finding.Severity), meaning, finding.Location ?? ""] : [DiagnosticText.Text("Severity." + finding.Severity), finding.Code, finding.Message, finding.Location ?? ""],
                        Detail(meaning + "\n" + DiagnosticText.FindingEvidence(finding, Profile), finding.Code + "\n" + finding.Location + "\n" + finding.Message), finding.Severity.ToString(), finding.Code, finding.Message);
                }); break;
            case 4:
                headers = [T("Quality.MaterialLot"), T("Column.Test"), T("Quality.DataType"), T("Quality.Unit"), T("Quality.Minimum"), T("Quality.MedianValue"), T("Quality.Maximum"), T("Quality.SD"), T("Quality.N"), T("Quality.Unperformed"), T("Quality.State")];
                var quality = QualityGroups();
                _table.SetGroups("quality:" + interpreted + ":" + _group.IsChecked, headers,
                    _group.IsChecked ? quality : DiagnosticGrouping.Build(quality.SelectMany(g => g.Children), false), 10);
                return;
            case 5:
                headers = interpreted ? [T("Column.Layer"), T("Column.Sequence"), T("Column.Detail")] : [T("Column.Layer"), T("Column.Sequence"), T("Column.Code"), T("Column.Detail")];
                rows = (_analysis?.Events ?? []).Select(item =>
                {
                    var meaning = item.Explanation is { } explanation && Profile.TracePlugin is { } tracePlugin ? DiagnosticText.Explanation(tracePlugin, explanation)
                        : item.Code == "lis02.message" && item.Detail is { } records
                        ? ProtocolExplainer.RecordNames(string.Join("\r", records.Select(code => code + "|")), Profile)
                        : DiagnosticText.Event(item.Code, item.Detail);
                    return Row(interpreted ? [DiagnosticText.Text("Level." + item.Level), item.Sequence.ToString(), meaning] : [DiagnosticText.Text("Level." + item.Level), item.Sequence.ToString(), item.Code, item.Detail ?? ""],
                        Detail(meaning, item.Level + "\n" + item.Code + "\n" + item.Detail), item.Level.ToString(), item.Direction.ToString(), item.Code, item.Detail, meaning);
                }); break;
            case 7:
                headers = [T("Timing.Metric"), T("Column.Direction"), T("Timing.N"), T("Timing.Minimum"), T("Timing.Median"), T("Timing.P95"), T("Timing.Maximum"), T("Timing.Pending")];
                _table.SetGroups("communication", headers, CommunicationGroups(), 0); return;
            default:
                headers = [T("Column.Profile"), T("Column.Configuration")];
                rows = _catalog.Instruments.Select(instrument => Row([instrument.DisplayName, string.Join(" · ", instrument.Profiles.Select(p => p.Configuration))],
                    string.Join("\n", instrument.Profiles.Select(p => p.DisplayName)), instrument.Id)); break;
        }
        var flexible = _tab switch { 0 => 4, 1 => 3, 2 => 6, 3 => interpreted ? 1 : 2, 4 => 2, 5 => interpreted ? 2 : 3, _ => 1 };
        _table.SetRows(_tab + ":" + interpreted, headers, rows, _group.IsChecked && _tab != 6, flexible, consecutive: _tab is 0 or 5);
    }
    private string CaptureDirection(string direction)
    {
        if (direction == "All") return T("Timing.All");
        if (direction == nameof(TraceDirection.Event)) return DiagnosticText.Direction(direction);
        if (_case?.Header.Properties?.GetValueOrDefault("directionUncertain") != "true") return DiagnosticText.Direction(direction);
        var reversed = _case.Header.Properties.GetValueOrDefault("recordedBy") == nameof(RecordedBy.Instrument);
        var read = (direction == nameof(TraceDirection.Received)) != reversed;
        return T(read ? "Timing.CaptureRead" : "Timing.CaptureWrite");
    }
    private IReadOnlyList<DiagnosticRowGroup> WireGroups(DiagnosticRow[] rows)
    {
        var entries = _case!.Entries; var messages = _analysis!.Messages;
        var positions = entries.Select((e, i) => (e.Sequence, i)).ToDictionary(p => p.Sequence, p => p.i);
        var ranges = messages.Select(m => (Message: m, First: positions[m.FirstSequence], Last: positions[m.Sequence])).ToArray();
        if (ranges.Zip(ranges.Skip(1)).Any(p => p.First.Last >= p.Second.First)) return DiagnosticGrouping.Build(rows, true, consecutive: true);
        var groups = new List<DiagnosticRowGroup>(); var cursor = 0; var index = 0;
        while (index < ranges.Length)
        {
            var first = ranges[index];
            while (cursor < first.First)
            { groups.Add(new("wire-entry:" + entries[cursor].Sequence, [rows[cursor]])); cursor++; }
            var end = index;
            while (end + 1 < ranges.Length && ranges[end + 1].First == ranges[end].Last + 1
                && ranges[end + 1].Message.Direction == first.Message.Direction && ranges[end + 1].Message.Text == first.Message.Text) end++;
            var last = ranges[end]; var key = "wire-block:" + entries[first.First].Sequence;
            var children = rows[first.First..(last.Last + 1)]; var cells = rows[first.First].Cells.ToArray();
            cells[0] = entries[first.First].Sequence + " → " + entries[last.Last].Sequence;
            cells[3] = entries.Skip(first.First).Take(last.Last - first.First + 1).Sum(e => (long)e.Data.Length).ToString(CultureInfo.CurrentCulture);
            var meaning = ProtocolExplainer.MessageContent(first.Message, Profile, _analysis.Observations);
            cells[4] = _interpret.IsChecked ? meaning : first.Message.Text;
            var complete = entries.Skip(first.First).Take(first.Last - first.First + 1).SelectMany(e => e.Data.ToArray()).ToArray();
            var details = meaning + "\n" + F("Wire.CompleteBlock", entries[first.First].Sequence, entries[last.Last].Sequence, end - index + 1)
                + "\n" + BinaryTracePresentation.DescribeMessage(first.Message, Profile)
                + "\n\n" + DiagnosticText.Text("Original") + "\n" + DiagnosticReader.DescribeBytes(complete, _hex.IsChecked);
            groups.Add(new(key, children) { Repetitions = end - index + 1, SummaryRow = new(cells, details, key) });
            cursor = last.Last + 1; index = end + 1;
        }
        while (cursor < rows.Length) { groups.Add(new("wire-entry:" + entries[cursor].Sequence, [rows[cursor]])); cursor++; }
        return groups;
    }
    private IReadOnlyList<DiagnosticRowGroup> CommunicationGroups()
    {
        static string Time(TimeSpan? value) => DiagnosticDuration.Format(value);
        var groups = new List<DiagnosticRowGroup>();
        if (_analysis is null) return groups;
        foreach (var item in CommunicationStatistics.Build(_analysis, _case is not null && CommunicationStatistics.HasCaptureClock(_case)))
        {
            var stats = item.Distribution; var metric = T("Timing." + item.Code); var direction = CaptureDirection(item.Direction);
            if (item.ExchangeCode is { } exchange && Profile.TracePlugin is { } plugin
                && _analysis.Timings.FirstOrDefault(t => t.Code == exchange && t.Kind.ToString() == item.Code)?.Explanation is { } explanation)
                metric += " · " + DiagnosticText.Explanation(plugin, explanation);
            var key = DiagnosticGrouping.Key(item.Code, item.Direction, item.ExchangeCode);
            var detail = T("Timing.Detail") + "\n" + F("Timing.Flow", item.Reads, item.Bytes, item.InvalidIntervals)
                + (item.ExchangeCode is { } code ? "\n" + code : "") + (item.Pending > 0 ? "\n" + T("Timing.NoEnd") : "")
                + (item.ClockAvailable ? "" : "\n" + T("Timing.NoClock"));
            var children = item.Intervals.Select(i => new DiagnosticRow([i.FirstSequence + " → " + i.LastSequence, direction, "1", "", Time(i.Duration), "", "", ""],
                F("Timing.Boundaries", i.FirstSequence, i.LastSequence, Time(i.Duration)) + "\n" + T("Timing.Detail"), key)).ToArray();
            var pending = item.Code is "ReadInterval" or "DirectionTurn" or "MessageReception" ? "—" : item.Pending.ToString(CultureInfo.CurrentCulture);
            groups.Add(new(key, children) { SummaryRow = new([metric, direction, stats.Count.ToString(CultureInfo.CurrentCulture), Time(stats.Minimum), Time(stats.Median), Time(stats.P95), Time(stats.Maximum), pending], detail, key)
                { Attention = item.InvalidIntervals > 0 } });
        }
        return groups;
    }
    private IReadOnlyList<DiagnosticRowGroup> QualityGroups()
    {
        static string Number(decimal? n) => n?.ToString("G6", CultureInfo.CurrentCulture) ?? "—";
        static string Approximate(double? n) => n?.ToString("G6", CultureInfo.CurrentCulture) ?? "—";
        string Frequency(QualityValueFrequency value) => F("Quality.Frequency", value.Value.Length == 0
            ? T(value.NotPerformed ? "Quality.Unperformed" : "Quality.NoValue")
            : value.Value + (value.NotPerformed ? " · " + T("Quality.Unperformed") : ""), value.Count);
        var groups = new List<DiagnosticRowGroup>();
        var knowledge = ((object?)Profile.TracePlugin ?? Profile.AstmPlugin ?? (object?)Profile.ContentPlugin) as Atsm.Plugins.IInstrumentValueKnowledge;
        foreach (var series in QualitySeriesBuilder.Build(_analysis?.Observations ?? [], knowledge))
        {
            var material = series.Material + (string.IsNullOrWhiteSpace(series.Lot) ? " / " + T("Quality.NoLot") : " / " + series.Lot)
                + (string.IsNullOrWhiteSpace(series.Level) ? "" : " / " + series.Level);
            var described = series.Records.FirstOrDefault(o => knowledge?.DescribeValue(o) is not null) ?? series.Records[0];
            var kind = _interpret.IsChecked ? ProtocolExplainer.ResultKind(described, Profile) : described.ResultKind ?? "";
            var unit = _interpret.IsChecked ? ProtocolExplainer.Unit(described, Profile) : described.Value.RawUnit ?? "";
            var signals = new List<string>();
            if (series.PotentialOutliers.Count > 0) signals.Add(F("Quality.Outliers", series.PotentialOutliers.Count));
            if (series.Flagged > 0) signals.Add(F("Quality.Flagged", series.Flagged));
            if (series.Repeated > 0) signals.Add(F("Quality.Repeated", series.Repeated));
            if (series.Range.Incomparable) signals.Add(T("Quality.Unordered"));
            var reported = series.Records.Sum(o => o.Quality?.Statistics.Count ?? 0);
            if (reported > 0) signals.Add(F("Quality.Reported", reported));
            var state = QualityStatus.Summarize(series, Profile, _interpret.IsChecked);
            var detail = string.Join("\n", material, series.Test + " · " + kind, T("Quality.Unit") + ": " + unit, T("Quality.State") + ": " + state,
                T("Quality.Minimum") + ": " + series.Minimum?.ToString("G29"), T("Quality.Median") + ": " + series.Median?.ToString("G29"),
                T("Quality.Maximum") + ": " + series.Maximum?.ToString("G29"), T("Quality.SD") + ": " + series.StandardDeviation?.ToString("G17"),
                T("Quality.Mean") + ": " + series.Mean?.ToString("G17"), T("Quality.N") + ": " + series.MeasurementCount,
                T("Quality.NumericCount") + ": " + series.NumericCount,
                T("Quality.Unperformed") + ": " + series.NotPerformed,
                T("Quality.ReportedRange") + ": " + series.Range.Minimum + " … " + series.Range.Maximum,
                T("Quality.Distribution") + ": " + string.Join(" · ", series.ValueFrequencies.Select(Frequency)),
                F("Quality.Last", series.Last.Value.RawText, series.Last.ResultTime?.ToString("O") ?? "#" + series.Last.Trace.LastSequence),
                T("Quality.Method"), string.Join(" · ", signals));
            var children = series.Records.Select(o => new DiagnosticRow([material, series.Test,
                _interpret.IsChecked ? ProtocolExplainer.ResultKind(o, Profile) : o.ResultKind ?? "",
                _interpret.IsChecked ? ProtocolExplainer.Unit(o, Profile) : o.Value.RawUnit ?? "", "", string.IsNullOrWhiteSpace(o.Value.RawText)
                ? o.Value.Coded?.Display ?? o.Value.Coded?.Code ?? o.Value.RawText : _interpret.IsChecked ? ProtocolExplainer.ResultValue(o, Profile) : o.Value.RawText,
                "", "", "1", o.InstrumentStatusMeaning == InstrumentStatusMeaning.NotPerformed ? "1" : "",
                _interpret.IsChecked ? ProtocolExplainer.Status(o, Profile) : string.Join(" ", new[] { o.InstrumentStatus }.Concat(o.Flags.Select(f => f.Code))).Trim()],
                "#" + o.Trace.LastSequence + (o.ResultTime is { } time ? " · " + time.ToString("g") : "") + "\n" + Json(o), series.Key)
                { Attention = ProtocolExplainer.RequiresAttention(o, knowledge as Atsm.Plugins.IInstrumentFlagKnowledge) || series.PotentialOutliers.Contains(o.ObservationId) }).ToArray();
            var measuredValues = series.ValueFrequencies.Where(f => !f.NotPerformed && f.Value.Length > 0).ToArray();
            var modes = measuredValues.Where(f => f.Count == measuredValues.Max(v => v.Count)).ToArray();
            var displays = series.Records.Select(o => knowledge?.DescribeValue(o) is { DisplayText: not null } or { DisplayValue: not null }
                ? ProtocolExplainer.ResultValue(o, Profile) : null).Distinct(StringComparer.Ordinal).ToArray();
            var compactValue = _interpret.IsChecked && displays.Length == 1 ? displays[0] : null;
            var result = series.Range.Median ?? (series.NumericCount > 0 ? Number(series.Median)
                : series.Range.BoundedCount > 0 ? "—" : compactValue ?? (modes.Length == 1 ? modes[0].Value : modes.Length > 1 ? T("Quality.MixedResults") : "—"));
            groups.Add(new(series.Key, children) { SummaryRow = new([material, series.Test, kind, unit, series.Range.Minimum ?? "—", result, series.Range.Maximum ?? "—",
                Approximate(series.StandardDeviation), series.MeasurementCount.ToString(CultureInfo.CurrentCulture), series.NotPerformed.ToString(CultureInfo.CurrentCulture),
                state], detail, series.Key)
                { Attention = series.Flagged > 0 || series.PotentialOutliers.Count > 0 } });
        }
        foreach (var resource in _analysis?.QualityResources ?? [])
        {
            var row = Row([T("Quality.External"), "", T("Quality.Targets"), "", "", "", "", "", "", "", T("Quality.RequiresHost")], Json(resource), resource.SourceField);
            groups.Add(new(row.GroupKey, [row]));
        }
        return groups;
    }
    public void LoadDemo()
    {
        SelectProfile(CfxPlugin.CsvProfileId);
        var data = Encoding.UTF8.GetBytes("Well,Sample,Target,Cq,Fluor,Content,Cq Mean,Cq Std. Dev\r\nA01,CONTROL-SYN-1,TARGET-A,23.45,FAM,Pos-01,23.50,0.07\r\nA02,STANDARD-SYN-2,TARGET-A,28.12,FAM,Std-01,28.10,0.03\r\nA03,NTC-SYN-3,TARGET-B,Undetermined,HEX,NTC,Undetermined,\r\n");
        _case = DiagnosticReader.Read(data, DiagnosticInputFormat.Export, Profile, T("View.DemoTitle"), TimeSpan.FromHours(-3)); _tab = 4; Analyze();
        _status.Text = T("View.DemoStatus");
    }
    private async Task SelfTestAsync()
    {
        var runtime = Path.GetFullPath(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
        if (!runtime.StartsWith(Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("External runtime used.");
        LoadDemo(); var path = Path.Combine(Path.GetTempPath(), "atsm-maui-selftest-" + Guid.NewGuid().ToString("N") + ".atsmcase");
        try
        {
            using (var journal = new ProtectedCaseJournal(File.Create(path), _platform.CaseProtection, _case!.Header)) foreach (var entry in _case.Entries) journal.Append("entry", TraceLine.From(entry));
            using var input = File.OpenRead(path); var loaded = ProtectedCaseJournal.Read(input, _platform.CaseProtection); var analyzed = DiagnosticReader.Analyze(loaded, Profile, Timing());
            if (!_interpret.IsChecked || !_group.IsChecked || _catalog.Instruments.Count != 8 || _catalog.Instruments.Count(i => i.Id == SysmexUrinalysisPlugin.PluginId) != 1 || _catalog.Profiles.Count != 14 || analyzed.Observations.Count != 3 || analyzed.Observations.Sum(o => o.Quality?.Statistics.Count ?? 0) != 6) throw new InvalidDataException("ATSM-MAUI-SELFTEST");
            var quality = QualitySeriesBuilder.Build(analyzed.Observations);
            if (quality.Sum(s => s.NumericCount) != 2 || quality.Sum(s => s.MeasurementCount) != 3 || QualityGroups().Any(g => g.SummaryRow is null)) throw new InvalidDataException("ATSM-MAUI-QUALITY-SELFTEST");
            var unknownCq = QualityGroups().Single(g => g.Children.Any(r => r.Cells[5] == "Undetermined"));
            if (!unknownCq.SummaryRow!.Cells[5].Contains("Undetermined", StringComparison.Ordinal) || unknownCq.SummaryRow.Cells[8] != "1") throw new InvalidDataException("ATSM-MAUI-QUALITATIVE-SELFTEST");
            var normal = analyzed.Observations[0] with { Value = new("normal ") };
            _analysis = analyzed with { Observations = [normal, normal, normal, normal] };
            var normalSummary = QualityGroups().Single().SummaryRow!;
            if (normalSummary.Cells[5] != "normal" || normalSummary.Cells[8] != "4" || normalSummary.Cells[9] != "0") throw new InvalidDataException("ATSM-MAUI-QUALITATIVE-COUNT-SELFTEST");
            _analysis = analyzed with { Observations = [normal, normal, normal with { Value = new(""), InstrumentStatusMeaning = InstrumentStatusMeaning.NotPerformed }] };
            var mixedSummary = QualityGroups().Single().SummaryRow!;
            if (mixedSummary.Cells[5] != "normal" || mixedSummary.Cells[9] != "1" || mixedSummary.Cells[8] != "3") throw new InvalidDataException("ATSM-MAUI-UNPERFORMED-COLUMN-SELFTEST");
            _analysis = analyzed;
            if (Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains(Convert.ToBase64String(_case.Entries[0].Data.Span), StringComparison.Ordinal)) throw new InvalidDataException("ATSM-UNPROTECTED-CASE");
        }
        finally { File.Delete(path); }
        foreach (var interpreted in new[] { false, true })
        {
            _interpret.IsChecked = interpreted;
            for (_tab = 0; _tab < _tabs.Length; _tab++) { ShowTable(); await Task.Delay(100); _table.VerifyAutomaticLayout(); }
        }
        if (_title.Handler?.PlatformView is not Microsoft.UI.Xaml.Controls.TextBlock { IsTextSelectionEnabled: true })
            throw new InvalidOperationException("ATSM-MAUI-TEXT-NOT-SELECTABLE");
        SelectProfile(SysmexUrinalysisPlugin.WithChemistryProfile);
        _case = DiagnosticReader.Read("RQ02,SYN-0001      01\r\n"u8.ToArray(), DiagnosticInputFormat.WireBytes, Profile, "synthetic", TimeSpan.Zero); Analyze();
        if (!AutomaticInterpretation || Profile.Id != SysmexUrinalysisPlugin.Uc3500Profile || SelectedInstrument.Id != SysmexUrinalysisPlugin.PluginId)
            throw new InvalidDataException("ATSM-MAUI-AUTO-INTERPRETATION-SELFTEST");
        _configurations.SelectedIndex = 1;
        if (AutomaticInterpretation || Profile.TracePlugin is not null) throw new InvalidDataException("ATSM-MAUI-MANUAL-INTERPRETATION-SELFTEST");
        _configurations.SelectedIndex = 0;
        if (Profile.Id != SysmexUrinalysisPlugin.Uc3500Profile) throw new InvalidDataException("ATSM-MAUI-AUTO-RESTORE-SELFTEST");
        var saved = _analysis!;
        var cross = new InstrumentObservation(Guid.NewGuid(), new("synthetic"), new("CONTROL-SYN", Role: SpecimenRole.Control),
            CodeSet.Of(new("BLD", null, "urn:atsm:vendor:sysmex.uc3500")), new("2+"), null, [], "0", InstrumentStatusMeaning.Unknown,
            "QUALITATIVE", null, null, null, [], [], VendorIdentity.Empty, new(_case.Header.TraceId, 0, 0))
        { Quality = new(new(QualityMaterialKind.Control, "CONTROL-SYN", "LOT-SYN")) };
        _analysis = saved with { Observations = [cross, cross with { ObservationId = Guid.NewGuid(), Value = new("-") },
            cross with { ObservationId = Guid.NewGuid(), Value = new("-") }, cross with { ObservationId = Guid.NewGuid(), Value = new(""), InstrumentStatusMeaning = InstrumentStatusMeaning.NotPerformed }] };
        var crossSummary = QualityGroups().Single().SummaryRow!;
        if (crossSummary.Cells.Length != 11 || crossSummary.Cells[2] != "Semicuantitativo (cruces)" || crossSummary.Cells[4] != "0"
            || crossSummary.Cells[5] != "0" || crossSummary.Cells[6] != "2+" || crossSummary.Cells[7] != "—" || crossSummary.Cells[8] != "4" || crossSummary.Cells[9] != "1")
            throw new InvalidDataException("ATSM-MAUI-ORDINAL-SELFTEST");
        _analysis = saved with { Observations = [cross with { Quality = new(new(QualityMaterialKind.Control, "CONTROL-SYN")) }] };
        if (QualityGroups().Single().SummaryRow!.Cells[0] != "CONTROL-SYN / -")
            throw new InvalidDataException("ATSM-MAUI-NO-LOT-SELFTEST");
        foreach (var (comparison, minimum, maximum) in new[] { (">=", ">=50", "—"), ("<=", "—", "<=50") })
        {
            _analysis = saved with { Observations = [cross with { ResultKind = "CONCENTRATION", Value = new(comparison + "50", 50, comparison) }] };
            var bounded = QualityGroups().Single().SummaryRow!;
            if (bounded.Cells[2] != "Valor semicuantitativo" || bounded.Cells[4] != minimum || bounded.Cells[6] != maximum || bounded.Cells[7] != "—")
                throw new InvalidDataException("ATSM-MAUI-BOUNDED-SELFTEST");
        }
        _analysis = saved;
        foreach (var interpreted in new[] { false, true })
        {
            _interpret.IsChecked = interpreted;
            for (_tab = 0; _tab < _tabs.Length; _tab++) { ShowTable(); await Task.Delay(100); _table.VerifyAutomaticLayout(); }
        }
        _tab = 4; ShowTable();
        VerifySemanticPresentation();
        VerifyQualityPresentation();
        VerifyKnowledgePresentation();
        VerifyControlCommunication();
        await _table.VerifyExpansionKeepsScrollAsync(); ShowTable();
    }
    private void VerifyControlCommunication()
    {
        _interpret.IsChecked = true; SelectProfile(SysmexUrinalysisPlugin.WithChemistryProfile);
        var notice = new byte[266]; notice[0] = 1; notice[265] = 3;
        Encoding.ASCII.GetBytes("SYNTHETIC").CopyTo(notice.AsSpan(3)); Encoding.ASCII.GetBytes("Limpieza").CopyTo(notice.AsSpan(37));
        Encoding.ASCII.GetBytes("DBS_LANG_02215Necesidad").CopyTo(notice.AsSpan(87)); Encoding.ASCII.GetBytes("DBS_LANG_01745").CopyTo(notice.AsSpan(151));
        var envelope = new byte[308]; envelope[0] = 2; envelope[33] = 12; envelope[36] = 2; envelope[37] = 1; envelope[^1] = 3;
        Encoding.ASCII.GetBytes("SYNTHETIC-ID").CopyTo(envelope.AsSpan(1)); Encoding.ASCII.GetBytes("SYN-02").CopyTo(envelope.AsSpan(21));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(envelope.AsSpan(41), 262); notice.AsSpan(3, 262).CopyTo(envelope.AsSpan(45)); notice = envelope;
        var block = envelope[..46].Concat(new byte[102]).ToArray(); block[33] = 2; block[^1] = 3;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(block.AsSpan(41), 102);
        block[45] = 4;
        for (var i = 0; i < 7; i++)
        {
            var offset = 45 + 18 + i * 12; block[offset] = (byte)(i + 1);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(offset + 6), (ushort)(i < 4 ? 1541 + i : 0));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(offset + 8), (uint)(i < 4 ? 90 : 0));
        }
        _case = DiagnosticReader.Read(notice, DiagnosticInputFormat.Auto, Profile, "synthetic", TimeSpan.Zero);
        _case = _case with { Header = _case.Header with { Properties = new Dictionary<string, string>(_case.Header.Properties!) { ["timingSource"] = "synthetic" } } };
        _case = _case with { Entries = [new(0, TimeSpan.Zero, DateTimeOffset.UnixEpoch, TraceDirection.Received, notice[..100]),
            new(1, TimeSpan.FromMilliseconds(10), DateTimeOffset.UnixEpoch, TraceDirection.Received, notice[100..]),
            new(2, TimeSpan.FromMilliseconds(100), DateTimeOffset.UnixEpoch, TraceDirection.Received, block),
            new(3, TimeSpan.FromMilliseconds(110), DateTimeOffset.UnixEpoch, TraceDirection.Received, block)] };
        Analyze();
        if (Profile.Id != SysmexUrinalysisPlugin.UfBinaryProfile || _analysis!.Messages.Count != 3 || _analysis.Observations.Count != 1
            || ProtocolExplainer.ResultValue(_analysis.Observations[0], Profile) != "Limpieza requerida"
            || CaptureDirection("Received") != T("Timing.CaptureRead")) throw new InvalidDataException("ATSM-MAUI-UF-CONTROL-SELFTEST");
        var flow = CommunicationGroups().First();
        if (flow.SummaryRow!.Cells[2] != "3" || flow.SummaryRow.Cells[4] != "00:00:00.010" || flow.SummaryRow.Cells[6] != "00:00:00.090" || flow.Children.Count != 3)
            throw new InvalidDataException("ATSM-MAUI-COMMUNICATION-SELFTEST");
        _tab = 0; ShowTable(); _table.VerifyAutomaticLayout();
        var syntheticRows = _case.Entries.Select(e => new DiagnosticRow([e.Sequence.ToString(), DiagnosticDuration.Format(e.Elapsed), "R", e.Data.Length.ToString(), "SYN"], "SYN", "SYN")).ToArray();
        var blocks = WireGroups(syntheticRows);
        if (blocks.Count != 2 || blocks[0].Children.Count != 2 || blocks[1].Repetitions != 2 || blocks.SelectMany(b => b.Children).Count() != 4)
            throw new InvalidDataException("ATSM-MAUI-CHRONOLOGICAL-BLOCKS-SELFTEST");
        if (blocks[0].SummaryRow is not { } blockSummary || !blockSummary.Details.Contains(DiagnosticReader.DescribeBytes(notice, _hex.IsChecked), StringComparison.Ordinal)
            || !_summary.Text.Contains("SYNTHETIC-ID", StringComparison.Ordinal)) throw new InvalidDataException("ATSM-MAUI-COMPLETE-BLOCK-DETAIL-SELFTEST");
        var counterParts = BinaryTracePresentation.DescribeEntries(_case.Entries, _analysis.Messages, Profile);
        if (_analysis.Messages.Count(m => m.Type == "UF_COUNTERS") != 2 || !counterParts[2].Meaning.Contains("Contador 1: 1541", StringComparison.Ordinal)
            || counterParts[2].Meaning.Contains("ACK", StringComparison.Ordinal) || !blocks[1].SummaryRow!.Details.Contains("Contador 1: 1541", StringComparison.Ordinal))
            throw new InvalidDataException("ATSM-MAUI-BINARY-FIELD-CONTEXT-SELFTEST");
        _tab = 7; ShowTable(); _table.VerifyAutomaticLayout();
        _case = DiagnosticReader.Read("RQ02,SYN-0001      01\r\n"u8.ToArray(), DiagnosticInputFormat.Auto, Profile, "synthetic", TimeSpan.Zero); Analyze();
        if (Profile.Id != SysmexUrinalysisPlugin.Uc3500Profile)
            throw new InvalidDataException("ATSM-MAUI-UF-STALE-STATUS-SELFTEST");
        _tab = 7; ShowTable(); _table.VerifyAutomaticLayout();
    }
    private void VerifyKnowledgePresentation()
    {
        _interpret.IsChecked = true; SelectProfile(SysmexUrinalysisPlugin.WithChemistryProfile);
        var payload = "H|\\^&|||U-WAM\rP|1\rO|1|SYN-QC|||||||||Q||||||||||||||F\rR|1|^^^BACT-Info.^A^1^S|3^RAW|||N|||||||UF-5000\rL|1|N\r"u8.ToArray();
        _case = DiagnosticReader.Read(payload, DiagnosticInputFormat.AstmMessage, Profile, "synthetic", TimeSpan.Zero); Analyze();
        var group = QualityGroups().Single();
        if (group.SummaryRow!.Cells[2] != "Indicador de investigación" || group.SummaryRow.Cells[5] != "Posible mezcla grampositiva/gramnegativa"
            || group.SummaryRow.Cells[3] != "—" || group.SummaryRow.Cells[4] != "—" || group.Children.Single().Cells[5] != group.SummaryRow.Cells[5])
            throw new InvalidDataException("ATSM-MAUI-KNOWLEDGE-SELFTEST");
        _tab = 4; ShowTable(); _table.VerifyAutomaticLayout();
        _interpret.IsChecked = false;
        if (QualityGroups().Single().Children.Single().Cells[5] != "3") throw new InvalidDataException("ATSM-MAUI-KNOWLEDGE-RAW-SELFTEST");
        _interpret.IsChecked = true;
    }
    private void VerifySemanticPresentation()
    {
        _interpret.IsChecked = true; SelectProfile(SysmexUrinalysisPlugin.WithChemistryProfile);
        var payload = "H|\\^&|||U-WAM\rP|1\rO|1|SYN-QC|||||||||Q||||||||||||||F\rR|1|^^^SYN-IMAGE^A^1^IF|SYN.png^RAW|||N|||||||UF-5000\rL|1|N\r"u8.ToArray();
        _case = DiagnosticReader.Read(payload, DiagnosticInputFormat.AstmMessage, Profile, "synthetic", TimeSpan.Zero); Analyze();
        var observation = _analysis!.Observations.Single(); var summary = QualityGroups().Single().SummaryRow!;
        if (summary.Cells[2] != "Imagen" || summary.Cells[5] != "Imagen" || summary.Cells[3] != "—" || summary.Cells[10] != "Normal"
            || summary.Attention || ProtocolExplainer.Status(observation, Profile) != "Normal")
            throw new InvalidDataException("ATSM-MAUI-SEMANTIC-IMAGE-STATUS-SELFTEST");
        _tab = 4; ShowTable(); _table.VerifyAutomaticLayout();
        var query = "H|\\^&\rQ|1|SYN-123\rL|1|N\r"u8.ToArray();
        _case = DiagnosticReader.Read(query, DiagnosticInputFormat.AstmMessage, Profile, "synthetic", TimeSpan.Zero); Analyze();
        if (ProtocolExplainer.MessageContent(_analysis!.Messages.Single(), Profile, []) != "Consulta SYN-123 por número de muestra")
            throw new InvalidDataException("ATSM-MAUI-SEMANTIC-QUERY-SELFTEST");
        _tab = 1; ShowTable(); _table.VerifyAutomaticLayout();
        _case = DiagnosticReader.Read("2026/10/25 10:00:00.000 R 006{[STX][NUL][SOH][NUL][ACK][ETX]}\r\n"u8.ToArray(), DiagnosticInputFormat.Auto, Profile, "synthetic", TimeSpan.Zero); Analyze();
        if (_status.Text != DiagnosticText.Text("Finding.UnsupportedBinary") || _analysis!.Observations.Count != 0)
            throw new InvalidDataException("ATSM-MAUI-BINARY-CONTROL-SELFTEST");
        _case = DiagnosticReader.Read(payload, DiagnosticInputFormat.AstmMessage, Profile, "synthetic", TimeSpan.Zero); Analyze();
        if (_status.Text == DiagnosticText.Text("Finding.UnsupportedBinary") || _analysis!.Observations.Count != 1)
            throw new InvalidDataException("ATSM-MAUI-STALE-BINARY-STATUS-SELFTEST");
        _tab = 4; ShowTable();
    }
    private void VerifyQualityPresentation()
    {
        _interpret.IsChecked = true; SelectProfile(SysmexUrinalysisPlugin.WithChemistryProfile);
        var payload = "H|\\^&|||UF-5000\rP|1\rO|1||^^CONTROL-SYN^B||||||||Q||||||||||||||F\rR|1|^^^^RBC^1^^^|73.50^73.5^SYN^SYN^SYN|ch^||W|||||||\rL|1|N\r"u8.ToArray();
        _case = DiagnosticReader.Read(payload, DiagnosticInputFormat.AstmMessage, Profile, "synthetic", TimeSpan.Zero); Analyze();
        var group = QualityGroups().Single(); var row = group.SummaryRow!;
        if (row.Cells.Length != 11 || row.Cells[0] != "CONTROL-SYN / -" || row.Cells[2] != "Numérico" || row.Cells[3] != "ch"
            || row.Cells[10] != "Baja fiabilidad" || group.Children.Single().Cells[10] != "Baja fiabilidad" || !row.Attention)
            throw new InvalidDataException("ATSM-MAUI-QUALITY-TYPE-UNIT-STATE-SELFTEST");
        _tab = 4; ShowTable(); _table.VerifyAutomaticLayout();
        _tab = 2; ShowTable(); _table.VerifyAutomaticLayout();
        _tab = 4; ShowTable();
    }
    private void ShowError(Exception error)
    {
        var code = error.Message.StartsWith("ATSM-", StringComparison.Ordinal) ? error.Message : error.GetType().Name;
        _status.Text = F("App.Error", code); _detail.Text = error.Message;
        if (lang.TryGetValue("Error." + error.Message, out var explanation)) _status.Text = explanation;
        if (lang.Values.Contains(error.Message, StringComparer.Ordinal) || DiagnosticText.IsLocalized(error.Message)) _status.Text = error.Message;
    }
    private static Entry Input(string text) => new() { Text = text, FontSize = 13, TextColor = Ink, BackgroundColor = Colors.Transparent, MinimumHeightRequest = 36 };
    private static Picker Choice(string title) => new() { Title = title, FontSize = 13, TextColor = Ink, BackgroundColor = Colors.Transparent, MinimumHeightRequest = 38 };
    private static Label Text(string value, double size) => new() { Text = value, FontSize = size, TextColor = size <= 12 ? Muted : Ink, VerticalTextAlignment = TextAlignment.Center };
    private static View Field(string label, View input) => new VerticalStackLayout
    {
        Spacing = 4, Margin = new Thickness(0, 3),
        Children = { Text(label, 12), new Border { Content = input, Padding = new Thickness(8, 1), Stroke = Color.FromArgb("#B7C7D4"), StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 5 }, BackgroundColor = Color.FromArgb("#F7FAFC") } }
    };
    private static View Option(string label, CheckBox check)
    {
        check.Color = Teal; check.MinimumWidthRequest = 0; check.WidthRequest = 24; check.HeightRequest = 28;
        SemanticProperties.SetDescription(check, label);
        var caption = Text(label, 12); var toggle = new TapGestureRecognizer(); toggle.Tapped += (_, _) => check.IsChecked = !check.IsChecked; caption.GestureRecognizers.Add(toggle);
        var option = new Grid { ColumnDefinitions = [new(24), new(GridLength.Auto)], ColumnSpacing = 5, Margin = new Thickness(6, 2, 12, 2), VerticalOptions = LayoutOptions.Center };
        Place(option, check, 0, 0); Place(option, caption, 1, 0); return option;
    }
    private static Button Action(string title, Func<Task> action)
    {
        var button = new Button { Text = title, FontSize = 12, BackgroundColor = Teal, TextColor = Colors.White, Padding = new Thickness(10, 7), Margin = new Thickness(0, 1, 4, 1), CornerRadius = 5, MinimumHeightRequest = 34 };
        button.Clicked += async (_, _) => await action(); return button;
    }
    private static void Place(Grid grid, View view, int column, int row) { Grid.SetColumn(view, column); Grid.SetRow(view, row); grid.Children.Add(view); }
}
