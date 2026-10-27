using System.Diagnostics;
using Astm.Plugins;
using Astm.Protocol.Lis01;
using Microsoft.Maui.Controls;
using static Astm.Diagnostics.Maui.Globals;

namespace Astm.Diagnostics.Maui;

public sealed partial class MainPage
{
    private readonly Picker _commandChoice = Choice(T("Command.Action"));
    private readonly VerticalStackLayout _commandArguments = new() { Spacing = 8 };
    private readonly VerticalStackLayout _commandPanel = new() { Spacing = 8 };
    private readonly Dictionary<string, Entry> _commandInputs = [];
    private IReadOnlyList<InstrumentCommandDescriptor> _availableCommands = [];
    private IInstrumentCommandPlugin? _commandPlugin;
    private Button? _sendCommand;
    private DiagnosticCommandConnection? _liveCommands;
    private void InitializeCommandControls()
    {
        _commandPanel.Add(Text(T("Command.Title"), 12)); _commandPanel.Add(Field(T("Command.Action"), _commandChoice)); _commandPanel.Add(_commandArguments);
        _sendCommand = Action(T("Command.Send"), SendCommandAsync); _sendCommand.IsEnabled = false; _commandPanel.Add(_sendCommand); _interface.Add(_commandPanel);
        _commandChoice.SelectedIndexChanged += (_, _) =>
        {
            _commandArguments.Clear(); _commandInputs.Clear();
            if (_commandPlugin is null || _commandChoice.SelectedIndex < 0) return;
            foreach (var parameter in _availableCommands[_commandChoice.SelectedIndex].Parameters)
            {
                var entry = Input(""); _commandInputs.Add(parameter.Id, entry);
                _commandArguments.Add(Field(DiagnosticText.Plugin(_commandPlugin, parameter.Label), entry));
            }
        };
    }
    private void SetLiveCommands(DiagnosticCommandConnection? connection)
    {
        _liveCommands = connection; ConfigureCommandCatalog();
    }
    private void ConfigureCommandCatalog()
    {
        _commandPlugin = _liveCommands?.Plugin ?? Profile.AstmPlugin as IInstrumentCommandPlugin;
        _availableCommands = _commandPlugin?.Commands(_liveCommands?.ProfileId ?? Profile.Id) ?? [];
        _commandChoice.Items.Clear(); _commandInputs.Clear(); _commandArguments.Clear();
        foreach (var command in _availableCommands) _commandChoice.Items.Add(DiagnosticText.Plugin(_commandPlugin!, command.Label));
        _commandChoice.SelectedIndex = _commandChoice.Items.Count > 0 ? 0 : -1;
        _commandChoice.IsEnabled = _commandChoice.Items.Count > 0;
        _commandPanel.IsVisible = _availableCommands.Count > 0;
        if (_sendCommand is not null) _sendCommand.IsEnabled = _liveCommands is not null && _commandChoice.Items.Count > 0;
    }
    private async Task SendCommandAsync()
    {
        if (_liveCommands is not { } connection || _commandChoice.SelectedIndex < 0 || _liveStop is null) return;
        var command = connection.Commands[_commandChoice.SelectedIndex];
        _sendCommand!.IsEnabled = false;
        try
        {
            var parameters = _commandInputs.ToDictionary(p => p.Key, p => p.Value.Text ?? "", StringComparer.Ordinal);
            foreach (var parameter in command.Parameters.Where(p => p.Required))
                if (string.IsNullOrWhiteSpace(parameters.GetValueOrDefault(parameter.Id)))
                    throw new ArgumentException(F("Validation.CommandParameter", DiagnosticText.Plugin(connection.Plugin, parameter.Label)));
            var watch = Stopwatch.StartNew();
            var receipt = await connection.SendAsync(new(command.Id, parameters), _liveStop.Token);
            _status.Text = F(receipt.Result == Lis01SendResult.Delivered ? "Command.Delivered" : "Command.Failed", DiagnosticDuration.Format(watch.Elapsed));
            RefreshCapture(force: true);
        }
        catch (Exception error) { ShowError(error); }
        finally { _sendCommand.IsEnabled = _liveCommands is not null; }
    }
    private void VerifyTransportAndCommandPresentation()
    {
        SelectProfile(Astm.Plugin.Abbott.Architect.ArchitectPlugin.AsiProfileId); ConfigureTransportDefaults();
        if (_transport.Items.Count != 4 || ReadSerialSettings().BaudRate != 9600 || !_serialFields.IsVisible)
            throw new InvalidDataException("ASTM-MAUI-TRANSPORT-SELFTEST");
        var plugin = (IInstrumentCommandPlugin)Profile.AstmPlugin!;
        SetLiveCommands(new(plugin, Profile.Id, (_, _) => Task.FromResult(new Lis01SendCompleted(1, Lis01SendResult.Delivered, 1, 1, null))));
        if (_commandChoice.Items.Count != 1 || !_commandInputs.ContainsKey("sample") || !_commandInputs.ContainsKey("tests") || !_sendCommand!.IsEnabled)
            throw new InvalidDataException("ASTM-MAUI-COMMAND-CATALOG-SELFTEST");
        SetLiveCommands(null);
        if (_sendCommand.IsEnabled) throw new InvalidDataException("ASTM-MAUI-COMMAND-DISCONNECTED-SELFTEST");
    }
}
