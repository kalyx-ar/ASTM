using System.Text;
using System.Text.Json;
using Astm.Abstractions.Tracing;
using Astm.Formats.Medical;
using Astm.Plugin.Abbott.Architect;
using Astm.Protocol.Hl7v2;
using Astm.Runtime.Tracing;

namespace Astm.Tools.Trace;

internal static class CalibrationSelfTest
{
    internal static void Run()
    {
        IMedicalOperationProvider plugin = new ArchitectHl7Plugin(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeZoneInfo.Utc);
        var operations = plugin.CreateOperations(ArchitectHl7Plugin.ProfileId, new(new("NATIVE-SYN"), TimeProvider.System));
        var query = operations.Prepare(MedicalRequests.Inventory(operations, new("MOD-SYN", "1"), Guid.NewGuid()));
        if (Hl7Codec.Parse(Encoding.UTF8.GetString(query.Bytes.Span)).Message?.Trigger != "U06")
            throw new InvalidDataException("Native inventory contract mismatch.");
        const string document = "MODEL\tSYNTHETIC\r\n";
        var text = "MSH|^~\\&|ARCHITECT|SYN|||||OUL^R24^OUL_R24|CAL-SYN|P|2.5.1\rSFT|SYN|1|SYN|SYN\r"
            + "OBR|1|||0042^SYN^ARCHITECT\rSPM|1|CAL-SYN|||||||||C\rSAC|1\rINV|CAL-SYN|OK|RC\r"
            + Hl7Segment.Build("OBX", Hl7Delimiters.Default, (1, "1"), (2, "ED"), (3, "0042&UNDILUTED&C^SYN^ARCHITECT"),
                (5, "^text^plain^Base64^" + Convert.ToBase64String(Encoding.UTF8.GetBytes(document))), (11, "F"),
                (18, "MOD-SYN&1~SYS-SYN"), (19, "20261102120000")).Raw + "\rSID|0042&1&SR|LOT-SYN|PACK-SYN\r";
        var decoded = operations.Decode(Encoding.UTF8.GetBytes(text), TraceRef.None, TimeZoneInfo.Utc);
        var json = JsonSerializer.Serialize(decoded, MedicalJsonContext.Default.MedicalDecoded);
        var report = JsonSerializer.Deserialize(json, MedicalJsonContext.Default.MedicalDecoded)!.CalibrationReports.Single();
        var batch = new Astm.Formats.Medical.Model.InstrumentObservationBatch(Guid.NewGuid(), report.Instrument, [], TraceRef.None)
            { CalibrationReports = [report] };
        var stored = JsonSerializer.Deserialize(JsonSerializer.Serialize(batch, AstmJsonContext.Default.InstrumentObservationBatch), AstmJsonContext.Default.InstrumentObservationBatch)!;
        if (decoded.Diagnostics.Count != 0 || stored.CalibrationReports.Single().Text != document || decoded.Observations.Count != 0)
            throw new InvalidDataException("Native calibration custody mismatch.");
    }
}
