using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Atsm.Abstractions.Hosting;
using Atsm.Abstractions.Model;
using Atsm.Abstractions.Tracing;
using Atsm.Runtime.Tracing;

namespace Atsm.Diagnostics;

/// <summary>Application-selected authenticated encryption. Windows uses current-user DPAPI.
/// No provider, keys or fallback storage are selected by the ATSM engine.</summary>
public interface IDiagnosticCaseProtection
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] ciphertext);
}

public sealed class ProtectedCaseJournal : IDisposable
{
    private static ReadOnlySpan<byte> Magic => "ATSMCASE1"u8;
    private const int MaxRecord = 64 * 1024 * 1024;
    private readonly Stream _stream;
    private readonly IDiagnosticCaseProtection _protection;
    private readonly Guid _caseId;
    private readonly Lock _gate = new();
    private bool _disposed, _failed, _incomplete;
    private long _recordSequence;
    private string _previousHash = "";
    public ProtectedCaseJournal(Stream stream, IDiagnosticCaseProtection protection, TraceHeader header)
    {
        _stream = stream; _protection = protection; _caseId = header.TraceId;
        try { _stream.Write(Magic); Append("header", header); }
        catch { _stream.Dispose(); throw; }
    }
    public void EnsureReady() { ObjectDisposedException.ThrowIf(_disposed, this); if (_failed || !_stream.CanWrite) throw new IOException("ATSM-CASE-NOT-WRITABLE"); }
    public void MarkIncomplete() { lock (_gate) _incomplete = true; }
    public bool IsComplete { get { lock (_gate) return _disposed && !_failed && !_incomplete; } }
    public void Append<T>(string kind, T value)
    {
        lock (_gate)
        {
            EnsureReady();
            var plain = JsonSerializer.SerializeToUtf8Bytes(new Envelope(_caseId, _recordSequence, _previousHash, kind, JsonSerializer.SerializeToElement(value, TraceJson.Options)), TraceJson.Options);
            byte[] encrypted;
            try { encrypted = _protection.Protect(plain); }
            catch { _failed = true; throw; }
            finally { CryptographicOperations.ZeroMemory(plain); }
            if (encrypted.Length == 0 || encrypted.Length > MaxRecord) { _failed = true; throw new InvalidDataException("ATSM-CASE-RECORD-LIMIT"); }
            Span<byte> size = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(size, encrypted.Length);
            try
            {
                _stream.Write(size); _stream.Write(encrypted);
                if (_stream is FileStream file) file.Flush(flushToDisk: true); else _stream.Flush();
                _previousHash = Convert.ToHexString(SHA256.HashData(encrypted)); _recordSequence++;
            }
            catch { _failed = true; throw; }
        }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            try { if (!_failed) Append("end", new Closure(_recordSequence, !_incomplete)); }
            finally { _disposed = true; _stream.Dispose(); }
        }
    }

    public static DiagnosticCase Read(Stream stream, IDiagnosticCaseProtection protection)
    {
        var prefix = new byte[Magic.Length]; stream.ReadExactly(prefix);
        if (!prefix.AsSpan().SequenceEqual(Magic)) throw new InvalidDataException("ATSM-CASE-INVALID-FORMAT");
        TraceHeader? header = null; var entries = new List<TraceEntry>(); var batches = new List<InstrumentObservationBatch>();
        var sizeBytes = new byte[4]; long previous = -1, expectedRecord = 0, totalBytes = 0;
        var previousHash = ""; var complete = false;
        while (true)
        {
            var first = stream.ReadByte(); if (first < 0) break;
            if (complete) throw new InvalidDataException("ATSM-CASE-DATA-AFTER-END");
            sizeBytes[0] = (byte)first;
            try { stream.ReadExactly(sizeBytes.AsSpan(1)); } catch (EndOfStreamException) { break; }
            var size = BinaryPrimitives.ReadInt32LittleEndian(sizeBytes);
            if (size <= 0 || size > MaxRecord) throw new InvalidDataException("ATSM-CASE-RECORD-LIMIT");
            var ciphertext = new byte[size];
            try { stream.ReadExactly(ciphertext); } catch (EndOfStreamException) { break; }
            var plain = protection.Unprotect(ciphertext);
            try
            {
                var record = JsonSerializer.Deserialize<Envelope>(plain, TraceJson.Options) ?? throw new InvalidDataException("ATSM-CASE-INVALID-RECORD");
                if (record.RecordSequence != expectedRecord || record.PreviousHash != previousHash) throw new InvalidDataException("ATSM-CASE-BROKEN-CHAIN");
                if (record.Kind == "header" && header is null && expectedRecord == 0)
                {
                    header = record.Value.Deserialize<TraceHeader>(TraceJson.Options) ?? throw new InvalidDataException("ATSM-CASE-INVALID-HEADER");
                    if (record.CaseId != header.TraceId) throw new InvalidDataException("ATSM-CASE-HEADER-MISMATCH");
                }
                else if (header is null) throw new InvalidDataException("ATSM-CASE-MISSING-HEADER");
                else if (record.CaseId != header.TraceId) throw new InvalidDataException("ATSM-CASE-IDENTITY-MISMATCH");
                else if (record.Kind == "entry")
                {
                    var entry = (record.Value.Deserialize<TraceLine>(TraceJson.Options) ?? throw new InvalidDataException("ATSM-CASE-INVALID-ENTRY")).ToEntry();
                    if (entry.Sequence <= previous) throw new InvalidDataException("ATSM-CASE-INVALID-SEQUENCE");
                    totalBytes += entry.Data.Length;
                    if (entries.Count >= 100000 || totalBytes > DiagnosticReader.MaxInputBytes) throw new InvalidDataException("ATSM-CASE-CAPTURE-LIMIT");
                    previous = entry.Sequence; entries.Add(entry);
                }
                else if (record.Kind == "batch") batches.Add(record.Value.Deserialize<InstrumentObservationBatch>(TraceJson.Options) ?? throw new InvalidDataException("ATSM-CASE-INVALID-BATCH"));
                else if (record.Kind == "end")
                {
                    var closure = record.Value.Deserialize<Closure>(TraceJson.Options) ?? throw new InvalidDataException("ATSM-CASE-INVALID-END");
                    if (closure.RecordCount != expectedRecord) throw new InvalidDataException("ATSM-CASE-INVALID-END");
                    complete = closure.CaptureComplete;
                    // A sealed incomplete case still forbids records after its footer.
                    if (stream.ReadByte() >= 0) throw new InvalidDataException("ATSM-CASE-DATA-AFTER-END");
                    break;
                }
                else throw new InvalidDataException("ATSM-CASE-UNSUPPORTED-RECORD");
                expectedRecord++; previousHash = Convert.ToHexString(SHA256.HashData(ciphertext));
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        return new(header ?? throw new InvalidDataException("ATSM-CASE-MISSING-HEADER"), entries, batches, complete);
    }
    private sealed record Envelope(Guid CaseId, long RecordSequence, string PreviousHash, string Kind, JsonElement Value);
    private sealed record Closure(long RecordCount, bool CaptureComplete);
}

public sealed class DiagnosticTraceSink(TraceHeader header, ProtectedCaseJournal? journal = null) : TraceSinkBase(header.TraceId, null)
{
    private readonly List<TraceEntry> _entries = [];
    private readonly List<InstrumentObservationBatch> _batches = [];
    private long _bytes;
    public override void EnsureReady() { base.EnsureReady(); journal?.EnsureReady(); }
    protected override void Append(TraceEntry entry)
    {
        if (_entries.Count >= 100000 || _bytes + entry.Data.Length > DiagnosticReader.MaxInputBytes)
        { journal?.MarkIncomplete(); throw new IOException("ATSM-CASE-CAPTURE-LIMIT"); }
        journal?.Append("entry", TraceLine.From(entry));
        lock (_entries) { _entries.Add(entry); _bytes += entry.Data.Length; }
    }
    public IReadOnlyList<TraceEntry> Snapshot() { lock (_entries) return _entries.ToArray(); }
    public IReadOnlyList<InstrumentObservationBatch> Batches() { lock (_batches) return _batches.ToArray(); }
    public void StoreBatch(InstrumentObservationBatch batch)
    {
        if (journal is null) throw new InvalidOperationException("ATSM-CASE-DURABLE-CUSTODY-REQUIRED");
        journal.Append("batch", batch);
        lock (_batches) _batches.Add(batch);
    }
}

/// <summary>The maintenance application takes durable custody of a diagnostic batch; it never
/// posts to a LIMS or generates work orders. Protocol simulation is selected explicitly in the UI.</summary>
public sealed class DiagnosticWorkflowHost(DiagnosticTraceSink trace) : ILabWorkflowHost
{
    public ValueTask<IngestOutcome> IngestAsync(InstrumentObservationBatch batch, CancellationToken token)
    { token.ThrowIfCancellationRequested(); trace.StoreBatch(batch); return ValueTask.FromResult(IngestOutcome.Accepted); }
    public ValueTask<OrderQueryAnswer> AnswerQueryAsync(OrderQuery query, CancellationToken token) => ValueTask.FromResult(OrderQueryAnswer.NoInformation);
    public async IAsyncEnumerable<WorkOrder> PendingOrdersAsync(InstrumentRef instrument, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    { await Task.CompletedTask; yield break; }
    public ValueTask AcknowledgeOrderAsync(string orderId, OrderDeliveryOutcome outcome, CancellationToken token) => ValueTask.CompletedTask;
    public ValueTask<CodeMappingSet?> MappingsAsync(InstrumentRef instrument, CancellationToken token) => ValueTask.FromResult<CodeMappingSet?>(null);
}
