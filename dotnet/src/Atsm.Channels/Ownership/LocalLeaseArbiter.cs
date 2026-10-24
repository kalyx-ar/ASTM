using Atsm.Abstractions.Channels;

namespace Atsm.Channels.Ownership;

/// <summary>
/// In-process arbiter for channels whose exclusion is only as strong as this process
/// (TCP native, local serial, replay). It grants advisory leases with increasing generations,
/// so late writes of a previous owner can still be rejected locally.
/// </summary>
public sealed class LocalLeaseArbiter(string channelId, OwnershipEnforcement enforcement, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly Guid _epoch = Guid.CreateVersion7();
    private ControlLease? _current;
    private uint _generation;
    private ulong _connection;

    public ControlLease Acquire(ControlRequest request)
    {
        lock (_gate)
        {
            if (_current is not null && !IsExpired(_current) && _current.ControllerId != request.ControllerId)
                throw new InvalidOperationException($"Channel {channelId} is owned by {_current.ControllerId}.");
            _current = new ControlLease(channelId, request.ControllerId, _epoch, ++_connection, ++_generation,
                Guid.CreateVersion7(), enforcement, _clock.GetUtcNow(), request.Deadline ?? TimeSpan.FromDays(1));
            return _current;
        }
    }

    public ControlLease Renew(ControlLease lease)
    {
        lock (_gate)
        {
            if (!IsCurrent(lease))
                throw new InvalidOperationException("Lease is not current.");
            _current = lease with { GrantedAt = _clock.GetUtcNow() };
            return _current;
        }
    }

    public ReleaseOutcome Release(ControlLease lease)
    {
        lock (_gate)
        {
            if (_current is null || !SameGrant(_current, lease))
                return ReleaseOutcome.NotOwner;
            var expired = IsExpired(_current);
            _current = null;
            return expired ? ReleaseOutcome.AlreadyExpired : ReleaseOutcome.Released;
        }
    }

    public bool IsCurrent(ControlLease lease)
    {
        lock (_gate)
            return _current is not null && SameGrant(_current, lease) && !IsExpired(_current);
    }

    private bool IsExpired(ControlLease lease) => _clock.GetUtcNow() - lease.GrantedAt > lease.Duration;

    private static bool SameGrant(ControlLease a, ControlLease b) =>
        a.AgentEpoch == b.AgentEpoch && a.Generation == b.Generation && a.ConnectionId == b.ConnectionId && a.ControllerId == b.ControllerId;
}
