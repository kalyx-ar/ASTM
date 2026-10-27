namespace Astm.Protocol.Lis01;

public abstract record Lis01Action;

/// <summary>Bytes the caller must write to the channel, in order.</summary>
public sealed record Lis01Write(byte[] Bytes) : Lis01Action;

/// <summary>
/// End of a received transmission. Each message is the concatenated text of its frames up to ETX.
/// <paramref name="Complete"/> is false when the transmission ended by timeout or with a message cut after ETB;
/// the partial text is then the last element of <paramref name="Messages"/>.
/// </summary>
public sealed record Lis01Received(IReadOnlyList<byte[]> Messages, bool Complete) : Lis01Action;

public enum Lis01SendResult { Delivered, Interrupted, Failed }

/// <param name="FramesAcknowledged">Frames the receiver acknowledged. Bytes of an unacknowledged frame may still have reached the instrument.</param>
public sealed record Lis01SendCompleted(long TransmissionId, Lis01SendResult Result, int FramesAcknowledged, int TotalFrames, string? Code) : Lis01Action;

/// <summary>Technical observation for the trace (timeouts, NAK, contention, noise...).</summary>
public sealed record Lis01Notice(string Code, string? Detail = null) : Lis01Action;

public static class Lis01Codes
{
    public const string EstablishmentTimeout = "ASTM-L01-ESTABLISH-TIMEOUT";
    public const string EstablishmentExhausted = "ASTM-L01-ESTABLISH-EXHAUSTED";
    public const string ReceiverBusy = "ASTM-L01-RECEIVER-BUSY";
    public const string Contention = "ASTM-L01-CONTENTION";
    public const string ContentionTimeout = "ASTM-L01-CONTENTION-TIMEOUT";
    public const string FrameAckTimeout = "ASTM-L01-FRAME-ACK-TIMEOUT";
    public const string FrameRejected = "ASTM-L01-FRAME-REJECTED";
    public const string RetransmissionsExhausted = "ASTM-L01-RETRANSMISSIONS-EXHAUSTED";
    public const string ReceiverInterrupt = "ASTM-L01-RECEIVER-INTERRUPT";
    public const string ReceiverTimeout = "ASTM-L01-RECEIVER-TIMEOUT";
    public const string InvalidFrame = "ASTM-L01-INVALID-FRAME";
    public const string UnexpectedFrameNumber = "ASTM-L01-UNEXPECTED-FRAME-NUMBER";
    public const string FrameTooLong = "ASTM-L01-FRAME-TOO-LONG";
    public const string IncompleteMessage = "ASTM-L01-INCOMPLETE-MESSAGE";
    public const string EmptyTransmission = "ASTM-L01-EMPTY-TRANSMISSION";
    public const string Noise = "ASTM-L01-NOISE";
    public const string Cancelled = "ASTM-L01-CANCELLED";
}
