using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Astm.Abstractions.Diagnostics;
using Astm.Abstractions.Tracing;
using Astm.Formats.Medical;
using Astm.Formats.Medical.Model;
using Astm.Operations;
using Astm.Plugins;
using Astm.Protocol.Hl7v2;

namespace Astm.Plugin.Abbott.Architect;

public sealed partial class ArchitectHl7Dialect
{
    // Abbott 206797-101 §2-17/30, §3-12/23 and §3-57/63. No calibration execution command.
    public IReadOnlyList<Hl7OperationBinding> AdditionalOperations(Hl7Profile profile)
    {
        OperationDefinition Definition(string purpose, OperationKind kind, string message, OperationContractReference? input = null) =>
            new(purpose, 1, kind, input, MedicalDataContracts.Decoded,
                input is null ? OperationCapabilities.Decode | OperationCapabilities.Observe
                    : OperationCapabilities.Prepare | OperationCapabilities.Execute | OperationCapabilities.Observe,
                OperationCompletionScope.MessageExchange, profile.Id, profile.Version, profile.Evidence)
            {
                Initiator = input is null ? OperationInitiator.Instrument : OperationInitiator.Host,
                WireBindings = input is null ? new Dictionary<string, string> { ["message.type"] = message }
                    : new Dictionary<string, string> { ["message.type"] = message, ["ack.message"] = "ACK^U06^ACK",
                        ["moduleSerial"] = "EQU.1.1.1", ["moduleId"] = "EQU.1.1.2", ["inventory.scope"] = "INV.1=ALL" },
            };
        var contract = new OperationDataContract(MedicalDataContracts.InventoryRequest, new(OperationDataKind.Object)
        {
            Fields = new Dictionary<string, OperationDataField>
            {
                ["moduleSerial"] = new(new(OperationDataKind.String) { MinLength = 1, MaxLength = 11 }, OperationFieldRequirement.Required),
                ["moduleId"] = new(new(OperationDataKind.String) { AllowedValues = ["1", "2", "3", "4", "5"] }, OperationFieldRequirement.Required),
            },
        });
        return [new(Definition(MedicalPurposes.ReceiveCalibration, OperationKind.Event, "OUL^R24^OUL_R24")),
            new(Definition(MedicalPurposes.ReceiveInventory, OperationKind.Event, "INU^U05^INU_U05")),
            new(Definition(MedicalPurposes.QueryInventory, OperationKind.Query, "INR^U06^INR_U06", contract.Reference), contract, BuildInventoryRequest,
                request => string.IsNullOrWhiteSpace(request.Data.GetProperty("moduleSerial").GetString())
                    ? [new("medical.inventory.module-required", "$.moduleSerial")] : [])];
    }

    private Hl7Message BuildInventoryRequest(OperationEnvelope envelope)
    {
        var request = envelope.Data.Deserialize(MedicalJsonContext.Default.MedicalInventoryRequest)!;
        var d = Hl7Delimiters.Default;
        var now = context.Clock.GetUtcNow();
        return new(d,
        [
            Hl7Segment.Build("MSH", d, (3, "ASTM"), (7, now.ToString("yyyyMMddHHmmss+0000", CultureInfo.InvariantCulture)),
                (9, "INR^U06^INR_U06"), (10, envelope.OperationId.ToString()), (11, "P"), (12, "2.5.1"), (18, "UNICODE UTF-8")),
            Hl7Segment.Build("EQU", d, (1, d.Encode(request.ModuleSerial) + d.Subcomponent + request.ModuleId),
                (2, now.ToString("yyyyMMddHHmmss+0000", CultureInfo.InvariantCulture))),
            Hl7Segment.Build("INV", d, (1, "ALL"), (2, "OK")),
        ]);
    }

    private static Hl7Interpretation InvalidQuality(string code) => new([], null,
        [new(code, AstmSeverity.Error, "Invalid ARCHITECT quality message; inspect the protected source trace.")]);

    private Hl7Interpretation InterpretCalibration(Hl7Message message, TraceRef trace)
    {
        const string invalid = "ASTM-ARC-HL7-INVALID-CALIBRATION";
        if (message.Header.Text(9, 3) != "OUL_R24") return InvalidQuality(invalid);
        var segments = message.Segments;
        var index = 1;
        while (index < segments.Count && segments[index].Name == "SFT") index++;
        if (index == 1 || index >= segments.Count || segments[index].Name != "OBR") return InvalidQuality(invalid);
        var order = segments[index++];
        var assay = order.Text(4, subcomponent: 1);
        if (assay.Length == 0 || order.Text(4, 3) != "ARCHITECT") return InvalidQuality(invalid);
        var materials = new List<QualityMaterial>();
        while (index < segments.Count && segments[index].Name == "SPM")
        {
            if (index + 2 >= segments.Count) return InvalidQuality(invalid);
            var specimen = segments[index++];
            if (segments[index++].Name != "SAC") return InvalidQuality(invalid);
            var inv = segments[index++];
            if (specimen.Text(2).Length == 0 || specimen.Text(11) != "C" || inv.Name != "INV"
                || inv.Text(1) != specimen.Text(2) || inv.Text(2).Length == 0 || inv.Text(3) != "RC") return InvalidQuality(invalid);
            materials.Add(new(QualityMaterialKind.Calibrator, inv.Text(1), EmptyNull(inv.Text(16)),
                ExpirationRaw: EmptyNull(inv.Text(12)), TypeRaw: inv.Text(3)));
        }
        if (materials.Count == 0 || index >= segments.Count || segments[index].Name != "OBX") return InvalidQuality(invalid);
        var obx = segments[index++];
        var equipment = obx.Repetitions(18);
        if (obx.Text(1) != "1" || obx.Text(2) != "ED" || obx.Text(3, subcomponent: 1) != assay
            || obx.Text(3, subcomponent: 3) != "C" || obx.Text(3, 3) != "ARCHITECT"
            || obx.Text(11) is not ("F" or "X") || equipment.Count != 2 || equipment.Any(string.IsNullOrWhiteSpace)
            || obx.Text(19).Length == 0 || obx.Repetitions(5).Count != 1
            || obx.Text(5, 1).Length != 0 || obx.Text(5, 2) != "text" || obx.Text(5, 3) != "plain"
            || obx.Text(5, 4) != "Base64" || obx.Field(5).Split(message.Delimiters.Component).Length != 5)
            return InvalidQuality(invalid);
        var encoded = obx.Text(5, 5);
        if (encoded.Length == 0 || encoded.Length > 65535) return InvalidQuality(invalid);
        byte[] content;
        try { content = Convert.FromBase64String(encoded); }
        catch (FormatException) { return InvalidQuality("ASTM-ARC-HL7-INVALID-CALIBRATION-BASE64"); }
        if (content.Length == 0) return InvalidQuality(invalid);
        string? text;
        var findings = new List<AstmDiagnostic>();
        try { text = new UTF8Encoding(false, true).GetString(content); }
        catch (DecoderFallbackException)
        {
            text = null;
            findings.Add(new("ASTM-ARC-HL7-CALIBRATION-TEXT-ENCODING", AstmSeverity.Warning,
                "Calibration bytes retained as Base64; the text encoding is not known."));
        }
        var reagents = new List<ReagentLot>();
        while (index < segments.Count && segments[index].Name == "SID")
        {
            var sid = segments[index++];
            reagents.Add(new(EmptyNull(sid.Text(2)), EmptyNull(sid.Text(3)), sid.Field(1)));
        }
        if (reagents.Count == 0 || index != segments.Count) return InvalidQuality(invalid);
        var report = new CalibrationReport(Guid.CreateVersion7(), context.Instrument,
            new(assay, EmptyNull(obx.Text(3, 2)), ArchitectDialect.VendorSystem), EmptyNull(obx.Text(3, subcomponent: 2)),
            message.Delimiters.Decode(equipment[0]), message.Delimiters.Decode(equipment[1]), obx.Text(19), obx.Text(11),
            obx.Text(11) == "F", encoded, Convert.ToHexString(SHA256.HashData(content)), text, materials.ToArray(), reagents.ToArray(), trace)
        { SourceFields = segments.Select((s, i) => (s, i)).ToDictionary(p => "segment." + p.i, p => p.s.Raw) };
        return new([], null, findings) { CalibrationReports = [report] };
    }

    private Hl7Interpretation InterpretInventory(Hl7Message message, TraceRef trace)
    {
        const string invalid = "ASTM-ARC-HL7-INVALID-INVENTORY";
        if (message.Header.Text(9, 3) != "INU_U05") return InvalidQuality(invalid);
        var segments = message.Segments;
        var index = 1;
        while (index < segments.Count && segments[index].Name == "SFT") index++;
        if (index == 1 || index >= segments.Count || segments[index].Name != "EQU") return InvalidQuality(invalid);
        var equ = segments[index++];
        if (equ.Text(1).Length == 0 || equ.Text(2).Length == 0 || index >= segments.Count) return InvalidQuality(invalid);
        var statuses = new List<InstrumentInventoryStatus>();
        for (; index < segments.Count; index++)
        {
            var inv = segments[index];
            if (inv.Name != "INV" || inv.Text(1).Length == 0 || inv.Text(3).Length == 0
                || inv.Repetitions(2).Any(string.IsNullOrWhiteSpace)) return InvalidQuality(invalid);
            var codes = inv.Repetitions(2).Select((_, i) => inv.Text(2, repetition: i + 1)).ToArray();
            if (codes.Any(string.IsNullOrWhiteSpace)) return InvalidQuality(invalid);
            var states = codes.Select(CalibrationState).Where(s => s != CalibrationStatusMeaning.Unknown).ToArray();
            // §3-15/16: a segment reports either inventory or calibration status, never both.
            if (states.Length > 0 && codes.Any(IsInventoryState)) return InvalidQuality(invalid);
            var assays = inv.Field(15).Length == 0 ? [] : inv.Repetitions(15).Select((_, i) =>
                new CodedValue(inv.Text(15, repetition: i + 1), EmptyNull(inv.Text(15, 2, i + 1)), ArchitectDialect.VendorSystem)).ToArray();
            if (assays.Any(a => a.Code.Length == 0)) return InvalidQuality(invalid);
            if (assays.Where((_, i) => inv.Text(15, 3, i + 1) != "ARCHITECT").Any()) return InvalidQuality(invalid);
            statuses.Add(new(Guid.CreateVersion7(), context.Instrument, equ.Text(1), equ.Text(2), inv.Field(1), inv.Text(3),
                codes.Select(c => new CodedValue(c, null, ArchitectDialect.VendorSystem + ":inventory-status")).ToArray(),
                states, assays, EmptyNull(inv.Text(16)), EmptyNull(inv.Text(4)), trace)
            {
                SourceFields = Enumerable.Range(1, inv.Raw.Split(message.Delimiters.Field).Length - 1)
                    .ToDictionary(n => "INV." + n, inv.Field)
                    .Concat(new Dictionary<string, string> { ["EQU"] = equ.Raw, ["MSH.10"] = message.ControlId })
                    .ToDictionary(p => p.Key, p => p.Value),
            });
        }
        return new([], null, []) { InventoryStatuses = statuses.ToArray() };
    }

    private static bool IsInventoryState(string code) => code is "EE" or "EO" or "SE" or "SO" or "NW" or "NE" or "DI"
        or "LE" or "LD" or "UL" or "RP" or "IO" or "MX";
    private static CalibrationStatusMeaning CalibrationState(string code) => code switch
    {
        "CN" => CalibrationStatusMeaning.None, "CE" => CalibrationStatusMeaning.Failed, "CX" => CalibrationStatusMeaning.Expired,
        "AX" => CalibrationStatusMeaning.AdjustmentExpired, "CI" => CalibrationStatusMeaning.InProgress, "CO" => CalibrationStatusMeaning.Valid,
        "CV" => CalibrationStatusMeaning.Overridden, "OL" => CalibrationStatusMeaning.CalibratorLotOverridden,
        "CQ" => CalibrationStatusMeaning.PendingQualityControl, _ => CalibrationStatusMeaning.Unknown,
    };
}
