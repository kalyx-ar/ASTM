using Astm.Abstractions.Transport;
using Astm.Transport;
using Microsoft.Maui.Controls;
using static Astm.Diagnostics.Maui.Globals;

namespace Astm.Diagnostics.Maui;

public sealed partial class MainPage
{
    private void ConfigureTransportDefaults()
    {
        if (IsCapturing) return;
        var knowledge = Profile.AstmProfile?.Transport;
        _configureSerial.IsChecked = knowledge?.Serial is not null;
        if (knowledge?.Serial?.Default is { } serial)
        {
            _baud.Text = serial.BaudRate.ToString(); _dataBits.Text = serial.DataBits.ToString();
            _parity.SelectedIndex = (int)serial.Parity; _stopBits.SelectedIndex = (int)serial.StopBits; _flow.SelectedIndex = (int)serial.FlowControl;
            _dtr.IsChecked = serial.Dtr; _rts.IsChecked = serial.Rts; _configureSerial.IsChecked = true;
            _transport.SelectedIndex = (int)DiagnosticTransport.LocalSerial;
        }
        if (knowledge?.Network is { } network)
        {
            _transport.SelectedIndex = (int)(network.Role == NetworkRole.InstrumentIsClient ? DiagnosticTransport.TcpServer : DiagnosticTransport.TcpClient);
            if (network.DefaultPort is { } port) _port.Text = port.ToString();
        }
        ConfigureTransportFields();
    }
    private void ConfigureTransportFields()
    {
        var kind = (DiagnosticTransport)_transport.SelectedIndex;
        _networkFields.IsVisible = kind != DiagnosticTransport.LocalSerial;
        _serialFields.IsVisible = kind is DiagnosticTransport.LocalSerial or DiagnosticTransport.PortGate;
        _serialPort.IsEnabled = kind == DiagnosticTransport.LocalSerial;
        _configureSerial.IsEnabled = kind == DiagnosticTransport.PortGate;
    }
    private SerialSettings ReadSerialSettings()
    {
        if (!int.TryParse(_baud.Text, out var baud) || baud <= 0 || !int.TryParse(_dataBits.Text, out var bits) || bits is < 5 or > 8)
            throw new ArgumentException(T("Validation.Serial"));
        var serial = new SerialSettings(baud, bits, (SerialParity)_parity.SelectedIndex, (SerialStopBits)_stopBits.SelectedIndex, (SerialFlowControl)_flow.SelectedIndex)
            { Dtr = _dtr.IsChecked, Rts = _rts.IsChecked };
        if ((DiagnosticTransport)_transport.SelectedIndex == DiagnosticTransport.LocalSerial || _configureSerial.IsChecked)
            if (Profile.AstmProfile?.Transport.Validate(serial).Count > 0) throw new ArgumentException(T("Validation.SerialProfile"));
        return serial;
    }
}
