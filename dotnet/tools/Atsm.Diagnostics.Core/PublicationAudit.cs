using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Atsm.Diagnostics;

/// <summary>Read PE metadata without loading code. Detects stale own DLL references after a plugin
/// rename; this is build integrity, not signature admission or a replacement for host trust policy.</summary>
public static class PublicationAudit
{
    public static IReadOnlyList<string> MissingOwnReferences(string directory)
    {
        var paths = Directory.GetFiles(directory, "*.dll", SearchOption.TopDirectoryOnly);
        var names = paths.Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var issues = new List<string>(); var inspected = 0;
        foreach (var path in paths.Where(p => Own(Path.GetFileNameWithoutExtension(p))))
        {
            inspected++; using var stream = File.OpenRead(path); using var pe = new PEReader(stream);
            if (!pe.HasMetadata) { issues.Add(Path.GetFileName(path) + ": invalid managed assembly"); continue; }
            var metadata = pe.GetMetadataReader();
            foreach (var handle in metadata.AssemblyReferences)
            {
                var name = metadata.GetString(metadata.GetAssemblyReference(handle).Name);
                if (Own(name) && !names.Contains(name)) issues.Add(Path.GetFileName(path) + " -> " + name + ".dll");
            }
        }
        if (inspected == 0) issues.Add("ATSM-PUBLISH-NO-OWN-ASSEMBLIES");
        return issues;
    }
    private static bool Own(string name) => name.StartsWith("Atsm.", StringComparison.OrdinalIgnoreCase) || name.Equals("atsm-diagnostics", StringComparison.OrdinalIgnoreCase);
}
