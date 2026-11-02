using System.Text;
using Astm.Abstractions.Diagnostics;
using Astm.Abstractions.Tracing;
using Astm.Formats.Medical;
using Astm.Formats.Medical.Hosting;
using Astm.Formats.Medical.Model;
using Astm.Operations;

namespace Astm.Runtime.Sessions;

public sealed record MedicalExchangeResult(IngestOutcome? Outcome, IReadOnlyList<PreparedOperationMessage> Responses,
    IReadOnlyList<AstmDiagnostic> Diagnostics);

/// <summary>One medical ETL binding across instruments and formats. Host owns authorization, custody, source lifecycle and durability.</summary>
public sealed class MedicalExchangeProcessor(IMedicalOperations operations, ILabWorkflowHost host, ITraceSink trace)
{
    public async ValueTask<MedicalExchangeResult> ProcessAsync(ReadOnlyMemory<byte> content, Guid receptionId,
        TimeZoneInfo timeZone, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); trace.EnsureReady();
        if (receptionId == Guid.Empty) throw new ArgumentException("medical.reception.id");
        if (content.IsEmpty || content.Length > 16 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(content));
        var sequence = trace.Write(TraceDirection.Received, content.Span, "medical.content");
        var reference = new TraceRef(trace.TraceId, sequence, sequence);
        var decoded = operations.Decode(content, reference, timeZone);
        if (decoded.Diagnostics.Any(d => d.Severity == AstmSeverity.Error)) return new(new(IngestState.Rejected, "medical.decode.rejected"), [], decoded.Diagnostics);
        IngestOutcome? outcome = null;
        if (decoded.Observations.Count > 0 || decoded.QualityResources.Count > 0 || decoded.CalibrationReports.Count > 0 || decoded.InventoryStatuses.Count > 0)
        {
            var observations = decoded.Observations.Select((o, i) => o with
                { ObservationId = ReceptionIdentity.Derive(receptionId, operations.Profile.Id + "/observation", i) }).ToArray();
            var instrument = observations.FirstOrDefault()?.Instrument ?? decoded.QualityResources.FirstOrDefault()?.Instrument
                ?? decoded.CalibrationReports.FirstOrDefault()?.Instrument ?? decoded.InventoryStatuses[0].Instrument;
            var batch = new InstrumentObservationBatch(ReceptionIdentity.Derive(receptionId, operations.Profile.Id + "/batch", 0), instrument, observations, reference)
            {
                QualityResources = decoded.QualityResources.Select((r, i) => r with { ResourceId = ReceptionIdentity.Derive(receptionId, operations.Profile.Id + "/resource", i) }).ToArray(),
                CalibrationReports = decoded.CalibrationReports.Select((r, i) => r with { ReportId = ReceptionIdentity.Derive(receptionId, operations.Profile.Id + "/calibration", i) }).ToArray(),
                InventoryStatuses = decoded.InventoryStatuses.Select((s, i) => s with { StatusId = ReceptionIdentity.Derive(receptionId, operations.Profile.Id + "/inventory", i) }).ToArray(),
            };
            outcome = await host.IngestAsync(batch, token).ConfigureAwait(false);
        }
        var responses = new List<PreparedOperationMessage>();
        foreach (var query in decoded.Queries)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            if (operations.Profile.QueryAnswerBudget <= TimeSpan.Zero) throw new InvalidOperationException("medical.query.budget-required");
            budget.CancelAfter(operations.Profile.QueryAnswerBudget);
            var answer = await host.AnswerQueryAsync(query, budget.Token).AsTask().WaitAsync(budget.Token).ConfigureAwait(false);
            var request = MedicalRequests.Answer(operations, query, answer,
                ReceptionIdentity.Derive(receptionId, operations.Profile.Id + "/answer", responses.Count),
                operations.Profile.Format == "hl7v2" ? Encoding.GetEncoding(operations.Profile.EncodingName).GetString(content.Span) : null);
            responses.Add(operations.Prepare(request));
        }
        return new(outcome, responses.AsReadOnly(), decoded.Diagnostics);
    }
}
