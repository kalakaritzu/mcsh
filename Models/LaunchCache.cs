namespace McSH.Models;

/// <summary>
/// Persisted per-instance fast-check data. Caches the version, natives state,
/// and pre-built classpath so subsequent launches skip all redundant work.
/// </summary>
public class LaunchCache
{
    public string MinecraftVersion { get; set; } = string.Empty;
    public DateTime LastSuccess    { get; set; }

    /// <summary>Version for which natives have already been extracted.</summary>
    public string? NativesVersion { get; set; }

    /// <summary>Pre-built classpath string for the current version.</summary>
    public string? Classpath { get; set; }

    /// <summary>Loader version at the time the classpath was built (e.g. Fabric loader "0.16.9").</summary>
    public string? LoaderVersion { get; set; }
}
