using Atsm.Abstractions.Diagnostics;
using Atsm.Abstractions.Hosting;
using Atsm.Abstractions.Model;
using Atsm.Abstractions.Tracing;
using Atsm.Plugins;
using Atsm.Protocol.Hl7v2;

namespace Atsm.Runtime.Sessions;

public sealed record Hl7ProcessingResult(IngestOutcome? Outcome, IReadOnlyList<Hl7Message> Responses,
    IReadOnlyList<AtsmDiagnostic> Diagnostics);

/// <summary>Host-owned complete messages; no filesystem or automatic retries. Positive application
/// acknowledgement requires durable acceptance (or a confirmed duplicate) of the entire batch.</summary>
public sealed class Hl7MessageProcessor(IHl7Dialect dialect, Hl7Profile profile, ILabWorkflowHost host,
    InstrumentRef instrument, ITraceSink trace, Action<AtsmDiagnostic>? diagnostics = null)
{
    public int MaxMessageBytes { get; init; } = 1024 * 1024;

    public ValueTask<Hl7ProcessingResult> ProcessAsync(ReadOnlyMemory<byte> payload, Guid receptionId, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (receptionId == Guid.Empty) throw new ArgumentException("A durable reception identity is required.", nameof(receptionId));
        trace.EnsureReady();
        if (payload.IsEmpty || payload.Length > MaxMessageBytes) throw new ArgumentOutOfRangeException(nameof(payload));
        var sequence = trace.Write(TraceDirection.Received, payload.Span, "ATSM-HL7-MESSAGE");
        return ProcessRecordedAsync(payload, new(trace.TraceId, sequence, sequence), receptionId, token);
    }

    public async ValueTask<Hl7ProcessingResult> ProcessRecordedAsync(ReadOnlyMemory<byte> payload, TraceRef reference,
        Guid receptionId, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested(); trace.EnsureReady();
        if (receptionId == Guid.Empty) throw new ArgumentException("A durable reception identity is required.", nameof(receptionId));
        if (payload.IsEmpty || payload.Length > MaxMessageBytes) throw new ArgumentOutOfRangeException(nameof(payload));
        var findings = new List<AtsmDiagnostic>();
        void Report(string code, AtsmSeverity severity = AtsmSeverity.Error)
        {
            var finding = new AtsmDiagnostic(code, severity, "HL7 processing diagnostic; inspect the protected trace.");
            findings.Add(finding); trace.Write(TraceDirection.Event, [], finding.Code, finding.Message); diagnostics?.Invoke(finding);
        }
        Hl7ParseResult parsed;
        try { parsed = Hl7Codec.Parse(profile.Encoding.GetString(payload.Span)); }
        catch (System.Text.DecoderFallbackException) { Report("ATSM-HL7-INVALID-ENCODING"); return new(null, [], findings); }
        if (parsed.Message is not { } message)
        { foreach (var diagnostic in parsed.Diagnostics) Report(diagnostic.Code); return new(null, [], findings); }
        if (message.Type is "ACK" or "ORL" or "RSP") return new(null, [], findings);
        Hl7ProcessingResult Reject(string code)
        {
            Report(code);
            return new(new(IngestState.Rejected, code), [Hl7Acknowledgements.Build(message, "AR", Guid.CreateVersion7().ToString(), DateTimeOffset.UtcNow, code)], findings);
        }
        // This increment implements original mode. Enhanced-mode primitives are exposed separately;
        // a runtime without a commit-custody contract must not emit a false durable commit ACK.
        if (message.OfType("MSA").Any()) return Reject("ATSM-HL7-UNEXPECTED-MSA");
        if (message.Header.Field(15).Length > 0 || message.Header.Field(16).Length > 0) return Reject("ATSM-HL7-ENHANCED-MODE-NOT-SUPPORTED");
        if (message.Header.Text(12) != profile.Hl7Version) return Reject("ATSM-HL7-UNSUPPORTED-VERSION");
        if (message.Header.Field(18).Length > 0 && message.Header.Field(18) != profile.CharacterSet) return Reject("ATSM-HL7-UNSUPPORTED-CHARSET");
        Hl7Interpretation interpretation;
        try { interpretation = dialect.Interpret(message, reference); }
        catch (Exception error) when (error is not OperationCanceledException) { return Reject(SessionCodes.DialectFailed); }
        foreach (var finding in interpretation.Diagnostics) { findings.Add(finding); trace.Write(TraceDirection.Event, [], finding.Code, finding.Message); diagnostics?.Invoke(finding); }
        if (findings.Any(f => f.Severity == AtsmSeverity.Error))
            return new(new(IngestState.Rejected, findings[0].Code), [Hl7Acknowledgements.Build(message, "AR", Guid.CreateVersion7().ToString(), DateTimeOffset.UtcNow, findings[0].Code)], findings);
        if (interpretation.Query is { } query)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(profile.QueryAnswerBudget);
            OrderQueryAnswer answer;
            try { answer = await host.AnswerQueryAsync(query, budget.Token).AsTask().WaitAsync(budget.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { Report(SessionCodes.HostQueryTimeout); answer = OrderQueryAnswer.Unavailable; }
            catch (Exception error) when (error is not OperationCanceledException) { Report(SessionCodes.HostQueryFailed); answer = OrderQueryAnswer.Unavailable; }
            try { return new(null, [dialect.BuildQueryAnswer(message, new(answer, Guid.CreateVersion7().ToString()))], findings); }
            catch (Exception error) when (error is not OperationCanceledException) { return Reject(SessionCodes.DialectFailed); }
        }
        if (interpretation.Observations.Count == 0) return Reject("ATSM-HL7-NO-SUPPORTED-CONTENT");
        var observations = interpretation.Observations.Select((o, i) => o with { ObservationId = ReceptionIdentity.Derive(receptionId, profile.Id + "/observation", i) }).ToArray();
        IngestOutcome outcome;
        try { outcome = await host.IngestAsync(new(ReceptionIdentity.Derive(receptionId, profile.Id + "/batch", 0), instrument, observations, reference), token).ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
        { Report(SessionCodes.HostIngestFailed); outcome = new(IngestState.Deferred, SessionCodes.HostIngestFailed); }
        var code = outcome.State switch { IngestState.Accepted or IngestState.Duplicate => "AA", IngestState.Rejected => "AR", _ => "AE" };
        if (code != "AA") Report(SessionCodes.HostIngestNotAccepted, AtsmSeverity.Warning);
        return new(outcome, [Hl7Acknowledgements.Build(message, code, Guid.CreateVersion7().ToString(), DateTimeOffset.UtcNow,
            code == "AA" ? null : outcome.Code ?? SessionCodes.HostIngestNotAccepted)], findings);
    }
}
