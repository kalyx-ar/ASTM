using System.Security.Cryptography;
using System.Text;
using Atsm.Abstractions.Model;
using Atsm.Abstractions.Tracing;
using Atsm.Diagnostics;
using Atsm.Plugin.BioRad.Cfx;
using Atsm.Plugin.Sysmex.Urinalysis;
using Atsm.Runtime.Tracing;
using Atsm.Runtime.Sessions;
using Atsm.Testing;

namespace Atsm.Tools.Trace;

internal static class NativeSelfTest
{
    // Synthetic, in-memory coverage of the closed catalog, parsers, metadata and encrypted custody.
    // This is an explicit CLI self-test, not a file logger or runtime storage fallback.
    internal static int Run()
    {
        var catalog = new DiagnosticCatalog(); var profile = catalog.Resolve(CfxPlugin.CsvProfileId);
        var data = "Well,Sample,Target,Cq,Content,Cq Mean,Cq Std. Dev\nA01,SYN,T1,23.4,NTC,23.5,0.1\n"u8.ToArray();
        var input = DiagnosticReader.Read(data, DiagnosticInputFormat.Export, profile, "synthetic-native-self-test", TimeSpan.Zero);
        var timing = new DiagnosticTiming(TimeSpan.Zero, TimeSpan.Zero, TimeZoneInfo.Utc);
        var analysis = DiagnosticReader.Analyze(input, profile, timing);
        var value = analysis.Observations.Single();
        using var protection = new SelfTestProtection(); using var storage = new MemoryStream();
        using (var journal = new ProtectedCaseJournal(storage, protection, input.Header))
        {
            foreach (var entry in input.Entries) journal.Append("entry", TraceLine.From(entry));
            journal.Append("batch", new InstrumentObservationBatch(Guid.NewGuid(), value.Instrument, [value], value.Trace));
        }
        var encrypted = storage.ToArray();
        if (Encoding.UTF8.GetString(encrypted).Contains(Convert.ToBase64String(data), StringComparison.Ordinal)) throw new InvalidDataException("Unprotected self-test payload.");
        var read = ProtectedCaseJournal.Read(new MemoryStream(encrypted), protection);
        var stored = read.Batches!.Single().Observations.Single();
        if (!read.IsComplete || stored.Value.Numeric != 23.4m || stored.Quality!.Statistics.Count != 2 || catalog.Profiles.Count != 14)
            throw new InvalidDataException("Native self-test mismatch.");
        using var text = new StringWriter(); TraceFile.Write(text, input.Header, input.Entries);
        if (TraceFile.Read(new StringReader(text.ToString())).Entries.Count != 1) throw new InvalidDataException("Native trace JSON mismatch.");
        var selected = catalog.Resolve("abbott.architect.asi-rs232"); var host = new InMemoryLabHost();
        var processor = new AstmMessageProcessor(selected.AstmPlugin!.CreateDialect(selected.AstmProfile!, new(new("native-test"), TimeProvider.System)),
            selected.AstmProfile!, host, new("native-test"), new MemoryTraceSink());
        var message = "H|\\^&\rP|1\rO|1|SYN\rR|1|^^^0021^SYN^UNDILUTED^P^LOT^PACK^^F|1.2|u||||F||||20261025120000\rL|1\r";
        processor.ProcessAsync(selected.AstmProfile!.Encoding.GetBytes(message), Guid.NewGuid()).AsTask().GetAwaiter().GetResult();
        if (host.Batches.Single().Observations.Single().Value.Numeric != 1.2m) throw new InvalidDataException("Native ASTM host mismatch.");
        var ucInput = DiagnosticReader.Read("RQ02,SYN-0001      01\r\n"u8.ToArray(), DiagnosticInputFormat.Auto, profile, "synthetic", TimeSpan.Zero);
        var ucProfile = catalog.Resolve(SysmexUrinalysisPlugin.Uc3500Profile);
        var uc = DiagnosticReader.Analyze(ucInput, ucProfile, timing);
        if (ucInput.Header.Profile != ucProfile.Id || uc.Messages.Count != 1 || !ProtocolExplainer.MessageContent(uc.Messages[0], ucProfile, []).Contains("Consulta", StringComparison.Ordinal))
            throw new InvalidDataException("Native UC-3500 mismatch.");
        var quality = QualitySeriesBuilder.Build(analysis.Observations).Single();
        if (quality.NumericCount != 1 || quality.MeasurementCount != 1 || quality.Median != 23.4m || quality.StandardDeviation is not null) throw new InvalidDataException("Native quality summary mismatch.");
        var normal = value with { Value = new("normal ") };
        var qualitative = QualitySeriesBuilder.Build([normal, normal, normal, normal]).Single();
        if (qualitative.MeasurementCount != 4 || qualitative.NumericCount != 0 || qualitative.ValueFrequencies.Single() is not { Value: "normal", Count: 4 })
            throw new InvalidDataException("Native qualitative summary mismatch.");
        var crosses = value with { Test = CodeSet.Of(new("BLD", null, "urn:atsm:vendor:sysmex.uc3500")), ResultKind = "QUALITATIVE", Value = new("2+") };
        var knowledge = (Atsm.Plugins.IInstrumentValueKnowledge)ucProfile.TracePlugin!;
        var ordinal = QualitySeriesBuilder.Build([crosses], knowledge).Single();
        var bounded = QualitySeriesBuilder.Build([crosses with { ResultKind = "CONCENTRATION", Value = new(">=50", 50, ">=") }], knowledge).Single();
        if (ordinal.Range.Median != "2+" || ordinal.StandardDeviation is not null || bounded.Range.Minimum != ">=50" || bounded.Range.Maximum is not null || bounded.NumericCount != 0)
            throw new InvalidDataException("Native semiquantitative summary mismatch.");
        var negative = crosses with { Value = new("-") };
        var zero = QualitySeriesBuilder.Build([negative], knowledge).Single();
        if (zero.Range.Minimum != "0" || zero.Range.Median != "0" || zero.NotPerformed != 0 || zero.NumericCount != 0 || negative.Value.RawText != "-")
            throw new InvalidDataException("Native negative category mismatch.");
        var uwam = catalog.Resolve(SysmexUrinalysisPlugin.WithChemistryProfile);
        var semanticInput = DiagnosticReader.Read("H|\\^&|||U-WAM\rP|1\rO|1|SYN\rR|1|^^^SYN-IMAGE^A^1^IF|SYN.png^RAW|||N|||||||UF-5000\rR|2|^^^C-Error Code^A^1^S|9902^RAW|||||||||UC-3500\rL|1\r"u8.ToArray(), DiagnosticInputFormat.AstmMessage, uwam, "synthetic", TimeSpan.Zero);
        var semantic = DiagnosticReader.Analyze(semanticInput, uwam, timing);
        if (ProtocolExplainer.Status(semantic.Observations[0], uwam) != "Normal" || ProtocolExplainer.ResultValue(semantic.Observations[0], uwam) != "Imagen"
            || !semantic.Diagnostics.Any(d => DiagnosticText.Finding(d, uwam) == "Error de solicitud (9902)"))
            throw new InvalidDataException("Native semantic presentation mismatch.");
        var cs = catalog.Resolve("sysmex.cs2500.astm02.results");
        var csInput = DiagnosticReader.Read("H|\\^&|||CS-2500\rP|1\rO|1||01^02^SYN||||||||Q\rR|1|^^^PT^PT^100^1|******|s||N^[0000.4000.0030 SOURCE]||F\rL|1|N\r"u8.ToArray(), DiagnosticInputFormat.AstmMessage, cs, "synthetic", TimeSpan.Zero);
        var csResult = DiagnosticReader.Analyze(csInput, cs, timing).Observations.Single();
        if (ProtocolExplainer.Status(csResult, cs) != "Sin curva de calibración validada") throw new InvalidDataException("Native CS-2500 knowledge mismatch.");
        var xn = catalog.Resolve("sysmex.xnl.astm02.results");
        var ip = csResult with { Test = CodeSet.Of(new("PLT_Clumps?", null, "urn:atsm:vendor:sysmex.xnl")), Flags = [new("A", null, "urn:atsm:vendor:sysmex.xnl:flag")] };
        if (ProtocolExplainer.Status(ip, xn) != "Sospecha de agregados plaquetarios"
            || ((Atsm.Plugins.IInstrumentFlagKnowledge)xn.AstmPlugin!).DescribeFlag(ip, ip.Flags[0])!.IsError)
            throw new InvalidDataException("Native XN-L knowledge mismatch.");
        var qw = csResult with { Test = CodeSet.Of(new("SYN", null, "urn:atsm:vendor:diagast.qwalys3")), ResultKind = "A", Value = new("^^^^^^CMP", Coded: new("CMP", null, "urn:synthetic")) };
        if (ProtocolExplainer.ResultValue(qw, catalog.Resolve("diagast.qwalys3.astm.results")) != "Compatible") throw new InvalidDataException("Native QWALYS knowledge mismatch.");
        var notice = new byte[266]; notice[0] = 1; notice[265] = 3;
        Encoding.ASCII.GetBytes("SYNTHETIC").CopyTo(notice.AsSpan(3)); Encoding.ASCII.GetBytes("Limpieza").CopyTo(notice.AsSpan(37));
        Encoding.ASCII.GetBytes("DBS_LANG_02215Necesidad").CopyTo(notice.AsSpan(87)); Encoding.ASCII.GetBytes("DBS_LANG_01745").CopyTo(notice.AsSpan(151));
        var controlInput = DiagnosticReader.Read(notice, DiagnosticInputFormat.Auto, uwam, "synthetic", TimeSpan.Zero);
        var controlProfile = catalog.Resolve(controlInput.Header.Profile!); var control = DiagnosticReader.Analyze(controlInput, controlProfile, timing);
        if (controlProfile.Id != SysmexUrinalysisPlugin.UfControlProfile || control.Observations.Count != 1
            || ProtocolExplainer.ResultValue(control.Observations[0], controlProfile) != "Limpieza requerida"
            || CommunicationStatistics.Build(control).Any(s => s.Code.StartsWith("Operation", StringComparison.Ordinal)))
            throw new InvalidDataException("Native UF control/communication mismatch.");
        Console.WriteLine("ATSM_NATIVE_SELFTEST_OK catalog=14 cfx=1 qc=2 trace-json=ok encrypted-case=ok astm-host=ok uc3500=ok quality-summary=ok qualitative-count=ok semiquantitative=ok negative-zero-crosses=ok semantic-flags-images-errors=ok manufacturer-tables=ok uf-control-partial=ok communication-stats=ok");
        return 0;
    }
    private sealed class SelfTestProtection : IDiagnosticCaseProtection, IDisposable
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
        public byte[] Protect(byte[] plain)
        {
            var encrypted = new byte[28 + plain.Length]; RandomNumberGenerator.Fill(encrypted.AsSpan(0, 12));
            using var aes = new AesGcm(_key, 16); aes.Encrypt(encrypted.AsSpan(0, 12), plain, encrypted.AsSpan(28), encrypted.AsSpan(12, 16)); return encrypted;
        }
        public byte[] Unprotect(byte[] encrypted)
        {
            var plain = new byte[encrypted.Length - 28]; using var aes = new AesGcm(_key, 16);
            aes.Decrypt(encrypted.AsSpan(0, 12), encrypted.AsSpan(28), encrypted.AsSpan(12, 16), plain); return plain;
        }
        public void Dispose() => CryptographicOperations.ZeroMemory(_key);
    }
}
