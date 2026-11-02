using System.Text.Json.Serialization;

namespace Astm.Plugins;

/// <summary>Evidence and implementation are separate from the executable operation catalog.
/// Unconfirmed is not an exclusion; Documented is not an implemented command.</summary>
public enum InstrumentKnowledgeSupport { Implemented, Documented, Unconfirmed, Excluded }

public sealed record InstrumentProfileFact(string Id, string Topic, string Channel, InstrumentKnowledgeSupport Support,
    ProtocolText Title, ProtocolText Detail, string? OperationPurpose = null)
{
    public string ProfileId { get; init; } = "";
}

public interface IInstrumentProfileKnowledge
{
    IReadOnlyList<InstrumentProfileFact> DescribeKnowledge(string profileId);
}

public static class InstrumentProfileKnowledge
{
    public static InstrumentProfileFact Fact(string id, string topic, string channel, InstrumentKnowledgeSupport support,
        string title, string detail, string evidence, string? operationPurpose = null) =>
        new(id, topic, channel, support, new("knowledge." + id + ".title", title, evidence),
            new("knowledge." + id + ".detail", detail, evidence), operationPurpose);

    public static IReadOnlyList<InstrumentProfileFact> Bind(Type owner, string profileId, params InstrumentProfileFact[] facts)
    {
        if (string.IsNullOrWhiteSpace(profileId) || facts.Select(f => f.Id).Distinct(StringComparer.Ordinal).Count() != facts.Length
            || facts.Any(f => string.IsNullOrWhiteSpace(f.Id) || string.IsNullOrWhiteSpace(f.Topic) || string.IsNullOrWhiteSpace(f.Channel)
                || string.IsNullOrWhiteSpace(f.Title.Evidence) || string.IsNullOrWhiteSpace(f.Detail.Evidence) || !Enum.IsDefined(f.Support)
                || f.OperationPurpose is not null && f.Support != InstrumentKnowledgeSupport.Implemented))
            throw new ArgumentException("instrument.knowledge.invalid");
        var source = owner.Assembly.GetName().Name!;
        return Array.AsReadOnly(facts.Select(f => f with { ProfileId = profileId,
            Title = f.Title with { SourceAssembly = source }, Detail = f.Detail with { SourceAssembly = source } }).ToArray());
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(InstrumentProfileFact[]))]
public partial class InstrumentKnowledgeJsonContext : JsonSerializerContext;
