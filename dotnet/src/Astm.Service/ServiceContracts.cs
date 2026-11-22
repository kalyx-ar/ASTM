using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Astm.Abstractions.Tracing;
using Astm.Abstractions.Transport;
using Astm.Formats.Medical;
using Astm.Formats.Medical.Hosting;
using Astm.Formats.Medical.Model;
using Astm.Operations;
using Astm.Plugins;

namespace Astm.Service;

public sealed record ServiceLimits
{
    public int MaxFrameBytes { get; set; } = 4 * 1024 * 1024;
    public int MaxContentBytes { get; set; } = 1024 * 1024;
    public int MaxSessions { get; set; } = 16;
    public int MaxRequests { get; set; } = 32;
    public int MaxCallbacks { get; set; } = 64;
    public int OutboundCapacity { get; set; } = 32;
    public int OperationCapacity { get; set; } = 16;
    public int OperationHistory { get; set; } = 1024;
    public int CallbackTimeoutMs { get; set; } = 10000;
    public int RequestTimeoutMs { get; set; } = 120000;
    public void Validate()
    {
        if (MaxFrameBytes is < 4096 or > 16 * 1024 * 1024 || MaxContentBytes < 1 || MaxContentBytes > (MaxFrameBytes - 4096) / 2
            || MaxSessions is < 1 or > 256 || MaxRequests is < 1 or > 256 || MaxCallbacks is < 1 or > 1024
            || OutboundCapacity is < 1 or > 256 || OperationCapacity is < 1 or > 256 || OperationHistory is < 1 or > 65536
            || CallbackTimeoutMs is < 1 or > 300000 || RequestTimeoutMs is < 1 or > 3600000)
            throw new ArgumentException("service.limits.invalid");
    }
}

/// <summary>Administrator-owned configuration. Clients can only select a configured instrument.</summary>
public sealed record ServiceConfiguration(string Endpoint, string OwnershipDirectory, IReadOnlyList<InstrumentConfiguration> Instruments)
{
    public ServiceLimits Limits { get; set; } = new();
    /// <summary>Incremented by every IPC change (optimistic concurrency of the administration methods).</summary>
    public long Revision { get; init; }
    public IReadOnlyList<GatewayConfiguration> Gateways { get; init; } = [];
    /// <summary>Without it there is no IPC write access. Principals and rights are only granted in this local file.</summary>
    public AdministrationConfiguration? Administration { get; init; }
}
/// <summary>A PortGate agent (Link endpoint). Instruments reference it by <see cref="TransportConfiguration.GatewayId"/>.</summary>
public sealed record GatewayConfiguration(string Id, string Host, int Port)
{
    public bool Tls { get; init; }
    public string? CertificateSha256 { get; init; }
    public string? TlsTargetHost { get; init; }
    public string? ClientCertificatePath { get; init; }
    /// <summary>Controller id used to inspect the agent; sessions keep <c>astm-service/&lt;instrumentId&gt;</c>.</summary>
    public string ControllerId { get; init; } = "astm-service/discovery";
    /// <summary>Ask for the semaphore of every port when listing (needs the observe right; the agent records it).</summary>
    public bool Observe { get; init; }
    public int ConnectTimeoutMs { get; init; } = 10000;
}
public sealed record AdministrationConfiguration(string AuditPath, IReadOnlyList<AdminPrincipal> Principals);
/// <summary>ECDSA P-256 public key (SubjectPublicKeyInfo, Base64); the private key stays with the host.</summary>
public sealed record AdminPrincipal(string Id, string PublicKey, IReadOnlyList<string> Rights);
public sealed record InstrumentConfiguration(string Id, string ProfileId, TransportConfiguration Transport)
{
    public int? QueryBudgetMs { get; init; }
    public int? ResponseTimeoutMs { get; init; }
    public string TimeZone { get; set; } = "UTC";
}
public sealed record TransportConfiguration(string Kind)
{
    public string? PortName { get; init; }
    public SerialSettings? Serial { get; init; }
    public string? Host { get; init; }
    public int Port { get; init; }
    public string? PortId { get; init; }
    /// <summary>PortGate agent of <see cref="ServiceConfiguration.Gateways"/>; excludes Host/Port/TLS fields.</summary>
    public string? GatewayId { get; init; }
    public string? TlsTargetHost { get; init; }
    public string? CertificateSha256 { get; init; }
    public string? ClientCertificatePath { get; init; }
    public bool Tls { get; init; }
    public int ConnectTimeoutMs { get; set; } = 10000;
}

public sealed record IpcEnvelope(int V, Guid Id, string Kind, JsonElement Data)
{
    public string? Method { get; init; }
    public Guid? SessionId { get; init; }
    public DateTimeOffset? DeadlineUtc { get; init; }
    public string? Error { get; init; }
    public SessionCorrelation? Correlation { get; init; }
}
public sealed record SessionCorrelation(Guid Generation, string InstrumentId, string ProfileId, string ProfileRevision, Guid TraceId)
{
    public Guid? ReceptionId { get; init; }
}
public sealed record ProfileRequest(string ProfileId, int? QueryBudgetMs = null, int? ResponseTimeoutMs = null, string TimeZone = "UTC");
public sealed record ProfileDescription(string Id, string Revision, string Kind, PluginDescriptor Plugin,
    IReadOnlyList<InstrumentProfileFact> Knowledge, bool RequiresTiming);
public sealed record ProfileDetails(ProfileDescription Profile, MedicalOperationProfile Medical,
    IReadOnlyList<OperationDefinition> Operations, IReadOnlyList<OperationDataContract> Contracts, TransportKnowledge? Transport)
{
    public IReadOnlyList<WireOutputContract> WireOutputs { get; init; } = [];
}
public sealed record WireOutputContract(OperationContractReference Engine, OperationDataContract Wire);
public sealed record ServiceHello(string Protocol, Guid InstanceId, ServiceLimits Limits, string[] Methods)
{
    public string DecimalEncoding { get; init; } = "invariant-decimal-string";
    public string BinaryEncoding { get; init; } = "base64";
}
public sealed record OpenSessionRequest(string InstrumentId, Guid TraceId);
public sealed record SessionStatus(Guid SessionId, Guid Generation, string InstrumentId, string ProfileId, string State,
    Guid TraceId, long LastTraceSequence, DateTimeOffset LastProgressUtc, string? Code, OperationSnapshot Observation,
    TransportTenure? Tenure, int OperationCount)
{
    public string ProfileRevision { get; init; } = "";
}
public sealed record TransportTenure(Guid AgentEpoch, uint Generation, Guid TransferId, bool UncertainTx, ulong UnownedRxBytes);
public sealed record ContentRequest(Guid ReceptionId, byte[] Content);
public sealed record ContentResult(IReadOnlyList<IngestOutcome> Outcomes, byte[][] Responses, Astm.Abstractions.Diagnostics.AstmDiagnostic[] Diagnostics);
public sealed record PrepareResult(string Format, byte[] Content, OperationDefinition Definition);
public sealed record OperationKey(Guid OperationId);
public sealed record OperationStatus(Guid OperationId, string State, OrderDeliveryOutcome? Delivery, string? Code);
public sealed record TraceReadyRequest(Guid TraceId, Guid Generation);
public sealed record TraceReadyReply(Guid TraceId, bool Ready);
public sealed record TraceAppendRequest(Guid TraceId, Guid Generation, TraceEntry Entry);
public sealed record TraceAppendReply(Guid TraceId, long Sequence, bool Stored);
public sealed record CancelRequest(Guid RequestId);
public sealed record Ok(bool Success = true);

// Additive methods of astm-service/1.0 (Documentacion/ADMINISTRACION-SERVICIO.md); announced in service.hello methods.
public sealed record GatewayListRequest(IReadOnlyList<string>? GatewayIds = null);
public sealed record GatewayListing(long Revision, IReadOnlyList<GatewayView> Gateways);
public sealed record GatewayView(string Id, string Host, int Port, bool Tls, bool Implicit, bool Reachable)
{
    public string? Code { get; init; }
    public string? AgentId { get; init; }
    public Guid? AgentEpoch { get; init; }
    public string? Platform { get; init; }
    public bool? ObserverSoftKill { get; init; }
    public IReadOnlyList<GatewayPortView> Ports { get; init; } = [];
}
public sealed record GatewayPortView(string PortId, string? InstrumentId, string Ownership, string TraceCapture, int MaxWriteBytes,
    bool QueuedAcquisition, PortGate.Client.PortGateAnnouncedDevice? Device, IReadOnlyList<GatewayPortInstrument> Instruments)
{
    public string? VirtualCom { get; init; }
    public PortGate.Client.PortGatePortState? State { get; init; }
    public string? StateCode { get; init; }
}
public sealed record GatewayPortInstrument(string InstrumentId, bool InSession, bool? SettingsMatch);
public sealed record AdminChallenge(string Nonce, Guid InstanceId, DateTimeOffset ExpiresUtc, string Algorithm = ServiceAdministration.Algorithm);
public sealed record AdminAuthenticateRequest(string PrincipalId, string Signature);
public sealed record AdminIdentity(string PrincipalId, IReadOnlyList<string> Rights);
public sealed record ConfigurationView(long Revision, IReadOnlyList<InstrumentConfiguration> Instruments, IReadOnlyList<GatewayConfiguration> Gateways);
public sealed record InstrumentPutRequest(long Revision, InstrumentConfiguration Instrument)
{
    public bool DryRun { get; init; }
    public bool AllowUnreachableGateway { get; init; }
}
public sealed record InstrumentPutResult(long Revision, InstrumentConfiguration Instrument, string Verification);
public sealed record InstrumentRemoveRequest(long Revision, string InstrumentId);
public sealed record GatewayPutRequest(long Revision, GatewayConfiguration Gateway)
{
    public bool DryRun { get; init; }
}
public sealed record GatewayPutResult(long Revision, GatewayConfiguration Gateway);
public sealed record GatewayRemoveRequest(long Revision, string GatewayId);
public sealed record RevisionResult(long Revision);
/// <summary>One line of the administration audit (JSON Lines). Configuration holds no secrets nor patient data.</summary>
public sealed record ServiceAuditRecord(DateTimeOffset TimeUtc, Guid InstanceId, Guid ConnectionId, string? PrincipalId, string Action,
    string? Target, string Outcome)
{
    public string? Code { get; init; }
    public long? RevisionBefore { get; init; }
    public long? RevisionAfter { get; init; }
    public string? BeforeSha256 { get; init; }
    public JsonElement? After { get; init; }
}

public sealed class ServiceException(string code) : IOException(code)
{
    public string Code { get; } = code;
}
public static class ServiceJson
{
    public static JsonTypeInfo<T> Type<T>() => (JsonTypeInfo<T>)(ServiceJsonContext.Default.GetTypeInfo(typeof(T))
        ?? MedicalJsonContext.Default.GetTypeInfo(typeof(T)) ?? OperationJsonContext.Default.GetTypeInfo(typeof(T))
        ?? throw new NotSupportedException(typeof(T).Name));
    public static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Type<T>());
    public static T Read<T>(JsonElement data)
    {
        // Required callback acknowledgements cannot acquire enum/bool/sequence defaults from an absent field.
        string[] required = typeof(T) == typeof(IngestOutcome) ? ["state"]
            : typeof(T) == typeof(OrderQueryAnswer) ? ["state", "orders"]
            : typeof(T) == typeof(TraceReadyReply) ? ["traceId", "ready"]
            : typeof(T) == typeof(TraceAppendReply) ? ["traceId", "sequence", "stored"]
            // An absent revision must not silently become 0 on configuration writes.
            : typeof(T) == typeof(InstrumentPutRequest) ? ["revision", "instrument"]
            : typeof(T) == typeof(InstrumentRemoveRequest) ? ["revision", "instrumentId"]
            : typeof(T) == typeof(GatewayPutRequest) ? ["revision", "gateway"]
            : typeof(T) == typeof(GatewayRemoveRequest) ? ["revision", "gatewayId"]
            : typeof(T) == typeof(AdminAuthenticateRequest) ? ["principalId", "signature"] : [];
        if (required.Length > 0 && (data.ValueKind != JsonValueKind.Object || required.Any(field => !data.TryGetProperty(field, out _))))
            throw new JsonException("callback.field.required");
        return data.Deserialize(Type<T>()) ?? throw new ServiceException("ipc.data.required");
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(ExactDecimalConverter), typeof(StrictEnumConverter<IngestState>), typeof(StrictEnumConverter<QueryAnswerState>)])]
[JsonSerializable(typeof(ServiceConfiguration))]
[JsonSerializable(typeof(IpcEnvelope))]
[JsonSerializable(typeof(ProfileRequest))]
[JsonSerializable(typeof(ProfileDescription[]))]
[JsonSerializable(typeof(ProfileDetails))]
[JsonSerializable(typeof(ServiceHello))]
[JsonSerializable(typeof(OpenSessionRequest))]
[JsonSerializable(typeof(SessionStatus))]
[JsonSerializable(typeof(SessionStatus[]))]
[JsonSerializable(typeof(ContentRequest))]
[JsonSerializable(typeof(ContentResult))]
[JsonSerializable(typeof(PrepareResult))]
[JsonSerializable(typeof(OperationKey))]
[JsonSerializable(typeof(OperationStatus))]
[JsonSerializable(typeof(TraceReadyRequest))]
[JsonSerializable(typeof(TraceReadyReply))]
[JsonSerializable(typeof(TraceAppendRequest))]
[JsonSerializable(typeof(TraceAppendReply))]
[JsonSerializable(typeof(OrderQuery))]
[JsonSerializable(typeof(OrderQueryAnswer))]
[JsonSerializable(typeof(IngestOutcome))]
[JsonSerializable(typeof(InstrumentObservationBatch))]
[JsonSerializable(typeof(PortGate.Client.PortGateEvent))]
[JsonSerializable(typeof(PortGate.Client.PortGateAuditEvent))]
[JsonSerializable(typeof(PortGate.Client.PortGatePortState))]
[JsonSerializable(typeof(PortGate.Client.PortGateObservation))]
[JsonSerializable(typeof(CancelRequest))]
[JsonSerializable(typeof(Ok))]
[JsonSerializable(typeof(GatewayConfiguration))]
[JsonSerializable(typeof(InstrumentConfiguration))]
[JsonSerializable(typeof(GatewayListRequest))]
[JsonSerializable(typeof(GatewayListing))]
[JsonSerializable(typeof(AdminChallenge))]
[JsonSerializable(typeof(AdminAuthenticateRequest))]
[JsonSerializable(typeof(AdminIdentity))]
[JsonSerializable(typeof(ConfigurationView))]
[JsonSerializable(typeof(InstrumentPutRequest))]
[JsonSerializable(typeof(InstrumentPutResult))]
[JsonSerializable(typeof(InstrumentRemoveRequest))]
[JsonSerializable(typeof(GatewayPutRequest))]
[JsonSerializable(typeof(GatewayPutResult))]
[JsonSerializable(typeof(GatewayRemoveRequest))]
[JsonSerializable(typeof(RevisionResult))]
[JsonSerializable(typeof(ServiceAuditRecord))]
public partial class ServiceJsonContext : JsonSerializerContext;

/// <summary>Exact invariant decimal strings on the IPC boundary; raw instrument text is also preserved.</summary>
public sealed class ExactDecimalConverter : JsonConverter<decimal>
{
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.String when decimal.TryParse(reader.GetString(), System.Globalization.NumberStyles.AllowLeadingSign | System.Globalization.NumberStyles.AllowDecimalPoint,
            System.Globalization.CultureInfo.InvariantCulture, out var value) => value,
        _ => throw new JsonException("decimal.string.required"),
    };
    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

public sealed class StrictEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String || !Enum.TryParse<T>(reader.GetString(), out var value)
            || Enum.GetName(value) != reader.GetString()) throw new JsonException("enum.name.required");
        return value;
    }
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}
