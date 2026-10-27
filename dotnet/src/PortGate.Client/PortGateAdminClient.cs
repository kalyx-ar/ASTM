using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using Astm.Transport;

namespace PortGate.Client;

public sealed class PortGateAdminException(string code, string message) : Exception($"{code}: {message}")
{
    public string Code { get; } = code;
}

/// <summary>Serial port reported by <see cref="PortGateAdminClient.ListDevicesAsync"/> with its physical identity.</summary>
public sealed record PortGateDeviceInfo(string Name, string FriendlyName, string UsbVid, string UsbPid, string UsbSerial, string Location, string InstanceId, bool InUse);

public sealed record PortGateAuditEvent(ulong Id, DateTimeOffset Time, string Kind, string? PortId, string? Actor, string? Target, uint Generation, string? Reason,
    DateTimeOffset? Until, bool Uncertain, string? Detail)
{
    internal static PortGateAuditEvent From(SortedDictionary<ulong, object?> map) => new(
        map.GetValueOrDefault(0UL) as ulong? ?? 0,
        DateTimeOffset.FromUnixTimeMilliseconds((long)(map.GetValueOrDefault(1UL) as ulong? ?? 0)),
        map.GetValueOrDefault(2UL) as string ?? "",
        map.GetValueOrDefault(3UL) as string,
        map.GetValueOrDefault(4UL) as string,
        map.GetValueOrDefault(5UL) as string,
        (uint)(map.GetValueOrDefault(6UL) as ulong? ?? 0),
        map.GetValueOrDefault(7UL) as string,
        map.GetValueOrDefault(8UL) is ulong until ? DateTimeOffset.FromUnixTimeMilliseconds((long)until) : null,
        map.GetValueOrDefault(9UL) is true,
        map.GetValueOrDefault(10UL) as string);
}

/// <summary>
/// Client of the PortGate administration endpoint (spec/admin-v1.md). The server keeps versions and history of the
/// configuration; the agent keeps only the one in force. With TLS, the agent certificate is pinned by its SHA-256.
/// </summary>
public sealed class PortGateAdminClient : IAsyncDisposable
{
    public const int DefaultPort = 28240;

    private readonly IDisposable _owner;
    private readonly Stream _stream;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<SortedDictionary<ulong, object?>>> _replies = new();
    private readonly Channel<PortGateAuditEvent> _events = Channel.CreateUnbounded<PortGateAuditEvent>();
    private readonly CancellationTokenSource _stop = new();
    private Task _readLoop = Task.CompletedTask;
    private long _requestId;
    private Exception? _refusal;

    private PortGateAdminClient(IDisposable owner, Stream stream, string agentId)
    {
        _owner = owner;
        _stream = stream;
        AgentId = agentId;
    }

    public string AgentId { get; }

    /// <summary>
    /// Connects and identifies as <paramref name="serverId"/>. <paramref name="agentCertificateSha256"/> pins the agent
    /// certificate (TLS); <paramref name="clientCertificate"/> identifies this server when the agent is restricted to it.
    /// Without either the channel is plain text.
    /// </summary>
    public static async Task<PortGateAdminClient> ConnectAsync(string host, int port, string serverId, string? agentCertificateSha256 = null,
        X509Certificate2? clientCertificate = null, CancellationToken cancellationToken = default)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            Stream stream = client.GetStream();
            if (agentCertificateSha256 is not null || clientCertificate is not null)
            {
                var pinned = agentCertificateSha256?.Replace(":", "", StringComparison.Ordinal).ToLowerInvariant();
                var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    // The agent certificate is self-signed: it is trusted by fingerprint, never by name or chain.
                    RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null &&
                        (pinned is null || Convert.ToHexStringLower(SHA256.HashData(certificate.GetRawCertData())) == pinned),
                    // The agent pins this certificate by fingerprint, so it is offered whatever CA list the agent announces.
                    LocalCertificateSelectionCallback = (_, _, _, _, _) => clientCertificate!,
                    ClientCertificates = clientCertificate is null ? null : [clientCertificate],
                }, timeout.Token).ConfigureAwait(false);
                stream = tls;
            }
            return await OpenAsync(new OwnedStream(stream, client), serverId, timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Administers over an established connection: one this server dialed, or one the agent dialed in the
    /// agent-initiated mode (<see cref="PortGateListener"/>). Answering Hello as admin gives it that role. Owns <paramref name="stream"/>.
    /// </summary>
    public static async Task<PortGateAdminClient> OpenAsync(Stream stream, string serverId, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var decoder = new LinkDecoder(1024 * 1024);
            var units = new List<LinkUnit>();
            await stream.WriteAsync(LinkUnit.Control(LinkType.Hello, 0, LinkCbor.Map((0, 1UL), (1, "admin"), (2, serverId))).Encode(), timeout.Token).ConfigureAwait(false);
            var hello = await ReceiveAsync(stream, decoder, units, LinkType.Hello, timeout.Token).ConfigureAwait(false);
            var admin = new PortGateAdminClient(stream, stream, hello.Map()?.GetValueOrDefault(2UL) as string ?? "");
            admin._readLoop = Task.Run(() => admin.ReadLoopAsync(decoder, units));
            return admin;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task<SortedDictionary<ulong, object?>> GetStatusAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(LinkType.GetStatus, [], cancellationToken);

    /// <summary>Returns the revision and the configuration in force (null when the agent is not configured).</summary>
    public async Task<(ulong Revision, SortedDictionary<ulong, object?>? Configuration)> GetConfigAsync(CancellationToken cancellationToken = default)
    {
        var body = await RequestAsync(LinkType.GetConfig, [], cancellationToken).ConfigureAwait(false);
        return (body.GetValueOrDefault(0UL) as ulong? ?? 0, body.GetValueOrDefault(1UL) as SortedDictionary<ulong, object?>);
    }

    /// <summary>Installs a complete configuration. Returns the Link port in force.</summary>
    public async Task<int> SetConfigAsync(ulong revision, PortGateConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var body = await RequestAsync(LinkType.SetConfig, LinkCbor.Map((1, revision), (2, configuration.ToMap())), cancellationToken).ConfigureAwait(false);
        return (int)(body.GetValueOrDefault(1UL) as ulong? ?? 0);
    }

    /// <summary>Installs TLS material for the Link endpoint (<paramref name="administration"/> false) or the administration endpoint. Requires TLS when it carries a key or PSK.</summary>
    public async Task<string> SetTlsAsync(bool administration, string? certificatePem, string? privateKeyPem, string? caPem = null, string? pskIdentity = null, byte[]? psk = null,
        CancellationToken cancellationToken = default)
    {
        var request = LinkCbor.Map((1, administration ? 1UL : 0UL));
        if (certificatePem is not null) request[2] = certificatePem;
        if (privateKeyPem is not null) request[3] = privateKeyPem;
        if (caPem is not null) request[4] = caPem;
        if (pskIdentity is not null) request[5] = pskIdentity;
        if (psk is not null) request[6] = psk;
        var body = await RequestAsync(LinkType.SetTls, request, cancellationToken).ConfigureAwait(false);
        return body.GetValueOrDefault(0UL) as string ?? "";
    }

    public async Task<IReadOnlyList<PortGateDeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken = default)
    {
        var reply = await RequestRawAsync(LinkType.ListDevices, [], cancellationToken).ConfigureAwait(false);
        return (reply.GetValueOrDefault(4UL) as List<object?> ?? []).OfType<SortedDictionary<ulong, object?>>().Select(d => new PortGateDeviceInfo(
            d.GetValueOrDefault(0UL) as string ?? "", d.GetValueOrDefault(1UL) as string ?? "", d.GetValueOrDefault(2UL) as string ?? "",
            d.GetValueOrDefault(3UL) as string ?? "", d.GetValueOrDefault(4UL) as string ?? "", d.GetValueOrDefault(5UL) as string ?? "",
            d.GetValueOrDefault(6UL) as string ?? "", d.GetValueOrDefault(7UL) is true)).ToList();
    }

    public async Task<IReadOnlyList<PortGateAuditEvent>> ReadEventsAsync(ulong afterId = 0, int max = 200, CancellationToken cancellationToken = default)
    {
        var body = await RequestAsync(LinkType.ReadEvents, LinkCbor.Map((1, afterId), (2, (ulong)max)), cancellationToken).ConfigureAwait(false);
        return (body.GetValueOrDefault(0UL) as List<object?> ?? []).OfType<SortedDictionary<ulong, object?>>().Select(PortGateAuditEvent.From).ToList();
    }

    /// <summary>Provisions the AES-256 key that seals the durable capture (TLS only). Keep previous keys: older records keep their key id.</summary>
    public Task SetCaptureKeyAsync(uint keyId, byte[] key, CancellationToken cancellationToken = default) =>
        RequestAsync(LinkType.SetCaptureKey, LinkCbor.Map((1, (ulong)keyId), (2, key)), cancellationToken);

    /// <summary>
    /// Reads sealed capture records after <paramref name="afterSeq"/> and opens them with the keys of <paramref name="keys"/>
    /// (by key id). A record that does not authenticate is reported, never returned as data.
    /// </summary>
    public async Task<PortGateCapturePage> ReadCaptureAsync(string portId, Func<uint, byte[]?> keys, ulong afterSeq = 0, int max = 200, CancellationToken cancellationToken = default)
    {
        var body = await RequestAsync(LinkType.ReadCapture, LinkCbor.Map((1, portId), (2, afterSeq), (3, (ulong)max)), cancellationToken).ConfigureAwait(false);
        var agentId = body.GetValueOrDefault(4UL) as string ?? AgentId;
        var records = new List<PortGateCaptured>();
        var rejected = new List<ulong>();
        foreach (var record in (body.GetValueOrDefault(0UL) as List<object?> ?? []).OfType<SortedDictionary<ulong, object?>>())
        {
            var seq = record.GetValueOrDefault(0UL) as ulong? ?? 0;
            var keyId = (uint)(record.GetValueOrDefault(1UL) as ulong? ?? 0);
            var nonce = record.GetValueOrDefault(3UL) as byte[] ?? [];
            var sealedBytes = record.GetValueOrDefault(4UL) as byte[] ?? [];
            var key = keys(keyId);
            if (key is null || sealedBytes.Length < 16 || nonce.Length != 12)
            {
                rejected.Add(seq);
                continue;
            }
            var aad = LinkCbor.Encode(new List<object?> { "portgate-capture/1", agentId, portId, seq, (ulong)keyId });
            var plain = new byte[sealedBytes.Length - 16];
            try
            {
                using var gcm = new AesGcm(key, 16);
                gcm.Decrypt(nonce, sealedBytes.AsSpan(0, plain.Length), sealedBytes.AsSpan(plain.Length), plain, aad);
            }
            catch (CryptographicException)
            {
                rejected.Add(seq);
                continue;
            }
            if (!LinkCbor.TryDecode(plain, out var value) || value is not SortedDictionary<ulong, object?> map)
            {
                rejected.Add(seq);
                continue;
            }
            records.Add(new PortGateCaptured(seq, keyId,
                map.GetValueOrDefault(0UL) is byte[] { Length: 16 } epoch ? new Guid(epoch) : Guid.Empty,
                (int)(map.GetValueOrDefault(2UL) as ulong? ?? 0), (uint)(map.GetValueOrDefault(3UL) as ulong? ?? 0), map.GetValueOrDefault(4UL) as ulong? ?? 0,
                DateTimeOffset.FromUnixTimeMilliseconds((long)(map.GetValueOrDefault(5UL) as ulong? ?? 0)), map.GetValueOrDefault(6UL) as byte[] ?? [],
                map.GetValueOrDefault(7UL) as string));
        }
        var gaps = (body.GetValueOrDefault(3UL) as List<object?> ?? []).OfType<List<object?>>()
            .Select(g => new PortGateCaptureGap(g[0] as ulong? ?? 0, g[1] as ulong? ?? 0, g[2] as string ?? "")).ToList();
        return new PortGateCapturePage(records, body.GetValueOrDefault(1UL) as ulong? ?? 0, body.GetValueOrDefault(2UL) as ulong? ?? 0, gaps, rejected);
    }

    /// <summary>Confirms custody up to <paramref name="uptoSeq"/>: the agent deletes those records.</summary>
    public Task AckCaptureAsync(string portId, ulong uptoSeq, CancellationToken cancellationToken = default) =>
        RequestAsync(LinkType.AckCapture, LinkCbor.Map((1, portId), (2, uptoSeq)), cancellationToken);

    public Task RevokePortAsync(string portId, CancellationToken cancellationToken = default) => RequestAsync(LinkType.RevokePort, LinkCbor.Map((1, portId)), cancellationToken);

    public Task ReopenPortAsync(string portId, CancellationToken cancellationToken = default) => RequestAsync(LinkType.ReopenPort, LinkCbor.Map((1, portId)), cancellationToken);

    public Task UnblockControllerAsync(string controllerId, CancellationToken cancellationToken = default) =>
        RequestAsync(LinkType.UnblockController, LinkCbor.Map((1, controllerId)), cancellationToken);

    public Task RestartAsync(CancellationToken cancellationToken = default) => RequestAsync(LinkType.Restart, [], cancellationToken);

    /// <summary>Events pushed by the agent as they are recorded.</summary>
    public async IAsyncEnumerable<PortGateAuditEvent> EventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in _events.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            yield return item;
    }

    public async Task<SortedDictionary<ulong, object?>> RequestAsync(LinkType type, SortedDictionary<ulong, object?> fields, CancellationToken cancellationToken = default)
    {
        var reply = await RequestRawAsync(type, fields, cancellationToken).ConfigureAwait(false);
        return reply.GetValueOrDefault(4UL) as SortedDictionary<ulong, object?> ?? [];
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _owner.Dispose();
        await _readLoop.ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
        _sendGate.Dispose();
        _stop.Dispose();
    }

    private async Task<SortedDictionary<ulong, object?>> RequestRawAsync(LinkType type, SortedDictionary<ulong, object?> fields, CancellationToken cancellationToken)
    {
        var requestId = (ulong)Interlocked.Increment(ref _requestId);
        var completion = new TaskCompletionSource<SortedDictionary<ulong, object?>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _replies[requestId] = completion;
        if (_readLoop.IsCompleted)
            completion.TrySetException(_refusal ?? new IOException("Administration connection closed."));
        fields[0] = requestId;
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(LinkUnit.Control(type, 0, fields).Encode(), cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The agent may have refused this connection and closed it: report its refusal, not the broken pipe.
            await _readLoop.WaitAsync(TimeSpan.FromSeconds(2)).ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
            if (_refusal is not null)
                throw _refusal;
            throw;
        }
        finally
        {
            _sendGate.Release();
        }
        var reply = await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (reply.GetValueOrDefault(1UL) is not true)
            throw new PortGateAdminException(reply.GetValueOrDefault(2UL) as string ?? "PORTGATE-ADMIN", reply.GetValueOrDefault(3UL) as string ?? "request refused");
        return reply;
    }

    private async Task ReadLoopAsync(LinkDecoder decoder, List<LinkUnit> pending)
    {
        var buffer = new byte[16 * 1024];
        var units = new List<LinkUnit>(pending);
        try
        {
            while (true)
            {
                foreach (var unit in units)
                    Dispatch(unit);
                units.Clear();
                var count = await _stream.ReadAsync(buffer, _stop.Token).ConfigureAwait(false);
                if (count == 0 || decoder.Feed(buffer.AsSpan(0, count), units) != LinkDecodeError.None)
                    break;
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            _events.Writer.TryComplete();
            foreach (var reply in _replies.Values)
                reply.TrySetException(_refusal ?? new IOException("Administration connection closed."));
        }
    }

    private void Dispatch(LinkUnit unit)
    {
        var map = unit.Map();
        if (map is null)
            return;
        if (unit.Type == LinkType.AdminResult && _replies.TryRemove(map.GetValueOrDefault(0UL) as ulong? ?? 0, out var reply))
            reply.TrySetResult(map);
        else if (unit.Type == LinkType.AdminResult && map.GetValueOrDefault(1UL) is false)
        {
            // Refusal of the Hello itself (no request id): every request of this connection fails with it.
            _refusal = new PortGateAdminException(map.GetValueOrDefault(2UL) as string ?? "PORTGATE-ADMIN", map.GetValueOrDefault(3UL) as string ?? "");
            foreach (var pending in _replies.Values)
                pending.TrySetException(_refusal);
        }
        else if (unit.Type == LinkType.AdminEvent)
            _events.Writer.TryWrite(PortGateAuditEvent.From(map));
    }

    private static async Task<LinkUnit> ReceiveAsync(Stream stream, LinkDecoder decoder, List<LinkUnit> units, LinkType type, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        while (true)
        {
            var index = units.FindIndex(u => u.Type == type);
            if (index >= 0)
            {
                var unit = units[index];
                units.RemoveAt(index);
                return unit;
            }
            var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
                throw new IOException("Agent closed the administration connection.");
            if (decoder.Feed(buffer.AsSpan(0, count), units) != LinkDecodeError.None)
                throw new PortGateAdminException("PORTGATE-BAD-UNIT", decoder.Error.ToString());
        }
    }
}

/// <summary>Opened capture record: epoch, direction (0 instrument→agent, 1 agent→instrument), generation, offset per direction, owner at that moment.</summary>
public sealed record PortGateCaptured(ulong Seq, uint KeyId, Guid Epoch, int Direction, uint Generation, ulong Offset, DateTimeOffset CapturedAt, byte[] Data, string? Owner);

public sealed record PortGateCaptureGap(ulong From, ulong To, string Reason);

/// <param name="Rejected">Sequences that did not authenticate or whose key is unknown: evidence of tampering or a missing key.</param>
public sealed record PortGateCapturePage(IReadOnlyList<PortGateCaptured> Records, ulong First, ulong Last, IReadOnlyList<PortGateCaptureGap> Gaps, IReadOnlyList<ulong> Rejected);
