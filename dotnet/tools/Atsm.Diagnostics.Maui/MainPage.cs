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
    private readonly Label _evidence = Text("", 12), _certificate = Text(T("Live.NoCertificate"), 12), _title = Text(T("View.NoCase"), 21), _summary = Text(T("View.Prompt"), 12), _status = Text(T("View.Ready"), 12);
    private readonly Button _open, _save, _connect, _stop;
    private readonly VerticalStackLayout _interface = new() { Spacing = 8 }, _source = new() { Spacing = 8 };
    private readonly DiagnosticTable _table;
    private readonly Editor _detail = new() { IsReadOnly = true, Text = T("View.DetailPrompt"), TextColor = Color.FromArgb("#D7E7EF"), BackgroundColor = Color.FromArgb("#132632"), FontSize = 12, FontFamily = "Consolas" };
    private readonly Grid _body = new(), _workspace = new();
    private readonly ScrollView _sourceScroll;
    private readonly List<Button> _tabButtons = [];
    private readonly IDispatcherTimer _refresh;
    private readonly string[] _tabs = [T("View.Wire"), T("View.Messages"), T("View.Observations"), T("View.Findings"), T("View.Quality"), T("View.Events"), T("View.Catalog")];
    private readonly DiagnosticInputFormat[] _inputFormats = [DiagnosticInputFormat.Auto, DiagnosticInputFormat.Export, DiagnosticInputFormat.AstmMessage, DiagnosticInputFormat.Hl7Message, DiagnosticInputFormat.SysmexLog, DiagnosticInputFormat.LegacyLog, DiagnosticInputFormat.WireBytes, DiagnosticInputFormat.AtsmTrace];
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
        _source.Add(Text(T("Source.Title"), 12)); _source.Add(Field(T("Source.Plugin"), _profiles)); _source.Add(Field(T("Source.Configuration"), _configurations)); _source.Add(_evidence);
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
                try { if (Handler?.PlatformView is null) throw new InvalidOperationException("Window not realized."); _platform.EnsureWindowReady(); await SelfTestAsync(); File.WriteAllText(args[selfTest + 1], "PASS: MAUI window, file-dialog-owner, private-runtime, instruments=8, profiles=13, family-auto-and-manual, quality-median-sd-with-originals, qualitative-values-and-communicated-count, ordinal-crosses-and-bounds, unperformed-column, selectable-title-and-table, interpreted-default, grouped-default, auto-columns-left-aligned, seven-views-raw-and-interpreted, CFX=3, protected-case-roundtrip, QC=6"); Environment.Exit(0); }
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
        _evidence.Text = Profile.Evidence; _interface.IsVisible = Profile.CanConnect;
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
        _evidence.Text = Profile.Evidence; _interface.IsVisible = Profile.CanConnect; _connect.IsEnabled = Profile.CanConnect && !IsCapturing;
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
                if (_case.Header.Properties?.GetValueOrDefault("unsupportedProtocol") == "true") _status.Text = DiagnosticText.Text("Detection.Unsupported");
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
                _evidence.Text = detected.Evidence;
            }
            _analysis = DiagnosticReader.Analyze(_case, Profile, Timing()); _title.Text = _case.Header.ImportedFrom ?? _case.Header.Source;
            _summary.Text = F("View.Summary", _case.Entries.Count, _analysis.Messages.Count, _analysis.Observations.Count, _analysis.Diagnostics.Count); ShowTable();
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
        string D(string raw) => interpreted ? DiagnosticText.Direction(raw) : raw;
        string Detail(string meaning, string raw) => meaning + "\n\n" + DiagnosticText.Text("Original") + "\n" + raw;
        var unsupported = Profile.TracePlugin is null && _case?.Header.Properties?.GetValueOrDefault("unsupportedProtocol") == "true";
        switch (_tab)
        {
            case 0:
                headers = [T("Column.Sequence"), T("Column.Milliseconds"), T("Column.Direction"), T("Column.Bytes"), T("Column.Command")];
                var protocolMessages = (_analysis?.Messages ?? []).Where(m => m.Explanation is not null).ToLookup(m => m.FirstSequence);
                var protocolEvents = (_analysis?.Events ?? []).Where(e => e.Explanation is not null && e.Level == Atsm.Tracing.StackLevel.Link).ToLookup(e => e.Sequence);
                rows = (_case?.Entries ?? []).Select(entry =>
                {
                    var meaning = unsupported ? DiagnosticText.Format("Message.Wire", entry.Data.Length) : ProtocolExplainer.Entry(entry, Profile);
                    if (Profile.TracePlugin is { } plugin)
                    {
                        var explanations = protocolMessages[entry.Sequence].Select(m => m.Explanation!).Concat(protocolEvents[entry.Sequence].Select(e => e.Explanation!)).ToArray();
                        if (explanations.Length > 0) meaning = string.Join(" · ", explanations.Select(e => DiagnosticText.Explanation(plugin, e)));
                    }
                    var raw = DiagnosticReader.DescribeBytes(entry.Data, _hex.IsChecked);
                    return Row([entry.Sequence.ToString(), entry.Elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture), D(entry.Direction.ToString()), entry.Data.Length.ToString(), interpreted ? meaning : entry.Code ?? raw],
                        Detail(meaning, "#" + entry.Sequence + "\n" + entry.Code + "\n" + entry.Detail + "\n" + raw), entry.Direction.ToString(), meaning);
                }); break;
            case 1:
                headers = [T("Column.Sequence"), T("Column.Direction"), T("Column.Type"), T("Column.Content")];
                var observations = (_analysis?.Observations ?? []).ToLookup(o => o.Trace.LastSequence);
                rows = (_analysis?.Messages ?? []).Select(message =>
                {
                    var kind = ProtocolExplainer.MessageKind(message, Profile);
                    var meaning = ProtocolExplainer.MessageContent(message, Profile, observations[message.Sequence].ToArray());
                    return Row([message.Sequence.ToString(), D(message.Direction), interpreted ? kind : message.Type, ProtocolExplainer.SingleLine(interpreted ? meaning : message.Text)],
                        Detail(kind + "\n" + meaning + "\n" + DiagnosticText.Text("Evidence") + "\n" + (message.Explanation?.Text.Evidence ?? ProtocolExplainer.RecordEvidence(message.Text, Profile))
                            + "\n" + string.Join("\n", message.Fields.Select(f => f.Key + ": " + f.Value)), message.Text), message.Direction, kind);
                }); break;
            case 2:
                headers = [T("Column.Specimen"), T("Column.Position"), T("Column.Test"), T("Column.Type"), T("Column.Value"), T("Column.Unit"), T("Column.Status")];
                rows = (_analysis?.Observations ?? []).Select(o => Row([o.Specimen.SpecimenId, o.Specimen.Position ?? "", o.Test.Vendor.Display ?? o.Test.Vendor.Code,
                    interpreted ? ProtocolExplainer.ResultKind(o, Profile) : o.ResultKind ?? "", interpreted ? ProtocolExplainer.ResultValue(o, Profile) : o.Value.RawText, o.Value.RawUnit ?? "",
                    interpreted ? ProtocolExplainer.Status(o) : o.InstrumentStatus + " " + string.Join(", ", o.Flags.Select(f => f.Code))], Json(o),
                    o.Specimen.SpecimenId, o.Test.Vendor.Code, o.ResultKind)); break;
            case 3:
                headers = interpreted ? [T("Column.Severity"), T("Column.Finding"), T("Column.Sequence")] : [T("Column.Severity"), T("Column.Code"), T("Column.Finding"), T("Column.Sequence")];
                rows = (_analysis?.Diagnostics ?? []).Select(finding =>
                {
                    var meaning = DiagnosticText.Finding(finding, Profile);
                    return Row(interpreted ? [DiagnosticText.Text("Severity." + finding.Severity), meaning, finding.Location ?? ""] : [DiagnosticText.Text("Severity." + finding.Severity), finding.Code, finding.Message, finding.Location ?? ""],
                        Detail(meaning, finding.Code + "\n" + finding.Location + "\n" + finding.Message), finding.Severity.ToString(), finding.Code);
                }); break;
            case 4:
                headers = [T("Quality.MaterialLot"), T("Column.Test"), T("Quality.DataType"), T("Quality.Minimum"), T("Quality.MedianValue"), T("Quality.Maximum"), T("Quality.SD"), T("Quality.N"), T("Quality.Unperformed"), T("Quality.Signals")];
                var quality = QualityGroups();
                _table.SetGroups("quality:" + interpreted + ":" + _group.IsChecked, headers,
                    _group.IsChecked ? quality : DiagnosticGrouping.Build(quality.SelectMany(g => g.Children), false), 9);
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
                        Detail(meaning, item.Level + "\n" + item.Code + "\n" + item.Detail), item.Level.ToString(), item.Code);
                }); break;
            default:
                headers = [T("Column.Profile"), T("Column.Configuration"), T("Column.Evidence")];
                rows = _catalog.Instruments.Select(instrument => Row([instrument.DisplayName, string.Join(" · ", instrument.Profiles.Select(p => p.Configuration)), string.Join(" · ", instrument.Profiles.Select(p => p.Evidence).Distinct(StringComparer.Ordinal))],
                    string.Join("\n\n", instrument.Profiles.Select(p => p.Id + "\n" + p.DisplayName + "\n" + p.Evidence + "\n" + string.Join("\n", p.AstmProfile?.Evidence ?? p.ContentProfile?.Evidence ?? p.TraceProfile?.Evidence ?? []))), instrument.Id)); break;
        }
        var flexible = _tab switch { 0 => 4, 1 => 3, 2 => 6, 3 => interpreted ? 1 : 2, 4 => 2, 5 => interpreted ? 2 : 3, _ => 2 };
        _table.SetRows(_tab + ":" + interpreted, headers, rows, _group.IsChecked && _tab != 6, flexible);
    }
    private IReadOnlyList<DiagnosticRowGroup> QualityGroups()
    {
        static string Number(decimal? n) => n?.ToString("G6", CultureInfo.CurrentCulture) ?? "—";
        static string Approximate(double? n) => n?.ToString("G6", CultureInfo.CurrentCulture) ?? "—";
        string Frequency(QualityValueFrequency value) => F("Quality.Frequency", value.Value.Length == 0
            ? T(value.NotPerformed ? "Quality.Unperformed" : "Quality.NoValue")
            : value.Value + (value.NotPerformed ? " · " + T("Quality.Unperformed") : ""), value.Count);
        var groups = new List<DiagnosticRowGroup>();
        var knowledge = ((object?)Profile.TracePlugin ?? Profile.AstmPlugin) as Atsm.Plugins.IInstrumentValueKnowledge;
        foreach (var series in QualitySeriesBuilder.Build(_analysis?.Observations ?? [], knowledge))
        {
            var material = series.Material + (string.IsNullOrWhiteSpace(series.Lot) ? " / " + T("Quality.NoLot") : " / " + series.Lot)
                + (string.IsNullOrWhiteSpace(series.Level) ? "" : " / " + series.Level);
            var described = series.Records.FirstOrDefault(o => knowledge?.DescribeValue(o) is not null) ?? series.Records[0];
            var kind = ProtocolExplainer.ResultKind(described, Profile) + (series.Unit.Length == 0 ? "" : " · " + series.Unit);
            var signals = new List<string>();
            if (series.MeasurementCount == 1) signals.Add(T("Quality.Single"));
            if (series.PotentialOutliers.Count > 0) signals.Add(F("Quality.Outliers", series.PotentialOutliers.Count));
            if (series.Flagged > 0) signals.Add(F("Quality.Flagged", series.Flagged));
            if (series.Repeated > 0) signals.Add(F("Quality.Repeated", series.Repeated));
            if (series.Range.Incomparable) signals.Add(T("Quality.Unordered"));
            var reported = series.Records.Sum(o => o.Quality?.Statistics.Count ?? 0);
            if (reported > 0) signals.Add(F("Quality.Reported", reported));
            var detail = string.Join("\n", material, series.Test + " · " + kind,
                T("Quality.Minimum") + ": " + series.Minimum?.ToString("G29"), T("Quality.Median") + ": " + series.Median?.ToString("G29"),
                T("Quality.Maximum") + ": " + series.Maximum?.ToString("G29"), T("Quality.SD") + ": " + series.StandardDeviation?.ToString("G17"),
                T("Quality.Mean") + ": " + series.Mean?.ToString("G17"), T("Quality.N") + ": " + series.MeasurementCount,
                T("Quality.NumericCount") + ": " + series.NumericCount,
                T("Quality.Unperformed") + ": " + series.NotPerformed,
                T("Quality.ReportedRange") + ": " + series.Range.Minimum + " … " + series.Range.Maximum,
                T("Quality.Distribution") + ": " + string.Join(" · ", series.ValueFrequencies.Select(Frequency)),
                F("Quality.Last", series.Last.Value.RawText, series.Last.ResultTime?.ToString("O") ?? "#" + series.Last.Trace.LastSequence),
                T("Quality.Method"), string.Join(" · ", signals));
            var children = series.Records.Select(o => new DiagnosticRow([material, series.Test, kind, "", string.IsNullOrWhiteSpace(o.Value.RawText)
                ? o.Value.Coded?.Display ?? o.Value.Coded?.Code ?? o.Value.RawText : _interpret.IsChecked ? ProtocolExplainer.ResultValue(o, Profile) : o.Value.RawText,
                "", "", "1", o.InstrumentStatusMeaning == InstrumentStatusMeaning.NotPerformed ? "1" : "",
                "#" + o.Trace.LastSequence + (o.ResultTime is { } time ? " · " + time.ToString("g") : "") + " · " + ProtocolExplainer.Status(o)
                    + (series.PotentialOutliers.Contains(o.ObservationId) ? " · " + T("Quality.Outlier") : "")], Json(o), series.Key)
                { Attention = o.Flags.Count > 0 || series.PotentialOutliers.Contains(o.ObservationId) }).ToArray();
            var measuredValues = series.ValueFrequencies.Where(f => !f.NotPerformed && f.Value.Length > 0).ToArray();
            var modes = measuredValues.Where(f => f.Count == measuredValues.Max(v => v.Count)).ToArray();
            var result = series.Range.Median ?? (series.NumericCount > 0 ? Number(series.Median)
                : series.Range.BoundedCount > 0 ? "—" : modes.Length == 1 ? modes[0].Value : modes.Length > 1 ? T("Quality.MixedResults") : "—");
            groups.Add(new(series.Key, children) { SummaryRow = new([material, series.Test, kind, series.Range.Minimum ?? "—", result, series.Range.Maximum ?? "—",
                Approximate(series.StandardDeviation), series.MeasurementCount.ToString(CultureInfo.CurrentCulture), series.NotPerformed.ToString(CultureInfo.CurrentCulture),
                signals.Count == 0 ? T("Quality.NoSignals") : string.Join(" · ", signals)], detail, series.Key)
                { Attention = series.Flagged > 0 || series.PotentialOutliers.Count > 0 } });
        }
        foreach (var resource in _analysis?.QualityResources ?? [])
        {
            var row = Row([T("Quality.External"), "", T("Quality.Targets"), "", "", "", "", "", "", T("Quality.RequiresHost")], Json(resource), resource.SourceField);
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
            if (!_interpret.IsChecked || !_group.IsChecked || _catalog.Instruments.Count != 8 || _catalog.Instruments.Count(i => i.Id == SysmexUrinalysisPlugin.PluginId) != 1 || _catalog.Profiles.Count != 13 || analyzed.Observations.Count != 3 || analyzed.Observations.Sum(o => o.Quality?.Statistics.Count ?? 0) != 6) throw new InvalidDataException("ATSM-MAUI-SELFTEST");
            var quality = QualitySeriesBuilder.Build(analyzed.Observations);
            if (quality.Sum(s => s.NumericCount) != 2 || quality.Sum(s => s.MeasurementCount) != 3 || QualityGroups().Any(g => g.SummaryRow is null)) throw new InvalidDataException("ATSM-MAUI-QUALITY-SELFTEST");
            var unknownCq = QualityGroups().Single(g => g.Children.Any(r => r.Cells[4] == "Undetermined"));
            if (!unknownCq.SummaryRow!.Cells[4].Contains("Undetermined", StringComparison.Ordinal) || unknownCq.SummaryRow.Cells[7] != "1") throw new InvalidDataException("ATSM-MAUI-QUALITATIVE-SELFTEST");
            var normal = analyzed.Observations[0] with { Value = new("normal ") };
            _analysis = analyzed with { Observations = [normal, normal, normal, normal] };
            var normalSummary = QualityGroups().Single().SummaryRow!;
            if (normalSummary.Cells[4] != "normal" || normalSummary.Cells[7] != "4" || normalSummary.Cells[8] != "0") throw new InvalidDataException("ATSM-MAUI-QUALITATIVE-COUNT-SELFTEST");
            _analysis = analyzed with { Observations = [normal, normal, normal with { Value = new(""), InstrumentStatusMeaning = InstrumentStatusMeaning.NotPerformed }] };
            var mixedSummary = QualityGroups().Single().SummaryRow!;
            if (mixedSummary.Cells[4] != "normal" || mixedSummary.Cells[8] != "1" || mixedSummary.Cells[7] != "3") throw new InvalidDataException("ATSM-MAUI-UNPERFORMED-COLUMN-SELFTEST");
            _analysis = analyzed;
            if (Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains(Convert.ToBase64String(_case.Entries[0].Data.Span), StringComparison.Ordinal)) throw new InvalidDataException("ATSM-UNPROTECTED-CASE");
        }
        finally { File.Delete(path); }
        foreach (var interpreted in new[] { false, true })
        {
            _interpret.IsChecked = interpreted;
            for (_tab = 0; _tab < 7; _tab++) { ShowTable(); await Task.Delay(100); _table.VerifyAutomaticLayout(); }
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
        if (crossSummary.Cells.Length != 10 || crossSummary.Cells[2] != "Semicuantitativo (cruces)" || crossSummary.Cells[3] != "0"
            || crossSummary.Cells[4] != "0" || crossSummary.Cells[5] != "2+" || crossSummary.Cells[6] != "—" || crossSummary.Cells[7] != "4" || crossSummary.Cells[8] != "1")
            throw new InvalidDataException("ATSM-MAUI-ORDINAL-SELFTEST");
        foreach (var (comparison, minimum, maximum) in new[] { (">=", ">=50", "—"), ("<=", "—", "<=50") })
        {
            _analysis = saved with { Observations = [cross with { ResultKind = "CONCENTRATION", Value = new(comparison + "50", 50, comparison) }] };
            var bounded = QualityGroups().Single().SummaryRow!;
            if (bounded.Cells[2] != "Valor semicuantitativo" || bounded.Cells[3] != minimum || bounded.Cells[5] != maximum || bounded.Cells[6] != "—")
                throw new InvalidDataException("ATSM-MAUI-BOUNDED-SELFTEST");
        }
        _analysis = saved;
        foreach (var interpreted in new[] { false, true })
        {
            _interpret.IsChecked = interpreted;
            for (_tab = 0; _tab < 7; _tab++) { ShowTable(); await Task.Delay(100); _table.VerifyAutomaticLayout(); }
        }
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
