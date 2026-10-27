using Astm.Abstractions.Hosting;
using Astm.Abstractions.Model;
using Astm.Abstractions.Tracing;
using Astm.Plugin.Abbott.Architect;
using Astm.Plugins;
using Astm.Protocol.Lis02;
using Astm.Runtime.Sessions;
using Astm.Runtime.Tracing;
using Astm.Testing;
using Astm.Tests.Architect;

namespace Astm.Tests.Runtime;

public class CompleteMessageTests
{
    [Fact]
    public async Task Complete_content_matches_the_existing_dialect_and_retries_keep_ids()
    {
        var plugin = new ArchitectPlugin(new() { InstrumentTimeZone = TimeZoneInfo.Utc });
        var profile = plugin.Profiles[0];
        var instrument = new InstrumentRef("a1");
        var dialect = plugin.CreateDialect(profile, new(instrument, TimeProvider.System));
        var host = new InMemoryLabHost();
        var trace = new MemoryTraceSink();
        var processor = new AstmMessageProcessor(dialect, profile, host, instrument, trace);
        var receipt = Guid.NewGuid();
        using var stream = new MemoryStream(profile.Encoding.GetBytes(ArchitectDialectTests.PatientUpload));
        Assert.True((await processor.ProcessAsync(stream, receipt)).Succeeded);
        Assert.True(stream.CanRead);
        await processor.ProcessAsync(profile.Encoding.GetBytes(ArchitectDialectTests.PatientUpload), receipt);
        Assert.Equal(host.Batches[0].BatchId, host.Batches[1].BatchId);
        Assert.Equal(host.Batches[0].Observations.Select(o => o.ObservationId), host.Batches[1].Observations.Select(o => o.ObservationId));
        var expected = dialect.Interpret(Assert.Single(Lis02Codec.Parse(ArchitectDialectTests.PatientUpload).Messages), TraceRef.None).Observations;
        Assert.Equal(expected.Select(o => (o.Specimen.SpecimenId, o.Test.Vendor.Code, o.ResultKind, o.Value.RawText)),
            host.Batches[0].Observations.Select(o => (o.Specimen.SpecimenId, o.Test.Vendor.Code, o.ResultKind, o.Value.RawText)));
        await processor.ProcessAsync(profile.Encoding.GetBytes(ArchitectDialectTests.PatientUpload), Guid.NewGuid());
        Assert.NotEqual(host.Batches[0].Observations[0].ObservationId, host.Batches[2].Observations[0].ObservationId);
    }

    [Fact]
    public async Task Truncated_content_is_never_ingested()
    {
        var (processor, profile, host, _) = Setup();
        var result = await processor.ProcessAsync(profile.Encoding.GetBytes("H|\\^&\rP|1\r"), Guid.NewGuid());
        Assert.False(result.Succeeded); Assert.Empty(host.Batches);
    }

    [Fact]
    public async Task Streams_are_bounded_and_cancellable()
    {
        var (_, profile, host, trace) = Setup();
        var processor = new AstmMessageProcessor(new ArchitectPlugin().CreateDialect(profile, new(new("a1"), TimeProvider.System)), profile, host, new("a1"), trace) { MaxMessageBytes = 8 };
        using var source = new MemoryStream(new byte[9]);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await processor.ProcessAsync(source, Guid.NewGuid()));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await processor.ProcessAsync(new byte[1], Guid.NewGuid(), cancelled.Token));
        Assert.Empty(host.Batches);
    }

    [Fact]
    public async Task Host_supplied_messages_are_processed_without_LIS01_handshakes()
    {
        var (processor, profile, host, trace) = Setup();
        var session = new WholeMessageInstrumentSession(processor, profile, trace);
        await session.RunAsync(Messages(profile.Encoding.GetBytes(ArchitectDialectTests.PatientUpload)), respond: null, CancellationToken.None);
        Assert.Equal(4, Assert.Single(host.Batches).Observations.Count);
        Assert.DoesNotContain(trace.Snapshot(), e => e.Direction == TraceDirection.Transmitted);
    }

    [Fact]
    public async Task Receive_only_medium_traces_the_answer_without_transmitting_it()
    {
        var (processor, profile, _, trace) = Setup();
        await new WholeMessageInstrumentSession(processor, profile, trace)
            .RunAsync(Messages(profile.Encoding.GetBytes("H|\\^&\rQ|1|^SID-NONE||^^^ALL||||||||O\rL|1\r")), respond: null, CancellationToken.None);
        Assert.Contains(trace.Snapshot(), e => e.Code == SessionCodes.TransmissionSuppressed);
        Assert.DoesNotContain(trace.Snapshot(), e => e.Direction == TraceDirection.Transmitted);
    }

    [Fact]
    public async Task Query_response_is_unframed_LIS02_content()
    {
        var (processor, profile, _, trace) = Setup();
        var writes = new List<byte[]>();
        await new WholeMessageInstrumentSession(processor, profile, trace).RunAsync(Messages(profile.Encoding.GetBytes("H|\\^&\rQ|1|^SID-NONE||^^^ALL||||||||O\rL|1\r")),
            (bytes, _) => { writes.Add(bytes.ToArray()); return ValueTask.CompletedTask; }, CancellationToken.None);
        var response = profile.Encoding.GetString(Assert.Single(writes));
        Assert.StartsWith("H|", response); Assert.EndsWith("L|1\r", response);
        Assert.DoesNotContain('\x06', response); Assert.DoesNotContain('\x02', response);
        Assert.Single(Lis02Codec.Parse(response).Messages);
    }

    [Fact]
    public async Task Trace_readiness_precedes_the_transport_factory()
    {
        var trace = new FailingTrace(ready: false);
        var called = false;
        await Assert.ThrowsAsync<IOException>(() => InstrumentCommunicationHost.RunAsync(trace, _ =>
        { called = true; return ValueTask.FromResult<Stream>(new SuppliedStream([])); }, (_, _) => Task.CompletedTask));
        Assert.False(called);
        var transport = new SuppliedStream([]);
        var plugin = new ArchitectPlugin(); var profile = plugin.Profiles[0]; var instrument = new InstrumentRef("a1");
        var session = new AstmInstrumentSession(transport, plugin.CreateDialect(profile, new(instrument, TimeProvider.System)), profile,
            new InMemoryLabHost(), instrument, trace, new());
        await Assert.ThrowsAsync<IOException>(() => session.RunAsync(CancellationToken.None));
        Assert.Equal(0, transport.Reads);
    }

    [Fact]
    public async Task Live_LIS01_trace_failure_propagates_and_prevents_ACK()
    {
        var plugin = new ArchitectPlugin(); var profile = plugin.Profiles[0]; var instrument = new InstrumentRef("a1");
        var transport = new SuppliedStream([0x05]);
        var session = new AstmInstrumentSession(transport, plugin.CreateDialect(profile, new(instrument, TimeProvider.System)), profile,
            new InMemoryLabHost(), instrument, new FailingTrace(ready: true), new());
        await Assert.ThrowsAsync<IOException>(() => session.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Empty(transport.Writes);
    }

    [Fact]
    public async Task Transport_failure_ends_the_session_and_is_reported()
    {
        var plugin = new ArchitectPlugin(); var profile = plugin.Profiles[0]; var instrument = new InstrumentRef("a1");
        var trace = new MemoryTraceSink();
        var session = new AstmInstrumentSession(new SuppliedStream([0x05], failAfterContent: true), plugin.CreateDialect(profile, new(instrument, TimeProvider.System)), profile,
            new InMemoryLabHost(), instrument, trace, new());
        await session.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(trace.Snapshot(), e => e.Code == SessionCodes.TransportFailed);
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> Messages(params byte[][] messages)
    {
        foreach (var message in messages)
        {
            await Task.Yield();
            yield return message;
        }
    }

    private static (AstmMessageProcessor, AstmProfile, InMemoryLabHost, MemoryTraceSink) Setup()
    {
        var plugin = new ArchitectPlugin(); var profile = plugin.Profiles[0]; var instrument = new InstrumentRef("a1");
        var host = new InMemoryLabHost(); var trace = new MemoryTraceSink();
        return (new(plugin.CreateDialect(profile, new(instrument, TimeProvider.System)), profile, host, instrument, trace), profile, host, trace);
    }
}

internal sealed class FailingTrace(bool ready) : TraceSinkBase(Guid.NewGuid(), null)
{
    public override void EnsureReady() { if (!ready) throw new IOException("trace unavailable"); }
    protected override void Append(TraceEntry entry) => throw new IOException("capture failed");
}

/// <summary>Transport with fixed content, recorded writes and an optional failure after the content.</summary>
internal sealed class SuppliedStream(byte[] content, int fragment = 4096, bool failAfterContent = false) : Stream
{
    private int _position;
    public int Reads { get; private set; }
    public List<byte[]> Writes { get; } = [];
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => content.Length;
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Reads++;
        if (_position == content.Length)
            return failAfterContent ? ValueTask.FromException<int>(new IOException("connection reset")) : ValueTask.FromResult(0);
        var count = Math.Min(fragment, Math.Min(buffer.Length, content.Length - _position));
        content.AsMemory(_position, count).CopyTo(buffer); _position += count;
        return ValueTask.FromResult(count);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken token = default)
    {
        Writes.Add(bytes.ToArray());
        return ValueTask.CompletedTask;
    }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) => Writes.Add(buffer.AsSpan(offset, count).ToArray());
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
