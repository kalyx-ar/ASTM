using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Atsm.Abstractions.Model;
using Atsm.Abstractions.Tracing;
using Atsm.Diagnostics;
using Atsm.Plugin.BioRad.Cfx;
using Atsm.Plugins;
using Atsm.Protocol.Lis01;
using Atsm.Runtime.Tracing;
using Atsm.Testing;
using Atsm.Tests.Runtime;

namespace Atsm.Tests.Diagnostics;

public class DiagnosticTests
{
    [Fact]
    public void Catalog_identifies_real_capabilities_and_never_connects_a_CFX_file_profile()
    {
        var catalog = new DiagnosticCatalog();
        Assert.Equal(catalog.Profiles.Count, catalog.Profiles.Select(p => p.Id).Distinct().Count());
        Assert.False(catalog.Resolve(CfxPlugin.CsvProfileId).CanConnect);
        Assert.True(catalog.Resolve("sysmex.xnl.astm02.results").CanConnect);
        Assert.Contains(catalog.Profiles, p => p.Manufacturer == "DIAGAST");
        Assert.Contains(catalog.LogAdapters, a => a.Id == "sysmex.port-monitor");
        Assert.DoesNotContain(typeof(DiagnosticCatalog).Assembly.GetReferencedAssemblies(), a => a.Name!.Contains("EDC", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CFX_CSV_keeps_quoted_identifiers_and_original_Cq_without_clinical_classification()
    {
        var plugin = new CfxPlugin();
        var result = Content(plugin, CfxPlugin.CsvProfileId, "Well,Sample,Target,Cq\r\nA01,\"SYN,\"\"Q\"\"\",T1,23.45\r\nA02,SYN2,T1,Undetermined\r\n");
        Assert.Equal(2, result.Observations.Count); Assert.Equal("SYN,\"Q\"", result.Observations[0].Specimen.SpecimenId);
        Assert.Equal(23.45m, result.Observations[0].Value.Numeric); Assert.Equal("23.45", result.Observations[0].Value.RawText);
        Assert.Null(result.Observations[1].Value.Numeric); Assert.Equal(InstrumentStatusMeaning.Unknown, result.Observations[1].InstrumentStatusMeaning);
    }

    [Fact]
    public void CFX_CSV_locale_is_explicit_and_missing_sample_does_not_become_a_well_identifier()
    {
        var plugin = new CfxPlugin(new(';', "es-AR"));
        var result = Content(plugin, CfxPlugin.CsvProfileId, "Well;Sample;Target;Cq\nA01;;T1;23,45\n");
        var observation = Assert.Single(result.Observations);
        Assert.Equal(23.45m, observation.Value.Numeric); Assert.Equal("", observation.Specimen.SpecimenId); Assert.Equal("A01", observation.Specimen.Position);
        Assert.Contains(result.Diagnostics, d => d.Code == "ATSM-CFX-MISSING-SAMPLE");
    }

    [Theory]
    [InlineData("Well,Sample,Target\nA01,SYN,T1\n")]
    [InlineData("Well,Sample,Target,Cq,Cq\nA01,SYN,T1,2,2\n")]
    [InlineData("Well,Sample,Target,Cq\nA01,SYN,T1,23\nA02,\"unfinished,T2,25\n")]
    [InlineData("Well,Sample,Target,Cq\nA01,SYN,T1,23,extra\n")]
    public void Invalid_CFX_CSV_never_returns_a_partial_batch(string text)
    {
        var result = Content(new CfxPlugin(), CfxPlugin.CsvProfileId, text);
        Assert.Empty(result.Observations); Assert.Contains(result.Diagnostics, d => d.Severity == Atsm.Abstractions.Diagnostics.AtsmSeverity.Error);
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1.1")]
    public void RDML_XML_and_compressed_export_preserve_exclusions_and_unavailable_sentinels(string version)
    {
        var text = Rdml(version, "-1", "<excl>synthetic exclusion</excl>");
        var direct = Content(new CfxPlugin(), CfxPlugin.RdmlProfileId, text);
        var observation = Assert.Single(direct.Observations);
        Assert.Null(observation.Value.Numeric); Assert.Equal("-1", observation.Value.RawText);
        Assert.Contains(observation.Flags, f => f.Code == "excluded"); Assert.Contains(observation.Flags, f => f.Code == "cq.unavailable");
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(archive.CreateEntry("rdml_data.xml").Open())) writer.Write(text);
        var zipped = new CfxPlugin().Interpret(new CfxPlugin().Profiles.Single(p => p.Id == CfxPlugin.RdmlProfileId), buffer.ToArray(), new("CFX-SYN"), TraceRef.None);
        Assert.Equal(observation.Value.RawText, Assert.Single(zipped.Observations).Value.RawText);
    }

    [Theory]
    [InlineData("1.3", "S1")]
    [InlineData("1.1", "UNDECLARED")]
    public void Unsupported_RDML_versions_and_unknown_references_are_explicit(string version, string sample)
    {
        var text = Rdml(version, "23", "").Replace("<sample id='S1'/>", $"<sample id='{sample}'/>", StringComparison.Ordinal);
        var result = Content(new CfxPlugin(), CfxPlugin.RdmlProfileId, text);
        Assert.Empty(result.Observations); Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public void RDML_DTDs_and_external_entities_are_not_resolved()
    {
        var text = "<!DOCTYPE rdml [<!ENTITY external SYSTEM 'file:///does-not-exist'>]>" + Rdml("1.1", "&external;", "");
        var result = Content(new CfxPlugin(), CfxPlugin.RdmlProfileId, text);
        Assert.Empty(result.Observations); Assert.Contains(result.Diagnostics, d => d.Code == "ATSM-CFX-INVALID-EXPORT");
    }

    [Fact]
    public void Sysmex_XNL_uses_component_five_and_preserves_rerun_judgement()
    {
        var profile = new DiagnosticCatalog().Resolve("sysmex.xnl.astm02.results");
        var result = Analyze(profile, "H|\\^&\rP|1\rO|1||2^3^SYN-XNL\rR|1|^^^^WBC^1^^^W|1.19|10*3/uL||L||P||||20261024120000\rL|1\r");
        var value = Assert.Single(result.Observations);
        Assert.Equal("WBC", value.Test.Vendor.Code); Assert.Equal("SYN-XNL", value.Specimen.SpecimenId);
        Assert.Equal("P", value.InstrumentStatus); Assert.Equal(InstrumentStatusMeaning.Unknown, value.InstrumentStatusMeaning);
        Assert.Equal("1", value.VendorIdentity["dilution"]);
    }

    [Fact]
    public void Sysmex_CS2500_keeps_parameter_dilution_and_flags_without_dereferencing_paths()
    {
        var profile = new DiagnosticCatalog().Resolve("sysmex.cs2500.astm02.results");
        var result = Analyze(profile, "H|\\^&\rP|1\rO|1||RACK^04^SYN-CS^B\rR|1|^^^041^PT sec^100.00^9^^^|10.2|sec||N||||||20261024120000\rL|1\r");
        var value = Assert.Single(result.Observations);
        Assert.Equal("041", value.Test.Vendor.Code); Assert.Equal("SYN-CS", value.Specimen.SpecimenId); Assert.Equal("100.00", value.Dilution);
        Assert.Equal("9", value.ResultKind); Assert.Equal(10.2m, value.Value.Numeric); Assert.Equal("N", value.VendorIdentity["flags.raw"]);
    }

    [Fact]
    public void Qwalys_reactions_use_O4_R4_component_seven_and_C3()
    {
        var profile = new DiagnosticCatalog().Resolve("diagast.qwalys3.astm.results");
        var result = Analyze(profile, "H|\\^&\rP|1\rO|1||SYN-QW|^^^GROUP\rR|1|^^^W^0^Anti A^GROUP^1|^^^^^^4+^^^^^^^^^^|M||||R|||20261024120000\rC|0|SYN-COMMENT\rL|1\r");
        var value = Assert.Single(result.Observations);
        Assert.Equal("SYN-QW", value.Specimen.SpecimenId); Assert.Equal("4+", value.Value.Coded!.Code); Assert.Null(value.Value.Numeric);
        Assert.Null(value.Value.RawUnit); Assert.Equal("M", value.VendorIdentity["reaction.support"]);
        Assert.Equal(InstrumentStatusMeaning.Unknown, value.InstrumentStatusMeaning); Assert.Equal("SYN-COMMENT", value.Comments.Single());
    }

    [Theory]
    [InlineData("sysmex.xnl.astm02.results")]
    [InlineData("sysmex.cs2500.astm02.results")]
    [InlineData("diagast.qwalys3.astm.results")]
    public void Receive_only_profiles_report_unimplemented_queries(string profileId)
    {
        var result = Analyze(new DiagnosticCatalog().Resolve(profileId), "H|\\^&\rQ|1|^SYN-Q||ALL\rL|1\r");
        Assert.Empty(result.Observations);
        Assert.Contains(result.Diagnostics, d => d.Code.EndsWith("QUERY-UNSUPPORTED", StringComparison.Ordinal));
    }

    [Fact]
    public void Protected_cases_round_trip_and_do_not_expose_capture_text()
    {
        var bytes = MakeCase(); var protection = Protector;
        Assert.DoesNotContain("SENSITIVE-SYNTHETIC-MARKER", Encoding.UTF8.GetString(bytes));
        using var stream = new MemoryStream(bytes); var read = ProtectedCaseJournal.Read(stream, protection);
        Assert.True(read.IsComplete); Assert.Equal(2, read.Entries.Count);
        Assert.Equal("SENSITIVE-SYNTHETIC-MARKER", Encoding.UTF8.GetString(read.Entries[0].Data.Span));
    }

    [Fact]
    public void Corruption_and_reordering_are_rejected_and_removed_footer_is_visible()
    {
        var bytes = MakeCase(); var blocks = Blocks(bytes);
        var corrupt = bytes.ToArray(); corrupt[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => ProtectedCaseJournal.Read(new MemoryStream(corrupt), Protector));
        var reordered = bytes.AsSpan(0, 9).ToArray().Concat(blocks[0]).Concat(blocks[2]).Concat(blocks[1]).Concat(blocks[3]).ToArray();
        Assert.Throws<InvalidDataException>(() => ProtectedCaseJournal.Read(new MemoryStream(reordered), Protector));
        var unsealed = bytes.AsSpan(0, 9).ToArray().Concat(blocks.Take(3).SelectMany(b => b)).ToArray();
        var read = ProtectedCaseJournal.Read(new MemoryStream(unsealed), Protector);
        Assert.False(read.IsComplete); Assert.Equal(2, read.Entries.Count);
        var tornFooter = ProtectedCaseJournal.Read(new MemoryStream(bytes[..^7]), Protector);
        Assert.False(tornFooter.IsComplete); Assert.Equal(2, tornFooter.Entries.Count);
    }

    [Fact]
    public async Task Capture_mode_never_sends_protocol_bytes_and_requires_a_ready_store_before_connecting()
    {
        var profile = new DiagnosticCatalog().Resolve("abbott.architect.asi-rs232");
        var header = Header(profile.Id); var trace = new DiagnosticTraceSink(header); var channel = new SuppliedStream([5, 4]);
        await LiveDiagnostic.RunAsync(profile, Timing, trace, _ => ValueTask.FromResult<Stream>(channel), false, CancellationToken.None);
        Assert.Empty(channel.Writes); Assert.Single(trace.Snapshot());
        using var buffer = new MemoryStream(); var journal = new ProtectedCaseJournal(buffer, Protector, header); var unavailable = new DiagnosticTraceSink(header, journal); journal.Dispose();
        var connected = false;
        await Assert.ThrowsAsync<ObjectDisposedException>(() => LiveDiagnostic.RunAsync(profile, Timing, unavailable, _ => { connected = true; return ValueTask.FromResult<Stream>(channel); }, false, CancellationToken.None));
        Assert.False(connected);
    }

    [Fact]
    public async Task Host_simulation_takes_durable_diagnostic_custody_before_returning_success()
    {
        var header = Header("abbott.architect.asi-rs232");
        using var storage = new MemoryStream(); using var journal = new ProtectedCaseJournal(storage, Protector, header);
        var trace = new DiagnosticTraceSink(header, journal); var host = new DiagnosticWorkflowHost(trace);
        var batch = new InstrumentObservationBatch(Guid.NewGuid(), new("SYN-I"), [], TraceRef.None);
        Assert.Equal(Atsm.Abstractions.Hosting.IngestState.Accepted, (await host.IngestAsync(batch, CancellationToken.None)).State);
        Assert.Single(trace.Batches());
        Assert.Throws<InvalidOperationException>(() => new DiagnosticTraceSink(header).StoreBatch(batch));
        Assert.Equal(Atsm.Abstractions.Hosting.QueryAnswerState.NoInformation, (await host.AnswerQueryAsync(new(Guid.NewGuid(), new("SYN-I"), [], "ALL", TraceRef.None), CancellationToken.None)).State);
    }

    [Fact]
    public async Task Live_instrument_results_survive_protected_case_reopening_and_offline_analysis()
    {
        var profile = new DiagnosticCatalog().Resolve("sysmex.xnl.astm02.results");
        var header = Header(profile.Id); using var storage = new MemoryStream();
        var journal = new ProtectedCaseJournal(storage, Protector, header); var trace = new DiagnosticTraceSink(header, journal);
        var (hostSide, instrumentSide) = DuplexStream.CreatePair();
        await using var simulator = new InstrumentSimulator(instrumentSide, encoding: profile.AstmProfile!.Encoding);
        Stream channel = hostSide;
        using var stop = new CancellationTokenSource();
        var run = LiveDiagnostic.RunAsync(profile, Timing, trace, _ => ValueTask.FromResult<Stream>(channel), true, stop.Token);
        try
        {
            var sent = await simulator.SendAsync("H|\\^&", "P|1", "O|1||2^3^SYN-XNL", "R|1|^^^^WBC^1^^^W|1.19|10*3/uL||L||P||||20261024120000", "L|1").WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(Lis01SendResult.Delivered, sent.Result);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (trace.Batches().Count == 0) await Task.Delay(5, deadline.Token);
        }
        finally
        {
            stop.Cancel();
            try { await run.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
            journal.Dispose();
        }
        var reopened = ProtectedCaseJournal.Read(new MemoryStream(storage.ToArray()), Protector);
        Assert.True(reopened.IsComplete);
        var stored = Assert.Single(Assert.Single(reopened.Batches!).Observations);
        Assert.Equal("SYN-XNL", stored.Specimen.SpecimenId); Assert.Equal(1.19m, stored.Value.Numeric);
        Assert.Equal("WBC", stored.Test.Vendor.Code); Assert.Equal("1", stored.VendorIdentity["dilution"]);
        var replayed = Assert.Single(DiagnosticReader.Analyze(reopened, profile, Timing).Observations);
        Assert.Equal(stored.Value, replayed.Value); Assert.Equal(stored.Specimen, replayed.Specimen);
        Assert.Contains(reopened.Entries, e => e.Direction == TraceDirection.Transmitted && e.Data.Span.Contains((byte)6));
    }

    [Fact]
    public void An_interrupted_case_keeps_its_incomplete_status_when_saved_again()
    {
        using var storage = new MemoryStream();
        using (var journal = new ProtectedCaseJournal(storage, Protector, Header(CfxPlugin.CsvProfileId))) journal.MarkIncomplete();
        var reopened = ProtectedCaseJournal.Read(new MemoryStream(storage.ToArray()), Protector);
        Assert.False(reopened.IsComplete);
        using var saved = new MemoryStream();
        using (var journal = new ProtectedCaseJournal(saved, Protector, reopened.Header))
        {
            if (!reopened.IsComplete) journal.MarkIncomplete();
            foreach (var entry in reopened.Entries) journal.Append("entry", TraceLine.From(entry));
        }
        Assert.False(ProtectedCaseJournal.Read(new MemoryStream(saved.ToArray()), Protector).IsComplete);
        Assert.Contains(DiagnosticReader.Analyze(reopened, new DiagnosticCatalog().Resolve(CfxPlugin.CsvProfileId), Timing).Diagnostics,
            d => d.Code == "ATSM-DIAG-INCOMPLETE-CASE");
    }

    private static DiagnosticAnalysis Analyze(DiagnosticProfile profile, string text) => DiagnosticReader.Analyze(
        DiagnosticReader.Read(Encoding.ASCII.GetBytes(text), DiagnosticInputFormat.AstmMessage, profile, "synthetic", TimeSpan.Zero), profile, Timing);
    private static ContentInterpretation Content(CfxPlugin plugin, string id, string text) => plugin.Interpret(plugin.Profiles.Single(p => p.Id == id), Encoding.UTF8.GetBytes(text), new("CFX-SYN"), TraceRef.None);
    private static string Rdml(string version, string cq, string exclusion) => $"<rdml xmlns='http://www.rdml.org' version='{version}'><sample id='S1'><type>unkn</type></sample><target id='T1'/><experiment id='E1'><run id='R1'><react id='1'><sample id='S1'/><data><tar id='T1'/><cq>{cq}</cq>{exclusion}</data></react></run></experiment></rdml>";
    private static TraceHeader Header(string profile) => new(Guid.NewGuid(), "synthetic", "test", profile, DateTimeOffset.UtcNow);
    private static DiagnosticTiming Timing => new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeZoneInfo.Utc);
    private static readonly TestProtection Protector = new();
    private static byte[] MakeCase()
    {
        using var storage = new MemoryStream();
        using (var journal = new ProtectedCaseJournal(storage, Protector, Header(CfxPlugin.CsvProfileId)))
        {
            journal.Append("entry", new TraceLine(0, 0, DateTimeOffset.UtcNow, "rx", Convert.ToBase64String("SENSITIVE-SYNTHETIC-MARKER"u8), null, null));
            journal.Append("entry", new TraceLine(1, 1, DateTimeOffset.UtcNow, "ev", null, "TEST", null));
        }
        return storage.ToArray();
    }
    private static List<byte[]> Blocks(byte[] bytes)
    {
        var blocks = new List<byte[]>(); var offset = 9;
        while (offset < bytes.Length) { var size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset)); blocks.Add(bytes.AsSpan(offset, size + 4).ToArray()); offset += size + 4; }
        return blocks;
    }
    internal sealed class TestProtection : IDiagnosticCaseProtection
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
        public byte[] Protect(byte[] plain)
        {
            var output = new byte[28 + plain.Length]; RandomNumberGenerator.Fill(output.AsSpan(0, 12));
            using var aes = new AesGcm(_key, 16); aes.Encrypt(output.AsSpan(0, 12), plain, output.AsSpan(28), output.AsSpan(12, 16)); return output;
        }
        public byte[] Unprotect(byte[] encrypted)
        {
            var output = new byte[encrypted.Length - 28]; using var aes = new AesGcm(_key, 16);
            aes.Decrypt(encrypted.AsSpan(0, 12), encrypted.AsSpan(28), encrypted.AsSpan(12, 16), output); return output;
        }
    }
}
