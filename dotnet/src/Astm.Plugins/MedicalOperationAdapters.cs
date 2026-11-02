using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using Astm.Formats.Medical;
using Astm.Formats.Medical.Model;
using Astm.Formats.Medical.Hosting;
using Astm.Abstractions.Diagnostics;
using Astm.Abstractions.Tracing;
using Astm.Operations;
using Astm.Protocol.Lis02;
using Astm.Protocol.Hl7v2;

namespace Astm.Plugins;

/// <summary>The existing instrument SDK is explicitly medical. Other industries depend on Astm.Operations instead.</summary>
public interface IMedicalInstrumentPlugin : IMedicalOperationProvider
{
    IReadOnlyList<string> IMedicalOperationProvider.MedicalProfiles => MedicalOperationAdapters.ProfileIds(this);
    IMedicalOperations IMedicalOperationProvider.CreateOperations(string profileId, MedicalOperationContext context) =>
        MedicalOperationAdapters.Create(this, profileId, context);
    IMedicalOperationObserver IMedicalOperationProvider.CreateOperationObserver(string profileId, MedicalOperationContext context, Guid traceId) =>
        Operations.MedicalOperationObservers.Create(this, profileId, context, traceId);
}

public static class MedicalOperationAdapters
{
    public static IReadOnlyList<string> ProfileIds(object plugin) => Array.AsReadOnly(
        (plugin is IInstrumentPlugin astm ? astm.Profiles.Select(p => p.Id) : [])
        .Concat(plugin is IHl7InstrumentPlugin hl7 ? hl7.Profiles.Select(p => p.Id) : [])
        .Concat(plugin is IInstrumentContentPlugin content ? content.Profiles.Select(p => p.Id) : [])
        .Concat(plugin is IInstrumentTracePlugin trace ? trace.Profiles.Select(p => p.Id) : []).Distinct(StringComparer.Ordinal).ToArray());

    public static IMedicalOperations Create(object plugin, string profileId, MedicalOperationContext context)
    {
        if (plugin is IInstrumentPlugin astm && astm.Profiles.SingleOrDefault(p => p.Id == profileId) is { } a)
            return ForAstm(a, astm.CreateDialect(a, new(context.Instrument, context.Clock)), context);
        if (plugin is IHl7InstrumentPlugin hl7 && hl7.Profiles.SingleOrDefault(p => p.Id == profileId) is { } h)
            return ForHl7(h, hl7.CreateDialect(h, new(context.Instrument, context.Clock)), context);
        if (plugin is IInstrumentContentPlugin content && content.Profiles.SingleOrDefault(p => p.Id == profileId) is { } c)
            return new Adapter(new(c.Id, c.Version, "content", "utf-8", new()), c.Evidence, false, false, null, null,
                (bytes, reference, _) => { var result = content.Interpret(c, bytes, context.Instrument, reference); return new(result.Observations, [], result.Diagnostics, []); });
        if (plugin is IInstrumentTracePlugin trace && trace.Profiles.SingleOrDefault(p => p.Id == profileId) is { } t)
            return new Adapter(new(t.Id, "1", "trace", "utf-8", new()), t.Evidence, false, false, null, null,
                (bytes, reference, zone) =>
                {
                    var entry = new TraceEntry(reference.LastSequence, TimeSpan.Zero, context.Clock.GetUtcNow(), TraceDirection.Received, bytes);
                    var result = trace.Interpret(t, [entry], context.Instrument, reference.TraceId, zone);
                    return new(result.Observations, [], result.Diagnostics, []);
                });
        throw new OperationCompatibilityException("medical.profile.unsupported");
    }
    public static IMedicalOperations ForAstm(AstmProfile profile, IAstmDialect dialect, MedicalOperationContext context) =>
        new Adapter(new MedicalOperationProfile(profile.Id, profile.Version, "lis02", profile.Encoding.WebName, profile.OperationRules)
            { Instrument = context.Instrument, QueryAnswerBudget = profile.QueryAnswerBudget }, profile.Evidence,
            profile.Workflows.Contains(AstmWorkflow.OrderDownload), profile.Workflows.Contains(AstmWorkflow.HostQuery),
            (orders, _) => profile.Encoding.GetBytes(Lis02Codec.Serialize(dialect.BuildOrders(orders.Orders))),
            (answer, _) => profile.Encoding.GetBytes(Lis02Codec.Serialize(dialect.BuildQueryAnswer(answer.Query, answer.Answer))),
            (bytes, reference, _) =>
            {
                var parsed = Lis02Codec.Parse(bytes.Span, profile.Encoding);
                if (parsed.Messages.Count == 0 || parsed.Diagnostics.Count > 0 || parsed.Messages.Any(m => !m.IsTerminated || m.Header is null))
                    return new([], [], parsed.Diagnostics.Append(new("medical.message.incomplete", AstmSeverity.Error, "Invalid or incomplete message.")).ToArray(), []);
                var results = parsed.Messages.Select(m => dialect.Interpret(m, reference)).ToArray();
                return new(results.SelectMany(r => r.Observations).ToArray(), results.SelectMany(r => r.Queries).ToArray(),
                    results.SelectMany(r => r.Diagnostics).ToArray(), results.SelectMany(r => r.QualityResources).ToArray());
            });
    public static IMedicalOperations ForHl7(Hl7Profile profile, IHl7Dialect dialect, MedicalOperationContext context) =>
        new Adapter(new MedicalOperationProfile(profile.Id, profile.Version, "hl7v2", profile.Encoding.WebName, profile.OperationRules)
            { Instrument = context.Instrument, QueryAnswerBudget = profile.QueryAnswerBudget }, profile.Evidence, true, true,
            (orders, request) => profile.Encoding.GetBytes(dialect.BuildOrders(orders.Orders, request.OperationId.ToString()).Serialize()),
            (answer, request) =>
            {
                var original = Hl7Codec.Parse(answer.OriginalMessage!).Message!;
                return profile.Encoding.GetBytes(dialect.BuildQueryAnswer(original, new(answer.Answer, request.OperationId.ToString())).Serialize());
            },
            (bytes, reference, _) =>
            {
                var parsed = Hl7Codec.Parse(profile.Encoding.GetString(bytes.Span));
                if (parsed.Message is null) return new([], [], parsed.Diagnostics, []);
                var result = dialect.Interpret(parsed.Message, reference);
                return new(result.Observations, result.Query is { } q ? [q] : [], [.. parsed.Diagnostics, .. result.Diagnostics], [])
                    { CalibrationReports = result.CalibrationReports, InventoryStatuses = result.InventoryStatuses };
            }, dialect.AdditionalOperations(profile));

    private sealed class Adapter : IMedicalOperations
    {
        private readonly OperationCatalog _catalog;
        private readonly Func<MedicalOrderSubmission, OperationEnvelope, ReadOnlyMemory<byte>>? _orders;
        private readonly Func<MedicalWorklistAnswer, OperationEnvelope, ReadOnlyMemory<byte>>? _answer;
        private readonly Func<ReadOnlyMemory<byte>, TraceRef, TimeZoneInfo, MedicalDecoded> _decode;
        private readonly Encoding _encoding;
        private readonly IReadOnlyList<Hl7OperationBinding> _additional;
        public MedicalOperationProfile Profile { get; }
        public Adapter(MedicalOperationProfile profile, IReadOnlyList<string> evidence, bool orders, bool query,
            Func<MedicalOrderSubmission, OperationEnvelope, ReadOnlyMemory<byte>>? buildOrders,
            Func<MedicalWorklistAnswer, OperationEnvelope, ReadOnlyMemory<byte>>? buildAnswer,
            Func<ReadOnlyMemory<byte>, TraceRef, TimeZoneInfo, MedicalDecoded> decode,
            IReadOnlyList<Hl7OperationBinding>? additional = null)
        {
            Profile = profile with { Rules = profile.Rules with { PatientFields = Array.AsReadOnly(profile.Rules.PatientFields.ToArray()),
                OrderActions = Array.AsReadOnly(profile.Rules.OrderActions.ToArray()), WireBindings = profile.Rules.WireBindings.ToFrozenDictionary() } };
            _orders = buildOrders; _answer = buildAnswer; _decode = decode;
            _additional = additional?.ToArray() ?? [];
            _encoding = Encoding.GetEncoding(profile.EncodingName, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            var definitions = new List<OperationDefinition>();
            void Add(string purpose, OperationKind kind, OperationContractReference? input, OperationContractReference output,
                OperationCapabilities capabilities, OperationInitiator initiator, OperationCompletionScope scope) =>
                definitions.Add(new(purpose, 1, kind, input, output, capabilities, scope, profile.Id, profile.Revision, evidence)
                    { Initiator = initiator, WireBindings = Profile.Rules.WireBindings });
            if (orders) Add(MedicalPurposes.SubmitOrders, OperationKind.Command, MedicalDataContracts.Orders, MedicalDataContracts.Decoded,
                OperationCapabilities.Prepare | OperationCapabilities.Execute | OperationCapabilities.Observe, OperationInitiator.Host, OperationCompletionScope.MessageExchange);
            if (query)
            {
                Add(MedicalPurposes.ReceiveQuery, OperationKind.Query, null, MedicalDataContracts.Decoded,
                    OperationCapabilities.Decode | OperationCapabilities.Observe, OperationInitiator.Instrument, OperationCompletionScope.MessageExchange);
                Add(MedicalPurposes.AnswerWorklist, OperationKind.Command, MedicalDataContracts.Answer, MedicalDataContracts.Decoded,
                    OperationCapabilities.Prepare | OperationCapabilities.Execute | OperationCapabilities.Observe, OperationInitiator.Host, OperationCompletionScope.MessageExchange);
            }
            Add(profile.Format == "content" ? MedicalPurposes.ImportContent : MedicalPurposes.ReceiveObservations,
                profile.Format == "content" ? OperationKind.Import : OperationKind.Event, null, MedicalDataContracts.Decoded,
                OperationCapabilities.Decode | (profile.Format == "content" ? OperationCapabilities.None : OperationCapabilities.Observe),
                profile.Format == "content" ? OperationInitiator.Content : OperationInitiator.Instrument,
                profile.Format == "content" ? OperationCompletionScope.ContentImport : OperationCompletionScope.MessageExchange);
            definitions.AddRange(_additional.Select(b => b.Definition));
            _catalog = new(definitions, MedicalDataContracts.Create(Profile.Rules, Profile.Format)
                .Concat(_additional.Where(b => b.Input is not null).Select(b => b.Input!)));
        }
        public IReadOnlyList<OperationDefinition> Describe(string profileId) => _catalog.Describe(profileId);
        public OperationDataContract Resolve(OperationContractReference reference) => _catalog.Resolve(reference);
        public OperationDefinition Select(string purpose) => Describe(Profile.Id).SingleOrDefault(d => d.Id == purpose)
            ?? throw new OperationCompatibilityException("operation.unsupported");
        public IReadOnlyList<OperationValidationIssue> Validate(OperationEnvelope request)
        {
            if (request.OperationId == Guid.Empty) return [new("operation.id", "$")];
            OperationDefinition definition;
            try { definition = _catalog.Select(request.ProfileId, request.ProfileRevision, request.Purpose, request.Major, request.Contract, OperationCapabilities.Prepare); }
            catch (OperationCompatibilityException e) { return [new(e.Code, "$")]; }
            var issues = OperationDataValidator.Validate(Resolve(definition.Input!), request.Data).ToList();
            if (issues.Count > 0) return issues;
            if (_additional.SingleOrDefault(b => b.Definition.Id == request.Purpose && b.Build is not null) is { } additional)
            {
                CheckStrings(request.Data, "$", issues);
                if (additional.Validate is { } validate) issues.AddRange(validate(request));
                return issues.AsReadOnly();
            }
            try
            {
                IReadOnlyList<WorkOrder> orders;
                if (request.Purpose == MedicalPurposes.SubmitOrders)
                    orders = request.Data.Deserialize(MedicalJsonContext.Default.MedicalOrderSubmission)!.Orders;
                else
                {
                    var answer = request.Data.Deserialize(MedicalJsonContext.Default.MedicalWorklistAnswer)!;
                    if (answer.Query is null || answer.Answer is null || answer.Query.QueryId == Guid.Empty || answer.Query.Specimens is null || answer.Query.Instrument is null)
                    {
                        issues.Add(new("medical.query.invalid", "$.query"));
                        return issues;
                    }
                    orders = answer.Answer.Orders;
                    if (Profile.Instrument is { } target && answer.Query.Instrument.InstrumentId != target.InstrumentId)
                        issues.Add(new("medical.query.instrument", "$.query.instrument"));
                    if (answer.Answer.State == QueryAnswerState.Orders && orders.Count == 0 || answer.Answer.State != QueryAnswerState.Orders && orders.Count > 0)
                        issues.Add(new("medical.answer.state", "$.answer"));
                    if (answer.Query.Specimens.Count > 0 && orders.Any(o => !answer.Query.Specimens.Any(s => s.SpecimenId == o.Specimen.SpecimenId)))
                        issues.Add(new("medical.answer.specimen", "$.answer.orders"));
                    if (Profile.Format == "hl7v2")
                    {
                        var original = answer.OriginalMessage is { } raw ? Hl7Codec.Parse(raw) : null;
                        if (original?.Message is not { Type: "QBP", Trigger: "ZOS" } message || original.Diagnostics.Count > 0 ||
                            !message.OfType("QPD").Any(q => answer.Query.Specimens.Any(s => s.SpecimenId == q.Text(3))))
                            issues.Add(new("medical.hl7.original-query-required", "$.originalMessage"));
                    }
                }
                if (Profile.Rules.SingleSpecimenPerMessage && orders.Select(o => o.Specimen.SpecimenId).Distinct(StringComparer.Ordinal).Count() > 1)
                    issues.Add(new("medical.message.single-specimen", "$.orders"));
                if (Profile.Rules.SingleActionPerMessage && orders.Select(o => o.Action).Distinct().Count() > 1)
                    issues.Add(new("medical.message.single-action", "$.orders"));
                if (orders.GroupBy(o => o.Specimen.SpecimenId).Any(g => g.Select(o => o.Patient).Distinct().Count() > 1))
                    issues.Add(new("medical.specimen.patient-conflict", "$.orders"));
                for (var i = 0; i < orders.Count; i++)
                {
                    var order = orders[i];
                    if (order.Tests.Any(t => string.IsNullOrWhiteSpace(t.Vendor.Code))) issues.Add(new("medical.test.required", "$.orders[" + i + "].tests"));
                    if (Profile.Rules.SuppressControlPatient && (order.Action == OrderAction.Control || order.Specimen.Role is SpecimenRole.Control or SpecimenRole.Calibrator) && order.Patient is not null)
                        issues.Add(new("medical.patient.not-admitted-for-control", "$.orders[" + i + "].patient"));
                }
                CheckStrings(request.Data, "$", issues);
            }
            catch (Exception e) when (e is JsonException or NotSupportedException or NullReferenceException)
            { issues.Add(new("medical.data.invalid", "$")); }
            return issues.AsReadOnly();
        }
        private void CheckStrings(JsonElement value, string path, List<OperationValidationIssue> issues)
        {
            if (value.ValueKind == JsonValueKind.Object)
                foreach (var p in value.EnumerateObject()) CheckStrings(p.Value, path + "." + p.Name, issues);
            else if (value.ValueKind == JsonValueKind.Array)
                foreach (var item in value.EnumerateArray()) CheckStrings(item, path + "[]", issues);
            else if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString()!;
                if (text.Any(c => char.IsControl(c) && !(path == "$.originalMessage" && c == '\r'))) issues.Add(new("medical.text.control", path));
                try { _encoding.GetByteCount(text); } catch (EncoderFallbackException) { issues.Add(new("medical.text.encoding", path)); }
            }
        }
        public PreparedOperationMessage Prepare(OperationEnvelope request)
        {
            if (Validate(request).Count > 0) throw new OperationCompatibilityException("medical.operation.invalid");
            // Deserialize from the owned envelope, never retain caller collections; wire output is copied by the envelope.
            var owned = request with { Data = request.Data.Clone() };
            var binding = _additional.SingleOrDefault(b => b.Definition.Id == owned.Purpose);
            var bytes = binding?.Build is { } build ? _encoding.GetBytes(build(owned).Serialize()) : owned.Purpose == MedicalPurposes.SubmitOrders
                ? _orders!(owned.Data.Deserialize(MedicalJsonContext.Default.MedicalOrderSubmission)!, owned)
                : _answer!(owned.Data.Deserialize(MedicalJsonContext.Default.MedicalWorklistAnswer)!, owned);
            return new(owned, Select(owned.Purpose), Profile.Format, bytes.Span);
        }
        public MedicalDecoded Decode(ReadOnlyMemory<byte> bytes, TraceRef trace, TimeZoneInfo timeZone)
        {
            if (bytes.Length > 16 * 1024 * 1024) throw new InvalidDataException("medical.content.limit");
            return _decode(bytes, trace, timeZone);
        }
    }
}
