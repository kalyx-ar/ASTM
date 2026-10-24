using System.Runtime.CompilerServices;
using Atsm.Abstractions.Channels;
using Atsm.Abstractions.Hosting;
using Atsm.Abstractions.Model;
using Atsm.Abstractions.Tracing;
using Atsm.Plugin.Abbott.Architect;
using Atsm.Plugins;
using Atsm.Protocol.Lis02;
using Atsm.Runtime.Sessions;
using Atsm.Runtime.Tracing;
using Atsm.Testing;
using Atsm.Tests.Architect;

namespace Atsm.Tests.Runtime;

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
    public async Task Boundary_channel_reassembles_fragments_without_LIS01_handshakes()
    {
        var (processor, profile, host, trace) = Setup();
        var channel = new SuppliedChannel(profile.Encoding.GetBytes(ArchitectDialectTests.PatientUpload), fragment: 7);
        var session = new WholeMessageInstrumentSession(channel, processor, profile, trace, "host");
        await session.RunAsync(CancellationToken.None);
        Assert.Equal(4, Assert.Single(host.Batches).Observations.Count);
        Assert.Empty(channel.Writes); Assert.Equal(1, channel.Releases);
    }

    [Fact]
    public async Task End_of_stream_without_boundary_is_not_a_complete_message()
    {
        var (processor, profile, host, trace) = Setup();
        var channel = new SuppliedChannel(profile.Encoding.GetBytes(ArchitectDialectTests.PatientUpload), boundary: false);
        await Assert.ThrowsAsync<InvalidDataException>(() => new WholeMessageInstrumentSession(channel, processor, profile, trace, "host").RunAsync(CancellationToken.None));
        Assert.Empty(host.Batches); Assert.Equal(1, channel.Releases);
    }

    [Fact]
    public async Task Boundary_channel_query_response_is_unframed_LIS02_content()
    {
        var (processor, profile, _, trace) = Setup();
        var channel = new SuppliedChannel(profile.Encoding.GetBytes("H|\\^&\rQ|1|^SID-NONE||^^^ALL||||||||O\rL|1\r"), fragment: 5);
        await new WholeMessageInstrumentSession(channel, processor, profile, trace, "host").RunAsync(CancellationToken.None);
        var response = profile.Encoding.GetString(Assert.Single(channel.Writes));
        Assert.StartsWith("H|", response); Assert.EndsWith("L|1\r", response);
        Assert.DoesNotContain('\x06', response); Assert.DoesNotContain('\x02', response);
        Assert.Single(Lis02Codec.Parse(response).Messages);
    }

    [Fact]
    public async Task Trace_readiness_precedes_channel_factory_and_acquisition()
    {
        var trace = new FailingTrace(ready: false);
        var called = false;
        await Assert.ThrowsAsync<IOException>(() => InstrumentCommunicationHost.RunAsync(trace, _ =>
        { called = true; return ValueTask.FromResult<IInstrumentChannel>(new SuppliedChannel([])); }, (_, _) => Task.CompletedTask));
        Assert.False(called);
        var channel = new SuppliedChannel([]);
        var plugin = new ArchitectPlugin(); var profile = plugin.Profiles[0]; var instrument = new InstrumentRef("a1");
        var session = new AstmInstrumentSession(channel, plugin.CreateDialect(profile, new(instrument, TimeProvider.System)), profile,
            new InMemoryLabHost(), instrument, trace, new("host"));
        await Assert.ThrowsAsync<IOException>(() => session.RunAsync(CancellationToken.None));
        Assert.Equal(0, channel.Acquisitions);
    }

    [Fact]
    public async Task Live_LIS01_trace_failure_propagates_and_prevents_ACK()
    {
        var plugin = new ArchitectPlugin(); var profile = plugin.Profiles[0]; var instrument = new InstrumentRef("a1");
        var channel = new SuppliedChannel([0x05]);
        var session = new AstmInstrumentSession(channel, plugin.CreateDialect(profile, new(instrument, TimeProvider.System)), profile,
            new InMemoryLabHost(), instrument, new FailingTrace(ready: true), new("host"));
        await Assert.ThrowsAsync<IOException>(() => session.RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Empty(channel.Writes); Assert.Equal(1, channel.Releases);
    }

    [Fact]
    public async Task Rejected_transfer_releases_the_lease_without_communicating()
    {
        var plugin = new ArchitectPlugin(); var profile = plugin.Profiles[0]; var instrument = new InstrumentRef("a1");
        var channel = new SuppliedChannel([0x05], admitted: false);
        var session = new AstmInstrumentSession(channel, plugin.CreateDialect(profile, new(instrument, TimeProvider.System)), profile,
            new InMemoryLabHost(), instrument, new MemoryTraceSink(), new("host"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.RunAsync(CancellationToken.None));
        Assert.Empty(channel.Writes); Assert.Equal(1, channel.Releases);
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

internal sealed class SuppliedChannel(byte[] content, int fragment = 4096, bool boundary = true, bool admitted = true) : IInstrumentChannel
{
    private int _position;
    public int Acquisitions { get; private set; }
    public int Releases { get; private set; }
    public List<byte[]> Writes { get; } = [];
    public ChannelDescriptor Descriptor { get; } = new("supplied", ChannelKind.WholeMessage, "memory://supplied");
    public ChannelCapabilities Capabilities { get; } = ChannelCapabilities.Stream(OwnershipEnforcement.Advisory) with { MessageBoundaries = true };
    public ValueTask<ControlLease> AcquireAsync(ControlRequest request, CancellationToken token)
    { Acquisitions++; return ValueTask.FromResult(new ControlLease("supplied", request.ControllerId, Guid.NewGuid(), 1, 1, Guid.NewGuid(), OwnershipEnforcement.Advisory, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1))); }
    public ValueTask<ControlLease> RenewAsync(ControlLease lease, CancellationToken token) => ValueTask.FromResult(lease);
    public ValueTask<TransferAdmission> ConfirmTransferAsync(ControlLease lease, Guid transferId, CancellationToken token) => ValueTask.FromResult(new TransferAdmission(admitted ? TransferAdmissionState.Open : TransferAdmissionState.Rejected));
    public ValueTask<ReleaseOutcome> ReleaseAsync(ControlLease lease, CancellationToken token) { Releases++; return ValueTask.FromResult(ReleaseOutcome.Released); }
    public ValueTask<ReadOutcome> ReadAsync(Memory<byte> buffer, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_position == content.Length) return ValueTask.FromResult(ReadOutcome.End);
        var count = Math.Min(fragment, Math.Min(buffer.Length, content.Length - _position));
        content.AsMemory(_position, count).CopyTo(buffer); _position += count;
        return ValueTask.FromResult(new ReadOutcome(count, false, boundary && _position == content.Length));
    }
    public ValueTask<WriteOutcome> WriteAsync(ReadOnlyMemory<byte> bytes, ControlLease lease, CancellationToken token)
    { Writes.Add(bytes.ToArray()); return ValueTask.FromResult(new WriteOutcome(Guid.NewGuid(), bytes.Length, bytes.Length, WriteProgress.Completed)); }
    public async IAsyncEnumerable<ChannelEvent> EventsAsync([EnumeratorCancellation] CancellationToken token) { await Task.CompletedTask; yield break; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
