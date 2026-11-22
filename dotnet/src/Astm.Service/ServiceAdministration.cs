using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Astm.Abstractions.Transport;
using Astm.Transport;

namespace Astm.Service;

/// <summary>Principal authentication of the administration methods (Documentacion/ADMINISTRACION-SERVICIO.md).</summary>
public static class ServiceAdministration
{
    public const string Algorithm = "ES256-DER";
    public const string RightInstruments = "instruments";
    public const string RightGateways = "gateways";
    public static readonly TimeSpan ChallengeLifetime = TimeSpan.FromSeconds(60);
    public const int MaxFailures = 3;

    /// <summary>Exact bytes the principal signs.</summary>
    public static byte[] Payload(Guid instanceId, string nonce, string principalId) =>
        Encoding.UTF8.GetBytes("astm-service/admin-v1\n" + instanceId.ToString("D") + "\n" + nonce + "\n" + principalId);

    /// <summary>Signature for <c>admin.authenticate</c> (ECDSA P-256, SHA-256, DER), in Base64.</summary>
    public static string Sign(ECDsa key, Guid instanceId, string nonce, string principalId) =>
        Convert.ToBase64String(key.SignData(Payload(instanceId, nonce, principalId), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

    internal static byte[] PublicKey(AdminPrincipal principal)
    {
        try
        {
            var spki = Convert.FromBase64String(principal.PublicKey);
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(spki, out var read);
            // Windows CNG may report the named curve by friendly name only.
            var curve = key.ExportParameters(false).Curve;
            if (read != spki.Length || key.KeySize != 256 || !curve.IsNamed
                || curve.Oid.Value != "1.2.840.10045.3.1.7" && curve.Oid.FriendlyName is not ("nistP256" or "ECDSA_P256" or "prime256v1" or "secp256r1"))
                throw new ServiceException("administration.principal.key");
            return spki;
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        { throw new ServiceException("administration.principal.key"); }
    }

    internal static bool Verify(byte[] spki, Guid instanceId, string nonce, string principalId, string signature)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(spki, out _);
            return key.VerifyData(Payload(instanceId, nonce, principalId), Convert.FromBase64String(signature), HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException) { return false; }
    }
}

/// <summary>Validation shared by the startup configuration and the IPC writes; strict rules apply to what arrives by IPC.</summary>
internal static partial class ConfigurationRules
{
    private static readonly string[] Kinds = ["messages", "serial", "tcp", "tcp-listen", "portgate"];

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    private static partial Regex Identifier();

    [GeneratedRegex(@"^(\\\\\.\\)?COM[1-9][0-9]{0,4}$", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsSerialPort();

    public static void ValidateStartup(ServiceConfiguration configuration)
    {
        if (configuration.Revision < 0) throw new ArgumentException("service.revision.invalid");
        var gateways = configuration.Gateways ?? throw new ArgumentException("service.gateways.invalid");
        foreach (var gateway in gateways) ValidateGateway(gateway, strict: false);
        if (gateways.Select(g => g.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != gateways.Count)
            throw new ArgumentException("service.gateways.invalid");
        foreach (var instrument in configuration.Instruments)
            if (instrument.Transport.GatewayId is { } id)
            {
                if (instrument.Transport.Kind != "portgate" || !gateways.Any(g => g.Id == id)) throw new ServiceException("transport.gateway.unknown");
                GatewayExclusive(instrument.Transport);
            }
        if (configuration.Administration is { } administration)
        {
            if (string.IsNullOrWhiteSpace(administration.AuditPath) || administration.Principals is null || administration.Principals.Count == 0
                || administration.Principals.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != administration.Principals.Count)
                throw new ArgumentException("service.administration.invalid");
            foreach (var principal in administration.Principals)
            {
                if (principal.Id is null || !Identifier().IsMatch(principal.Id) || principal.Rights is null || principal.Rights.Count == 0
                    || principal.Rights.Any(r => r is not (ServiceAdministration.RightInstruments or ServiceAdministration.RightGateways)))
                    throw new ArgumentException("service.administration.invalid");
                _ = ServiceAdministration.PublicKey(principal);
            }
        }
    }

    public static void ValidateGateway(GatewayConfiguration gateway, bool strict)
    {
        if (gateway.Id is null || gateway.Id.Length is < 1 or > 128 || strict && !Identifier().IsMatch(gateway.Id))
            throw new ServiceException("gateway.id.invalid");
        if (string.IsNullOrWhiteSpace(gateway.Host) || gateway.Host.Length > 253 || gateway.Port is < 1 or > 65535)
            throw new ServiceException("gateway.address.invalid");
        if (string.IsNullOrWhiteSpace(gateway.ControllerId) || gateway.ControllerId.Length > 128
            || gateway.ControllerId.StartsWith("vcom:", StringComparison.Ordinal)) throw new ServiceException("gateway.controller.invalid");
        if (gateway.ConnectTimeoutMs is < 1 or > 300000) throw new ServiceException("transport.timeout.invalid");
        Tls(gateway.Tls, gateway.CertificateSha256, gateway.TlsTargetHost, gateway.ClientCertificatePath, strict, listen: false);
    }

    /// <summary>Strict validation of an instrument received by IPC. Returns the effective (gateway-resolved) instrument.</summary>
    public static InstrumentConfiguration ValidateInstrument(InstrumentConfiguration instrument, ServiceConfiguration configuration,
        IReadOnlyDictionary<string, ServiceProfile> profiles)
    {
        if (instrument.Id is null || !Identifier().IsMatch(instrument.Id)) throw new ServiceException("instrument.id.invalid");
        if (instrument.ProfileId is null || !profiles.TryGetValue(instrument.ProfileId, out var profile)) throw new ServiceException("profile.unknown");
        var transport = instrument.Transport ?? throw new ServiceException("transport.invalid");
        if (transport.Kind is null || !Kinds.Contains(transport.Kind)) throw new ServiceException("transport.kind.unknown");
        if (transport.ConnectTimeoutMs is < 1 or > 300000) throw new ServiceException("transport.timeout.invalid");
        var effective = Effective(instrument, configuration.Gateways);
        ProfileBinding binding;
        try { binding = profile.Bind(effective); }
        catch (ServiceException) { throw; }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or TimeZoneNotFoundException
            or InvalidTimeZoneException or NotSupportedException or Astm.Operations.OperationCompatibilityException)
        { throw new ServiceException("instrument.binding.invalid"); }
        var none = transport.PortName is null && transport.Host is null && transport.Port == 0 && transport.PortId is null && transport.GatewayId is null;
        switch (transport.Kind)
        {
            case "messages":
                if (!none || transport.Serial is not null || TlsFields(transport)) throw new ServiceException("transport.fields.invalid");
                break;
            case "serial":
                if (transport.Host is not null || transport.Port != 0 || transport.PortId is not null || transport.GatewayId is not null || TlsFields(transport))
                    throw new ServiceException("transport.fields.invalid");
                if (transport.PortName is null || (OperatingSystem.IsWindows() ? !WindowsSerialPort().IsMatch(transport.PortName)
                    : !Path.IsPathFullyQualified(transport.PortName) || !transport.PortName.StartsWith("/dev/", StringComparison.Ordinal)))
                    throw new ServiceException("transport.serial.port-invalid");
                Serial(transport.Serial ?? binding.Transport?.Serial?.Default ?? throw new ServiceException("transport.serial.settings-required"), binding);
                break;
            case "tcp" or "tcp-listen":
                if (transport.PortName is not null || transport.PortId is not null || transport.GatewayId is not null || transport.Serial is not null)
                    throw new ServiceException("transport.fields.invalid");
                if (string.IsNullOrWhiteSpace(transport.Host) || transport.Host.Length > 253 || transport.Port is < 1 or > 65535)
                    throw new ServiceException("transport.address.invalid");
                if (transport.Kind == "tcp-listen" && !IPAddress.TryParse(transport.Host, out _))
                    throw new ServiceException("transport.listen.numeric-address-required");
                Tls(transport.Tls, transport.CertificateSha256, transport.TlsTargetHost, transport.ClientCertificatePath, strict: true,
                    listen: transport.Kind == "tcp-listen");
                break;
            case "portgate":
                if (transport.PortName is not null) throw new ServiceException("transport.fields.invalid");
                if (string.IsNullOrWhiteSpace(transport.PortId) || transport.PortId.Length > 128) throw new ServiceException("transport.portgate.port-required");
                if (transport.GatewayId is not null) GatewayExclusive(transport);
                else
                {
                    if (string.IsNullOrWhiteSpace(transport.Host) || transport.Host.Length > 253 || transport.Port is < 1 or > 65535)
                        throw new ServiceException("transport.address.invalid");
                    Tls(transport.Tls, transport.CertificateSha256, transport.TlsTargetHost, transport.ClientCertificatePath, strict: true, listen: false);
                }
                if (transport.Serial is not null) Serial(transport.Serial, binding);
                break;
        }
        var resource = ResourceKey(effective);
        if (resource is not null && configuration.Instruments.Any(other => !string.Equals(other.Id, instrument.Id, StringComparison.OrdinalIgnoreCase)
            && ResourceKey(Effective(other, configuration.Gateways)) == resource))
            throw new ServiceException("transport.resource.duplicate");
        return effective;
    }

    /// <summary>Resolves <see cref="TransportConfiguration.GatewayId"/> into the agent address and TLS settings.</summary>
    public static InstrumentConfiguration Effective(InstrumentConfiguration instrument, IReadOnlyList<GatewayConfiguration> gateways)
    {
        if (instrument.Transport.GatewayId is not { } id) return instrument;
        var gateway = gateways.FirstOrDefault(g => g.Id == id) ?? throw new ServiceException("transport.gateway.unknown");
        return instrument with { Transport = instrument.Transport with { GatewayId = null, Host = gateway.Host, Port = gateway.Port, Tls = gateway.Tls,
            CertificateSha256 = gateway.CertificateSha256, TlsTargetHost = gateway.TlsTargetHost, ClientCertificatePath = gateway.ClientCertificatePath } };
    }

    /// <summary>Exclusive resource of an effective instrument: COM, listener or PortGate port. TCP clients and messages have none.</summary>
    public static string? ResourceKey(InstrumentConfiguration effective)
    {
        var t = effective.Transport;
        return t.Kind switch
        {
            "serial" when t.PortName is not null => "serial:" + (OperatingSystem.IsWindows()
                ? t.PortName.Replace("\\\\.\\", "", StringComparison.Ordinal).ToUpperInvariant() : t.PortName),
            "tcp-listen" => "tcp-listen:" + t.Host + ":" + t.Port,
            "portgate" => "portgate:" + t.Host?.ToLowerInvariant() + ":" + t.Port + ":" + t.PortId,
            _ => null,
        };
    }

    /// <summary>Serial settings the session will require from PortGate: the instrument's, else the profile default.</summary>
    public static SerialSettings? ExpectedSerial(InstrumentConfiguration effective, ProfileBinding binding) =>
        effective.Transport.Serial ?? binding.Transport?.Serial?.Default;

    private static void GatewayExclusive(TransportConfiguration transport)
    {
        if (transport.Host is not null || transport.Port != 0 || TlsFields(transport)) throw new ServiceException("transport.gateway.exclusive");
    }

    private static bool TlsFields(TransportConfiguration t) => t.Tls || t.CertificateSha256 is not null || t.TlsTargetHost is not null || t.ClientCertificatePath is not null;

    private static void Serial(SerialSettings settings, ProfileBinding binding)
    {
        if (settings.BaudRate is < 50 or > 4_000_000 || settings.DataBits is < 5 or > 8 || !Enum.IsDefined(settings.Parity)
            || !Enum.IsDefined(settings.StopBits) || !Enum.IsDefined(settings.FlowControl) || binding.Transport.Validate(settings).Count > 0)
            throw new ServiceException("transport.serial.settings");
    }

    private static void Tls(bool tls, string? pin, string? targetHost, string? certificate, bool strict, bool listen)
    {
        if (!tls)
        {
            if (pin is not null || certificate is not null || strict && targetHost is not null) throw new ServiceException("transport.tls.required");
            return;
        }
        if (pin is not null && (pin.Length != 64 || !pin.All(Uri.IsHexDigit))) throw new ServiceException("transport.tls.pin.invalid");
        if (strict && certificate is not null && (!Path.IsPathFullyQualified(certificate) || !File.Exists(certificate)))
            throw new ServiceException("transport.tls.certificate.unavailable");
        if (listen && certificate is null) throw new ServiceException("transport.tls.server-certificate-required");
    }
}

/// <summary>Where the service persists the configuration it was started with.</summary>
public interface IServiceConfigurationStore
{
    ServiceConfiguration Load();
    /// <summary>Atomically replaces the stored configuration; fails if it changed since the last load or save.</summary>
    void Save(ServiceConfiguration configuration);
}

/// <summary>JSON file: write a sibling temporary file through to disk, then rename it over the original.</summary>
public sealed class FileServiceConfigurationStore(string path) : IServiceConfigurationStore
{
    private readonly object _gate = new();
    private readonly string _path = Path.GetFullPath(path);
    private byte[]? _hash;

    public ServiceConfiguration Load()
    {
        lock (_gate)
        {
            var bytes = File.ReadAllBytes(_path);
            var configuration = JsonSerializer.Deserialize(bytes, ServiceJson.Type<ServiceConfiguration>())
                ?? throw new ServiceException("configuration.required");
            _hash = SHA256.HashData(bytes);
            return configuration;
        }
    }

    public void Save(ServiceConfiguration configuration)
    {
        lock (_gate)
        {
            if (_hash is null) throw new ServiceException("configuration.not-loaded");
            byte[] current;
            try { current = File.ReadAllBytes(_path); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { throw new ServiceException("configuration.unavailable"); }
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(current), _hash)) throw new ServiceException("configuration.changed-externally");
            var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
                JsonSerializer.Serialize(writer, configuration, ServiceJson.Type<ServiceConfiguration>());
            buffer.WriteByte((byte)'\n');
            var bytes = buffer.ToArray();
            var temporary = Path.Combine(Path.GetDirectoryName(_path)!, "." + Path.GetFileName(_path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.WriteThrough };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using (var stream = new FileStream(temporary, options))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, _path, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                try { File.Delete(temporary); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
                throw new ServiceException("configuration.write-failed");
            }
            _hash = SHA256.HashData(bytes);
        }
    }
}

/// <summary>Append-only JSON Lines audit in a private directory; every record is flushed to disk before the change proceeds.</summary>
internal sealed class ServiceAuditLog
{
    private readonly object _gate = new();
    private readonly string _path;

    public ServiceAuditLog(string path)
    {
        _path = Path.GetFullPath(path);
        LocalAccess.PrivateDirectory(Path.GetDirectoryName(_path)!);
    }

    public void Append(ServiceAuditRecord record)
    {
        var line = JsonSerializer.SerializeToUtf8Bytes(record, ServiceJson.Type<ServiceAuditRecord>());
        lock (_gate)
        {
            try
            {
                var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read, Options = FileOptions.WriteThrough };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using var stream = new FileStream(_path, options);
                stream.Write(line);
                stream.WriteByte((byte)'\n');
                stream.Flush(flushToDisk: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { throw new ServiceException("audit.unavailable"); }
        }
    }
}
