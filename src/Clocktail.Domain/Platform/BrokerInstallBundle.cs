namespace Clocktail.Domain.Platform;

/// <summary>The privileged installation must include the context-free device-proof helper beside the broker.</summary>
public static class BrokerInstallBundle
{
    public const string LabName = "clocktail-lab.exe";

    public static IReadOnlyList<string> Files(string source, string self, bool singleFile)
    {
        string lab = Path.Combine(source, LabName);
        if (!File.Exists(lab))
            throw new FileNotFoundException("clocktail-lab.exe must be beside the broker before installation; publish/install the matching bundle together", lab);
        var files = singleFile ? new List<string> { self } : Directory.EnumerateFiles(source)
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".exe" or ".dll" or ".json").ToList();
        if (!files.Contains(lab, StringComparer.OrdinalIgnoreCase)) files.Add(lab);
        return files;
    }
}
