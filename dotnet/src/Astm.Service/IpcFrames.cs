using System.Buffers.Binary;
using System.Text.Json;

namespace Astm.Service;

/// <summary>Four-byte unsigned big-endian UTF-8 JSON length; no text written to console.</summary>
public static class IpcFrames
{
    public static async ValueTask<IpcEnvelope?> ReadAsync(Stream stream, int maxBytes, CancellationToken token, int frameTimeoutMs = 10000)
    {
        var header = new byte[4];
        var first = await stream.ReadAsync(header.AsMemory(0, 1), token).ConfigureAwait(false);
        if (first == 0) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(frameTimeoutMs);
        await stream.ReadExactlyAsync(header.AsMemory(1), deadline.Token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length == 0 || length > maxBytes) throw new ServiceException("ipc.frame.limit");
        var content = new byte[(int)length];
        await stream.ReadExactlyAsync(content, deadline.Token).ConfigureAwait(false);
        var envelope = JsonSerializer.Deserialize(content, ServiceJson.Type<IpcEnvelope>()) ?? throw new ServiceException("ipc.envelope.required");
        if (envelope.V != 1 || envelope.Id == Guid.Empty || envelope.Data.ValueKind is JsonValueKind.Undefined)
            throw new ServiceException("ipc.version-or-envelope.invalid");
        return envelope;
    }
    public static async ValueTask WriteAsync(Stream stream, IpcEnvelope envelope, int maxBytes, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, ServiceJson.Type<IpcEnvelope>());
        if (bytes.Length > maxBytes) throw new ServiceException("ipc.frame.limit");
        var header = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(header, (uint)bytes.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }
}
