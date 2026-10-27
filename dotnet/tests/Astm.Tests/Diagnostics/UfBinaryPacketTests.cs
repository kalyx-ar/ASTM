using System.Buffers.Binary;
using System.Text;
using Astm.Abstractions.Tracing;
using Astm.Diagnostics;
using Astm.Plugin.Sysmex.Urinalysis;

namespace Astm.Tests.Diagnostics;

public sealed class UfBinaryPacketTests
{
    private static byte[] Counters()
    {
        var payload = new byte[102]; payload[0] = 4; payload[2] = 1; payload[14] = 70;
        for (var i = 0; i < 7; i++)
        {
            var offset = 18 + i * 12; payload[offset] = (byte)(i + 1);
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset + 6), (ushort)(i < 4 ? 1541 + i : 0));
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset + 8), (uint)(i < 4 ? 80 + i : 0));
        }
        return payload;
    }
    [Fact]
    public void Counters_decode_all_fields_and_every_split_maps_numeric_bytes_without_false_ASCII_commands()
    {
        var profile = new DiagnosticCatalog().Resolve(SysmexUrinalysisPlugin.UfBinaryProfile); var packet = Packet(2, Counters());
        for (var split = 1; split < packet.Length; split++)
        {
            var input = new DiagnosticCase(new(Guid.Empty, "synthetic", "synthetic", profile.Id, DateTimeOffset.UnixEpoch),
                [Entry(1, packet.AsMemory(0, split)), Entry(2, packet.AsMemory(split))]);
            var result = DiagnosticReader.Analyze(input, profile, new(TimeSpan.Zero, TimeSpan.Zero, TimeZoneInfo.Utc));
            var message = Assert.Single(result.Messages); Assert.Equal("UF_COUNTERS", message.Type);
            Assert.Equal("1541", message.Fields["counter.1.value"]); Assert.Equal("80", message.Fields["counter.1.level"]);
            Assert.Equal("4", message.Fields["status.code"]); Assert.Equal("70", message.Fields["status.level"]);
            Assert.Equal("0", message.Fields["counter.7.value"]); Assert.Equal(Convert.ToHexString(packet), message.Text);
            Assert.Equal(Enumerable.Range(0, packet.Length), message.BinaryFields.SelectMany(f => Enumerable.Range(f.Offset, f.Length)));
            var parts = BinaryTracePresentation.DescribeEntries(input.Entries, result.Messages, profile); Assert.Equal(2, parts.Count);
            Assert.All(parts.Values, p => { Assert.DoesNotContain("ACK", p.Meaning); Assert.DoesNotContain("EOT", p.Meaning); Assert.DoesNotContain("Fragmento", p.Meaning); });
            Assert.Contains("Contador 1: 1541", BinaryTracePresentation.DescribeMessage(message, profile));
            Assert.Contains("1=1541", ProtocolExplainer.MessageContent(message, profile, []));
            Assert.Empty(result.Observations); Assert.Empty(result.Timings);
        }
    }
    [Fact]
    public void Single_byte_reads_and_NUL_reads_inherit_the_whole_numeric_field()
    {
        var profile = new DiagnosticCatalog().Resolve(SysmexUrinalysisPlugin.UfBinaryProfile); var packet = Packet(2, Counters());
        var input = new DiagnosticCase(new(Guid.Empty, "synthetic", "synthetic", profile.Id, DateTimeOffset.UnixEpoch),
            packet.Select((b, i) => Entry(i, new byte[] { b })).ToArray());
        var result = DiagnosticReader.Analyze(input, profile, new(TimeSpan.Zero, TimeSpan.Zero, TimeZoneInfo.Utc));
        var fields = BinaryTracePresentation.DescribeEntries(input.Entries, result.Messages, profile);
        Assert.Equal(6, packet[70]); Assert.Equal("Contador 1: 1541", fields[70].Meaning);
        Assert.Equal(4, packet[99]); Assert.Equal("Registro del contador 4", fields[99].Meaning);
        Assert.Equal(0, packet[64]); Assert.Equal("Registro del contador 1", fields[64].Meaning);
        Assert.Equal(packet.Length, fields.Count);
    }
    [Fact]
    public void Several_packets_in_one_read_preserve_field_offsets_and_unknown_layouts_are_retained()
    {
        var profile = new DiagnosticCatalog().Resolve(SysmexUrinalysisPlugin.UfBinaryProfile); var packet = Packet(2, Counters());
        var unknown = Counters(); unknown[18] = 99; var other = Packet(2, unknown);
        var input = DiagnosticReader.Read(packet.Concat(other).ToArray(), DiagnosticInputFormat.WireBytes, profile, "synthetic", TimeSpan.Zero);
        var result = DiagnosticReader.Analyze(input, profile, new(TimeSpan.Zero, TimeSpan.Zero, TimeZoneInfo.Utc));
        Assert.Equal(new[] { "UF_COUNTERS", "UF_DATA" }, result.Messages.Select(m => m.Type));
        Assert.Equal(packet.Length, result.Messages[1].FirstByteOffset);
        Assert.False(result.Messages[1].Fields.ContainsKey("counter.1.value"));
        Assert.Equal(Convert.ToHexString(other), result.Messages[1].Text);
        var parts = BinaryTracePresentation.DescribeEntries(input.Entries, result.Messages, profile);
        Assert.Contains("Contador 1: 1541", Assert.Single(parts).Value.Meaning);
    }
    private static byte[] Packet(int type, byte[] payload)
    {
        var packet = new byte[46 + payload.Length]; packet[0] = 2; packet[36] = 2; packet[37] = 1; packet[^1] = 3;
        Encoding.ASCII.GetBytes("SYNTHETIC-ID").CopyTo(packet.AsSpan(1)); Encoding.ASCII.GetBytes("SYN-02").CopyTo(packet.AsSpan(21));
        packet[33] = (byte)type; BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(41), payload.Length); payload.CopyTo(packet.AsSpan(45)); return packet;
    }
    private static byte[] Notice(string code, string text)
    {
        var payload = new byte[376]; Encoding.UTF8.GetBytes(text).CopyTo(payload.AsSpan(62)); Encoding.ASCII.GetBytes(code).CopyTo(payload.AsSpan(262)); return payload;
    }
    private static TraceEntry Entry(int sequence, ReadOnlyMemory<byte> bytes, TraceDirection direction = TraceDirection.Received) =>
        new(sequence, TimeSpan.FromMilliseconds(sequence * 10), DateTimeOffset.UnixEpoch, direction, bytes);
    [Fact]
    public void UF_binary_and_hex_use_the_same_normalized_model_and_interpretation()
    {
        var profile = new DiagnosticCatalog().Resolve(SysmexUrinalysisPlugin.WithChemistryProfile);
        var bytes = Packet(11, Notice("DBS_LANG_01682", "Ejecute cierre"));
        var raw = DiagnosticReader.Read(bytes, DiagnosticInputFormat.Auto, profile, "synthetic", TimeSpan.Zero);
        var hex = DiagnosticReader.Read(Encoding.ASCII.GetBytes(string.Join(" ", bytes.Select(b => b.ToString("X2")))), DiagnosticInputFormat.Auto, profile, "synthetic", TimeSpan.Zero);
        var selected = new DiagnosticCatalog().Resolve(raw.Header.Profile!); var timing = new DiagnosticTiming(TimeSpan.Zero, TimeSpan.Zero, TimeZoneInfo.Utc);
        var a = DiagnosticReader.Analyze(raw, selected, timing); var b = DiagnosticReader.Analyze(hex, selected, timing);
        Assert.Equal(raw.Header.Profile, hex.Header.Profile); Assert.Equal(a.Messages.Select(m => m.Text), b.Messages.Select(m => m.Text));
        Assert.Equal(a.Observations.Select(o => ProtocolExplainer.ResultValue(o, selected)), b.Observations.Select(o => ProtocolExplainer.ResultValue(o, selected)));
    }
    [Fact]
    public void Complete_length_delimited_packets_survive_every_split_and_expose_identifiers_and_new_labels()
    {
        var plugin = new SysmexUrinalysisPlugin(); var profile = plugin.TraceProfiles.Single(p => p.Id == SysmexUrinalysisPlugin.UfBinaryProfile);
        var packet = Packet(11, Notice("DBS_LANG_99999", "Aviso futuro del equipo"));
        for (var split = 1; split < packet.Length; split++)
        {
            var result = plugin.Interpret(profile, [Entry(1, packet.AsMemory(0, split)), Entry(2, packet.AsMemory(split))], new("SYN"), Guid.Empty, TimeZoneInfo.Utc);
            var message = Assert.Single(result.Messages); Assert.Equal("UF_NOTICE", message.Code); Assert.Equal(packet.Length * 2, message.Text.Length);
            Assert.Equal("SYNTHETIC-ID", message.Fields["device.identifier"]); Assert.Equal("SYN-02", message.Fields["device.secondary"]);
            Assert.Equal("Aviso futuro del equipo", Assert.Single(result.Observations).Value.RawText);
            Assert.Equal(1, message.FirstSequence); Assert.Equal(2, message.LastSequence); Assert.Empty(result.Timings);
            Assert.Contains(result.Events, e => e.Code == "uf.header");
        }
        var changed = packet.ToArray(); changed.AsSpan(1, 20).Clear(); Encoding.ASCII.GetBytes("OTHER-SYN-ID").CopyTo(changed.AsSpan(1));
        var sequence = plugin.Interpret(profile, [Entry(1, packet), Entry(2, packet), Entry(3, changed)], new("SYN"), Guid.Empty, TimeZoneInfo.Utc);
        var change = Assert.Single(sequence.Events, e => e.Code == "uf.identity-changed");
        Assert.Equal(new[] { "SYNTHETIC-ID", "OTHER-SYN-ID" }, change.Explanation.Arguments);
    }
    [Fact]
    public void Payload_ETX_and_short_control_patterns_do_not_split_or_duplicate_a_complete_packet()
    {
        var payload = new byte[102]; new byte[] { 2, 0, 0, 0, 0, 0, 247, 9, 100, 0, 0, 0, 3 }.CopyTo(payload.AsSpan(30));
        var packets = Packet(2, payload).Concat(Packet(11, Notice("DBS_LANG_01682", "Ejecute cierre"))).ToArray();
        var profile = new DiagnosticCatalog().Resolve(SysmexUrinalysisPlugin.UfBinaryProfile);
        var input = DiagnosticReader.Read(packets, DiagnosticInputFormat.Auto, profile, "synthetic", TimeSpan.Zero);
        var result = DiagnosticReader.Analyze(input, profile, new(TimeSpan.Zero, TimeSpan.Zero, TimeZoneInfo.Utc));
        Assert.Equal(new[] { "UF_DATA", "UF_NOTICE" }, result.Messages.Select(m => m.Type));
        Assert.Equal(packets.Length * 2, result.Messages.Sum(m => m.Text.Length));
        Assert.Equal("Ejecute cierre", ProtocolExplainer.ResultValue(Assert.Single(result.Observations), profile));
        Assert.DoesNotContain(result.Diagnostics, d => d.Code.Contains("PARTIAL", StringComparison.Ordinal));
    }
    [Fact]
    public void Notice_lists_read_each_populated_slot_and_do_not_require_known_message_text_or_code()
    {
        var payload = new byte[1500];
        foreach (var (slot, code, label) in new[] { (0, 123, "Err mater. de control caducado"), (2, 456, "UF-CELLPACK SF caducado"), (9, 987, "Otro aviso nuevo") })
        {
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(slot * 150), code);
            Encoding.UTF8.GetBytes(label).CopyTo(payload.AsSpan(slot * 150 + 10));
        }
        var profile = new DiagnosticCatalog().Resolve(SysmexUrinalysisPlugin.UfBinaryProfile);
        var packet = Packet(6, payload); var input = DiagnosticReader.Read(packet, DiagnosticInputFormat.Auto, profile, "synthetic", TimeSpan.Zero);
        var result = DiagnosticReader.Analyze(input, profile, new(TimeSpan.Zero, TimeSpan.Zero, TimeZoneInfo.Utc));
        Assert.Equal("UF_ALERTS", Assert.Single(result.Messages).Type); Assert.Equal(3, result.Observations.Count);
        Assert.Equal(new[] { "Err mater. de control caducado", "UF-CELLPACK SF caducado", "Otro aviso nuevo" }, result.Observations.Select(o => o.Value.RawText));
        Assert.All(result.Observations, o => { Assert.Null(o.Value.Numeric); Assert.Null(o.Quality); Assert.Equal("Aviso del equipo", ProtocolExplainer.ResultKind(o, profile)); });
        var notices = result.Diagnostics.Where(d => d.Code == "ASTM-UF-DEVICE-NOTICE").ToArray();
        Assert.Equal(3, notices.Length); Assert.All(notices, d => Assert.DoesNotContain("sin interpretar", DiagnosticText.Finding(d, profile)));
    }
    [Fact]
    public void Wrong_lengths_or_separated_directions_never_produce_a_completed_outer_packet()
    {
        var plugin = new SysmexUrinalysisPlugin(); var profile = plugin.TraceProfiles.Single(p => p.Id == SysmexUrinalysisPlugin.UfBinaryProfile);
        var packet = Packet(2, new byte[102]); var split = 50;
        Assert.Empty(plugin.Interpret(profile, [Entry(1, packet.AsMemory(0, split)), Entry(2, packet.AsMemory(split), TraceDirection.Transmitted)], new("SYN"), Guid.Empty, TimeZoneInfo.Utc).Messages);
        packet[41] = 255;
        Assert.Empty(plugin.Interpret(profile, [Entry(1, packet)], new("SYN"), Guid.Empty, TimeZoneInfo.Utc).Messages);
    }
}
