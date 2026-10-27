using Atsm.Abstractions.Model;
using Atsm.Protocol.Lis02;

namespace Atsm.Plugins;

public enum InstrumentCommandEffect { Query, Order, Maintenance }
public sealed record InstrumentCommandParameter(string Id, ProtocolText Label, bool Required = true);
public sealed record InstrumentCommandDescriptor(string Id, ProtocolText Label, InstrumentCommandEffect Effect, IReadOnlyList<InstrumentCommandParameter> Parameters);
public sealed record InstrumentCommandRequest(string Id, IReadOnlyDictionary<string, string> Parameters);

/// <summary>The plugin owns available commands, parameters and construction; the engine only transmits protocol messages.</summary>
public interface IInstrumentCommandPlugin
{
    IReadOnlyList<InstrumentCommandDescriptor> Commands(string profileId);
    IReadOnlyList<Lis02Record> BuildAstmCommand(string profileId, InstrumentCommandRequest request, DialectContext context);
}

/// <summary>Reusable order action, explicitly adopted by a plugin whose profile supports order download.</summary>
public static class AstmOrderCommands
{
    public const string SendOrder = "order.send";
    public static IReadOnlyList<InstrumentCommandDescriptor> Describe(IInstrumentPlugin plugin, string profileId) =>
        plugin.Profiles.Any(p => p.Id == profileId && p.Workflows.Contains(AstmWorkflow.OrderDownload))
            ? [new(SendOrder, StandardDiagnosticVocabulary.Shared["command.SendOrder"], InstrumentCommandEffect.Order,
                [new("sample", StandardDiagnosticVocabulary.Shared["command.Sample"]), new("tests", StandardDiagnosticVocabulary.Shared["command.Tests"])])]
            : [];
    public static IReadOnlyList<Lis02Record> Build(IInstrumentPlugin plugin, string profileId, InstrumentCommandRequest request, DialectContext context)
    {
        var action = Describe(plugin, profileId).SingleOrDefault(d => d.Id == request.Id) ?? throw new NotSupportedException("ATSM-COMMAND-UNSUPPORTED");
        foreach (var parameter in action.Parameters)
            if (!request.Parameters.TryGetValue(parameter.Id, out var value) || string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
                throw new ArgumentException("ATSM-COMMAND-PARAMETER: " + parameter.Id);
        if (request.Parameters.Keys.Except(action.Parameters.Select(p => p.Id), StringComparer.Ordinal).Any()) throw new ArgumentException("ATSM-COMMAND-PARAMETER");
        var tests = request.Parameters["tests"].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(t => CodeSet.Of(new(t, null, CodeSystems.Vendor(plugin.Describe().Id)))).ToArray();
        if (tests.Length == 0) throw new ArgumentException("ATSM-COMMAND-PARAMETER: tests");
        var sample = request.Parameters["sample"].Trim();
        return plugin.CreateDialect(plugin.Profiles.Single(p => p.Id == profileId), context).BuildOrders([new(Guid.CreateVersion7().ToString(), new(sample), null, tests)]);
    }
}
