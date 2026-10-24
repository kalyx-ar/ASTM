namespace Atsm.Abstractions.Channels;

public enum ChannelKind { SerialTunnel, TcpNative, LocalSerial, FileDrop, Replay, Simulated }

/// <summary>Who actually prevents a second controller from writing to the instrument.</summary>
public enum OwnershipEnforcement { None, Advisory, Enforced }

public enum TraceCaptureLevel { None, Ram, Durable }

public sealed record ChannelDescriptor(string ChannelId, ChannelKind Kind, string Endpoint, string? InstrumentId = null);

public sealed record ChannelCapabilities(
    OwnershipEnforcement Ownership,
    bool QueuedAcquisition,
    TraceCaptureLevel TraceCapture,
    bool SerialSignals,
    bool MessageBoundaries,
    int MaxRecordBytes)
{
    public static ChannelCapabilities Stream(OwnershipEnforcement ownership) =>
        new(ownership, QueuedAcquisition: false, TraceCaptureLevel.None, SerialSignals: false, MessageBoundaries: false, MaxRecordBytes: 64 * 1024);
}

public sealed record ControlRequest(string ControllerId, int Priority = 0, TimeSpan? Deadline = null);

/// <summary>
/// Exclusive, renewable permission to write. <see cref="AgentEpoch"/> and <see cref="Generation"/>
/// let the arbiter reject writes from an earlier owner or an earlier agent start.
/// </summary>
public sealed record ControlLease(
    string ChannelId,
    string ControllerId,
    Guid AgentEpoch,
    ulong ConnectionId,
    uint Generation,
    Guid TransferId,
    OwnershipEnforcement Enforcement,
    DateTimeOffset GrantedAt,
    TimeSpan Duration);

public enum TransferAdmissionState { Open, Rejected }

public sealed record TransferAdmission(TransferAdmissionState State, string? Code = null);

public enum ReleaseOutcome { Released, NotOwner, AlreadyExpired }

/// <param name="Count">Bytes copied into the caller's buffer. Zero together with <paramref name="EndOfStream"/> ends the channel.</param>
/// <param name="IsMessageBoundary">True when the channel delivers whole messages (FileDrop) and this read ends one.</param>
public sealed record ReadOutcome(int Count, bool EndOfStream, bool IsMessageBoundary = false, ulong? StreamOffset = null)
{
    public static readonly ReadOutcome End = new(0, EndOfStream: true);
}

public enum WriteProgress { Completed, Partial, Unknown, Rejected }

/// <summary>Technical write progress only: it never proves that the instrument received or processed the bytes.</summary>
public sealed record WriteOutcome(Guid WriteId, int Requested, int Accepted, WriteProgress Progress, string? Code = null);

public enum ChannelEventKind { Opened, Closed, Faulted, OwnershipQueued, OwnershipGranted, OwnershipRevoked, Discontinuity, SignalChanged, Overflow }

public sealed record ChannelEvent(ChannelEventKind Kind, DateTimeOffset Timestamp, string? Code = null, string? Detail = null);
