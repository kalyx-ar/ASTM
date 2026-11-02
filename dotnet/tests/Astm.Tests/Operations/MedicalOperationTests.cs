using System.Text;
using System.Text.Json;
using Astm.Abstractions.Tracing;
using Astm.Diagnostics;
using Astm.Formats.Medical;
using Astm.Formats.Medical.Hosting;
using Astm.Formats.Medical.Model;
using Astm.Operations;
using Astm.Plugin.Abbott.Architect;
using Astm.Plugin.Abbott.CellDynRuby;
using Astm.Plugin.Sysmex.Urinalysis;
using Astm.Plugin.BioRad.Cfx;
using Astm.Plugins;
using Astm.Protocol.Lis01;
using Astm.Protocol.Lis02;
using Astm.Protocol.Hl7v2;
using Astm.Protocol.Mllp;
using Astm.Runtime.Sessions;
using Astm.Runtime.Tracing;
using Astm.Testing;
using Astm.Tests.Hl7;
using static Astm.Protocol.Lis01.ControlCharacters;

namespace Astm.Tests.Operations;

public class MedicalOperationTests
{
    private static readonly MedicalOperationContext Context = new(new("SYN"), TimeProvider.System);
    private static readonly ArchitectHl7Plugin Hl7 = new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeZoneInfo.Utc);
    private static IMedicalOperations Open(IMedicalOperationProvider plugin, string profile) => plugin.CreateOperations(profile, Context);
    private static WorkOrder Order(string sample = "SID-SYN-1") => new("ORDER-SYN", new(sample),
        new("PAT-SYN", "Synthetic", "Test", new(2000, 1, 1), "U"), [CodeSet.Of(new("0021", null, "urn:test"))]);
    private static readonly string[] Results = ["H|\\^&", "P|1", "O|1|SID-SYN-1", "R|1|^^^0021|5", "L|1|N"];
    public static IEnumerable<object[]> Profiles()
    {
        var catalog = new DiagnosticCatalog();
        foreach (IMedicalOperationProvider provider in catalog.AstmPlugins.Cast<IMedicalOperationProvider>()
            .Concat(catalog.ContentPlugins).Concat(catalog.TracePlugins).Append(Hl7).Distinct())
            foreach (var profile in provider.MedicalProfiles) yield return [provider, profile];
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public void Every_bundled_medical_profile_has_a_valid_catalog_and_common_observer(IMedicalOperationProvider provider, string profile)
    {
        var operations = Open(provider, profile);
        var descriptions = operations.Describe(profile);
        Assert.NotEmpty(descriptions);
        foreach (var definition in descriptions)
        {
            Assert.NotEmpty(definition.Evidence);
            if (definition.Input is { } input) Assert.Empty(OperationDataValidator.ValidateSchema(operations.Resolve(input)));
            if (definition.Output is { } output) Assert.Empty(OperationDataValidator.ValidateSchema(operations.Resolve(output)));
            Assert.False(definition.Capabilities.HasFlag(OperationCapabilities.Cancel));
        }
        var observer = provider.CreateOperationObserver(profile, Context, Guid.NewGuid());
        Assert.Null(observer.Current.OperationInProgress);
        Assert.False(observer.Current.NoPendingOperations);
        observer.Complete(); Assert.False(observer.Current.IsObserving);
        Assert.Throws<InvalidOperationException>(() => observer.Feed(new(0, TimeSpan.Zero, DateTimeOffset.UnixEpoch, TraceDirection.Received, new byte[] { 0 })));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Different_manufacturers_prepare_the_same_typed_contract_with_their_wire_rules(bool uwam)
    {
        IMedicalOperationProvider provider = uwam ? new SysmexUrinalysisPlugin() : new ArchitectPlugin();
        var profile = uwam ? SysmexUrinalysisPlugin.WithChemistryProfile : ArchitectPlugin.AsiProfileId;
        var operations = Open(provider, profile);
        var id = Guid.NewGuid(); var request = MedicalRequests.Orders(operations, [Order()], id);
        Assert.Empty(operations.Validate(request));
        var prepared = operations.Prepare(request);
        var parsed = Lis02Codec.Parse(Encoding.GetEncoding(operations.Profile.EncodingName).GetString(prepared.Bytes.Span));
        Assert.Empty(parsed.Diagnostics);
        var message = Assert.Single(parsed.Messages);
        Assert.Equal("PAT-SYN", Assert.Single(message.OfType('P')).Field(3));
        Assert.Equal("SID-SYN-1", Assert.Single(message.OfType('O')).Field(3));
        Assert.Equal(uwam ? "ORDER-SYN" : "", Assert.Single(message.OfType('O')).Field(19));
        Assert.Equal(id, prepared.Request.OperationId);
        Assert.Equal(MedicalDataContracts.Orders, prepared.Definition.Input);
        var bytes = prepared.Bytes.ToArray(); bytes[0] = 0;
        Assert.NotEqual(0, prepared.Bytes.Span[0]);
        Assert.Contains(operations.Validate(request with { ProfileRevision = "changed" }), i => i.Code == "operation.profile.changed");
        Assert.Contains(operations.Validate(request with { Contract = request.Contract with { Major = 2 } }), i => i.Code == "operation.contract.unsupported");
    }

    [Fact]
    public void Patient_requirements_are_per_operation_and_never_manufactured_for_controls()
    {
        var architect = Open(new ArchitectPlugin(), ArchitectPlugin.AsiProfileId);
        Assert.Empty(architect.Validate(MedicalRequests.Orders(architect, [Order() with { Patient = null }], Guid.NewGuid())));
        var control = Order() with { Action = OrderAction.Control, Specimen = new("QC-SYN", Role: SpecimenRole.Control) };
        Assert.Contains(architect.Validate(MedicalRequests.Orders(architect, [control], Guid.NewGuid())), i => i.Code == "medical.patient.not-admitted-for-control");
        Assert.Empty(architect.Validate(MedicalRequests.Orders(architect, [control with { Patient = null }], Guid.NewGuid())));
        var withPhysician = Order() with { Patient = Order().Patient! with { Physician = "SYN-PHYSICIAN" } };
        Assert.Empty(architect.Validate(MedicalRequests.Orders(architect, [withPhysician], Guid.NewGuid())));
        var standard = Open(new StandardAstmPlugin(), StandardAstmPlugin.ProfileId);
        var standardPrepared = standard.Prepare(MedicalRequests.Orders(standard, [withPhysician], Guid.NewGuid()));
        Assert.Equal("SYN-PHYSICIAN", Assert.Single(Assert.Single(Lis02Codec.Parse(standardPrepared.Bytes.Span, Encoding.Latin1).Messages).OfType('P')).Field(14));
        var ruby = Open(new CellDynRubyPlugin(), CellDynRubyPlugin.ProfileId);
        Assert.Contains(ruby.Validate(MedicalRequests.Orders(ruby, [withPhysician], Guid.NewGuid())), i => i.Code == "data.forbidden");
        var invalidEncoding = withPhysician with { Patient = withPhysician.Patient! with { GivenName = "😀" } };
        Assert.Contains(architect.Validate(MedicalRequests.Orders(architect, [invalidEncoding], Guid.NewGuid())), i => i.Code == "medical.text.encoding");
    }

    [Fact]
    public void Receiving_results_does_not_advertise_order_or_physical_control_capabilities()
    {
        var catalog = new DiagnosticCatalog();
        foreach (var plugin in catalog.AstmPlugins.Where(p => p.Profiles.All(p => !p.Workflows.Contains(AstmWorkflow.OrderDownload))))
            foreach (var profile in plugin.Profiles)
            {
                var operations = Open(plugin, profile.Id);
                Assert.DoesNotContain(operations.Describe(profile.Id), d => d.Capabilities.HasFlag(OperationCapabilities.Execute));
                Assert.Throws<OperationCompatibilityException>(() => MedicalRequests.Orders(operations, [Order()], Guid.NewGuid()));
            }
    }

    [Fact]
    public void Worklist_answers_validate_specimen_and_preserve_manufacturer_negative_responses()
    {
        foreach (var pair in new (IMedicalOperationProvider Provider, string Profile, char Expected)[]
        { (new ArchitectPlugin(), ArchitectPlugin.AsiProfileId, 'Q'), (new SysmexUrinalysisPlugin(), SysmexUrinalysisPlugin.WithChemistryProfile, 'O') })
        {
            var operations = Open(pair.Provider, pair.Profile);
            var query = new OrderQuery(Guid.NewGuid(), Context.Instrument, [new("SID-SYN-1")], "ALL", TraceRef.None);
            var negative = operations.Prepare(MedicalRequests.Answer(operations, query, OrderQueryAnswer.NoInformation, Guid.NewGuid()));
            var message = Assert.Single(Lis02Codec.Parse(negative.Bytes.Span, Encoding.GetEncoding(operations.Profile.EncodingName)).Messages);
            Assert.Single(message.OfType(pair.Expected));
            var invalid = MedicalRequests.Answer(operations, query, new(QueryAnswerState.Orders, [Order("OTHER")]), Guid.NewGuid());
            Assert.Contains(operations.Validate(invalid), i => i.Code == "medical.answer.specimen");
        }
    }

    private sealed class Wire(IMedicalOperationProvider provider, string profile)
    {
        public IMedicalOperationObserver Observer { get; } = provider.CreateOperationObserver(profile, Context, Guid.NewGuid());
        private long _sequence;
        public void Feed(TraceDirection direction, byte[] data) => Observer.Feed(new(_sequence++, TimeSpan.Zero, DateTimeOffset.UnixEpoch, direction, data));
        public void Gap() { _sequence++; Feed(TraceDirection.Event, []); }
        public void Transmission(string[] records, TraceDirection direction = TraceDirection.Received)
        {
            var other = direction == TraceDirection.Received ? TraceDirection.Transmitted : TraceDirection.Received;
            Feed(direction, [Enq]); Feed(other, [Ack]);
            var bytes = Encoding.Latin1.GetBytes(string.Join('\r', records) + "\r");
            foreach (var frame in Lis01Frame.BuildTransmission([(ReadOnlyMemory<byte>)bytes])) { Feed(direction, frame); Feed(other, [Ack]); }
            Feed(direction, [Eot]);
        }
    }

    [Fact]
    public void Multiple_queries_remain_pending_until_their_own_answers_and_unrelated_results_cannot_close_them()
    {
        var wire = new Wire(new ArchitectPlugin(), ArchitectPlugin.AsiProfileId);
        wire.Transmission(["H|\\^&", "Q|1|^A||^^^ALL||||||||O", "Q|2|^B||^^^ALL||||||||O", "L|1"]);
        Assert.True(wire.Observer.Current.ChannelOperationInProgress);
        wire.Transmission(Results); Assert.True(wire.Observer.Current.ChannelOperationInProgress);
        wire.Transmission(["H|\\^&", "P|1", "O|1|A||^^^0021", "L|1"], TraceDirection.Transmitted);
        Assert.True(wire.Observer.Current.ChannelOperationInProgress);
        wire.Transmission(["H|\\^&", "Q|1|^B||^^^ALL||||||||X", "L|1"], TraceDirection.Transmitted);
        Assert.True(wire.Observer.Current.NoPendingOperations);
        Assert.All(wire.Observer.Current.Operations, o => Assert.DoesNotContain("SID-SYN", o.Code));
        wire.Gap(); Assert.False(wire.Observer.Current.NoPendingOperations);
        wire.Transmission(Results); Assert.False(wire.Observer.Current.NoPendingOperations);
        wire.Observer.Resynchronize(); wire.Transmission(Results); Assert.True(wire.Observer.Current.NoPendingOperations);
    }

    [Fact]
    public void Unexpected_enquiry_after_establishment_revokes_previously_idle_coverage()
    {
        var wire = new Wire(new StandardAstmPlugin(), StandardAstmPlugin.ProfileId);
        wire.Transmission(Results);
        Assert.True(wire.Observer.Current.NoPendingOperations);
        wire.Feed(TraceDirection.Received, [Enq]);
        wire.Feed(TraceDirection.Transmitted, [Ack]);
        wire.Feed(TraceDirection.Received, [Enq]);
        Assert.False(wire.Observer.Current.CoverageComplete);
        Assert.False(wire.Observer.Current.NoPendingOperations);
    }

    [Fact]
    public void Ruby_ALL_is_not_closed_by_one_partial_positive_worklist()
    {
        var wire = new Wire(new CellDynRubyPlugin(), CellDynRubyPlugin.ProfileId);
        wire.Transmission(["H|\\^&", "Q|1|^ALL||^^^ALL||||||||O", "L|1"]);
        wire.Transmission(["H|\\^&", "P|1", "O|1|ONE||^^^0021", "L|1"], TraceDirection.Transmitted);
        Assert.True(wire.Observer.Current.ChannelOperationInProgress);
        wire.Transmission(["H|\\^&", "L|1|I"], TraceDirection.Transmitted);
        Assert.True(wire.Observer.Current.NoPendingOperations);
    }

    [Fact]
    public void HL7_uses_stable_request_identity_and_requires_original_query_context()
    {
        var operations = Open(Hl7, ArchitectHl7Plugin.ProfileId);
        var id = Guid.NewGuid(); var request = MedicalRequests.Orders(operations, [Order()], id);
        Assert.Empty(operations.Validate(request));
        var first = operations.Prepare(request); var again = operations.Prepare(request);
        Assert.Equal(id.ToString(), Hl7Codec.Parse(Encoding.UTF8.GetString(first.Bytes.Span)).Message!.ControlId);
        Assert.Equal(first.Bytes.ToArray(), again.Bytes.ToArray());
        var decoded = operations.Decode(Encoding.UTF8.GetBytes(Hl7Data.Query()), TraceRef.None, TimeZoneInfo.Utc);
        var query = Assert.Single(decoded.Queries);
        var answer = MedicalRequests.Answer(operations, query, new(QueryAnswerState.Orders, [Order()]), Guid.NewGuid());
        Assert.Contains(operations.Validate(answer), i => i.Code == "data.required" && i.Path.EndsWith("originalMessage", StringComparison.Ordinal));
        Assert.Empty(operations.Validate(MedicalRequests.Answer(operations, query, new(QueryAnswerState.Orders, [Order()]), Guid.NewGuid(), Hl7Data.Query())));
    }

    [Fact]
    public void HL7_waits_for_matching_ACK_and_does_not_claim_device_execution()
    {
        var observer = ((IMedicalOperationProvider)Hl7).CreateOperationObserver(ArchitectHl7Plugin.ProfileId, Context, Guid.NewGuid());
        var message = Hl7Codec.Parse(Hl7Data.Result()).Message!;
        var bytes = MllpFraming.Wrap(Encoding.UTF8.GetBytes(message.Serialize()));
        for (var i = 0; i < bytes.Length; i++) observer.Feed(new(i, TimeSpan.Zero, DateTimeOffset.UnixEpoch, TraceDirection.Received, bytes.AsMemory(i, 1)));
        Assert.True(observer.Current.ChannelOperationInProgress);
        var ack = Hl7Acknowledgements.Build(message, "AA", "ACK-SYN", DateTimeOffset.UnixEpoch);
        observer.Feed(new(bytes.Length, TimeSpan.Zero, DateTimeOffset.UnixEpoch, TraceDirection.Transmitted, MllpFraming.Wrap(Encoding.UTF8.GetBytes(ack.Serialize()))));
        Assert.True(observer.Current.NoPendingOperations);
        Assert.DoesNotContain(observer.Current.Operations, o => o.Evidence == OperationEvidence.DeviceReported);
    }

    [Fact]
    public async Task ASTM_HL7_and_content_use_one_medical_ETL_processor_and_stable_reception_identity()
    {
        IMedicalOperationProvider cfx = new CfxPlugin();
        var host = new InMemoryLabHost(); var trace = new MemoryTraceSink(); var reception = Guid.NewGuid();
        var cases = new[]
        {
            (Operations: Open(new ArchitectPlugin(), ArchitectPlugin.AsiProfileId), Bytes: Encoding.Latin1.GetBytes(string.Join('\r', Results) + "\r")),
            (Operations: Open(Hl7, ArchitectHl7Plugin.ProfileId), Bytes: Encoding.UTF8.GetBytes(Hl7Data.Result())),
            (Operations: Open(cfx, CfxPlugin.CsvProfileId), Bytes: Encoding.UTF8.GetBytes("Well,Sample,Target,Cq\r\nA01,SID-SYN-1,TEST,20.5\r\n")),
        };
        foreach (var item in cases)
        {
            var processor = new MedicalExchangeProcessor(item.Operations, host, trace);
            Assert.Equal(IngestState.Accepted, (await processor.ProcessAsync(item.Bytes, reception, TimeZoneInfo.Utc)).Outcome!.State);
            Assert.Equal(IngestState.Accepted, (await processor.ProcessAsync(item.Bytes, reception, TimeZoneInfo.Utc)).Outcome!.State);
            var batches = host.Batches.TakeLast(2).ToArray();
            Assert.Equal(batches[0].BatchId, batches[1].BatchId);
            Assert.Equal(batches[0].Observations.Select(o => o.ObservationId), batches[1].Observations.Select(o => o.ObservationId));
            var decoded = item.Operations.Decode(item.Bytes, TraceRef.None, TimeZoneInfo.Utc);
            var json = JsonSerializer.SerializeToElement(decoded, MedicalJsonContext.Default.MedicalDecoded);
            Assert.Empty(OperationDataValidator.Validate(item.Operations.Resolve(MedicalDataContracts.Decoded), json, OperationValidationPhase.Prepared));
        }
    }

    [Fact]
    public void Proprietary_protocols_report_unknown_boundaries_instead_of_inventing_idle()
    {
        IMedicalOperationProvider provider = new SysmexUrinalysisPlugin();
        foreach (var profile in new[] { SysmexUrinalysisPlugin.Uc3500Profile, SysmexUrinalysisPlugin.UfBinaryProfile })
        {
            var observer = provider.CreateOperationObserver(profile, Context, Guid.NewGuid());
            observer.Feed(new(0, TimeSpan.Zero, DateTimeOffset.UnixEpoch, TraceDirection.Received, new byte[] { 6 }));
            Assert.False(observer.Current.NoPendingOperations);
            Assert.Null(observer.Current.ChannelOperationInProgress);
        }
    }

    [Fact]
    public void Validation_rejects_ambiguous_patient_and_HL7_message_groups_before_preparation()
    {
        var hl7 = Open(Hl7, ArchitectHl7Plugin.ProfileId);
        Assert.Contains(hl7.Validate(MedicalRequests.Orders(hl7, [Order("A"), Order("B")], Guid.NewGuid())), i => i.Code == "medical.message.single-specimen");
        Assert.Contains(hl7.Validate(MedicalRequests.Orders(hl7, [Order(), Order() with { Action = OrderAction.Cancel }], Guid.NewGuid())), i => i.Code == "medical.message.single-action");
        var standard = Open(new StandardAstmPlugin(), StandardAstmPlugin.ProfileId);
        Assert.Contains(standard.Validate(MedicalRequests.Orders(standard, [Order(), Order() with { Patient = null }], Guid.NewGuid())), i => i.Code == "medical.specimen.patient-conflict");
        Assert.Contains(standard.Decode(ReadOnlyMemory<byte>.Empty, TraceRef.None, TimeZoneInfo.Utc).Diagnostics, d => d.Severity == Astm.Abstractions.Diagnostics.AstmSeverity.Error);
    }

    [Fact]
    public void Negative_standard_answer_only_closes_an_unambiguous_outstanding_query()
    {
        var wire = new Wire(new StandardAstmPlugin(), StandardAstmPlugin.ProfileId);
        wire.Transmission(["H|\\^&", "Q|1|^A||^^^ALL||||||||O", "L|1"]);
        wire.Transmission(["H|\\^&", "L|1|I"], TraceDirection.Transmitted);
        Assert.True(wire.Observer.Current.NoPendingOperations);
        wire.Transmission(["H|\\^&", "Q|1|^A||^^^ALL||||||||O", "Q|2|^B||^^^ALL||||||||O", "L|1"]);
        wire.Transmission(["H|\\^&", "L|1|I"], TraceDirection.Transmitted);
        Assert.True(wire.Observer.Current.ChannelOperationInProgress);
    }

    [Fact]
    public void Device_execution_is_separate_from_channel_custody_and_lost_coverage_never_means_idle()
    {
        var tracker = new OperationTracker(); tracker.Synchronize();
        tracker.Record(new(Guid.NewGuid(), "test.physical-analysis", OperationState.Running, OperationCompletionScope.DeviceExecution,
            OperationEvidence.DeviceReported, false, 0, "test.started"));
        Assert.True(tracker.Current.OperationInProgress);
        Assert.True(tracker.Current.NoPendingOperations);
        tracker.Invalidate(); Assert.False(tracker.Current.NoPendingOperations);
        var bounded = new OperationTracker(1);
        bounded.Record(new(Guid.NewGuid(), "test.query", OperationState.Pending, OperationCompletionScope.MessageExchange, OperationEvidence.Transport, true, 0, "test.pending"));
        Assert.Throws<InvalidOperationException>(() => bounded.Record(new(Guid.NewGuid(), "test.query", OperationState.Pending, OperationCompletionScope.MessageExchange, OperationEvidence.Transport, true, 1, "test.pending")));
        Assert.False(bounded.Current.NoPendingOperations);
    }

    [Fact]
    public void HL7_unknown_ack_codes_duplicate_payloads_and_unsupported_messages_invalidate_coverage()
    {
        IMedicalOperationObserver Observer() => ((IMedicalOperationProvider)Hl7).CreateOperationObserver(ArchitectHl7Plugin.ProfileId, Context, Guid.NewGuid());
        var message = Hl7Codec.Parse(Hl7Data.Result()).Message!;
        var observer = Observer();
        void Feed(IMedicalOperationObserver target, long sequence, TraceDirection direction, Hl7Message payload) =>
            target.Feed(new(sequence, TimeSpan.Zero, DateTimeOffset.UnixEpoch, direction, MllpFraming.Wrap(Encoding.UTF8.GetBytes(payload.Serialize()))));
        Feed(observer, 0, TraceDirection.Received, message);
        Feed(observer, 1, TraceDirection.Transmitted, Hl7Acknowledgements.Build(message, "CE", "ACK-SYN", DateTimeOffset.UnixEpoch));
        Assert.False(observer.Current.CoverageComplete);
        observer = Observer(); Feed(observer, 0, TraceDirection.Received, message);
        var altered = Hl7Codec.Parse(message.Serialize() + "NTE|1||Changed synthetic content\r").Message!;
        Feed(observer, 1, TraceDirection.Received, altered);
        Assert.False(observer.Current.CoverageComplete);
        observer = Observer();
        var unsupported = Hl7Codec.Parse(message.Serialize().Replace("OUL^R22", "OUL^R24", StringComparison.Ordinal)).Message!;
        Feed(observer, 0, TraceDirection.Received, unsupported);
        Assert.False(observer.Current.NoPendingOperations);
    }

    [Fact]
    public async Task Live_HL7_query_processor_uses_common_validation_and_stable_response_identity()
    {
        var host = new InMemoryLabHost();
        var processor = Hl7Data.Processor(host);
        var reception = Guid.NewGuid();
        var bytes = Encoding.UTF8.GetBytes(Hl7Data.Query());
        var first = await processor.ProcessAsync(bytes, reception);
        var repeat = await processor.ProcessAsync(bytes, reception);
        Assert.Equal(Assert.Single(first.Responses).ControlId, Assert.Single(repeat.Responses).ControlId);
        host.OrdersBySpecimen["SID-SYN-1"] = [Order() with { Patient = Order().Patient! with { Physician = "NOT-ADMITTED" } }];
        var invalid = await processor.ProcessAsync(bytes, Guid.NewGuid());
        Assert.Equal(IngestState.Rejected, invalid.Outcome!.State);
        Assert.Equal("ACK", Assert.Single(invalid.Responses).Type);
    }

    [Fact]
    public void HL7_query_response_remains_pending_until_the_response_itself_is_acknowledged()
    {
        var operations = Open(Hl7, ArchitectHl7Plugin.ProfileId);
        var observer = ((IMedicalOperationProvider)Hl7).CreateOperationObserver(ArchitectHl7Plugin.ProfileId, Context, Guid.NewGuid());
        void Feed(long sequence, TraceDirection direction, string payload) => observer.Feed(new(sequence, TimeSpan.Zero,
            DateTimeOffset.UnixEpoch, direction, MllpFraming.Wrap(Encoding.UTF8.GetBytes(payload))));
        var decoded = operations.Decode(Encoding.UTF8.GetBytes(Hl7Data.Query()), TraceRef.None, TimeZoneInfo.Utc);
        Feed(0, TraceDirection.Received, Hl7Data.Query());
        var prepared = operations.Prepare(MedicalRequests.Answer(operations, Assert.Single(decoded.Queries), OrderQueryAnswer.NoInformation, Guid.NewGuid(), Hl7Data.Query()));
        var response = Hl7Codec.Parse(Encoding.UTF8.GetString(prepared.Bytes.Span)).Message!;
        Feed(1, TraceDirection.Transmitted, response.Serialize());
        Assert.True(observer.Current.ChannelOperationInProgress);
        Feed(2, TraceDirection.Received, Hl7Acknowledgements.Build(response, "AA", "ACK-RESPONSE-SYN", DateTimeOffset.UnixEpoch).Serialize());
        Assert.True(observer.Current.NoPendingOperations);
    }

    [Fact]
    public void Core_catalog_owns_metadata_and_negotiation_rejects_revision_and_capability_changes()
    {
        var fields = new Dictionary<string, OperationDataField> { ["value"] = new(new(OperationDataKind.String), OperationFieldRequirement.Required) };
        var reference = new OperationContractReference("test.contract", 1);
        var evidence = new List<string> { "test" };
        var definition = new OperationDefinition("test.prepare", 1, OperationKind.Command, reference, null, OperationCapabilities.Prepare,
            OperationCompletionScope.MessageExchange, "test", "1", evidence);
        var catalog = new OperationCatalog([definition], [new(reference, new(OperationDataKind.Object) { Fields = fields })]);
        fields.Clear(); evidence.Clear();
        Assert.NotEmpty(catalog.Resolve(reference).Root.Fields);
        Assert.NotEmpty(catalog.Describe("test")[0].Evidence);
        Assert.Throws<OperationCompatibilityException>(() => catalog.Select("test", "2", "test.prepare", 1, reference, OperationCapabilities.Prepare));
        Assert.Throws<OperationCompatibilityException>(() => catalog.Select("test", "1", "test.prepare", 1, reference, OperationCapabilities.Execute));
        Assert.Throws<ArgumentException>(() => new OperationCatalog([definition with { Evidence = ["test"], Input = reference with { Major = 2 } }], [new(reference, new(OperationDataKind.String))]));
    }
}
