namespace Atsm.Plugin.Sysmex.XnL;

internal sealed record XnLMessage(string Code, string Text, int Table, bool IsError = false);
internal static class XnLMessages
{
    internal static IReadOnlyList<XnLMessage> Definitions { get; } =
    [
        new("WBC_Abn_Scattergram", "Dispersograma leucocitario anómalo", 7),
        new("Neutropenia", "Indicador de neutropenia", 7), new("Neutrophilia", "Indicador de neutrofilia", 7),
        new("Lymphopenia", "Indicador de linfopenia", 7), new("Lymphocytosis", "Indicador de linfocitosis", 7),
        new("Leukocytopenia", "Indicador de leucopenia", 7), new("Leukocytosis", "Indicador de leucocitosis", 7),
        new("Monocytosis", "Indicador de monocitosis", 7), new("Eosinophilia", "Indicador de eosinofilia", 7),
        new("Basophilia", "Indicador de basofilia", 7), new("IG_Present", "Granulocitos inmaduros señalados", 7),
        new("RBC_Abn_Distribution", "Distribución eritrocitaria anómala", 7), new("Dimorphic_Population", "Población dimórfica", 7),
        new("Anisocytosis", "Indicador de anisocitosis", 7), new("Microcytosis", "Indicador de microcitosis", 7),
        new("Macrocytosis", "Indicador de macrocitosis", 7), new("Hypochromia", "Indicador de hipocromía", 7),
        new("Anemia", "Indicador de anemia", 7), new("Erythrocytosis", "Indicador de eritrocitosis", 7),
        new("RET_Abn_Scattergram", "Dispersograma de reticulocitos anómalo", 7), new("Reticulocytosis", "Indicador de reticulocitosis", 7),
        new("PLT_Abn_Scattergram", "Dispersograma plaquetario anómalo", 7), new("PLT_Abn_Distribution", "Distribución plaquetaria anómala", 7),
        new("Thrombocytopenia", "Indicador de trombocitopenia", 7), new("Thrombocytosis", "Indicador de trombocitosis", 7),
        new("Left_Shift?", "Sospecha de desviación a la izquierda", 8), new("Atypical_Lympho?", "Sospecha de linfocitos atípicos", 8),
        new("Blasts/Abn_Lympho?", "Sospecha de blastos o linfocitos anómalos", 8), new("NRBC?", "Sospecha de eritroblastos", 8),
        new("RBC_Agglutination?", "Sospecha de aglutinación eritrocitaria", 8),
        new("Turbidity/HGB_Interference?", "Sospecha de turbidez o interferencia en hemoglobina", 8),
        new("Iron_Deficiency?", "Sospecha de déficit de hierro", 8), new("HGB_Defect?", "Sospecha de alteración de hemoglobina", 8),
        new("Fragments?", "Sospecha de fragmentos eritrocitarios", 8), new("PLT_Clumps?", "Sospecha de agregados plaquetarios", 8),
        new("Positive_Diff", "Indicador positivo en diferencial", 9), new("Positive_Morph", "Indicador positivo en morfología", 9),
        new("Positive_Count", "Indicador positivo en recuento", 9), new("Error_Func", "Error funcional del análisis", 9, true),
        new("Error_Result", "Error de resultado", 9, true),
        new("ACTION_MESSAGE_Delta", "Revisar cambio respecto del resultado anterior", 10),
        new("ACTION_MESSAGE_Delta_WBC", "Revisar cambio en leucocitos", 10),
        new("ACTION_MESSAGE_Delta_HGB", "Revisar cambio en hemoglobina", 10),
        new("ACTION_MESSAGE_Delta_MCV", "Revisar cambio en volumen corpuscular medio", 10),
        new("ACTION_MESSAGE_Delta_PLT", "Revisar cambio en plaquetas", 10),
        new("ACTION_MESSAGE_RBC", "Revisar eritrocitos", 10), new("ACTION_MESSAGE_Review_PLT", "Revisión plaquetaria indicada", 10),
        new("ACTION_MESSAGE_PLT", "Revisar plaquetas", 10), new("ACTION_MESSAGE_Aged_Sample?", "Revisar antigüedad de la muestra", 10),
    ];
    internal static XnLMessage? Find(string code) => Definitions.FirstOrDefault(e => e.Code == code);
}
