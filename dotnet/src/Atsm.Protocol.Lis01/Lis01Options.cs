namespace Atsm.Protocol.Lis01;

/// <summary>Which side of the link this engine plays. On contention the instrument has priority.</summary>
public enum Lis01Role { Host, Instrument }

/// <summary>Defaults follow CLSI LIS01-A2. A profile may change them only with documented evidence.</summary>
public sealed record Lis01Options
{
    public static readonly Lis01Options Default = new();

    public TimeSpan EstablishmentTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan FrameAckTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan ReceiverTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Host side: how long to wait for the instrument's ENQ after yielding on contention.</summary>
    public TimeSpan ContentionTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Instrument side: delay before repeating ENQ after contention.</summary>
    public TimeSpan ContentionRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Delay before a new ENQ after the receiver answered NAK (busy) or after an establishment timeout.</summary>
    public TimeSpan BusyRetryDelay { get; init; } = TimeSpan.FromSeconds(10);

    public int MaxRetransmissions { get; init; } = 6;

    /// <summary>Establishment attempts per transmission before reporting it as failed. Not fixed by the standard.</summary>
    public int MaxEstablishmentAttempts { get; init; } = 6;

    public int MaxTextLength { get; init; } = Lis01Frame.MaxTextLength;

    public bool AcceptLowercaseChecksum { get; init; } = true;

    public bool StrictTextCharacters { get; init; } = true;

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(EstablishmentTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(FrameAckTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ReceiverTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ContentionTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(ContentionRetryDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(BusyRetryDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxRetransmissions);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxEstablishmentAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxTextLength, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxTextLength, 64 * 1024);
    }
}
