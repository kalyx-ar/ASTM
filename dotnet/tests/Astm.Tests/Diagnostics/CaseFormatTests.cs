using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Astm.Diagnostics;
using Astm.Runtime.Tracing;

namespace Astm.Tests.Diagnostics;

public class CaseFormatTests
{
    [Theory]
    [InlineData("ASTMCASE1", true)]
    [InlineData("ATSMCASE1", false)]
    public void Source_generated_reader_accepts_only_the_current_case_signature(string signature, bool accepted)
    {
        // Independent producer for the current JSON contract.
        var producer = new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        var protection = new DiagnosticTests.TestProtection();
        var header = new TraceHeader(Guid.NewGuid(), "synthetic", "test", "clsi.lis01-a2.lis02-a2", DateTimeOffset.UtcNow);
        using var storage = new MemoryStream(); storage.Write(System.Text.Encoding.ASCII.GetBytes(signature));
        var previous = "";
        void Append<T>(long sequence, string kind, T value)
        {
            var plain = JsonSerializer.SerializeToUtf8Bytes(new { CaseId = header.TraceId, RecordSequence = sequence, PreviousHash = previous, Kind = kind, Value = value }, producer);
            var encrypted = protection.Protect(plain); CryptographicOperations.ZeroMemory(plain);
            Span<byte> size = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(size, encrypted.Length);
            storage.Write(size); storage.Write(encrypted); previous = Convert.ToHexString(SHA256.HashData(encrypted));
        }
        Append(0, "header", header);
        Append(1, "entry", new TraceLine(0, 0, DateTimeOffset.UtcNow, "rx", Convert.ToBase64String("SYN-CASE"u8), null, null));
        Append(2, "end", new { RecordCount = 2, CaptureComplete = true });
        if (!accepted)
        {
            Assert.Throws<InvalidDataException>(() => ProtectedCaseJournal.Read(new MemoryStream(storage.ToArray()), protection));
            return;
        }
        var read = ProtectedCaseJournal.Read(new MemoryStream(storage.ToArray()), protection);
        Assert.True(read.IsComplete); Assert.Equal(header.TraceId, read.Header.TraceId);
        Assert.Equal("SYN-CASE"u8.ToArray(), Assert.Single(read.Entries).Data.ToArray());
    }
}
