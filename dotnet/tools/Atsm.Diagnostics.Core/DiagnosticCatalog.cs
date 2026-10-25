using Atsm.Plugin.Abbott.Architect;
using Atsm.Plugin.Abbott.CellDynRuby;
using Atsm.Plugin.BioRad.Cfx;
using Atsm.Plugin.Diagast.Qwalys;
using Atsm.Plugin.Legacy.Testing;
using Atsm.Plugin.Sysmex.Cs2500;
using Atsm.Plugin.Sysmex.Uwam;
using Atsm.Plugin.Sysmex.Uc3500;
using Atsm.Plugin.Sysmex.XnL;
using Atsm.Plugins;
using Atsm.Abstractions.Tracing;

namespace Atsm.Diagnostics;

public sealed record DiagnosticTiming(TimeSpan QueryBudget, TimeSpan ResponseTimeout, TimeZoneInfo InstrumentTimeZone);

public sealed class DiagnosticProfile(string id, string manufacturer, string name, string protocol, string evidence)
{
    public string Id { get; } = id;
    public string Manufacturer { get; } = manufacturer;
    public string Name { get; } = name;
    public string Protocol { get; } = protocol;
    public string Evidence => DiagnosticText.Localize("Profile." + Id + ".Evidence", evidence);
    public string InstrumentId => AstmPlugin?.Describe().Id ?? ContentPlugin?.Describe().Id ?? TracePlugin?.Describe().Id ?? (IsHl7 ? "abbott.architect" : Id);
    public string Configuration => Id switch
    {
        UwamPlugin.WithChemistryProfile => DiagnosticText.Text("Configuration.WithChemistry"),
        UwamPlugin.WithoutChemistryProfile => DiagnosticText.Text("Configuration.WithoutChemistry"),
        _ => Protocol,
    };
    public string DisplayName => $"{Manufacturer} · {Name} · {Configuration}";
    public IInstrumentPlugin? AstmPlugin { get; init; }
    public AstmProfile? AstmProfile { get; init; }
    public IInstrumentContentPlugin? ContentPlugin { get; init; }
    public InstrumentContentProfile? ContentProfile { get; init; }
    public IInstrumentTracePlugin? TracePlugin { get; init; }
    public InstrumentTraceProfile? TraceProfile { get; init; }
    public bool IsHl7 { get; init; }
    public bool CanSimulateHost => AstmProfile is not null || IsHl7;
    public bool CanConnect => CanSimulateHost || TracePlugin is not null;
    public IHl7Dialect Hl7Dialect(TimeZoneInfo zone) => IsHl7
        ? new ArchitectHl7Dialect(new(new("diagnostic"), TimeProvider.System), zone)
        : throw new InvalidOperationException("Not an HL7 profile.");
    public (IHl7InstrumentPlugin Plugin, Hl7Profile Profile) Hl7(DiagnosticTiming timing)
    {
        if (!IsHl7) throw new InvalidOperationException("Not an HL7 profile.");
        var plugin = new ArchitectHl7Plugin(timing.QueryBudget, timing.ResponseTimeout, timing.InstrumentTimeZone);
        return (plugin, plugin.Profiles[0]);
    }
}

/// <summary>Application composition, outside the protocol engine. Bundled plugins only; this is not
/// admission of arbitrary DLLs or a trust-store update. Evidence does not imply hardware qualification.</summary>
public sealed class DiagnosticCatalog
{
    public IReadOnlyList<IInstrumentPlugin> AstmPlugins { get; } = [new StandardAstmPlugin(), new ArchitectPlugin(), new CellDynRubyPlugin(), new UwamPlugin(), new XnLPlugin(), new Cs2500Plugin(), new QwalysPlugin()];
    public IReadOnlyList<IInstrumentContentPlugin> ContentPlugins { get; } = [new CfxPlugin()];
    public IReadOnlyList<IInstrumentTracePlugin> TracePlugins { get; } = [new Uc3500Plugin()];
    public IReadOnlyList<ITraceLogAdapter> LogAdapters { get; }
    public IReadOnlyList<DiagnosticProfile> Profiles { get; }
    public IReadOnlyList<DiagnosticInstrument> Instruments { get; }
    public DiagnosticCatalog()
    {
        var profiles = new List<DiagnosticProfile>();
        foreach (var plugin in TracePlugins)
        foreach (var profile in plugin.Profiles)
            profiles.Add(new(profile.Id, plugin.Describe().Manufacturer, string.Join(", ", plugin.Describe().Models), profile.Protocol,
                "Manual y captura local; intérprete pasivo de órdenes, respuestas y resultados. Sin envío de órdenes ni homologación en hardware.")
            { TracePlugin = plugin, TraceProfile = profile });
        foreach (var plugin in ContentPlugins)
        foreach (var profile in plugin.Profiles)
            profiles.Add(new(profile.Id, plugin.Describe().Manufacturer, "CFX Maestro", profile.Format, "Manual + pruebas sintéticas; exportación, sin control directo")
            { ContentPlugin = plugin, ContentProfile = profile });
        foreach (var plugin in AstmPlugins)
        foreach (var profile in plugin.Profiles)
        {
            var descriptor = plugin.Describe();
            var evidence = descriptor.Id switch
            {
                "abbott.celldyn-ruby" => "Trazas locales; hipótesis pendientes de manual",
                "abbott.architect" => "Manual y trazas locales; sin homologación en hardware",
                "sysmex.uwam" => "Manual y trazas de la interfaz U-WAM ↔ LIS. Para el enlace directo del analizador, seleccione UC-3500. Sin homologación en hardware.",
                "clsi.lis02" => "Estándar genérico; no valida un equipo particular",
                _ => "Manual + pruebas sintéticas; sólo recepción de resultados",
            };
            profiles.Add(new(profile.Id, descriptor.Manufacturer, string.Join(", ", descriptor.Models), "ASTM", evidence) { AstmPlugin = plugin, AstmProfile = profile });
        }
        profiles.Add(new(ArchitectHl7Plugin.ProfileId, "Abbott", "ARCHITECT", "HL7 / MLLP", "Manual + pruebas sintéticas; ZOS, no actor LAW completo") { IsHl7 = true });
        if (profiles.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != profiles.Count) throw new InvalidOperationException("Duplicate profile ID.");
        Profiles = profiles;
        Instruments = profiles.GroupBy(p => p.InstrumentId, StringComparer.Ordinal).Select(g => new DiagnosticInstrument(g.Key,
            g.First().Manufacturer + " · " + (g.Key switch
            {
                "biorad.cfx" => "CFX Maestro", "abbott.architect" => "ARCHITECT", "sysmex.uwam" => "U-WAM (UF-5000, UC-3500, UD-10)",
                "clsi.lis02" => DiagnosticText.Text("Catalog.Generic"), _ => g.First().Name,
            }), g.ToArray())).ToArray();
        LogAdapters = AstmPlugins.OfType<ITraceLogAdapterProvider>().Append(new LegacyTestingPlugin()).SelectMany(p => p.LogAdapters).ToArray();
    }
    public DiagnosticProfile Resolve(string id) => Profiles.Single(p => p.Id == id);
}

public sealed record DiagnosticInstrument(string Id, string DisplayName, IReadOnlyList<DiagnosticProfile> Profiles);
