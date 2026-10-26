using Atsm.Plugins;

namespace Atsm.Plugin.Diagast.Qwalys;

public sealed partial class QwalysPlugin : IInstrumentDiagnosticVocabulary
{
    public IReadOnlyDictionary<string, ProtocolText> DiagnosticVocabulary { get; } = CreateVocabulary();
    private static IReadOnlyDictionary<string, ProtocolText> CreateVocabulary()
    {
        var values = new Dictionary<string, ProtocolText>(StandardDiagnosticVocabulary.Create(
        "QWALYS 3 ASTM Connection Manual §O/R/C",
        ("record.O", "Muestra y prueba de inmunohematología"),
        ("record.R", "Reacción de inmunohematología y soporte"),
        ("record.C", "Comentario de la reacción"),
        ("record.Q", "Consulta del instrumento; este perfil sólo recibe resultados")), StringComparer.Ordinal);
        foreach (var (key, text) in new (string, string)[]
        {
            ("valueKind.W", "Reacción en pocillo o columna"), ("valueKind.A", "Autocontrol de compatibilidad"),
            ("valueKind.G0", "Grupo ABO"), ("valueKind.G1", "Rh"), ("valueKind.G2", "Fenotipo Rh"),
            ("valueKind.G3", "Kell"), ("valueKind.G4", "Anticuerpos: antiglobulina indirecta"),
            ("valueKind.G5", "Anticuerpos: método enzimático"), ("valueKind.G6", "Antiglobulina directa"),
            ("state.R", "Lectura automática"), ("state.M", "Modificado por operador"),
            ("support.C", "Casete BioVue"), ("support.X", "Casete de compatibilidad"), ("support.M", "Microplaca"), ("support.?", "Soporte desconocido"),
            ("reaction.A.CMP", "Compatible"), ("reaction.A.INCMP", "Incompatible"), ("reaction.W.E", "Sin reacción informada"),
        }) values.Add(key, new(key, text, "DIAGAST QWALYS 3 ASTM Connection Manual §4.6 p.10; tipo R.3.4, soporte R.5, estado R.9 y resultado R.4.7 según tipo"));
        return values;
    }
}
