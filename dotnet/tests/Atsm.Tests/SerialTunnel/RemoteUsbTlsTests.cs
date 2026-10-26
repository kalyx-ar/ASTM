using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Atsm.Abstractions.Channels;
using Atsm.Channels;
using Atsm.Channels.SerialTunnel;

namespace Atsm.Tests.SerialTunnel;

public class RemoteUsbTlsTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private static async Task<RemoteUsbProcess> StartTlsAsync(int instrumentPort, TestPki pki, string? backupCertificateSha256 = null,
        IReadOnlyList<RemoteUsbController>? extraControllers = null, RemoteUsbSecurity? security = null, X509Certificate2? adminCertificate = null, bool restricted = true)
    {
        var agent = await RemoteUsbProcess.LaunchAsync();
        await agent.ConfigureAsync(RemoteUsbProcess.Configuration(instrumentPort, tls: true, backupCertificateSha256, extraControllers, security) with { Restricted = restricted },
            pki.ProvisionAsync, tls: true, adminCertificate);
        return agent;
    }

    [RemoteUsbFact]
    public async Task Bytes_cross_the_agent_over_mutual_tls()
    {
        using var pki = TestPki.Create();
        using var instrumentListener = new TcpListener(IPAddress.Loopback, 0);
        instrumentListener.Start();
        await using var agent = await StartTlsAsync(((IPEndPoint)instrumentListener.LocalEndpoint).Port, pki);
        using var instrument = await instrumentListener.AcceptTcpClientAsync().WaitAsync(Timeout);

        await using var channel = await SerialTunnelChannel.ConnectAsync("tls", pki.Endpoint(agent.Port, pki.Controller), new SerialTunnelOptions("edc-test", "architect-port"), CancellationToken.None);
        var lease = await channel.AcquireAsync(new ControlRequest("edc-test"), CancellationToken.None);
        await channel.ConfirmTransferAsync(lease, lease.TransferId, CancellationToken.None);
        await Task.Delay(200);

        var write = await channel.WriteAsync(new byte[] { 0x05, 0x00, 0xFF }, lease, CancellationToken.None);
        var received = new byte[3];
        await instrument.GetStream().ReadExactlyAsync(received).AsTask().WaitAsync(Timeout);
        await instrument.GetStream().WriteAsync(new byte[] { 0x06 });
        var reply = new byte[1];
        var read = await channel.ReadAsync(reply, CancellationToken.None).AsTask().WaitAsync(Timeout);

        Assert.Equal(WriteProgress.Completed, write.Progress);
        Assert.Equal(new byte[] { 0x05, 0x00, 0xFF }, received);
        Assert.Equal((1, (byte)0x06), (read.Count, reply[0]));
    }

    [RemoteUsbFact]
    public async Task Client_without_certificate_is_refused()
    {
        using var pki = TestPki.Create();
        using var instrumentListener = new TcpListener(IPAddress.Loopback, 0);
        instrumentListener.Start();
        await using var agent = await StartTlsAsync(((IPEndPoint)instrumentListener.LocalEndpoint).Port, pki);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            SerialTunnelChannel.ConnectAsync("tls", pki.Endpoint(agent.Port, clientCertificate: null), new SerialTunnelOptions("edc-test", "architect-port"), CancellationToken.None));
    }

    [RemoteUsbFact]
    public async Task Plain_client_cannot_use_a_tls_endpoint()
    {
        using var pki = TestPki.Create();
        using var instrumentListener = new TcpListener(IPAddress.Loopback, 0);
        instrumentListener.Start();
        await using var agent = await StartTlsAsync(((IPEndPoint)instrumentListener.LocalEndpoint).Port, pki);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            SerialTunnelChannel.ConnectAsync("plain", new TcpEndpointOptions("127.0.0.1", agent.Port), new SerialTunnelOptions("edc-test", "architect-port") { HandshakeTimeout = TimeSpan.FromSeconds(5) }, CancellationToken.None));
    }

    [RemoteUsbFact]
    public async Task Agent_server_certificate_from_an_unknown_ca_is_rejected_by_the_host()
    {
        using var pki = TestPki.Create();
        using var other = TestPki.Create();
        using var instrumentListener = new TcpListener(IPAddress.Loopback, 0);
        instrumentListener.Start();
        await using var agent = await StartTlsAsync(((IPEndPoint)instrumentListener.LocalEndpoint).Port, pki);

        // The host trusts another CA: the handshake must fail on the host side.
        var endpoint = pki.Endpoint(agent.Port, pki.Controller) with { CertificateValidation = other.Validator };
        await Assert.ThrowsAnyAsync<Exception>(() =>
            SerialTunnelChannel.ConnectAsync("tls", endpoint, new SerialTunnelOptions("edc-test", "architect-port"), CancellationToken.None));
    }

    [RemoteUsbFact]
    public async Task Certificate_must_identify_the_declared_controller()
    {
        using var pki = TestPki.Create();
        using var instrumentListener = new TcpListener(IPAddress.Loopback, 0);
        instrumentListener.Start();
        await using var agent = await StartTlsAsync(((IPEndPoint)instrumentListener.LocalEndpoint).Port, pki, backupCertificateSha256: Convert.ToHexStringLower(SHA256.HashData(pki.Backup.RawData)));

        // A valid certificate (same CA) whose CN is not the controller it claims to be.
        await using var intruder = await SerialTunnelChannel.ConnectAsync("tls", pki.Endpoint(agent.Port, pki.Intruder), new SerialTunnelOptions("edc-test", "architect-port"), CancellationToken.None);
        var refused = await Assert.ThrowsAsync<IOException>(() => intruder.AcquireAsync(new ControlRequest("edc-test"), CancellationToken.None).AsTask().WaitAsync(Timeout));
        Assert.Contains("REMOTEUSB-IDENTITY-MISMATCH", refused.Message);

        // A certificate pinned by its SHA-256 for edc-backup is accepted even though its CN differs.
        await using var backup = await SerialTunnelChannel.ConnectAsync("tls", pki.Endpoint(agent.Port, pki.Backup), new SerialTunnelOptions("edc-backup", "architect-port"), CancellationToken.None);
        var lease = await backup.AcquireAsync(new ControlRequest("edc-backup"), CancellationToken.None).AsTask().WaitAsync(Timeout);
        Assert.Equal("edc-backup", lease.ControllerId);
    }

    [RemoteUsbFact]
    public async Task Administration_is_restricted_to_the_certificate_that_configured_the_agent()
    {
        using var pki = TestPki.Create();
        using var instrumentListener = new TcpListener(IPAddress.Loopback, 0);
        instrumentListener.Start();
        await using var agent = await StartTlsAsync(((IPEndPoint)instrumentListener.LocalEndpoint).Port, pki, adminCertificate: pki.Controller);

        await using (var plain = await agent.AdminAsync())
            Assert.Equal("REMOTEUSB-ADMIN-NOT-AUTHORIZED", (await Assert.ThrowsAsync<RemoteUsbAdminException>(() => plain.GetStatusAsync())).Code);
        await using (var other = await agent.AdminAsync(tls: true, certificate: pki.Intruder))
            Assert.Equal("REMOTEUSB-ADMIN-NOT-AUTHORIZED", (await Assert.ThrowsAsync<RemoteUsbAdminException>(() => other.GetStatusAsync())).Code);

        await using var owner = await agent.AdminAsync(tls: true, certificate: pki.Controller);
        var status = await owner.GetStatusAsync();
        Assert.Equal(true, status[7UL]);  // restricted
        Assert.Equal(agent.AgentCertificateSha256, status[9UL]);
        var events = await owner.ReadEventsAsync();
        Assert.Equal(2, events.Count(e => e.Kind == "admin-denied"));
        Assert.Contains(events, e => e.Kind == "tls-updated" && e.Detail!.StartsWith("link", StringComparison.Ordinal));
    }

    [RemoteUsbFact]
    public async Task Kill_switch_is_audited_contested_and_the_port_closed_until_the_administrator_reopens_it()
    {
        using var pki = TestPki.Create();
        using var instrumentListener = new TcpListener(IPAddress.Loopback, 0);
        instrumentListener.Start();
        var port = ((IPEndPoint)instrumentListener.LocalEndpoint).Port;
        var configuration = new RemoteUsbConfiguration("test-agent", [new RemoteUsbPort("architect-port", "architect-01", RemoteUsbDevice.Tcp("127.0.0.1", port),
            [new RemoteUsbController("edc-test", 10), new RemoteUsbController("edc-backup", 20, Convert.ToHexStringLower(SHA256.HashData(pki.Backup.RawData)), RemoteUsbRights.KillSwitch),
             new RemoteUsbController("intruder", 30, null, RemoteUsbRights.Contest)]) { Lease = TimeSpan.FromSeconds(6) }])
        {
            PlatformProfile = "test",
            ListenHost = "127.0.0.1",
            Tls = true,
            Security = new RemoteUsbSecurity { KillSwitch = true, Contest = true, Quarantine = TimeSpan.FromSeconds(5) },
        };
        await using var agent = await RemoteUsbProcess.LaunchAsync();
        await agent.ConfigureAsync(configuration, pki.ProvisionAsync, tls: true);
        using var instrument = await instrumentListener.AcceptTcpClientAsync().WaitAsync(Timeout);

        await using var owner = await SerialTunnelChannel.ConnectAsync("owner", pki.Endpoint(agent.Port, pki.Controller), new SerialTunnelOptions("edc-test", "architect-port"), CancellationToken.None);
        await using var killer = await SerialTunnelChannel.ConnectAsync("killer", pki.Endpoint(agent.Port, pki.Backup), new SerialTunnelOptions("edc-backup", "architect-port"), CancellationToken.None);
        await using var observer = await SerialTunnelChannel.ConnectAsync("observer", pki.Endpoint(agent.Port, pki.Intruder), new SerialTunnelOptions("intruder", "architect-port"), CancellationToken.None);
        var lease = await owner.AcquireAsync(new ControlRequest("edc-test"), CancellationToken.None);
        using var observing = new CancellationTokenSource(Timeout);
        var observed = observer.SecurityEventsAsync(observing.Token).GetAsyncEnumerator(observing.Token);

        // An observer without the right cannot cut; the killer can, and the owner learns why.
        Assert.Equal("REMOTEUSB-KILL-NOT-ALLOWED", (await Assert.ThrowsAsync<SerialTunnelException>(() => observer.KillSwitchAsync("not mine", CancellationToken.None))).Code);
        var killId = await killer.KillSwitchAsync("maintenance test", CancellationToken.None).WaitAsync(Timeout);
        Assert.True(killId > 0);
        await WaitForEventAsync(owner, ChannelEventKind.OwnershipRevoked, "REMOTEUSB-KILLED");
        Assert.True(await observed.MoveNextAsync());
        Assert.Equal(("kill-switch", "edc-backup", "edc-test", lease.Generation), (observed.Current.Kind, observed.Current.Actor, observed.Current.Target, observed.Current.Generation));
        Assert.NotNull(observed.Current.Until);

        // Contest with secure close: the killer is dropped and blocked, the port is closed for everyone.
        Assert.True(await observer.ContestAsync(killId, "no maintenance window", secureClose: true, CancellationToken.None).WaitAsync(Timeout) > 0);
        await WaitForEventAsync(killer, ChannelEventKind.Closed, null);
        await using (var again = await SerialTunnelChannel.ConnectAsync("again", pki.Endpoint(agent.Port, pki.Backup), new SerialTunnelOptions("edc-backup", "architect-port"), CancellationToken.None))
        {
            var blocked = await Assert.ThrowsAnyAsync<Exception>(() => again.AcquireAsync(new ControlRequest("edc-backup"), CancellationToken.None).AsTask().WaitAsync(Timeout));
            Assert.Contains("REMOTEUSB-CONTROLLER-BLOCKED", blocked.Message);
        }
        Assert.Equal("REMOTEUSB-SECURE-CLOSED", (await Assert.ThrowsAsync<SerialTunnelException>(() => owner.AcquireAsync(new ControlRequest("edc-test"), CancellationToken.None).AsTask().WaitAsync(Timeout))).Code);

        await using var admin = await agent.AdminAsync(tls: true);
        await admin.ReopenPortAsync("architect-port");
        var reopened = await owner.AcquireAsync(new ControlRequest("edc-test"), CancellationToken.None).AsTask().WaitAsync(Timeout);
        Assert.True(reopened.Generation > lease.Generation);
        var kinds = (await admin.ReadEventsAsync()).Select(e => e.Kind).ToList();
        Assert.Equal(["kill-switch-denied", "kill-switch", "contest", "secure-close", "reopen"], kinds.Where(k => k is "kill-switch-denied" or "kill-switch" or "contest" or "secure-close" or "reopen"));
    }

    private static async Task WaitForEventAsync(SerialTunnelChannel channel, ChannelEventKind kind, string? code)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        await foreach (var item in channel.EventsAsync(timeout.Token))
            if (item.Kind == kind && (code is null || item.Code == code))
                return;
    }

    /// <summary>Throw-away PKI: CA, agent server certificate and controller client certificates, written as PEM for the agent.</summary>
    private sealed class TestPki : IDisposable
    {
        private TestPki(X509Certificate2 ca, X509Certificate2 server, X509Certificate2 controller, X509Certificate2 intruder, X509Certificate2 backup, string directory)
        {
            Ca = ca;
            Server = server;
            Controller = controller;
            Intruder = intruder;
            Backup = backup;
            Directory = directory;
        }

        public X509Certificate2 Ca { get; }

        public X509Certificate2 Server { get; }

        public X509Certificate2 Controller { get; }

        public X509Certificate2 Intruder { get; }

        public X509Certificate2 Backup { get; }

        public string Directory { get; }

        public static TestPki Create()
        {
            var directory = Path.Combine(Path.GetTempPath(), "atsm-pki-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var caRequest = new CertificateRequest("CN=ATSM Test CA", caKey, HashAlgorithmName.SHA256);
            caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
            var ca = caRequest.CreateSelfSigned(notBefore, notBefore.AddDays(2));

            var server = Issue(ca, notBefore, "CN=localhost", serverAuth: true, directory, "agent");
            var controller = Issue(ca, notBefore, "CN=edc-test", serverAuth: false, directory, null);
            var intruder = Issue(ca, notBefore, "CN=intruder", serverAuth: false, directory, null);
            var backup = Issue(ca, notBefore, "CN=backup-host", serverAuth: false, directory, null);
            File.WriteAllText(Path.Combine(directory, "ca.pem"), ca.ExportCertificatePem());
            return new TestPki(ca, server, controller, intruder, backup, directory);
        }

        /// <summary>Installs the agent certificate, key and client CA for the Link endpoint (over the TLS administration channel).</summary>
        public Task ProvisionAsync(RemoteUsbAdminClient admin) => admin.SetTlsAsync(false,
            File.ReadAllText(Path.Combine(Directory, "agent.pem")), File.ReadAllText(Path.Combine(Directory, "agent.key")), File.ReadAllText(Path.Combine(Directory, "ca.pem")));

        public TcpEndpointOptions Endpoint(int port, X509Certificate2? clientCertificate) => new("127.0.0.1", port, TlsMode.Tls)
        {
            TargetHost = "localhost",
            ClientCertificates = clientCertificate is null ? null : [clientCertificate],
            CertificateValidation = Validator,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };

        /// <summary>Accepts only certificates that chain to this CA and match the requested host name.</summary>
        public bool Validator(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
        {
            if (certificate is null || (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
                return false;
            using var custom = new X509Chain();
            custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            custom.ChainPolicy.CustomTrustStore.Add(Ca);
            custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return custom.Build(new X509Certificate2(certificate));
        }

        public void Dispose()
        {
            foreach (var certificate in new[] { Ca, Server, Controller, Intruder, Backup })
                certificate.Dispose();
            try
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        private static X509Certificate2 Issue(X509Certificate2 ca, DateTimeOffset notBefore, string subject, bool serverAuth, string directory, string? fileName)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                [new Oid(serverAuth ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2")], false));
            if (serverAuth)
            {
                var names = new SubjectAlternativeNameBuilder();
                names.AddDnsName("localhost");
                names.AddIpAddress(IPAddress.Loopback);
                request.CertificateExtensions.Add(names.Build());
            }
            var serial = RandomNumberGenerator.GetBytes(12);
            using var issued = request.Create(ca, notBefore, notBefore.AddDays(1), serial);
            using var withKey = issued.CopyWithPrivateKey(key);
            if (fileName is not null)
            {
                File.WriteAllText(Path.Combine(directory, fileName + ".pem"), withKey.ExportCertificatePem());
                File.WriteAllText(Path.Combine(directory, fileName + ".key"), key.ExportPkcs8PrivateKeyPem());
            }
            // Re-import through PKCS#12 so the Windows TLS stack can use the private key for client authentication.
            return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null);
        }
    }
}
