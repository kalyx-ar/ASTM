using System.Text.Json;
using Astm.Operations;

namespace Astm.Service;

/// <summary>Process-lifetime owner. A connection owns its callbacks and sessions; disconnect closes them.</summary>
public sealed class CommunicationService : IAsyncDisposable
{
    private static readonly string[] Methods = ["service.hello", "service.status", "service.shutdown", "profiles.list", "profiles.describe", "sessions.open",
        "sessions.status", "sessions.close", "sessions.reconcile", "messages.process", "operations.prepare", "operations.start",
        "operations.status", "operations.cancel", "operations.forget", "requests.cancel"];
    private readonly object _gate = new();
    private readonly ServiceConfiguration _configuration;
    private readonly Dictionary<string, ServiceProfile> _profiles;
    private readonly IServiceTransportFactory _transports;
    private readonly Dictionary<Guid, ServiceSession> _sessions = [];
    private readonly CancellationTokenSource _stop = new();
    public Guid InstanceId { get; } = Guid.CreateVersion7();
    public CancellationToken Stopping => _stop.Token;
    public CommunicationService(ServiceConfiguration configuration, IEnumerable<ServiceProfile> profiles, IServiceTransportFactory? transports = null)
    {
        configuration = configuration with { Limits = configuration.Limits with { },
            Instruments = Array.AsReadOnly(configuration.Instruments.Select(i => i with { Transport = i.Transport with { } }).ToArray()) };
        configuration.Limits.Validate();
        if (configuration.Instruments.Any(i => string.IsNullOrWhiteSpace(i.Id) || i.Id.Length > 128)
            || configuration.Instruments.Select(i => i.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != configuration.Instruments.Count)
            throw new ArgumentException("service.instruments.invalid");
        _configuration = configuration with { OwnershipDirectory = LocalAccess.PrivateDirectory(configuration.OwnershipDirectory) };
        _profiles = profiles.ToDictionary(p => p.Description.Id, StringComparer.Ordinal);
        foreach (var instrument in configuration.Instruments)
        {
            if (!_profiles.TryGetValue(instrument.ProfileId, out var profile)) throw new ServiceException("profile.unknown");
            _ = profile.Bind(instrument); // Validate timing/time zone before accepting clients.
        }
        _transports = transports ?? new ServiceTransportFactory();
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
            ServiceSession[] owned;
            lock (_gate) owned = _sessions.Values.Where(s => s.Owner == peer.Id).ToArray();
            foreach (var session in owned) await CloseAsync(session).ConfigureAwait(false);
        }
    }
    private async ValueTask<JsonElement> HandleAsync(IpcPeer peer, IpcEnvelope request, CancellationToken token)
    {
        if (request.Method == "service.hello") return ServiceJson.Element(new ServiceHello("astm-service/1.0", InstanceId, _configuration.Limits, Methods));
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
        if (request.Method == "sessions.open")
        {
            var input = ServiceJson.Read<OpenSessionRequest>(request.Data);
            if (input.TraceId == Guid.Empty) throw new ServiceException("trace.id.required");
            var instrument = _configuration.Instruments.SingleOrDefault(i => i.Id == input.InstrumentId) ?? throw new ServiceException("instrument.unknown");
            ServiceSession session;
            lock (_gate)
            {
                if (_sessions.Count >= _configuration.Limits.MaxSessions) throw new ServiceException("sessions.capacity");
                if (_sessions.Values.Any(s => s.InstrumentId == instrument.Id)) throw new ServiceException("instrument.busy");
                if (_sessions.Values.Any(s => s.Status.TraceId == input.TraceId)) throw new ServiceException("trace.id.reused");
                session = new ServiceSession(peer.Id, instrument, _profiles[instrument.ProfileId].Bind(instrument), input.TraceId, peer,
                    _configuration.Limits, peer.Disconnected);
                _sessions.Add(session.Id, session);
            }
            try
            {
                await session.OpenAsync(_transports, _configuration.OwnershipDirectory, token).ConfigureAwait(false);
                return ServiceJson.Element(session.Status);
            }
            catch { await CloseAsync(session).ConfigureAwait(false); throw; }
        }
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
