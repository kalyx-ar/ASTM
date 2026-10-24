using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Atsm.Abstractions.Channels;

namespace Atsm.Channels;

public enum TlsMode { Plain, Tls }

/// <summary>
/// TLS is decided per endpoint and never downgraded. Certificate validation uses the platform trust store
/// unless the endpoint pins a validation callback.
/// </summary>
public sealed record TcpEndpointOptions(string Host, int Port, TlsMode Mode = TlsMode.Plain)
{
    public string? TargetHost { get; init; }

    public X509CertificateCollection? ClientCertificates { get; init; }

    public X509Certificate? ServerCertificate { get; init; }

    public bool RequireClientCertificate { get; init; }

    public RemoteCertificateValidationCallback? CertificateValidation { get; init; }

    public SslProtocols Protocols { get; init; } = SslProtocols.Tls12 | SslProtocols.Tls13;

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>TCP connection to an instrument (or to a vendor gateway), as client or as server.</summary>
public static class TcpNativeChannel
{
    public static async Task<StreamInstrumentChannel> ConnectAsync(string channelId, TcpEndpointOptions options, string? instrumentId, CancellationToken cancellationToken)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(options.ConnectTimeout);
                await client.ConnectAsync(options.Host, options.Port, timeout.Token).ConfigureAwait(false);
            }
            var stream = await SecureAsync(client.GetStream(), options, asServer: false, cancellationToken).ConfigureAwait(false);
            return new StreamInstrumentChannel(Describe(channelId, options, instrumentId), new OwnedStream(stream, client));
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Waits for the instrument to connect to <paramref name="listener"/> (instrument acting as TCP client).</summary>
    public static async Task<StreamInstrumentChannel> AcceptAsync(string channelId, TcpListener listener, TcpEndpointOptions options, string? instrumentId, CancellationToken cancellationToken)
    {
        var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            client.NoDelay = true;
            var stream = await SecureAsync(client.GetStream(), options, asServer: true, cancellationToken).ConfigureAwait(false);
            return new StreamInstrumentChannel(Describe(channelId, options, instrumentId), new OwnedStream(stream, client));
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static ChannelDescriptor Describe(string channelId, TcpEndpointOptions options, string? instrumentId) =>
        new(channelId, ChannelKind.TcpNative, $"{(options.Mode == TlsMode.Tls ? "tls" : "tcp")}://{options.Host}:{options.Port}", instrumentId);

    internal static async Task<Stream> SecureAsync(NetworkStream network, TcpEndpointOptions options, bool asServer, CancellationToken cancellationToken)
    {
        if (options.Mode == TlsMode.Plain)
            return network;
        var tls = new SslStream(network, leaveInnerStreamOpen: false, options.CertificateValidation);
        if (asServer)
        {
            var certificate = options.ServerCertificate ?? throw new InvalidOperationException("TLS server endpoint requires a server certificate.");
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                ClientCertificateRequired = options.RequireClientCertificate,
                EnabledSslProtocols = options.Protocols,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = options.TargetHost ?? options.Host,
                ClientCertificates = options.ClientCertificates,
                EnabledSslProtocols = options.Protocols,
            }, cancellationToken).ConfigureAwait(false);
        }
        return tls;
    }

    /// <summary>Disposes the socket together with the stream.</summary>
    private sealed class OwnedStream(Stream inner, IDisposable owner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => inner.WriteAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                owner.Dispose();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            owner.Dispose();
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
