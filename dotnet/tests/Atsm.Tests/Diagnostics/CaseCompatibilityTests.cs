using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Atsm.Diagnostics;
using Atsm.Runtime.Tracing;

namespace Atsm.Tests.Diagnostics;

public class CaseCompatibilityTests
{
    [Fact]
    public void Source_generated_reader_opens_the_previous_reflection_serialized_case_format()
    {
        // Independent producer matching the pre-AOT JSON contract, not the new journal writer.
        var legacy = new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        var protection = new DiagnosticTests.TestProtection();
        var header = new TraceHeader(Guid.NewGuid(), "synthetic-legacy", "test", "clsi.lis01-a2.lis02-a2", DateTimeOffset.UtcNow);
        using var storage = new MemoryStream(); storage.Write("ATSMCASE1"u8);
        var previous = "";
        void Append<T>(long sequence, string kind, T value)
        {
            var plain = JsonSerializer.SerializeToUtf8Bytes(new { CaseId = header.TraceId, RecordSequence = sequence, PreviousHash = previous, Kind = kind, Value = value }, legacy);
            var encrypted = protection.Protect(plain); CryptographicOperations.ZeroMemory(plain);
            Span<byte> size = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(size, encrypted.Length);
            storage.Write(size); storage.Write(encrypted); previous = Convert.ToHexString(SHA256.HashData(encrypted));
        }
        Append(0, "header", header);
        Append(1, "entry", new TraceLine(0, 0, DateTimeOffset.UtcNow, "rx", Convert.ToBase64String("SYN-LEGACY"u8), null, null));
        Append(2, "end", new { RecordCount = 2, CaptureComplete = true });
        var read = ProtectedCaseJournal.Read(new MemoryStream(storage.ToArray()), protection);
        Assert.True(read.IsComplete); Assert.Equal(header.TraceId, read.Header.TraceId);
        Assert.Equal("SYN-LEGACY"u8.ToArray(), Assert.Single(read.Entries).Data.ToArray());
    }
}
