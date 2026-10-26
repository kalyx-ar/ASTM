using System.Text.RegularExpressions;
using Atsm.Abstractions.Model;

namespace Atsm.Plugins;

public static partial class InstrumentContent
{
    private static readonly InstrumentValueMeaning Image = new(StandardDiagnosticVocabulary.Shared["value.Image"], "image", false)
        { DisplayText = StandardDiagnosticVocabulary.Shared["value.Image"], IsStructuredContent = true };
    public static InstrumentValueMeaning? Describe(InstrumentObservation observation, IInstrumentValueKnowledge? specific = null) =>
        IsImageReference(observation) ? Image : specific?.DescribeValue(observation);
    /// <summary>Recognizes a reported image reference; never resolves or opens it.</summary>
    public static bool IsImageReference(InstrumentObservation observation) => ImageReference().IsMatch(observation.Value.RawText);

    [GeneratedRegex(@"(?:\.(?:png|jpe?g|gif|bmp|tiff?|webp|svg|ico)(?:$|[\s?#\^&|;,""'<>\]\)}])|(?:^|[^a-z0-9])image/(?:png|jpe?g|gif|bmp|tiff|webp|svg\+xml|x-icon)(?:$|[^a-z0-9]))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex ImageReference();
}
