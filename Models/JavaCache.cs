namespace McSH.Models;

/// <summary>
/// Caches the detected Java major version so `java -version` is not spawned on every launch.
/// </summary>
public class JavaCache
{
    public int      MajorVersion { get; set; }
    public DateTime CachedAt     { get; set; }
}
