using System.IO.Ports;
using Astm.Abstractions.Transport;

namespace Astm.Transport;

/// <summary>Local serial port with System.IO.Ports (Windows, Linux, macOS).</summary>
public static class LocalSerial
{
    /// <summary>Port names present on this machine.</summary>
    public static IReadOnlyList<string> PortNames() => SerialPort.GetPortNames().Order(StringComparer.Ordinal).ToArray();

    /// <summary>Opens <paramref name="portName"/> with <paramref name="settings"/>; the port closes with the returned stream.</summary>
    public static Stream Open(string portName, SerialSettings settings)
    {
        var port = Configure(new SerialPort(portName), settings);
        try
        {
            port.Open();
            return new OwnedStream(port.BaseStream, port, cancelPendingReads: true);
        }
        catch
        {
            port.Dispose();
            throw;
        }
    }

    /// <summary>Applies the settings to a port object without opening it.</summary>
    public static SerialPort Configure(SerialPort port, SerialSettings settings)
    {
        port.BaudRate = settings.BaudRate;
        port.DataBits = settings.DataBits;
        port.Parity = settings.Parity switch
        {
            SerialParity.Odd => Parity.Odd,
            SerialParity.Even => Parity.Even,
            SerialParity.Mark => Parity.Mark,
            SerialParity.Space => Parity.Space,
            _ => Parity.None,
        };
        port.StopBits = settings.StopBits switch
        {
            SerialStopBits.Two => StopBits.Two,
            SerialStopBits.OnePointFive => StopBits.OnePointFive,
            _ => StopBits.One,
        };
        port.Handshake = settings.FlowControl switch
        {
            SerialFlowControl.RtsCts => Handshake.RequestToSend,
            SerialFlowControl.XonXoff => Handshake.XOnXOff,
            _ => Handshake.None,
        };
        port.DtrEnable = settings.Dtr;
        // With RTS/CTS flow control the driver owns RTS; setting it would throw.
        if (settings.FlowControl != SerialFlowControl.RtsCts)
            port.RtsEnable = settings.Rts;
        return port;
    }
}
