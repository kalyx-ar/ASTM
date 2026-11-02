using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Astm.Abstractions.Diagnostics;
using Astm.Abstractions.Tracing;
using Astm.Formats.Medical;
using Astm.Formats.Medical.Hosting;
using Astm.Formats.Medical.Model;
using Astm.Operations;
using Astm.Plugin.Abbott.Architect;
using Astm.Plugins;
using Astm.Protocol.Hl7v2;
using Astm.Protocol.Mllp;
using Astm.Runtime.Quality;
using Astm.Runtime.Sessions;
using Astm.Runtime.Tracing;

namespace Astm.Tests.Hl7;

public class ArchitectCalibrationTests
{
    private static IMedicalOperations Open() => ((IMedicalOperationProvider)Hl7Data.Plugin())
        .CreateOperations(ArchitectHl7Plugin.ProfileId, new(new("a1"), TimeProvider.System));
    internal const string ReportText = "ASSAY\t0042\r\nMODEL\tSYNTHETIC\r\nSLOPE\t1.25\r\n";
    internal static string Calibration(string status = "F", byte[]? content = null)
    {
        var d = Hl7Delimiters.Default;
        string S(string name, params (int, string)[] fields) => Hl7Segment.Build(name, d, fields).Raw + "\r";
        return Hl7Data.Header("OUL^R24^OUL_R24", "CAL-SYN") + "SFT|SYN|1|SYN|SYN\r"
            + "OBR|1|||0042^SYNTEST^ARCHITECT\r"
            + "SPM|1|CAL-SYN-A|||||||||C\rSAC||||||||||RACK-SYN|1\rINV|CAL-SYN-A|OK|RC|||||||||20271231||||LOT-CAL-A\r"
            + "SPM|2|CAL-SYN-B|||||||||C\rSAC||||||||||RACK-SYN|2\rINV|CAL-SYN-B|OK|RC|||||||||20271231||||LOT-CAL-B\r"
            + S("OBX", (1, "1"), (2, "ED"), (3, "0042&UNDILUTED&C^SYNTEST^ARCHITECT"),
                (5, "^text^plain^Base64^" + Convert.ToBase64String(content ?? Encoding.UTF8.GetBytes(ReportText))),
                (11, status), (16, "OP-SYN-A~OP-SYN-B"), (18, "MOD-SYN&1~SYS-SYN"), (19, "20261102120000"))
            + "SID|0042&1&SR|REAGENT-SYN|PACK-SYN\r";
    }
    internal static string Inventory(string status = "CN~CI") => Hl7Data.Header("INU^U05^INU_U05", "INV-SYN")
        + "SFT|SYN|1|SYN|SYN\rEQU|MOD-SYN&1|20261102120100\r"
        + Hl7Segment.Build("INV", Hl7Delimiters.Default, (1, "REAG-SYN&1"), (2, status), (3, "SR"),
            (4, "PACK-SYN"), (15, "0042^SYNTEST^ARCHITECT~0043^OTHER-SYN^ARCHITECT"), (16, "REAGENT-SYN"), (19, "24^h")).Raw + "\r"
        + "INV|WASTE|IO|LW||||100|10|90||mL\r";

    [Theory]
    [InlineData("F", true)]
    [InlineData("X", false)]
    public void Calibration_is_a_document_with_material_reagent_module_and_status_not_a_patient_result(string status, bool success)
    {
        var operations = Open();
        var decoded = operations.Decode(Encoding.UTF8.GetBytes(Calibration(status)), TraceRef.None, TimeZoneInfo.Utc);
        Assert.Empty(decoded.Diagnostics); Assert.Empty(decoded.Observations);
        var report = Assert.Single(decoded.CalibrationReports);
        Assert.Equal(success, report.Successful); Assert.Equal(status, report.InstrumentStatus);
        Assert.Equal("0042", report.Assay.Code); Assert.Equal("MOD-SYN&1", report.ModuleId); Assert.Equal("SYS-SYN", report.SystemSerial);
        Assert.Equal("20261102120000", report.PerformedAtRaw); Assert.Equal(ReportText, report.Text);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ReportText))), report.ContentSha256);
        Assert.Equal(2, report.Materials.Count); Assert.Equal("LOT-CAL-B", report.Materials[1].Lot);
        Assert.Equal("REAGENT-SYN", Assert.Single(report.Reagents).Lot);
        Assert.Contains(report.SourceFields.Values, v => v.Contains("OP-SYN-A~OP-SYN-B"));
        var json = JsonSerializer.SerializeToElement(decoded, MedicalJsonContext.Default.MedicalDecoded);
        Assert.Empty(OperationDataValidator.Validate(operations.Resolve(MedicalDataContracts.Decoded), json, OperationValidationPhase.Prepared));
        Assert.Equal(ReportText, JsonSerializer.Deserialize(json, MedicalJsonContext.Default.MedicalDecoded)!.CalibrationReports[0].Text);
    }

    [Fact]
    public void Undocumented_text_encoding_preserves_exact_bytes_and_never_substitutes_replacement_characters()
    {
        byte[] bytes = [0xff, 0xfe, 0x41];
        var decoded = Open().Decode(Encoding.UTF8.GetBytes(Calibration(content: bytes)), TraceRef.None, TimeZoneInfo.Utc);
        var report = Assert.Single(decoded.CalibrationReports);
        Assert.Null(report.Text); Assert.Equal(bytes, Convert.FromBase64String(report.ContentBase64));
        Assert.Contains(decoded.Diagnostics, d => d.Severity == AstmSeverity.Warning && d.Code.EndsWith("TEXT-ENCODING"));
    }

    [Theory]
    [InlineData("base64")]
    [InlineData("empty-base64")]
    [InlineData("mime")]
    [InlineData("assay")]
    [InlineData("material")]
    [InlineData("missing-sid")]
    [InlineData("extra-obx")]
    [InlineData("extra-specimen")]
    [InlineData("equipment")]
    [InlineData("status")]
    [InlineData("oversize")]
    public async Task Malformed_calibration_rejects_the_entire_message_without_partial_custody(string fault)
    {
        var text = Calibration();
        text = fault switch
        {
            "base64" => text.Replace(Convert.ToBase64String(Encoding.UTF8.GetBytes(ReportText)), "###"),
            "empty-base64" => text.Replace(Convert.ToBase64String(Encoding.UTF8.GetBytes(ReportText)), " "),
            "mime" => text.Replace("^text^plain^Base64^", "^application^pdf^Base64^"),
            "assay" => text.Replace("0042&UNDILUTED&C", "0043&UNDILUTED&C"),
            "material" => text.Replace("INV|CAL-SYN-B|", "INV|DIFFERENT|"),
            "missing-sid" => text[..text.LastIndexOf("SID|", StringComparison.Ordinal)],
            "extra-obx" => text + "OBX|2|ED\r",
            "extra-specimen" => text + "SPM|3|OTHER\r",
            "equipment" => text.Replace("MOD-SYN&1~SYS-SYN", "MOD-SYN&1"),
            "status" => Calibration("P"),
            "oversize" => Calibration(content: new byte[50000]),
            _ => throw new InvalidOperationException(),
        };
        var host = new ControlledHost();
        var result = await Hl7Data.Processor(host).ProcessAsync(Encoding.UTF8.GetBytes(text), Guid.NewGuid());
        Assert.Equal("AR", Hl7Acknowledgements.Read(Assert.Single(result.Responses))!.Code);
        Assert.Empty(host.Batches);
    }

    [Theory]
    [InlineData("CN", CalibrationStatusMeaning.None)]
    [InlineData("CE", CalibrationStatusMeaning.Failed)]
    [InlineData("CX", CalibrationStatusMeaning.Expired)]
    [InlineData("AX", CalibrationStatusMeaning.AdjustmentExpired)]
    [InlineData("CI", CalibrationStatusMeaning.InProgress)]
    [InlineData("CO", CalibrationStatusMeaning.Valid)]
    [InlineData("CV", CalibrationStatusMeaning.Overridden)]
    [InlineData("OL", CalibrationStatusMeaning.CalibratorLotOverridden)]
    [InlineData("CQ", CalibrationStatusMeaning.PendingQualityControl)]
    public void Inventory_retains_all_items_and_assays_and_maps_only_documented_calibration_codes(string code, CalibrationStatusMeaning meaning)
    {
        var operations = Open();
        var decoded = operations.Decode(Encoding.UTF8.GetBytes(Inventory(code)), TraceRef.None, TimeZoneInfo.Utc);
        Assert.Empty(decoded.Diagnostics); Assert.Empty(decoded.Observations);
        Assert.Equal(2, decoded.InventoryStatuses.Count);
        var status = decoded.InventoryStatuses[0];
        Assert.Equal(meaning, Assert.Single(status.CalibrationStates)); Assert.Equal(2, status.Assays.Count);
        Assert.Equal("24^h", status.SourceFields["INV.19"]); Assert.Equal("PACK-SYN", status.ReagentSerial);
        Assert.Empty(decoded.InventoryStatuses[1].CalibrationStates);
        var json = JsonSerializer.SerializeToElement(decoded, MedicalJsonContext.Default.MedicalDecoded);
        Assert.Empty(OperationDataValidator.Validate(operations.Resolve(MedicalDataContracts.Decoded), json, OperationValidationPhase.Prepared));
    }

    [Fact]
    public void Unknown_inventory_codes_stay_raw_and_mixed_inventory_calibration_states_reject_the_whole_message()
    {
        var decoded = Open().Decode(Encoding.UTF8.GetBytes(Inventory("ZZ")), TraceRef.None, TimeZoneInfo.Utc);
        Assert.Empty(decoded.Diagnostics); Assert.Empty(decoded.InventoryStatuses[0].CalibrationStates);
        Assert.Equal("ZZ", decoded.InventoryStatuses[0].Statuses[0].Code);
        var invalid = Open().Decode(Encoding.UTF8.GetBytes(Inventory("CO~IO")), TraceRef.None, TimeZoneInfo.Utc);
        Assert.Contains(invalid.Diagnostics, d => d.Severity == AstmSeverity.Error); Assert.Empty(invalid.InventoryStatuses);
        invalid = Open().Decode(Encoding.UTF8.GetBytes(Inventory() + "INV|BROKEN\r"), TraceRef.None, TimeZoneInfo.Utc);
        Assert.Contains(invalid.Diagnostics, d => d.Severity == AstmSeverity.Error); Assert.Empty(invalid.InventoryStatuses);
    }

    [Theory]
    [InlineData(IngestState.Accepted, "AA")]
    [InlineData(IngestState.Duplicate, "AA")]
    [InlineData(IngestState.Deferred, "AE")]
    [InlineData(IngestState.Rejected, "AR")]
    public async Task Quality_only_batches_obey_custody_ACK_and_stable_reception_identity(IngestState state, string ack)
    {
        foreach (var text in new[] { Calibration(), Inventory() })
        {
            var host = new ControlledHost { Outcome = new(state) }; var processor = Hl7Data.Processor(host);
            var reception = Guid.NewGuid();
            var response = await processor.ProcessAsync(Encoding.UTF8.GetBytes(text), reception);
            Assert.Equal(ack, Hl7Acknowledgements.Read(Assert.Single(response.Responses))!.Code);
            await processor.ProcessAsync(Encoding.UTF8.GetBytes(text), reception);
            Assert.Equal(host.Batches[0].BatchId, host.Batches[1].BatchId);
            Assert.Equal(host.Batches[0].CalibrationReports.Select(r => r.ReportId), host.Batches[1].CalibrationReports.Select(r => r.ReportId));
            Assert.Equal(host.Batches[0].InventoryStatuses.Select(s => s.StatusId), host.Batches[1].InventoryStatuses.Select(s => s.StatusId));
            Assert.All(host.Batches, b => Assert.Empty(b.Observations));
        }
    }

    [Fact]
    public async Task Calibration_waits_for_custody_and_ETL_uses_the_same_report_identity_as_live_reception()
    {
        var host = new ControlledHost { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var id = Guid.NewGuid(); var bytes = Encoding.UTF8.GetBytes(Calibration());
        var pending = Hl7Data.Processor(host).ProcessAsync(bytes, id).AsTask();
        Assert.False(pending.IsCompleted); host.Gate.SetResult(IngestOutcome.Accepted);
        Assert.Equal("AA", Hl7Acknowledgements.Read(Assert.Single((await pending).Responses))!.Code);
        await new MedicalExchangeProcessor(Open(), host, new MemoryTraceSink()).ProcessAsync(bytes, id, TimeZoneInfo.Utc);
        Assert.Equal(host.Batches[0].CalibrationReports[0].ReportId, host.Batches[1].CalibrationReports[0].ReportId);
        var source = new ObservationQualityDataSource(Batches);
        var reports = new List<CalibrationReport>();
        await foreach (var report in source.ReadCalibrationReportsAsync(new(Assay: new("0042", null, ArchitectDialect.VendorSystem), ReagentLot: "REAGENT-SYN"))) reports.Add(report);
        Assert.Equal(2, reports.Count);
        await foreach (var _ in source.ReadCalibrationReportsAsync(new(Assay: new("0042", null, "wrong-system")))) Assert.Fail("Code systems must match.");
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        { await foreach (var _ in source.ReadCalibrationReportsAsync(new(), stop.Token)) { } });
        async IAsyncEnumerable<InstrumentObservationBatch> Batches([EnumeratorCancellation] CancellationToken token)
        { foreach (var batch in host.Batches) { token.ThrowIfCancellationRequested(); yield return batch; } await Task.CompletedTask; }
    }

    [Fact]
    public void Inventory_query_has_strict_schema_stable_control_id_and_documented_all_items_scope()
    {
        var operations = Open(); var id = Guid.NewGuid();
        var request = MedicalRequests.Inventory(operations, new("MOD-SYN", "1"), id);
        var message = Hl7Codec.Parse(Encoding.UTF8.GetString(operations.Prepare(request).Bytes.Span)).Message!;
        Assert.Equal("INR", message.Type); Assert.Equal("U06", message.Trigger); Assert.Equal(id.ToString(), message.ControlId);
        Assert.Equal("MOD-SYN&1", Assert.Single(message.OfType("EQU")).Text(1));
        Assert.Equal("ALL", Assert.Single(message.OfType("INV")).Text(1)); Assert.Empty(message.OfType("OBR"));
        Assert.Equal(id.ToString(), Hl7Codec.Parse(Encoding.UTF8.GetString(operations.Prepare(request).Bytes.Span)).Message!.ControlId);
        Assert.NotEmpty(operations.Validate(MedicalRequests.Inventory(operations, new("MOD-SYN", "6"), id)));
        Assert.NotEmpty(operations.Validate(MedicalRequests.Inventory(operations, new(" ", "1"), id)));
        Assert.NotEmpty(operations.Validate(MedicalRequests.Inventory(operations, new("BAD\rSEG", "1"), id)));
        Assert.NotEmpty(operations.Validate(request with { Data = JsonSerializer.SerializeToElement(new { moduleSerial = "MOD-SYN", moduleId = "1", assay = "0042" }) }));
        var asi = ((IMedicalOperationProvider)new ArchitectPlugin()).CreateOperations(ArchitectPlugin.AsiProfileId, new(new("a1"), TimeProvider.System));
        Assert.DoesNotContain(asi.Describe(asi.Profile.Id), d => d.Id == MedicalPurposes.QueryInventory || d.Id == MedicalPurposes.ReceiveCalibration);
    }

    [Fact]
    public async Task Inventory_ETL_and_read_port_preserve_all_rows_and_filter_by_actual_assay_authority()
    {
        var host = new ControlledHost(); var id = Guid.NewGuid(); var processor = new MedicalExchangeProcessor(Open(), host, new MemoryTraceSink());
        var bytes = Encoding.UTF8.GetBytes(Inventory());
        await processor.ProcessAsync(bytes, id, TimeZoneInfo.Utc); await processor.ProcessAsync(bytes, id, TimeZoneInfo.Utc);
        Assert.Equal(host.Batches[0].InventoryStatuses[0].StatusId, host.Batches[1].InventoryStatuses[0].StatusId);
        var source = new ObservationQualityDataSource(Batches);
        var rows = new List<InstrumentInventoryStatus>();
        await foreach (var row in source.ReadInventoryStatusesAsync(new(Assay: new("0043", null, ArchitectDialect.VendorSystem), ModuleId: "MOD-SYN&1"))) rows.Add(row);
        Assert.Equal(2, rows.Count);
        await foreach (var _ in source.ReadInventoryStatusesAsync(new(Assay: new("0043", null, "wrong-authority")))) Assert.Fail("Authority mismatch.");
        async IAsyncEnumerable<InstrumentObservationBatch> Batches([EnumeratorCancellation] CancellationToken token)
        { foreach (var batch in host.Batches) { token.ThrowIfCancellationRequested(); yield return batch; } await Task.CompletedTask; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Custom_HL7_delimiters_are_interpreted_and_observed_by_semantic_message_type(bool inventory)
    {
        var text = new string((inventory ? Inventory() : Calibration()).Select(c => c switch
            { '|' => '*', '^' => '%', '~' => '!', '\\' => '?', '&' => '$', _ => c }).ToArray());
        var decoded = Open().Decode(Encoding.UTF8.GetBytes(text), TraceRef.None, TimeZoneInfo.Utc);
        Assert.Empty(decoded.Diagnostics);
        var provider = (IMedicalOperationProvider)Hl7Data.Plugin();
        var observer = provider.CreateOperationObserver(ArchitectHl7Plugin.ProfileId, new(new("a1"), TimeProvider.System), Guid.NewGuid());
        observer.Feed(new(0, TimeSpan.Zero, DateTimeOffset.UnixEpoch, TraceDirection.Received, MllpFraming.Wrap(Encoding.UTF8.GetBytes(text))));
        Assert.Equal(inventory ? MedicalPurposes.ReceiveInventory : MedicalPurposes.ReceiveCalibration, Assert.Single(observer.Current.Operations).Purpose);
    }

    [Fact]
    public void Observer_keeps_inventory_query_separate_from_unsolicited_status_and_rejects_wrong_ACK_trigger()
    {
        var operations = Open(); var provider = (IMedicalOperationProvider)Hl7Data.Plugin();
        var observer = provider.CreateOperationObserver(ArchitectHl7Plugin.ProfileId, new(new("a1"), TimeProvider.System), Guid.NewGuid());
        var prepared = operations.Prepare(MedicalRequests.Inventory(operations, new("MOD-SYN", "1"), Guid.NewGuid()));
        var query = Hl7Codec.Parse(Encoding.UTF8.GetString(prepared.Bytes.Span)).Message!;
        observer.Feed(new(0, TimeSpan.Zero, DateTimeOffset.UnixEpoch, TraceDirection.Transmitted, MllpFraming.Wrap(prepared.Bytes.Span)));
        Assert.Equal(MedicalPurposes.QueryInventory, Assert.Single(observer.Current.Operations).Purpose);
        observer.Feed(new(1, TimeSpan.Zero, DateTimeOffset.UnixEpoch, TraceDirection.Received, MllpFraming.Wrap(Encoding.UTF8.GetBytes(Inventory()))));
        Assert.Contains(observer.Current.Operations, o => o.Purpose == MedicalPurposes.QueryInventory && o.State == OperationState.Pending);
        var wrong = Hl7Data.Header("ACK^R24^ACK", "WRONG") + "MSA|AA|" + query.ControlId + "\r";
        observer.Feed(new(2, TimeSpan.Zero, DateTimeOffset.UnixEpoch, TraceDirection.Received, MllpFraming.Wrap(Encoding.UTF8.GetBytes(wrong))));
        Assert.False(observer.Current.NoPendingOperations); Assert.Null(observer.Current.ChannelOperationInProgress);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Observer_tracks_quality_purpose_and_requires_matching_ACK_without_claiming_physical_execution(bool inventory)
    {
        var provider = (IMedicalOperationProvider)Hl7Data.Plugin();
        var observer = provider.CreateOperationObserver(ArchitectHl7Plugin.ProfileId, new(new("a1"), TimeProvider.System), Guid.NewGuid());
        var text = inventory ? Inventory() : Calibration(); var message = Hl7Codec.Parse(text).Message!;
        observer.Feed(new(0, TimeSpan.Zero, DateTimeOffset.UnixEpoch, TraceDirection.Received, MllpFraming.Wrap(Encoding.UTF8.GetBytes(text))));
        var operation = Assert.Single(observer.Current.Operations);
        Assert.Equal(inventory ? MedicalPurposes.ReceiveInventory : MedicalPurposes.ReceiveCalibration, operation.Purpose);
        Assert.True(observer.Current.ChannelOperationInProgress);
        var ack = Hl7Acknowledgements.Build(message, "AA", "ACK-SYN", DateTimeOffset.UnixEpoch);
        observer.Feed(new(1, TimeSpan.Zero, DateTimeOffset.UnixEpoch, TraceDirection.Transmitted, MllpFraming.Wrap(Encoding.UTF8.GetBytes(ack.Serialize()))));
        Assert.True(observer.Current.NoPendingOperations);
        Assert.DoesNotContain(observer.Current.Operations, o => o.Evidence == OperationEvidence.DeviceReported);
    }
}
