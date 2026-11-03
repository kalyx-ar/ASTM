using System.Buffers.Binary;
using System.Text;
using Astm.Abstractions.Tracing;
using Astm.Formats.Medical.Model;
using Astm.Plugin.Sysmex.Urinalysis;
using Astm.Plugins;

namespace Astm.Tests.Service;

public sealed class StreamReaderTests
{
    [Theory]
    [InlineData(Uc3500CheckMode.Bcc)]
    [InlineData(Uc3500CheckMode.Sum)]
    [InlineData(Uc3500CheckMode.Sum2)]
    [InlineData(Uc3500CheckMode.None)]
    public void UC_incremental_decoder_waits_for_the_actual_checksum_across_every_split(Uc3500CheckMode mode)
    {
        IInstrumentTraceStreamPlugin plugin = new SysmexUrinalysisPlugin(mode);
        var payload = Encoding.ASCII.GetBytes("RQ02,SYN-0001      01");
        byte xor = 3; var sum = 3; foreach (var b in payload) { xor ^= b; sum += b; }
        byte[] check = mode switch { Uc3500CheckMode.Bcc => [xor], Uc3500CheckMode.Sum => [(byte)sum],
            Uc3500CheckMode.Sum2 => Encoding.ASCII.GetBytes(((byte)sum).ToString("X2")), _ => [] };
        var frame = new byte[] { 2 }.Concat(payload).Append((byte)3).Concat(check).ToArray();
        for (var split = 1; split < frame.Length; split++)
        {
            var reader = plugin.CreateReader(plugin.Profiles.Single(p => p.Id == SysmexUrinalysisPlugin.Uc3500Profile), new("SYN"), Guid.NewGuid(), TimeZoneInfo.Utc, 4096);
            Assert.Empty(reader.Feed(Entry(0, frame[..split])).Messages);
            Assert.Single(reader.Feed(Entry(1, frame[split..])).Messages);
            Assert.Empty(reader.Complete().Messages);
        }
    }

    [Fact]
    public void UF_incremental_decoder_keeps_a_partial_outer_envelope_and_releases_completed_buffers()
    {
        IInstrumentTraceStreamPlugin plugin = new SysmexUrinalysisPlugin();
        var payload = new byte[376]; Encoding.UTF8.GetBytes("Aviso sintetico").CopyTo(payload.AsSpan(62));
        Encoding.ASCII.GetBytes("DBS_LANG_99999").CopyTo(payload.AsSpan(262));
        var packet = new byte[46 + payload.Length]; packet[0] = 2; packet[36] = 2; packet[37] = 1; packet[^1] = 3;
        Encoding.ASCII.GetBytes("SYNTHETIC-ID").CopyTo(packet.AsSpan(1)); Encoding.ASCII.GetBytes("SYN-02").CopyTo(packet.AsSpan(21));
        packet[33] = 11; BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(41), payload.Length); payload.CopyTo(packet.AsSpan(45));
        var profile = plugin.Profiles.Single(p => p.Id == SysmexUrinalysisPlugin.UfBinaryProfile);
        for (var split = 1; split < packet.Length; split++)
        {
            var reader = plugin.CreateReader(profile, new("SYN"), Guid.NewGuid(), TimeZoneInfo.Utc, 1024);
            Assert.Empty(reader.Feed(Entry(0, packet[..split])).Observations);
            Assert.Single(reader.Feed(Entry(1, packet[split..])).Observations);
            Assert.Empty(reader.Complete().Observations);
        }
        var continuous = plugin.CreateReader(profile, new("SYN"), Guid.NewGuid(), TimeZoneInfo.Utc, 1024);
        for (var i = 0; i < 2000; i++) Assert.Single(continuous.Feed(Entry(i, packet)).Observations);
        Assert.Empty(continuous.Complete().Diagnostics);
    }

    private static TraceEntry Entry(long sequence, byte[] content) => new(sequence, TimeSpan.FromMilliseconds(sequence), DateTimeOffset.UnixEpoch, TraceDirection.Received, content);
}
