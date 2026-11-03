using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Astm.Abstractions.Transport;
using Astm.Transport;
using PortGate.Client;
using Astm.Abstractions.Tracing;
using System.Text.Json;

namespace Astm.Service;

public interface IServiceTransportFactory
{
    ValueTask<IReadOnlyList<string>> OwnershipKeysAsync(InstrumentConfiguration configuration, CancellationToken token);
    ValueTask<ServiceConnection> OpenAsync(InstrumentConfiguration configuration, ProfileBinding binding, ITraceSink trace, CancellationToken token);
}
public sealed class ServiceConnection(Stream stream, TransportTenure? tenure = null, IAsyncDisposable? owner = null) : IAsyncDisposable
{
    private int _disposed;
    public Stream Stream { get; } = stream;
    public TransportTenure? Tenure { get; } = tenure;
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await Stream.DisposeAsync().ConfigureAwait(false); }
        finally { if (owner is not null) await owner.DisposeAsync().ConfigureAwait(false); }
    }
}

public sealed class ServiceTransportFactory : IServiceTransportFactory
{
    public async ValueTask<IReadOnlyList<string>> OwnershipKeysAsync(InstrumentConfiguration config, CancellationToken token)
    {
        var target = config.Transport;
        if (target.Kind == "messages") return [];
        if (target.Kind == "serial")
        {
            var port = target.PortName ?? throw new ServiceException("transport.serial.port-required");
            return ["serial:" + (OperatingSystem.IsWindows() ? port.Replace("\\\\.\\", "", StringComparison.Ordinal).ToUpperInvariant() : Path.GetFullPath(port))];
        }
        if (target.Kind is not ("tcp" or "tcp-listen" or "portgate") || target.Host is null || target.Port is < 1 or > 65535)
            throw new ServiceException("transport.invalid");
        var addresses = await Dns.GetHostAddressesAsync(target.Host, token).ConfigureAwait(false);
        if (addresses.Length == 0) throw new ServiceException("transport.address.unavailable");
        return addresses.Select(a => target.Kind + ":" + a.MapToIPv6() + ":" + target.Port + ":" + target.PortId).Order(StringComparer.Ordinal).ToArray();
    }
    public async ValueTask<ServiceConnection> OpenAsync(InstrumentConfiguration config, ProfileBinding binding, ITraceSink trace, CancellationToken token)
    {
        var target = config.Transport;
        if (target.ConnectTimeoutMs is < 1 or > 300000) throw new ServiceException("transport.timeout.invalid");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(target.ConnectTimeoutMs);
        var id = config.Id;
        var serial = target.Serial ?? binding.Transport?.Serial?.Default;
        if (serial is not null && binding.Transport.Validate(serial).Count > 0) throw new ServiceException("transport.serial.settings");
        if (target.Kind == "serial") return new(LocalSerial.Open(target.PortName!, serial ?? throw new ServiceException("transport.serial.settings-required")));
        var tls = Tls(target);
        var certificates = new CertificateLifetime(tls);
        var transferred = false;
        try
        {
            if (target.Kind == "tcp")
            {
                var stream = await LocalTransportProvider.ConnectAsync(
                    new(target.Host!, target.Port) { Tls = tls, ConnectTimeout = TimeSpan.FromMilliseconds(target.ConnectTimeoutMs) }, deadline.Token).ConfigureAwait(false);
                transferred = true; return new(stream, owner: certificates);
            }
            if (target.Kind == "tcp-listen")
            {
                if (!IPAddress.TryParse(target.Host, out var address)) throw new ServiceException("transport.listen.numeric-address-required");
                var stream = await new LocalTransportProvider().OpenAsync(new TcpListenTarget(id, new(address, target.Port)) { Tls = tls }, deadline.Token).ConfigureAwait(false);
                transferred = true; return new(stream, owner: certificates);
            }
            if (target.Kind != "portgate" || string.IsNullOrWhiteSpace(target.PortId)) throw new ServiceException("transport.invalid");
            var channel = await PortGateChannel.ConnectAsync(new(target.Host!, target.Port) { Tls = tls },
                new("astm-service/" + config.Id, target.PortId), deadline.Token).ConfigureAwait(false);
            try
            {
                if (serial is not null && channel.Port.Serial != serial) throw new ServiceException("transport.portgate.settings-mismatch");
                var drains = new PortGateDrains(channel, trace);
                try
                {
                    var lease = await channel.AcquireAsync(cancellationToken: deadline.Token).ConfigureAwait(false);
                    transferred = true;
                    return new(channel.Stream!, new(channel.AgentEpoch, lease.Generation, lease.TransferId, lease.UncertainTx, lease.UnownedRxBytes),
                        new CombinedLifetime(drains, certificates));
                }
                catch { await drains.DisposeAsync().ConfigureAwait(false); throw; }
            }
            catch { await channel.DisposeAsync().ConfigureAwait(false); throw; }
        }
        finally
        {
            if (!transferred) await certificates.DisposeAsync().ConfigureAwait(false);
        }
    }
    private sealed class CertificateLifetime(TlsOptions? tls) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            tls?.ServerCertificate?.Dispose();
            if (tls?.ClientCertificates is not null) foreach (var certificate in tls.ClientCertificates) certificate.Dispose();
            return ValueTask.CompletedTask;
        }
    }
    private sealed class CombinedLifetime(IAsyncDisposable first, IAsyncDisposable second) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try { await first.DisposeAsync().ConfigureAwait(false); }
            finally { await second.DisposeAsync().ConfigureAwait(false); }
        }
    }
    private static TlsOptions? Tls(TransportConfiguration target)
    {
        if (!target.Tls)
        {
            if (target.CertificateSha256 is not null || target.ClientCertificatePath is not null) throw new ServiceException("transport.tls.required");
            return null;
        }
        var pin = target.CertificateSha256;
        if (pin is not null && (pin.Length != 64 || !pin.All(Uri.IsHexDigit))) throw new ServiceException("transport.tls.pin.invalid");
        RemoteCertificateValidationCallback? validation = pin is null ? null : (_, certificate, _, errors) => certificate is not null
            && (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0
            && string.Equals(certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256), pin, StringComparison.OrdinalIgnoreCase);
        var certificate = target.ClientCertificatePath is null ? null : X509CertificateLoader.LoadPkcs12FromFile(target.ClientCertificatePath, null,
            X509KeyStorageFlags.EphemeralKeySet);
        if (target.Kind == "tcp-listen" && certificate is null) throw new ServiceException("transport.tls.server-certificate-required");
        return new() { TargetHost = target.TlsTargetHost ?? target.Host, CertificateValidation = validation,
            ServerCertificate = target.Kind == "tcp-listen" ? certificate : null,
            ClientCertificates = target.Kind != "tcp-listen" && certificate is not null ? new() { certificate } : null };
    }
    private sealed class PortGateDrains : IAsyncDisposable
    {
        private readonly PortGateChannel _channel;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task[] _tasks;
        private readonly ITraceSink _trace;
        private int _disposed;
        public PortGateDrains(PortGateChannel channel, ITraceSink trace)
        {
            _channel = channel; _trace = trace;
            _tasks = [Drain(channel.EventsAsync(_stop.Token)), Drain(channel.SecurityEventsAsync(_stop.Token)),
                Drain(channel.PortStatesAsync(_stop.Token)), Drain(channel.ObservationsAsync(_stop.Token))];
        }
        private async Task Drain<T>(IAsyncEnumerable<T> source)
        {
            try
            {
                await foreach (var item in source.WithCancellation(_stop.Token).ConfigureAwait(false))
                    _trace.Write(TraceDirection.Event, [], "portgate." + typeof(T).Name, JsonSerializer.Serialize(item, ServiceJson.Type<T>()));
            }
            catch (Exception) { await _channel.DisposeAsync().ConfigureAwait(false); }
        }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _stop.Cancel();
            try { await _channel.DisposeAsync().ConfigureAwait(false); }
            finally { await Task.WhenAll(_tasks).ConfigureAwait(false); _stop.Dispose(); }
        }
    }
}
