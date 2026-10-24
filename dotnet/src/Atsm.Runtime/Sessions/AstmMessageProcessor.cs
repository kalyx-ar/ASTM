using Atsm.Abstractions.Diagnostics;
using Atsm.Abstractions.Hosting;
using Atsm.Abstractions.Model;
using Atsm.Abstractions.Tracing;
using Atsm.Plugins;
using Atsm.Protocol.Lis02;

namespace Atsm.Runtime.Sessions;

public sealed record AstmProcessingResult(IReadOnlyList<IngestOutcome> Outcomes,
    IReadOnlyList<IReadOnlyList<Lis02Record>> Responses, IReadOnlyList<AtsmDiagnostic> Diagnostics)
{
    public bool Succeeded => Outcomes.All(o => o.State is IngestState.Accepted or IngestState.Duplicate)
        && Diagnostics.All(d => d.Severity != AtsmSeverity.Error);
}

/// <summary>Complete LIS02 messages, independent of LIS01, files and channels. The host owns the
/// source, durable reception ID, idempotent commit and disposition of any original file.
/// Instances process one ordered source and must not be used concurrently.</summary>
public sealed class AstmMessageProcessor(IAstmDialect dialect, AstmProfile profile, ILabWorkflowHost host,
    InstrumentRef instrument, ITraceSink trace, Action<AtsmDiagnostic>? diagnostics = null)
{
    public InstrumentRef Instrument { get; private set; } = instrument;
    public int MaxMessageBytes { get; init; } = 1024 * 1024;

    /// <summary>Reads caller-owned content without closing it; never opens or manages a file.</summary>
    public async ValueTask<AstmProcessingResult> ProcessAsync(Stream input, Guid receptionId, CancellationToken token = default)
    {
        trace.EnsureReady();
        if (receptionId == Guid.Empty) throw new ArgumentException("A durable reception identity is required.", nameof(receptionId));
        using var content = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (content.Length + read > MaxMessageBytes) throw new InvalidDataException("ATSM-MESSAGE-TOO-LARGE");
            content.Write(buffer, 0, read);
        }
        return await ProcessAsync(content.ToArray(), receptionId, token).ConfigureAwait(false);
    }

    public ValueTask<AstmProcessingResult> ProcessAsync(ReadOnlyMemory<byte> payload, Guid receptionId, CancellationToken token = default)
    {
        Validate(payload, receptionId, token);
        trace.EnsureReady();
        var sequence = trace.Write(TraceDirection.Received, payload.Span, "ATSM-MESSAGE", $"reception {receptionId:D}");
        return ProcessRecordedAsync(payload, new TraceRef(trace.TraceId, sequence, sequence), receptionId, token);
    }

    public async ValueTask<AstmProcessingResult> ProcessRecordedAsync(ReadOnlyMemory<byte> payload, TraceRef reference,
        Guid receptionId, CancellationToken token = default)
    {
        Validate(payload, receptionId, token);
        trace.EnsureReady();
        var findings = new List<AtsmDiagnostic>();
        var outcomes = new List<IngestOutcome>();
        var responses = new List<IReadOnlyList<Lis02Record>>();
        void Report(AtsmDiagnostic finding)
        {
            findings.Add(finding);
            trace.Write(TraceDirection.Event, [], finding.Code, finding.Message);
            diagnostics?.Invoke(finding);
        }
        var parsed = Lis02Codec.Parse(payload.Span, profile.Encoding);
        foreach (var finding in parsed.Diagnostics) Report(finding);
        if (parsed.Messages.Count == 0 || parsed.Messages.Any(m => m.Header is null || !m.IsTerminated)
            || parsed.Diagnostics.Any(d => d.Severity == AtsmSeverity.Error))
        {
            Report(new(SessionCodes.IncompleteTransmission, AtsmSeverity.Error, "Complete H..L messages are required."));
            return new(outcomes, responses, findings);
        }
        var ordinal = 0;
        foreach (var message in parsed.Messages)
        {
            token.ThrowIfCancellationRequested();
            AstmInterpretation interpretation;
            try { interpretation = dialect.Interpret(message, reference); }
            catch (Exception error) when (error is not OperationCanceledException)
            { Report(new(SessionCodes.DialectFailed, AtsmSeverity.Error, error.GetType().Name)); continue; }
            foreach (var finding in interpretation.Diagnostics) Report(finding);
            if (interpretation.Diagnostics.Any(d => d.Severity == AtsmSeverity.Error)) continue;
            if (interpretation.Instrument is { } identified) Instrument = identified;
            var messageOrdinal = ordinal++;
            if (interpretation.Observations.Count > 0)
            {
                var observations = interpretation.Observations.Select((o, i) => o with
                { ObservationId = ReceptionIdentity.Derive(receptionId, $"{profile.Id}/observation/{messageOrdinal}", i) }).ToArray();
                var batch = new InstrumentObservationBatch(ReceptionIdentity.Derive(receptionId, profile.Id + "/batch", messageOrdinal),
                    Instrument, observations, reference);
                IngestOutcome outcome;
                try { outcome = await host.IngestAsync(batch, token).ConfigureAwait(false); }
                catch (Exception error) when (error is not OperationCanceledException)
                { outcome = new(IngestState.Deferred, SessionCodes.HostIngestFailed); Report(new(SessionCodes.HostIngestFailed, AtsmSeverity.Error, error.GetType().Name)); }
                outcomes.Add(outcome);
                if (outcome.State is not (IngestState.Accepted or IngestState.Duplicate))
                    Report(new(SessionCodes.HostIngestNotAccepted, AtsmSeverity.Warning, $"{outcome.State} {outcome.Code}"));
            }
            foreach (var query in interpretation.Queries)
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
                budget.CancelAfter(profile.QueryAnswerBudget);
                OrderQueryAnswer answer;
                try { answer = await host.AnswerQueryAsync(query, budget.Token).AsTask().WaitAsync(budget.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                { Report(new(SessionCodes.HostQueryTimeout, AtsmSeverity.Warning, "Host query budget exceeded.")); answer = OrderQueryAnswer.Unavailable; }
                catch (Exception error) when (error is not OperationCanceledException)
                { Report(new(SessionCodes.HostQueryFailed, AtsmSeverity.Error, error.GetType().Name)); answer = OrderQueryAnswer.Unavailable; }
                var records = dialect.BuildQueryAnswer(query, answer);
                if (records.Count > 0) responses.Add(records);
            }
        }
        return new(outcomes, responses, findings);
    }

    private void Validate(ReadOnlyMemory<byte> payload, Guid receptionId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (receptionId == Guid.Empty) throw new ArgumentException("A durable reception identity is required.", nameof(receptionId));
        if (payload.IsEmpty || payload.Length > MaxMessageBytes) throw new ArgumentOutOfRangeException(nameof(payload));
    }
}
