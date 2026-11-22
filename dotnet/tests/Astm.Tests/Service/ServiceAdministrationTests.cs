using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Astm.Abstractions.Transport;
using Astm.Plugin.Abbott.Architect;
using Astm.Service;
using Astm.Service.Host;
using Astm.Testing;
using Astm.Tests.PortGate;
using PortGate.Client;
using static Astm.Tests.Service.ServiceTests;

namespace Astm.Tests.Service;

/// <summary>PortGate listing and authenticated configuration (Documentacion/ADMINISTRACION-SERVICIO.md). Synthetic only.</summary>
public sealed class ServiceAdministrationTests
{
    private const string Asi = ArchitectPlugin.AsiProfileId;
    private static readonly SerialSettings AsiSerial = new(9600);

    [Fact]
    public async Task Hello_announces_administration_only_when_enabled_and_unknown_methods_are_unsupported()
    {
        await using (var plain = new AdminRig(administration: false))
        {
            var hello = await plain.Client.Call<ServiceHello>("service.hello", new Ok());
            Assert.Equal("astm-service/1.0", hello.Protocol);
            Assert.Contains("gateways.list", hello.Methods); Assert.Contains("instruments.list", hello.Methods);
            Assert.DoesNotContain("instruments.put", hello.Methods); Assert.DoesNotContain("admin.challenge", hello.Methods);
            Assert.Equal("method.unsupported", (await Assert.ThrowsAsync<ServiceException>(() => plain.Client.Call<Ok>("admin.challenge", new Ok()))).Code);
            Assert.Equal("method.unsupported", (await Assert.ThrowsAsync<ServiceException>(() => plain.Client.Call<Ok>("nothing.here", new Ok()))).Code);
        }
        await using var rig = new AdminRig();
        var enabled = await rig.Client.Call<ServiceHello>("service.hello", new Ok());
        Assert.Equal("astm-service/1.0", enabled.Protocol);
        Assert.Contains("admin.challenge", enabled.Methods); Assert.Contains("instruments.put", enabled.Methods); Assert.Contains("gateways.remove", enabled.Methods);
    }

    [Fact]
    public void Administration_without_a_store_or_with_an_invalid_key_is_refused_at_startup()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var directory = Path.Combine(Path.GetTempPath(), "astm-admin-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var configuration = new ServiceConfiguration("unused", Path.Combine(directory, "owner"), [])
            { Administration = new(Path.Combine(directory, "audit", "audit.jsonl"), [new("lis", Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), ["instruments"])]) };
            Assert.Equal("service.administration.store-required",
                Assert.Throws<ArgumentException>(() => new CommunicationService(configuration, BundledServiceProfiles.Create())).Message);
            using var other = ECDsa.Create(ECCurve.NamedCurves.nistP384);
            var weak = configuration with { Administration = configuration.Administration with { Principals = [new("lis", Convert.ToBase64String(other.ExportSubjectPublicKeyInfo()), ["instruments"])] } };
            Assert.Equal("administration.principal.key", Assert.Throws<ServiceException>(() => new CommunicationService(weak, BundledServiceProfiles.Create())).Code);
            var rights = configuration with { Administration = configuration.Administration with { Principals = [new("lis", configuration.Administration.Principals[0].PublicKey, ["sessions"])] } };
            Assert.Throws<ArgumentException>(() => new CommunicationService(rights, BundledServiceProfiles.Create()));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Principal_signature_is_required_single_use_and_locks_after_three_failures()
    {
        await using var rig = new AdminRig();
        Assert.Equal("admin.challenge.required", await Code(rig.Client.Call<AdminIdentity>("admin.authenticate", new AdminAuthenticateRequest("lis", "AA=="))));
        var challenge = await rig.Client.Call<AdminChallenge>("admin.challenge", new Ok());
        Assert.Equal(rig.InstanceId, challenge.InstanceId); Assert.Equal("ES256-DER", challenge.Algorithm);
        Assert.Equal(32, Convert.FromBase64String(challenge.Nonce).Length);
        var signature = ServiceAdministration.Sign(rig.Key, challenge.InstanceId, challenge.Nonce, "lis");
        var identity = await rig.Client.Call<AdminIdentity>("admin.authenticate", new AdminAuthenticateRequest("lis", signature));
        Assert.Equal("lis", identity.PrincipalId); Assert.Equal(["instruments", "gateways"], identity.Rights);
        // The nonce is consumed: replaying the same signature fails.
        Assert.Equal("admin.challenge.required", await Code(rig.Client.Call<AdminIdentity>("admin.authenticate", new AdminAuthenticateRequest("lis", signature))));

        await using var attacker = rig.Connect();
        using var wrong = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var nonce = (await attacker.Call<AdminChallenge>("admin.challenge", new Ok())).Nonce;
            var principal = attempt == 0 ? "unknown" : "lis";
            var forged = attempt == 2 ? signature : ServiceAdministration.Sign(wrong, rig.InstanceId, nonce, principal);
            Assert.Equal("admin.unauthorized", await Code(attacker.Call<AdminIdentity>("admin.authenticate", new AdminAuthenticateRequest(principal, forged))));
        }
        Assert.Equal("admin.locked", await Code(attacker.Call<AdminChallenge>("admin.challenge", new Ok())));
        Assert.Equal("admin.unauthenticated", await Code(attacker.Call<InstrumentPutResult>("instruments.put",
            new InstrumentPutRequest(0, new("x", Asi, new("serial") { PortName = "COM7" })))));
        var audit = rig.Audit();
        Assert.Contains(audit, r => r.Action == "authenticate" && r.Outcome == "accepted" && r.PrincipalId == "lis");
        Assert.Equal(3, audit.Count(r => r.Action == "authenticate" && r.Code == "admin.unauthorized"));
        Assert.DoesNotContain(audit, r => r.PrincipalId == "unknown");
        Assert.Contains(audit, r => r.Action == "instruments.put" && r.Outcome == "denied" && r.Code == "admin.unauthenticated");
    }

    [Fact]
    public async Task Rights_are_enforced_per_principal()
    {
        await using var rig = new AdminRig();
        await rig.Login(rig.Client, rig.GatewaysOnly, "gateways-only");
        Assert.Equal("admin.forbidden", await Code(rig.Client.Call<InstrumentPutResult>("instruments.put",
            new InstrumentPutRequest(0, new("x", Asi, new("serial") { PortName = "COM7" })))));
        var put = await rig.Client.Call<GatewayPutResult>("gateways.put", new GatewayPutRequest(0, new("pc-1", "127.0.0.1", 28241)));
        Assert.Equal(1, put.Revision);
    }

    [Fact]
    public async Task Instruments_are_added_edited_and_removed_atomically_with_revision_and_audit()
    {
        await using var rig = new AdminRig();
        await rig.Login(rig.Client);
        var view = await rig.Client.Call<ConfigurationView>("instruments.list", new Ok());
        Assert.Equal(0, view.Revision); Assert.Single(view.Instruments);

        var instrument = new InstrumentConfiguration("architect-1", Asi, new("serial") { PortName = "COM7" });
        var dry = await rig.Client.Call<InstrumentPutResult>("instruments.put", new InstrumentPutRequest(0, instrument) { DryRun = true });
        Assert.Equal(0, dry.Revision); Assert.Equal("not-applicable", dry.Verification);
        Assert.Equal(0, rig.Stored().Revision); Assert.DoesNotContain(rig.Audit(), r => r.Action == "instruments.put");

        var added = await rig.Client.Call<InstrumentPutResult>("instruments.put", new InstrumentPutRequest(0, instrument));
        Assert.Equal(1, added.Revision);
        var stored = rig.Stored();
        Assert.Equal(1, stored.Revision);
        Assert.Equal("COM7", stored.Instruments.Single(i => i.Id == "architect-1").Transport.PortName);
        Assert.Equal(rig.OwnershipText, stored.OwnershipDirectory); // Persisted as configured, not normalized.
        Assert.NotNull(stored.Administration);

        Assert.Equal("configuration.revision-mismatch", await Code(rig.Client.Call<InstrumentPutResult>("instruments.put", new InstrumentPutRequest(0, instrument))));
        Assert.Equal("request.invalid", await Code(rig.Client.Call<InstrumentPutResult>("instruments.put",
            JsonSerializer.SerializeToElement(new { instrument = new { id = "x", profileId = Asi, transport = new { kind = "messages" } } }))));

        var edited = await rig.Client.Call<InstrumentPutResult>("instruments.put",
            new InstrumentPutRequest(1, instrument with { Transport = new("serial") { PortName = "COM8", Serial = AsiSerial with { Dtr = true } } }));
        Assert.Equal(2, edited.Revision);
        Assert.Equal("COM8", rig.Stored().Instruments.Single(i => i.Id == "architect-1").Transport.PortName);

        var removed = await rig.Client.Call<RevisionResult>("instruments.remove", new InstrumentRemoveRequest(2, "architect-1"));
        Assert.Equal(3, removed.Revision);
        Assert.DoesNotContain(rig.Stored().Instruments, i => i.Id == "architect-1");
        Assert.Equal("instrument.unknown", await Code(rig.Client.Call<RevisionResult>("instruments.remove", new InstrumentRemoveRequest(3, "architect-1"))));

        var audit = rig.Audit();
        var put = audit.Where(r => r.Action == "instruments.put").ToArray();
        Assert.Equal(["pending", "applied", "pending", "applied"], put.Select(r => r.Outcome));
        Assert.All(put, r => { Assert.Equal("lis", r.PrincipalId); Assert.Equal("architect-1", r.Target); Assert.Equal(rig.InstanceId, r.InstanceId); });
        Assert.Null(put[0].BeforeSha256); Assert.NotNull(put[2].BeforeSha256);
        Assert.Equal("COM8", put[2].After!.Value.GetProperty("transport").GetProperty("portName").GetString());
        Assert.Equal((1L, 2L), (put[2].RevisionBefore!.Value, put[2].RevisionAfter!.Value));
        Assert.Contains(audit, r => r.Action == "instruments.remove" && r.Outcome == "applied" && r.RevisionAfter == 3);
        Assert.DoesNotContain(rig.DirectoryFiles(), f => f.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("bad id", Asi, "serial", "COM7", null, "instrument.id.invalid")]
    [InlineData("x", "unknown.profile", "serial", "COM7", null, "profile.unknown")]
    [InlineData("x", Asi, "usb", null, null, "transport.kind.unknown")]
    [InlineData("x", Asi, "serial", "/etc/passwd", null, "transport.serial.port-invalid")]
    [InlineData("x", Asi, "serial", "COM9", 19200, "transport.serial.settings")]
    [InlineData("x", Asi, "messages", "COM9", null, "transport.fields.invalid")]
    [InlineData("x", ArchitectHl7Plugin.ProfileId, "messages", null, null, "profile.timing.required")]
    [InlineData("x", Asi, "serial", "COM3", null, "transport.resource.duplicate")]
    public async Task Invalid_instruments_are_rejected_without_changes(string id, string profile, string kind, string? portName, int? baud, string code)
    {
        if (!OperatingSystem.IsWindows() && portName is "COM7" or "COM9" or "COM3") return; // Serial names are platform paths elsewhere.
        await using var rig = new AdminRig();
        await rig.Login(rig.Client);
        var instrument = new InstrumentConfiguration(id, profile, new(kind) { PortName = portName, Serial = baud is { } b ? new SerialSettings(b) : null });
        Assert.Equal(code, await Code(rig.Client.Call<InstrumentPutResult>("instruments.put", new InstrumentPutRequest(0, instrument))));
        Assert.Equal(0, rig.Stored().Revision);
    }

    [Fact]
    public async Task Transport_field_rules_cover_tls_tcp_and_gateway_exclusivity()
    {
        await using var rig = new AdminRig();
        await rig.Login(rig.Client);
        async Task<string> Put(TransportConfiguration transport) =>
            await Code(rig.Client.Call<InstrumentPutResult>("instruments.put", new InstrumentPutRequest(0, new("x", Asi, transport))));
        Assert.Equal("transport.tls.required", await Put(new("tcp") { Host = "127.0.0.1", Port = 5000, CertificateSha256 = new string('a', 64) }));
        Assert.Equal("transport.tls.pin.invalid", await Put(new("tcp") { Host = "127.0.0.1", Port = 5000, Tls = true, CertificateSha256 = "abc" }));
        Assert.Equal("transport.tls.certificate.unavailable", await Put(new("tcp") { Host = "127.0.0.1", Port = 5000, Tls = true,
            ClientCertificatePath = Path.Combine(rig.Directory, "missing.pfx") }));
        Assert.Equal("transport.listen.numeric-address-required", await Put(new("tcp-listen") { Host = "localhost", Port = 5000 }));
        Assert.Equal("transport.address.invalid", await Put(new("tcp") { Port = 5000 }));
        Assert.Equal("transport.portgate.port-required", await Put(new("portgate") { Host = "127.0.0.1", Port = 28241 }));
        Assert.Equal("transport.gateway.unknown", await Put(new("portgate") { GatewayId = "missing", PortId = "p" }));
        await rig.Client.Call<GatewayPutResult>("gateways.put", new GatewayPutRequest(0, new("pc-1", "127.0.0.1", 28241)));
        Assert.Equal("transport.gateway.exclusive", await Code(rig.Client.Call<InstrumentPutResult>("instruments.put",
            new InstrumentPutRequest(1, new("x", Asi, new("portgate") { GatewayId = "pc-1", PortId = "p", Host = "10.0.0.1" })))));
        Assert.Equal("gateway.id.invalid", await Code(rig.Client.Call<GatewayPutResult>("gateways.put", new GatewayPutRequest(1, new("pc 2", "127.0.0.1", 1)))));
        Assert.Equal("gateway.address.invalid", await Code(rig.Client.Call<GatewayPutResult>("gateways.put", new GatewayPutRequest(1, new("pc-2", "", 1)))));
    }

    [Fact]
    public async Task External_edits_are_never_overwritten()
    {
        await using var rig = new AdminRig();
        await rig.Login(rig.Client);
        File.AppendAllText(rig.ConfigurationPath, " ");
        Assert.Equal("configuration.changed-externally", await Code(rig.Client.Call<InstrumentPutResult>("instruments.put",
            new InstrumentPutRequest(0, new("architect-1", Asi, new("serial") { PortName = OperatingSystem.IsWindows() ? "COM7" : "/dev/ttyS7" })))));
        Assert.Contains(rig.Audit(), r => r.Action == "instruments.put" && r.Outcome == "failed" && r.Code == "configuration.changed-externally");
        Assert.Equal(0, (await rig.Client.Call<ConfigurationView>("instruments.list", new Ok())).Revision);
    }

    [Fact]
    public async Task Instrument_in_session_cannot_be_reconfigured_and_new_configuration_opens_sessions()
    {
        var (host, instrument) = DuplexStream.CreatePair(); await using var peer = instrument;
        await using var rig = new AdminRig(new FakeTransport(host));
        await rig.Login(rig.Client);
        var tcp = new InstrumentConfiguration("hl7-1", ArchitectHl7Plugin.ProfileId, new("tcp") { Host = "127.0.0.1", Port = 5000 })
        { QueryBudgetMs = 200, ResponseTimeoutMs = 300 };
        Assert.Equal(1, (await rig.Client.Call<InstrumentPutResult>("instruments.put", new InstrumentPutRequest(0, tcp))).Revision);
        var session = await rig.Client.Call<SessionStatus>("sessions.open", new OpenSessionRequest("hl7-1", Guid.NewGuid()));
        Assert.Equal("hl7-1", session.InstrumentId);
        Assert.Equal("instrument.busy", await Code(rig.Client.Call<InstrumentPutResult>("instruments.put", new InstrumentPutRequest(1, tcp with { TimeZone = "UTC" }))));
        Assert.Equal("instrument.busy", await Code(rig.Client.Call<RevisionResult>("instruments.remove", new InstrumentRemoveRequest(1, "hl7-1"))));
        var listing = await rig.Client.Call<ConfigurationView>("instruments.list", new Ok());
        Assert.Equal(1, listing.Revision);
        await rig.Client.Call<Ok>("sessions.close", new Ok(), session.SessionId);
        Assert.Equal(2, (await rig.Client.Call<RevisionResult>("instruments.remove", new InstrumentRemoveRequest(1, "hl7-1"))).Revision);
        Assert.Equal("instrument.unknown", await Code(rig.Client.Call<SessionStatus>("sessions.open", new OpenSessionRequest("hl7-1", Guid.NewGuid()))));
    }

    [Fact]
    public async Task PortGate_instruments_are_verified_against_the_agent_without_changing_it()
    {
        var probe = new FakeProbe();
        probe.Agents["127.0.0.1:28241"] = Agent(new SerialSettings(9600));
        await using var rig = new AdminRig(probe: probe);
        await rig.Login(rig.Client);
        await rig.Client.Call<GatewayPutResult>("gateways.put", new GatewayPutRequest(0, new("pc-1", "127.0.0.1", 28241)));
        var instrument = new InstrumentConfiguration("architect-1", Asi, new("portgate") { GatewayId = "pc-1", PortId = "com3" });
        var verified = await rig.Client.Call<InstrumentPutResult>("instruments.put", new InstrumentPutRequest(1, instrument));
        Assert.Equal("verified", verified.Verification);
        Assert.Equal("astm-service/architect-1", probe.Calls.Last().ControllerId);

        probe.Agents["127.0.0.1:28241"] = Agent(new SerialSettings(9600, Parity: SerialParity.Even));
        Assert.Equal("transport.portgate.settings-mismatch", await Code(rig.Client.Call<InstrumentPutResult>("instruments.put", new InstrumentPutRequest(2, instrument))));
        Assert.Equal("transport.portgate.port-unknown", await Code(rig.Client.Call<InstrumentPutResult>("instruments.put",
            new InstrumentPutRequest(2, instrument with { Transport = instrument.Transport with { PortId = "com9" } }))));
        probe.Agents.Remove("127.0.0.1:28241");
        Assert.Equal("transport.portgate.unreachable", await Code(rig.Client.Call<InstrumentPutResult>("instruments.put", new InstrumentPutRequest(2, instrument))));
        var deferred = await rig.Client.Call<InstrumentPutResult>("instruments.put", new InstrumentPutRequest(2, instrument) { AllowUnreachableGateway = true });
        Assert.Equal(("unverified", 3L), (deferred.Verification, deferred.Revision));
        Assert.Equal("transport.resource.duplicate", await Code(rig.Client.Call<InstrumentPutResult>("instruments.put",
            new InstrumentPutRequest(3, instrument with { Id = "architect-2" }) { AllowUnreachableGateway = true })));
        Assert.Equal("gateway.referenced", await Code(rig.Client.Call<RevisionResult>("gateways.remove", new GatewayRemoveRequest(3, "pc-1"))));
        Assert.Equal(4, (await rig.Client.Call<RevisionResult>("instruments.remove", new InstrumentRemoveRequest(3, "architect-1"))).Revision);
        Assert.Equal(5, (await rig.Client.Call<RevisionResult>("gateways.remove", new GatewayRemoveRequest(4, "pc-1"))).Revision);
        Assert.Empty(rig.Stored().Gateways);
    }

    [Fact]
    public async Task Gateways_list_reports_ports_configured_instruments_and_unreachable_agents()
    {
        var probe = new FakeProbe();
        probe.Agents["127.0.0.1:28241"] = Agent(new SerialSettings(9600));
        await using var rig = new AdminRig(probe: probe, extra: [new("architect-1", Asi, new("portgate") { GatewayId = "pc-1", PortId = "com3" }),
            new("direct", Asi, new("portgate") { Host = "127.0.0.1", Port = 28299, PortId = "com1" })],
            gateways: [new("pc-1", "127.0.0.1", 28241) { Observe = true }]);
        var listing = await rig.Client.Call<GatewayListing>("gateways.list", new GatewayListRequest());
        Assert.Equal(2, listing.Gateways.Count);
        var pc = listing.Gateways.Single(g => g.Id == "pc-1");
        Assert.True(pc.Reachable); Assert.False(pc.Implicit); Assert.Equal("synthetic-agent", pc.AgentId);
        var com3 = pc.Ports.Single(p => p.PortId == "com3");
        Assert.Equal("serial", com3.Device!.Kind); Assert.Equal("COM3", com3.Device.Path); Assert.Equal(new SerialSettings(9600), com3.Device.Serial);
        Assert.Equal("architect-1", com3.Instruments.Single().InstrumentId);
        Assert.True(com3.Instruments.Single().SettingsMatch); Assert.False(com3.Instruments.Single().InSession);
        Assert.NotNull(com3.State); Assert.Null(com3.StateCode);
        var free = pc.Ports.Single(p => p.PortId == "com4");
        Assert.Empty(free.Instruments); Assert.Null(free.State); Assert.Equal("PORTGATE-OBSERVE-NOT-ALLOWED", free.StateCode);
        var direct = listing.Gateways.Single(g => g.Id == "127.0.0.1:28299");
        Assert.True(direct.Implicit); Assert.False(direct.Reachable); Assert.Equal("gateway.unreachable", direct.Code); Assert.Empty(direct.Ports);
        Assert.Equal("pc-1", Assert.Single((await rig.Client.Call<GatewayListing>("gateways.list", new GatewayListRequest(["pc-1"]))).Gateways).Id);
        Assert.Equal("gateway.unknown", await Code(rig.Client.Call<GatewayListing>("gateways.list", new GatewayListRequest(["10.0.0.9:1"]))));
    }

    [PortGateFact]
    public async Task Real_PortGate_ports_are_listed_and_verified_without_acquiring_them()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var peerPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        await using var agent = await PortGateProcess.LaunchAsync();
        await agent.ConfigureAsync(PortGateProcess.Configuration(0) with
        {
            Ports = [new PortGatePort("com3", "analyzer-a", PortGateDevice.TestSerial(peerPort, AsiSerial),
                    [new PortGateController("astm-service/architect-1", 10), new PortGateController("astm-service/discovery", 0, Rights: PortGateRights.Observe)]),
                new PortGatePort("com4", "analyzer-b", PortGateDevice.TestSerial(peerPort, new SerialSettings(19200)), [new PortGateController("other", 10)])],
        });
        await using var rig = new AdminRig(probe: new PortGateProbe(), gateways: [new("pc-1", "127.0.0.1", agent.Port) { Observe = true }]);
        var gateway = Assert.Single((await rig.Client.Call<GatewayListing>("gateways.list", new GatewayListRequest())).Gateways);
        Assert.True(gateway.Reachable, gateway.Code); Assert.Equal("test-agent", gateway.AgentId); Assert.NotEqual(Guid.Empty, gateway.AgentEpoch);
        var com3 = gateway.Ports.Single(p => p.PortId == "com3");
        Assert.Equal(("analyzer-a", "enforced", "serial"), (com3.InstrumentId, com3.Ownership, com3.Device!.Kind));
        Assert.Equal(AsiSerial, com3.Device.Serial);
        Assert.NotNull(com3.State); Assert.Null(com3.State!.Owner);
        var com4 = gateway.Ports.Single(p => p.PortId == "com4");
        Assert.Equal(19200, com4.Device!.Serial!.BaudRate);
        Assert.Equal("PORTGATE-OBSERVE-NOT-ALLOWED", com4.StateCode);

        await rig.Login(rig.Client);
        var instrument = new InstrumentConfiguration("architect-1", Asi, new("portgate") { GatewayId = "pc-1", PortId = "com3" });
        Assert.Equal("verified", (await rig.Client.Call<InstrumentPutResult>("instruments.put", new InstrumentPutRequest(0, instrument))).Verification);
        Assert.Equal("transport.portgate.settings-mismatch", await Code(rig.Client.Call<InstrumentPutResult>("instruments.put",
            new InstrumentPutRequest(1, instrument with { Id = "architect-2", Transport = instrument.Transport with { PortId = "com4" } }))));
        // The agent configuration in force was not changed and nobody holds the port.
        await using var admin = await agent.AdminAsync();
        var (revision, _) = await admin.GetConfigAsync();
        Assert.Equal(1UL, revision);
        var listed = (await rig.Client.Call<GatewayListing>("gateways.list", new GatewayListRequest())).Gateways.Single().Ports.Single(p => p.PortId == "com3");
        Assert.Null(listed.State!.Owner);
        Assert.True(listed.Instruments.Single().SettingsMatch);
    }

    private static PortGateAgentDescription Agent(SerialSettings com3) => new("synthetic-agent", Guid.NewGuid(), "test", true,
    [
        new("com3", "analyzer-a", "enforced", "none", 16384, true, new("serial", "COM3", null, null, com3))
        { State = new PortGatePortState(true, null, 0, 0, false, false, false) },
        new("com4", "analyzer-b", "enforced", "none", 16384, true, new("serial", "COM4", null, null, new SerialSettings(19200)))
        { StateCode = "PORTGATE-OBSERVE-NOT-ALLOWED" },
    ]);

    private static async Task<string> Code(Task task)
    {
        var error = await Assert.ThrowsAsync<ServiceException>(() => task);
        return error.Code;
    }

    internal sealed class FakeProbe : IPortGateProbe
    {
        public Dictionary<string, PortGateAgentDescription> Agents { get; } = [];
        public List<GatewayConfiguration> Calls { get; } = [];
        public Task<PortGateAgentDescription> DescribeAsync(GatewayConfiguration gateway, CancellationToken token)
        {
            lock (Calls) Calls.Add(gateway);
            return Agents.TryGetValue(gateway.Host + ":" + gateway.Port, out var agent) ? Task.FromResult(agent)
                : Task.FromException<PortGateAgentDescription>(new SocketException((int)SocketError.ConnectionRefused));
        }
    }

    internal sealed class AdminRig : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(30));
        private readonly CommunicationService _service;
        private readonly List<(TestClient Client, Task Serving)> _connections = [];
        public ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public ECDsa GatewaysOnly { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "astm-admin-tests", Guid.NewGuid().ToString("N"));
        public string ConfigurationPath => Path.Combine(Directory, "config", "service.json");
        public string AuditPath => Path.Combine(Directory, "audit", "audit.jsonl");
        public string OwnershipText => Path.Combine(Directory, "owner", ".", "");
        public TestClient Client { get; }
        public Guid InstanceId => _service.InstanceId;

        public AdminRig(IServiceTransportFactory? transports = null, IPortGateProbe? probe = null, bool administration = true,
            IReadOnlyList<InstrumentConfiguration>? extra = null, IReadOnlyList<GatewayConfiguration>? gateways = null)
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(ConfigurationPath)!);
            var configuration = new ServiceConfiguration("unused", OwnershipText,
                [new("existing", Asi, new("serial") { PortName = OperatingSystem.IsWindows() ? "COM3" : "/dev/ttyS3" }), .. extra ?? []])
            {
                Gateways = gateways ?? [],
                Administration = administration ? new(AuditPath, [new("lis", Convert.ToBase64String(Key.ExportSubjectPublicKeyInfo()), ["instruments", "gateways"]),
                    new("gateways-only", Convert.ToBase64String(GatewaysOnly.ExportSubjectPublicKeyInfo()), ["gateways"])]) : null,
            };
            File.WriteAllBytes(ConfigurationPath, JsonSerializer.SerializeToUtf8Bytes(configuration, ServiceJson.Type<ServiceConfiguration>()));
            var store = new FileServiceConfigurationStore(ConfigurationPath);
            _service = new(store.Load(), BundledServiceProfiles.Create(), transports, store, probe ?? new FakeProbe());
            Client = Connect();
        }

        public TestClient Connect()
        {
            var (server, client) = DuplexStream.CreatePair();
            var connection = new TestClient(client);
            _connections.Add((connection, _service.ServeAsync(server, _stop.Token)));
            return connection;
        }

        public async Task Login(TestClient client, ECDsa? key = null, string principal = "lis")
        {
            var challenge = await client.Call<AdminChallenge>("admin.challenge", new Ok());
            await client.Call<AdminIdentity>("admin.authenticate",
                new AdminAuthenticateRequest(principal, ServiceAdministration.Sign(key ?? Key, challenge.InstanceId, challenge.Nonce, principal)));
        }

        public ServiceConfiguration Stored() =>
            JsonSerializer.Deserialize(File.ReadAllBytes(ConfigurationPath), ServiceJson.Type<ServiceConfiguration>())!;

        public IReadOnlyList<ServiceAuditRecord> Audit() => !File.Exists(AuditPath) ? [] : File.ReadAllLines(AuditPath)
            .Select(line => JsonSerializer.Deserialize(line, ServiceJson.Type<ServiceAuditRecord>())!).ToArray();

        public IEnumerable<string> DirectoryFiles() => System.IO.Directory.EnumerateFiles(Path.GetDirectoryName(ConfigurationPath)!);

        public async ValueTask DisposeAsync()
        {
            foreach (var (client, _) in _connections) await client.DisposeAsync();
            _stop.Cancel();
            foreach (var (_, serving) in _connections) await serving;
            await _service.DisposeAsync(); _stop.Dispose(); Key.Dispose(); GatewaysOnly.Dispose();
            System.IO.Directory.Delete(Directory, true);
        }
    }
}
