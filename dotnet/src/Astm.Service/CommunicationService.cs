using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Astm.Operations;

namespace Astm.Service;

/// <summary>Process-lifetime owner. A connection owns its callbacks and sessions; disconnect closes them.</summary>
public sealed class CommunicationService : IAsyncDisposable
{
    private static readonly string[] BaseMethods = ["service.hello", "service.status", "service.shutdown", "profiles.list", "profiles.describe", "sessions.open",
        "sessions.status", "sessions.close", "sessions.reconcile", "messages.process", "operations.prepare", "operations.start",
        "operations.status", "operations.cancel", "operations.forget", "requests.cancel", "instruments.list", "gateways.list"];
    private static readonly string[] AdministrationMethods = ["admin.challenge", "admin.authenticate", "instruments.put", "instruments.remove",
        "gateways.put", "gateways.remove"];
    private static readonly string[] SessionMethods = ["sessions.status", "sessions.close", "sessions.reconcile", "messages.process",
        "operations.prepare", "operations.start", "operations.status", "operations.cancel", "operations.forget"];
    private readonly object _gate = new();
    private readonly string _ownershipDirectory;
    // As persisted (original strings); guarded by _gate and replaced only after a successful save.
    private ServiceConfiguration _configuration;
    private readonly Dictionary<string, ServiceProfile> _profiles;
    private readonly IServiceTransportFactory _transports;
    private readonly IServiceConfigurationStore? _store;
    private readonly IPortGateProbe _probe;
    private readonly ServiceAuditLog? _audit;
    private readonly Dictionary<string, (AdminPrincipal Principal, byte[] Key)> _principals = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, AdminConnection> _connections = new();
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly SemaphoreSlim _listing = new(1, 1);
    private readonly HashSet<string> _reconfiguring = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, ServiceSession> _sessions = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly string[] _methods;
    public Guid InstanceId { get; } = Guid.CreateVersion7();
    public CancellationToken Stopping => _stop.Token;
    public CommunicationService(ServiceConfiguration configuration, IEnumerable<ServiceProfile> profiles, IServiceTransportFactory? transports = null,
        IServiceConfigurationStore? store = null, IPortGateProbe? probe = null)
    {
        configuration = configuration with { Limits = configuration.Limits with { },
            Instruments = Array.AsReadOnly(configuration.Instruments.Select(i => i with { Transport = i.Transport with { } }).ToArray()),
            Gateways = Array.AsReadOnly((configuration.Gateways ?? []).ToArray()) };
        configuration.Limits.Validate();
        if (configuration.Instruments.Any(i => string.IsNullOrWhiteSpace(i.Id) || i.Id.Length > 128)
            || configuration.Instruments.Select(i => i.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != configuration.Instruments.Count)
            throw new ArgumentException("service.instruments.invalid");
        ConfigurationRules.ValidateStartup(configuration);
        _ownershipDirectory = LocalAccess.PrivateDirectory(configuration.OwnershipDirectory);
        _configuration = configuration;
        _profiles = profiles.ToDictionary(p => p.Description.Id, StringComparer.Ordinal);
        foreach (var instrument in configuration.Instruments)
        {
            if (!_profiles.TryGetValue(instrument.ProfileId, out var profile)) throw new ServiceException("profile.unknown");
            _ = profile.Bind(ConfigurationRules.Effective(instrument, configuration.Gateways)); // Validate timing/time zone before accepting clients.
        }
        _transports = transports ?? new ServiceTransportFactory();
        _probe = probe ?? new PortGateProbe();
        _store = store;
        if (configuration.Administration is { } administration)
        {
            if (store is null) throw new ArgumentException("service.administration.store-required");
            foreach (var principal in administration.Principals) _principals.Add(principal.Id, (principal, ServiceAdministration.PublicKey(principal)));
            _audit = new ServiceAuditLog(administration.AuditPath);
        }
        _methods = _audit is null ? BaseMethods : [.. BaseMethods, .. AdministrationMethods];
    }
    public async Task ServeAsync(Stream stream, CancellationToken token = default)
    {
        await using var peer = new IpcPeer(stream, _configuration.Limits)
        { ResponseSent = method => { if (method == "service.shutdown") _stop.Cancel(); } };
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
        try { await peer.RunAsync((request, deadline) => HandleAsync(peer, request, deadline), lifetime.Token).ConfigureAwait(false); }
        catch (Exception) when (peer.Disconnected.IsCancellationRequested) { }
        finally
        {
            _connections.TryRemove(peer.Id, out _);
            ServiceSession[] owned;
            lock (_gate) owned = _sessions.Values.Where(s => s.Owner == peer.Id).ToArray();
            foreach (var session in owned) await CloseAsync(session).ConfigureAwait(false);
        }
    }
    private async ValueTask<JsonElement> HandleAsync(IpcPeer peer, IpcEnvelope request, CancellationToken token)
    {
        if (!_methods.Contains(request.Method)) throw new ServiceException("method.unsupported");
        if (request.Method == "service.hello") return ServiceJson.Element(new ServiceHello("astm-service/1.0", InstanceId, _configuration.Limits, _methods));
        if (request.Method == "service.shutdown") return ServiceJson.Element(new Ok());
        if (request.Method == "profiles.list") return ServiceJson.Element(_profiles.Values.Select(p => p.Description).ToArray());
        if (request.Method == "profiles.describe")
        {
            var input = ServiceJson.Read<ProfileRequest>(request.Data);
            if (!_profiles.TryGetValue(input.ProfileId, out var profile)) throw new ServiceException("profile.unknown");
            var binding = profile.Bind(new("discovery", input.ProfileId, new("messages"))
            { QueryBudgetMs = input.QueryBudgetMs, ResponseTimeoutMs = input.ResponseTimeoutMs, TimeZone = input.TimeZone });
            var operations = binding.Operations.Describe(input.ProfileId);
            var contracts = operations.SelectMany(d => new[] { d.Input, d.Output }).OfType<OperationContractReference>()
                .Distinct().Select(binding.Operations.Resolve).ToArray();
            return ServiceJson.Element(new ProfileDetails(profile.Description, binding.Operations.Profile, operations, contracts, binding.Transport)
            {
                WireOutputs = operations.Select(d => d.Output).OfType<OperationContractReference>().Distinct().Select(reference =>
                    new WireOutputContract(reference, new(new("astm.service/" + reference.Id + "@" + reference.Major + "." + reference.Minor, 1),
                        DecimalWireSchema(binding.Operations.Resolve(reference).Root)))).ToArray(),
            });
        }
        if (request.Method == "service.status")
        {
            lock (_gate) return ServiceJson.Element(_sessions.Values.Where(s => s.Owner == peer.Id).Select(s => s.Status).ToArray());
        }
        switch (request.Method)
        {
            case "instruments.list":
                lock (_gate) return ServiceJson.Element(new ConfigurationView(_configuration.Revision, _configuration.Instruments, _configuration.Gateways));
            case "gateways.list": return ServiceJson.Element(await ListGatewaysAsync(ServiceJson.Read<GatewayListRequest>(request.Data), token).ConfigureAwait(false));
            case "admin.challenge": return ServiceJson.Element(Challenge(peer));
            case "admin.authenticate": return ServiceJson.Element(Authenticate(peer, ServiceJson.Read<AdminAuthenticateRequest>(request.Data)));
            case "instruments.put": return ServiceJson.Element(await PutInstrumentAsync(peer, ServiceJson.Read<InstrumentPutRequest>(request.Data), token).ConfigureAwait(false));
            case "instruments.remove": return ServiceJson.Element(await RemoveInstrumentAsync(peer, ServiceJson.Read<InstrumentRemoveRequest>(request.Data), token).ConfigureAwait(false));
            case "gateways.put": return ServiceJson.Element(await PutGatewayAsync(peer, ServiceJson.Read<GatewayPutRequest>(request.Data), token).ConfigureAwait(false));
            case "gateways.remove": return ServiceJson.Element(await RemoveGatewayAsync(peer, ServiceJson.Read<GatewayRemoveRequest>(request.Data), token).ConfigureAwait(false));
        }
        if (request.Method == "sessions.open")
        {
            var input = ServiceJson.Read<OpenSessionRequest>(request.Data);
            if (input.TraceId == Guid.Empty) throw new ServiceException("trace.id.required");
            ServiceSession session;
            lock (_gate)
            {
                var configured = _configuration.Instruments.SingleOrDefault(i => i.Id == input.InstrumentId) ?? throw new ServiceException("instrument.unknown");
                var instrument = ConfigurationRules.Effective(configured, _configuration.Gateways);
                if (_sessions.Count >= _configuration.Limits.MaxSessions) throw new ServiceException("sessions.capacity");
                if (_reconfiguring.Contains(instrument.Id) || _sessions.Values.Any(s => s.InstrumentId == instrument.Id)) throw new ServiceException("instrument.busy");
                if (_sessions.Values.Any(s => s.Status.TraceId == input.TraceId)) throw new ServiceException("trace.id.reused");
                session = new ServiceSession(peer.Id, instrument, _profiles[instrument.ProfileId].Bind(instrument), input.TraceId, peer,
                    _configuration.Limits, peer.Disconnected);
                _sessions.Add(session.Id, session);
            }
            try
            {
                await session.OpenAsync(_transports, _ownershipDirectory, token).ConfigureAwait(false);
                return ServiceJson.Element(session.Status);
            }
            catch { await CloseAsync(session).ConfigureAwait(false); throw; }
        }
        if (!SessionMethods.Contains(request.Method)) throw new ServiceException("method.unsupported");
        ServiceSession current;
        lock (_gate) current = request.SessionId is { } id && _sessions.TryGetValue(id, out var found) && found.Owner == peer.Id
            ? found : throw new ServiceException("session.unknown");
        switch (request.Method)
        {
            case "sessions.status": return ServiceJson.Element(current.Status);
            case "sessions.close": await CloseAsync(current).ConfigureAwait(false); return ServiceJson.Element(new Ok());
            case "sessions.reconcile":
                if (!request.Data.TryGetProperty("success", out var acknowledged) || acknowledged.ValueKind != JsonValueKind.True)
                    throw new ServiceException("reconciliation.confirmation-required");
                current.Reconcile(); return ServiceJson.Element(current.Status);
            case "messages.process": return ServiceJson.Element(await current.ProcessAsync(ServiceJson.Read<ContentRequest>(request.Data), token).ConfigureAwait(false));
            case "operations.prepare": return ServiceJson.Element(current.Prepare(ServiceJson.Read<OperationEnvelope>(request.Data)));
            case "operations.start": return ServiceJson.Element(current.Start(ServiceJson.Read<OperationEnvelope>(request.Data)));
            case "operations.status": return ServiceJson.Element(current.Operation(ServiceJson.Read<OperationKey>(request.Data).OperationId));
            case "operations.cancel": return ServiceJson.Element(current.Operation(ServiceJson.Read<OperationKey>(request.Data).OperationId, cancel: true));
            case "operations.forget": return ServiceJson.Element(current.Operation(ServiceJson.Read<OperationKey>(request.Data).OperationId, forget: true));
            default: throw new ServiceException("method.unsupported");
        }
    }

    // ---- PortGate agents and ports (read-only) ----

    private async Task<GatewayListing> ListGatewaysAsync(GatewayListRequest input, CancellationToken token)
    {
        ServiceConfiguration configuration;
        lock (_gate) configuration = _configuration;
        var targets = configuration.Gateways.Select(g => (Gateway: g, Implicit: false)).ToList();
        // Agents used directly by instruments (host/port without gatewayId) are listed too; never an arbitrary address.
        foreach (var direct in configuration.Instruments.Where(i => i.Transport is { Kind: "portgate", GatewayId: null, Host: not null }).Select(i => i.Transport))
        {
            var id = direct.Host + ":" + direct.Port;
            if (targets.Any(t => t.Gateway.Id == id)) continue;
            targets.Add((new GatewayConfiguration(id, direct.Host!, direct.Port)
            {
                Tls = direct.Tls, CertificateSha256 = direct.CertificateSha256, TlsTargetHost = direct.TlsTargetHost,
                ClientCertificatePath = direct.ClientCertificatePath, ConnectTimeoutMs = direct.ConnectTimeoutMs,
            }, true));
        }
        if (input.GatewayIds is { } filter)
        {
            if (filter.Except(targets.Select(t => t.Gateway.Id)).Any()) throw new ServiceException("gateway.unknown");
            targets = targets.Where(t => filter.Contains(t.Gateway.Id)).ToList();
        }
        await _listing.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var views = await Task.WhenAll(targets.Select(async target =>
            {
                var view = new GatewayView(target.Gateway.Id, target.Gateway.Host, target.Gateway.Port, target.Gateway.Tls, target.Implicit, false);
                try
                {
                    var agent = await _probe.DescribeAsync(target.Gateway, token).ConfigureAwait(false);
                    return view with
                    {
                        Reachable = true, AgentId = agent.AgentId, AgentEpoch = agent.AgentEpoch, Platform = agent.Platform, ObserverSoftKill = agent.ObserverSoftKill,
                        Ports = agent.Ports.Select(port => new GatewayPortView(port.PortId, port.InstrumentId, port.Ownership, port.TraceCapture, port.MaxWriteBytes,
                            port.QueuedAcquisition, port.Device, PortInstruments(configuration, target.Gateway, port))
                        { VirtualCom = port.VirtualCom, State = port.State, StateCode = port.State is null ? port.StateCode ?? "state.not-observed" : null }).ToArray(),
                    };
                }
                catch (Exception exception) when (!token.IsCancellationRequested) { return view with { Code = PortGateProbe.Code(exception, token) }; }
            })).ConfigureAwait(false);
            return new(configuration.Revision, views);
        }
        finally { _listing.Release(); }
    }

    private GatewayPortInstrument[] PortInstruments(ServiceConfiguration configuration, GatewayConfiguration gateway, PortGate.Client.PortGateAnnouncedPort port) =>
        configuration.Instruments.Select(i => (Configured: i, Effective: ConfigurationRules.Effective(i, configuration.Gateways)))
            .Where(i => i.Effective.Transport is { Kind: "portgate" } t && t.PortId == port.PortId
                && string.Equals(t.Host, gateway.Host, StringComparison.OrdinalIgnoreCase) && t.Port == gateway.Port)
            .Select(i =>
            {
                bool inSession;
                lock (_gate) inSession = _sessions.Values.Any(s => s.InstrumentId == i.Configured.Id);
                bool? match = null;
                try
                {
                    if (ConfigurationRules.ExpectedSerial(i.Effective, _profiles[i.Effective.ProfileId].Bind(i.Effective)) is { } expected)
                        match = port.Device?.Serial == expected;
                }
                catch (Exception exception) when (exception is ServiceException or ArgumentException or InvalidOperationException or TimeZoneNotFoundException) { }
                return new GatewayPortInstrument(i.Configured.Id, inSession, match);
            }).ToArray();

    // ---- Principal authentication ----

    private AdminChallenge Challenge(IpcPeer peer)
    {
        var state = _connections.GetOrAdd(peer.Id, _ => new());
        lock (state)
        {
            if (state.Failures >= ServiceAdministration.MaxFailures) throw new ServiceException("admin.locked");
            state.Nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            state.NonceExpires = DateTimeOffset.UtcNow + ServiceAdministration.ChallengeLifetime;
            return new(state.Nonce, InstanceId, state.NonceExpires);
        }
    }

    private AdminIdentity Authenticate(IpcPeer peer, AdminAuthenticateRequest input)
    {
        var state = _connections.GetOrAdd(peer.Id, _ => new());
        string? code = null;
        AdminPrincipal? principal = null;
        lock (state)
        {
            var nonce = state.Nonce;
            state.Nonce = null; // Single use, whatever the outcome.
            if (state.Failures >= ServiceAdministration.MaxFailures) code = "admin.locked";
            else if (nonce is null) code = "admin.challenge.required";
            else if (DateTimeOffset.UtcNow > state.NonceExpires) code = "admin.challenge.expired";
            else if (!_principals.TryGetValue(input.PrincipalId ?? "", out var known)
                || !ServiceAdministration.Verify(known.Key, InstanceId, nonce, known.Principal.Id, input.Signature ?? ""))
                code = "admin.unauthorized";
            else principal = known.Principal;
            if (code is "admin.unauthorized" or "admin.challenge.expired") state.Failures++;
            if (principal is not null) state.Principal = principal;
        }
        // Claimed principal ids are bounded by the frame and recorded verbatim only when configured.
        var claimed = input.PrincipalId is { } id && _principals.ContainsKey(id) ? id : null;
        _audit!.Append(new(DateTimeOffset.UtcNow, InstanceId, peer.Id, claimed, "authenticate", null, code is null ? "accepted" : "denied") { Code = code });
        if (code is not null) throw new ServiceException(code);
        return new(principal!.Id, principal.Rights);
    }

    private AdminPrincipal Authorize(IpcPeer peer, string right, string action, string? target)
    {
        var principal = _connections.TryGetValue(peer.Id, out var state) ? state.Principal : null;
        var code = principal is null ? "admin.unauthenticated" : !principal.Rights.Contains(right) ? "admin.forbidden" : null;
        if (code is null) return principal!;
        _audit!.Append(new(DateTimeOffset.UtcNow, InstanceId, peer.Id, principal?.Id, action, target, "denied") { Code = code });
        throw new ServiceException(code);
    }

    // ---- Configuration writes ----

    private async Task<InstrumentPutResult> PutInstrumentAsync(IpcPeer peer, InstrumentPutRequest input, CancellationToken token)
    {
        var instrument = input.Instrument ?? throw new ServiceException("instrument.required");
        var principal = Authorize(peer, ServiceAdministration.RightInstruments, "instruments.put", instrument.Id);
        await _writes.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ServiceConfiguration current;
            lock (_gate) current = _configuration;
            if (input.Revision != current.Revision) throw new ServiceException("configuration.revision-mismatch");
            instrument = instrument with { Transport = instrument.Transport is { } transport ? transport with { } : throw new ServiceException("transport.invalid") };
            var effective = ConfigurationRules.ValidateInstrument(instrument, current, _profiles);
            lock (_gate) if (_sessions.Values.Any(s => s.InstrumentId.Equals(instrument.Id, StringComparison.OrdinalIgnoreCase))) throw new ServiceException("instrument.busy");
            var verification = await VerifyPortGateAsync(effective, input.AllowUnreachableGateway, token).ConfigureAwait(false);
            if (input.DryRun) return new(current.Revision, instrument, verification);
            var previous = current.Instruments.FirstOrDefault(i => i.Id.Equals(instrument.Id, StringComparison.OrdinalIgnoreCase));
            var next = current with
            {
                Revision = current.Revision + 1,
                Instruments = Array.AsReadOnly(previous is null ? [.. current.Instruments, instrument]
                    : current.Instruments.Select(i => ReferenceEquals(i, previous) ? instrument : i).ToArray()),
            };
            Commit(peer, principal, "instruments.put", instrument.Id, current, next, previous is null ? null : Hash(previous),
                ServiceJson.Element(instrument), [instrument.Id]);
            return new(next.Revision, instrument, verification);
        }
        finally { _writes.Release(); }
    }

    private async Task<RevisionResult> RemoveInstrumentAsync(IpcPeer peer, InstrumentRemoveRequest input, CancellationToken token)
    {
        var principal = Authorize(peer, ServiceAdministration.RightInstruments, "instruments.remove", input.InstrumentId);
        await _writes.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ServiceConfiguration current;
            lock (_gate) current = _configuration;
            if (input.Revision != current.Revision) throw new ServiceException("configuration.revision-mismatch");
            var previous = current.Instruments.FirstOrDefault(i => i.Id == input.InstrumentId) ?? throw new ServiceException("instrument.unknown");
            var next = current with { Revision = current.Revision + 1, Instruments = Array.AsReadOnly(current.Instruments.Where(i => !ReferenceEquals(i, previous)).ToArray()) };
            Commit(peer, principal, "instruments.remove", previous.Id, current, next, Hash(previous), null, [previous.Id]);
            return new(next.Revision);
        }
        finally { _writes.Release(); }
    }

    private async Task<GatewayPutResult> PutGatewayAsync(IpcPeer peer, GatewayPutRequest input, CancellationToken token)
    {
        var gateway = input.Gateway ?? throw new ServiceException("gateway.required");
        var principal = Authorize(peer, ServiceAdministration.RightGateways, "gateways.put", gateway.Id);
        await _writes.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ServiceConfiguration current;
            lock (_gate) current = _configuration;
            if (input.Revision != current.Revision) throw new ServiceException("configuration.revision-mismatch");
            ConfigurationRules.ValidateGateway(gateway, strict: true);
            var previous = current.Gateways.FirstOrDefault(g => g.Id.Equals(gateway.Id, StringComparison.OrdinalIgnoreCase));
            if (previous is not null && previous.Id != gateway.Id) throw new ServiceException("gateway.id.case-conflict");
            var next = current with
            {
                Revision = current.Revision + 1,
                Gateways = Array.AsReadOnly(previous is null ? [.. current.Gateways, gateway]
                    : current.Gateways.Select(g => ReferenceEquals(g, previous) ? gateway : g).ToArray()),
            };
            // A new address must not make two instruments share one PortGate port.
            var keys = next.Instruments.Select(i => ConfigurationRules.ResourceKey(ConfigurationRules.Effective(i, next.Gateways))).OfType<string>().ToArray();
            if (keys.Distinct(StringComparer.Ordinal).Count() != keys.Length) throw new ServiceException("transport.resource.duplicate");
            var dependents = current.Instruments.Where(i => i.Transport.GatewayId == gateway.Id).Select(i => i.Id).ToArray();
            lock (_gate) if (_sessions.Values.Any(s => dependents.Contains(s.InstrumentId))) throw new ServiceException("gateway.busy");
            if (input.DryRun) return new(current.Revision, gateway);
            Commit(peer, principal, "gateways.put", gateway.Id, current, next, previous is null ? null : Hash(previous), ServiceJson.Element(gateway), dependents,
                busy: "gateway.busy");
            return new(next.Revision, gateway);
        }
        finally { _writes.Release(); }
    }

    private async Task<RevisionResult> RemoveGatewayAsync(IpcPeer peer, GatewayRemoveRequest input, CancellationToken token)
    {
        var principal = Authorize(peer, ServiceAdministration.RightGateways, "gateways.remove", input.GatewayId);
        await _writes.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ServiceConfiguration current;
            lock (_gate) current = _configuration;
            if (input.Revision != current.Revision) throw new ServiceException("configuration.revision-mismatch");
            var previous = current.Gateways.FirstOrDefault(g => g.Id == input.GatewayId) ?? throw new ServiceException("gateway.unknown");
            if (current.Instruments.Any(i => i.Transport.GatewayId == previous.Id)) throw new ServiceException("gateway.referenced");
            var next = current with { Revision = current.Revision + 1, Gateways = Array.AsReadOnly(current.Gateways.Where(g => !ReferenceEquals(g, previous)).ToArray()) };
            Commit(peer, principal, "gateways.remove", previous.Id, current, next, Hash(previous), null, []);
            return new(next.Revision);
        }
        finally { _writes.Release(); }
    }

    /// <summary>Audit (pending) → atomic save → in-memory swap → audit (applied). Instruments in <paramref name="locked"/> cannot open meanwhile.</summary>
    private void Commit(IpcPeer peer, AdminPrincipal principal, string action, string target, ServiceConfiguration current, ServiceConfiguration next,
        string? beforeSha256, JsonElement? after, IReadOnlyList<string> locked, string busy = "instrument.busy")
    {
        lock (_gate)
        {
            if (!ReferenceEquals(current, _configuration)) throw new ServiceException("configuration.revision-mismatch");
            if (_sessions.Values.Any(s => locked.Contains(s.InstrumentId, StringComparer.OrdinalIgnoreCase))) throw new ServiceException(busy);
            foreach (var id in locked) _reconfiguring.Add(id);
        }
        try
        {
            var record = new ServiceAuditRecord(DateTimeOffset.UtcNow, InstanceId, peer.Id, principal.Id, action, target, "pending")
            { RevisionBefore = current.Revision, RevisionAfter = next.Revision, BeforeSha256 = beforeSha256, After = after };
            _audit!.Append(record);
            try { _store!.Save(next); }
            catch (Exception exception)
            {
                var code = exception is ServiceException known ? known.Code : "configuration.write-failed";
                try { _audit.Append(record with { TimeUtc = DateTimeOffset.UtcNow, Outcome = "failed", Code = code, After = null }); }
                catch (ServiceException) { /* The pending record already shows the attempt. */ }
                throw new ServiceException(code);
            }
            lock (_gate) _configuration = next;
            _audit.Append(record with { TimeUtc = DateTimeOffset.UtcNow, Outcome = "applied", After = null });
        }
        finally { lock (_gate) foreach (var id in locked) _reconfiguring.Remove(id); }
    }

    /// <summary>Compares a PortGate instrument with what its agent announces. A disagreement fails; the agent is never changed.</summary>
    private async Task<string> VerifyPortGateAsync(InstrumentConfiguration effective, bool allowUnreachable, CancellationToken token)
    {
        if (effective.Transport is not { Kind: "portgate" } transport) return "not-applicable";
        var gateway = new GatewayConfiguration("verification", transport.Host!, transport.Port)
        {
            Tls = transport.Tls, CertificateSha256 = transport.CertificateSha256, TlsTargetHost = transport.TlsTargetHost,
            ClientCertificatePath = transport.ClientCertificatePath, ConnectTimeoutMs = transport.ConnectTimeoutMs,
            ControllerId = "astm-service/" + effective.Id,
        };
        PortGate.Client.PortGateAgentDescription agent;
        try { agent = await _probe.DescribeAsync(gateway, token).ConfigureAwait(false); }
        catch (Exception exception) when (!token.IsCancellationRequested && exception is not ServiceException)
        {
            if (allowUnreachable) return "unverified";
            throw new ServiceException("transport.portgate.unreachable");
        }
        var port = agent.Ports.FirstOrDefault(p => p.PortId == transport.PortId) ?? throw new ServiceException("transport.portgate.port-unknown");
        if (ConfigurationRules.ExpectedSerial(effective, _profiles[effective.ProfileId].Bind(effective)) is { } expected && port.Device?.Serial != expected)
            throw new ServiceException("transport.portgate.settings-mismatch");
        return "verified";
    }

    private static string Hash<T>(T entry) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(entry, ServiceJson.Type<T>())));

    private sealed class AdminConnection
    {
        public string? Nonce;
        public DateTimeOffset NonceExpires;
        public int Failures;
        public AdminPrincipal? Principal;
    }

    private async ValueTask CloseAsync(ServiceSession session)
    {
        await session.DisposeAsync().ConfigureAwait(false);
        lock (_gate) _sessions.Remove(session.Id);
    }
    private static OperationDataSchema DecimalWireSchema(OperationDataSchema schema) => schema with
    {
        Kind = schema.Kind == OperationDataKind.Number ? OperationDataKind.String : schema.Kind,
        Items = schema.Items is { } item ? DecimalWireSchema(item) : null,
        Fields = schema.Fields.ToDictionary(pair => pair.Key, pair => pair.Value with { Schema = DecimalWireSchema(pair.Value.Schema) }, StringComparer.Ordinal),
    };
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        ServiceSession[] sessions;
        lock (_gate) sessions = _sessions.Values.ToArray();
        foreach (var session in sessions) await CloseAsync(session).ConfigureAwait(false);
    }
}
