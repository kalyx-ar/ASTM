using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;

namespace Atsm.Channels.SerialTunnel;

public sealed class RemoteUsbAdminException(string code, string message) : Exception($"{code}: {message}")
{
    public string Code { get; } = code;
}

/// <summary>Serial port reported by <see cref="RemoteUsbAdminClient.ListDevicesAsync"/> with its physical identity.</summary>
public sealed record RemoteUsbDeviceInfo(string Name, string FriendlyName, string UsbVid, string UsbPid, string UsbSerial, string Location, string InstanceId, bool InUse);

public sealed record RemoteUsbEvent(ulong Id, DateTimeOffset Time, string Kind, string? PortId, string? Actor, string? Target, uint Generation, string? Reason,
    DateTimeOffset? Until, bool Uncertain, string? Detail)
{
    internal static RemoteUsbEvent From(SortedDictionary<ulong, object?> map) => new(
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
/// Client of the RemoteUSB administration endpoint (spec/admin-v1.md). The server keeps versions and history of the
/// configuration; the agent keeps only the one in force. With TLS, the agent certificate is pinned by its SHA-256.
/// </summary>
public sealed class RemoteUsbAdminClient : IAsyncDisposable
{
    public const int DefaultPort = 28240;

    private readonly TcpClient _client;
    private readonly Stream _stream;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<SortedDictionary<ulong, object?>>> _replies = new();
    private readonly Channel<RemoteUsbEvent> _events = Channel.CreateUnbounded<RemoteUsbEvent>();
    private readonly CancellationTokenSource _stop = new();
    private Task _readLoop = Task.CompletedTask;
    private long _requestId;
    private Exception? _refusal;

    private RemoteUsbAdminClient(TcpClient client, Stream stream, string agentId)
    {
        _client = client;
        _stream = stream;
        AgentId = agentId;
    }

    public string AgentId { get; }

    /// <summary>
    /// Connects and identifies as <paramref name="serverId"/>. <paramref name="agentCertificateSha256"/> pins the agent
    /// certificate (TLS); <paramref name="clientCertificate"/> identifies this server when the agent is restricted to it.
    /// Without either the channel is plain text.
    /// </summary>
    public static async Task<RemoteUsbAdminClient> ConnectAsync(string host, int port, string serverId, string? agentCertificateSha256 = null,
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
            var decoder = new LinkDecoder(1024 * 1024);
            var units = new List<LinkUnit>();
            await stream.WriteAsync(LinkUnit.Control(LinkType.Hello, 0, LinkCbor.Map((0, 1UL), (1, "admin"), (2, serverId))).Encode(), timeout.Token).ConfigureAwait(false);
            var hello = await ReceiveAsync(stream, decoder, units, LinkType.Hello, timeout.Token).ConfigureAwait(false);
            var admin = new RemoteUsbAdminClient(client, stream, hello.Map()?.GetValueOrDefault(2UL) as string ?? "");
            admin._readLoop = Task.Run(() => admin.ReadLoopAsync(decoder, units));
            return admin;
        }
        catch
        {
            client.Dispose();
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
    public async Task<int> SetConfigAsync(ulong revision, RemoteUsbConfiguration configuration, CancellationToken cancellationToken = default)
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

    public async Task<IReadOnlyList<RemoteUsbDeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken = default)
    {
        var reply = await RequestRawAsync(LinkType.ListDevices, [], cancellationToken).ConfigureAwait(false);
        return (reply.GetValueOrDefault(4UL) as List<object?> ?? []).OfType<SortedDictionary<ulong, object?>>().Select(d => new RemoteUsbDeviceInfo(
            d.GetValueOrDefault(0UL) as string ?? "", d.GetValueOrDefault(1UL) as string ?? "", d.GetValueOrDefault(2UL) as string ?? "",
            d.GetValueOrDefault(3UL) as string ?? "", d.GetValueOrDefault(4UL) as string ?? "", d.GetValueOrDefault(5UL) as string ?? "",
            d.GetValueOrDefault(6UL) as string ?? "", d.GetValueOrDefault(7UL) is true)).ToList();
    }

    public async Task<IReadOnlyList<RemoteUsbEvent>> ReadEventsAsync(ulong afterId = 0, int max = 200, CancellationToken cancellationToken = default)
    {
        var body = await RequestAsync(LinkType.ReadEvents, LinkCbor.Map((1, afterId), (2, (ulong)max)), cancellationToken).ConfigureAwait(false);
        return (body.GetValueOrDefault(0UL) as List<object?> ?? []).OfType<SortedDictionary<ulong, object?>>().Select(RemoteUsbEvent.From).ToList();
    }

    public Task RevokePortAsync(string portId, CancellationToken cancellationToken = default) => RequestAsync(LinkType.RevokePort, LinkCbor.Map((1, portId)), cancellationToken);

    public Task ReopenPortAsync(string portId, CancellationToken cancellationToken = default) => RequestAsync(LinkType.ReopenPort, LinkCbor.Map((1, portId)), cancellationToken);

    public Task UnblockControllerAsync(string controllerId, CancellationToken cancellationToken = default) =>
        RequestAsync(LinkType.UnblockController, LinkCbor.Map((1, controllerId)), cancellationToken);

    public Task RestartAsync(CancellationToken cancellationToken = default) => RequestAsync(LinkType.Restart, [], cancellationToken);

    /// <summary>Events pushed by the agent as they are recorded.</summary>
    public async IAsyncEnumerable<RemoteUsbEvent> EventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
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
        _client.Dispose();
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
        finally
        {
            _sendGate.Release();
        }
        var reply = await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (reply.GetValueOrDefault(1UL) is not true)
            throw new RemoteUsbAdminException(reply.GetValueOrDefault(2UL) as string ?? "REMOTEUSB-ADMIN", reply.GetValueOrDefault(3UL) as string ?? "request refused");
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
            _refusal = new RemoteUsbAdminException(map.GetValueOrDefault(2UL) as string ?? "REMOTEUSB-ADMIN", map.GetValueOrDefault(3UL) as string ?? "");
            foreach (var pending in _replies.Values)
                pending.TrySetException(_refusal);
        }
        else if (unit.Type == LinkType.AdminEvent)
            _events.Writer.TryWrite(RemoteUsbEvent.From(map));
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
                throw new RemoteUsbAdminException("REMOTEUSB-BAD-UNIT", decoder.Error.ToString());
        }
    }
}
