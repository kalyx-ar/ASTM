namespace Atsm.Protocol.Lis01;

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
    public const string EstablishmentTimeout = "ATSM-L01-ESTABLISH-TIMEOUT";
    public const string EstablishmentExhausted = "ATSM-L01-ESTABLISH-EXHAUSTED";
    public const string ReceiverBusy = "ATSM-L01-RECEIVER-BUSY";
    public const string Contention = "ATSM-L01-CONTENTION";
    public const string ContentionTimeout = "ATSM-L01-CONTENTION-TIMEOUT";
    public const string FrameAckTimeout = "ATSM-L01-FRAME-ACK-TIMEOUT";
    public const string FrameRejected = "ATSM-L01-FRAME-REJECTED";
    public const string RetransmissionsExhausted = "ATSM-L01-RETRANSMISSIONS-EXHAUSTED";
    public const string ReceiverInterrupt = "ATSM-L01-RECEIVER-INTERRUPT";
    public const string ReceiverTimeout = "ATSM-L01-RECEIVER-TIMEOUT";
    public const string InvalidFrame = "ATSM-L01-INVALID-FRAME";
    public const string UnexpectedFrameNumber = "ATSM-L01-UNEXPECTED-FRAME-NUMBER";
    public const string FrameTooLong = "ATSM-L01-FRAME-TOO-LONG";
    public const string IncompleteMessage = "ATSM-L01-INCOMPLETE-MESSAGE";
    public const string EmptyTransmission = "ATSM-L01-EMPTY-TRANSMISSION";
    public const string Noise = "ATSM-L01-NOISE";
    public const string Cancelled = "ATSM-L01-CANCELLED";
}
