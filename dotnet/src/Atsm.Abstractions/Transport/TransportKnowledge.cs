namespace Atsm.Abstractions.Transport;

public enum SerialParity { None, Odd, Even, Mark, Space }

public enum SerialStopBits { One, OnePointFive, Two }

public enum SerialFlowControl { None, RtsCts, XonXoff }

/// <summary>
/// Line settings of a serial port. The same value configures a local COM or a port behind PortGate; ATSM itself never
/// opens a port with it (Documentacion/TRANSPORTE.md).
/// </summary>
public sealed record SerialSettings(
    int BaudRate = 9600,
    int DataBits = 8,
    SerialParity Parity = SerialParity.None,
    SerialStopBits StopBits = SerialStopBits.One,
    SerialFlowControl FlowControl = SerialFlowControl.None)
{
    /// <summary>Data Terminal Ready asserted while the port is open. Some instruments do not transmit without it.</summary>
    public bool Dtr { get; init; } = true;

    /// <summary>Request To Send asserted while the port is open (ignored with RTS/CTS flow control, which drives it).</summary>
    public bool Rts { get; init; } = true;
}

/// <summary>Which side opens the TCP connection.</summary>
public enum NetworkRole { InstrumentIsServer, InstrumentIsClient }

public enum TransportDirection { Bidirectional, ReceiveOnly }

/// <summary>Serial interface of the instrument as its manual specifies it.</summary>
public sealed record SerialKnowledge(SerialSettings Default, IReadOnlyList<int> SupportedBaudRates)
{
    /// <summary>The instrument does not transmit unless DTR is asserted.</summary>
    public bool RequiresDtr { get; init; }
}

/// <summary>Network interface of the instrument as its manual specifies it.</summary>
public sealed record NetworkKnowledge(NetworkRole Role, int? DefaultPort = null, bool Mllp = false);

/// <summary>
/// What a plugin knows about the transport of a profile: theoretical specification with its evidence, never an open
/// connection. <see cref="Hypothesis"/> marks values inferred from traces without a manual.
/// </summary>
public sealed record TransportKnowledge(TransportDirection Direction, IReadOnlyList<string> Evidence)
{
    public SerialKnowledge? Serial { get; init; }

    public NetworkKnowledge? Network { get; init; }

    public bool Hypothesis { get; init; }
}
