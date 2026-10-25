using Atsm.Diagnostics;

namespace Atsm.Tests.Diagnostics;

public class PublicationTests
{
    [Fact]
    public void The_complete_test_output_has_no_missing_own_references()
        => Assert.Empty(PublicationAudit.MissingOwnReferences(AppContext.BaseDirectory));

    [Fact]
    public void A_core_DLL_without_its_plugin_dependencies_is_detected_before_release()
    {
        var directory = Path.Combine(Path.GetTempPath(), "atsm-publication-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var copy = Path.Combine(directory, "Atsm.Diagnostics.Core.dll");
        try
        {
            File.Copy(typeof(DiagnosticCatalog).Assembly.Location, copy);
            var issues = PublicationAudit.MissingOwnReferences(directory);
            Assert.Contains(issues, i => i.EndsWith("Atsm.Plugin.Legacy.Testing.dll", StringComparison.Ordinal));
            Assert.Contains(issues, i => i.EndsWith("Atsm.Plugins.dll", StringComparison.Ordinal));
        }
        finally { File.Delete(copy); Directory.Delete(directory); }
    }
}
