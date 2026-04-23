namespace McSH.Models;

/// <summary>
/// Represents a mod installed in a specific instance.
/// Persisted to mods.json inside the instance folder.
/// </summary>
public class ModEntry
{
    public string ProjectId     { get; set; } = string.Empty;  // Modrinth project ID (empty for manual)
    public string Slug          { get; set; } = string.Empty;  // e.g. "sodium"
    public string Name          { get; set; } = string.Empty;  // display name
    public string VersionId     { get; set; } = string.Empty;  // Modrinth version ID (empty for manual)
    public string VersionNumber { get; set; } = string.Empty;  // human-readable, e.g. "0.6.0+mc1.21.1"
    public string FileName      { get; set; } = string.Empty;  // actual filename on disk
    public string Sha512        { get; set; } = string.Empty;  // file hash for update checks
    public bool   Enabled       { get; set; } = true;
    public string Source        { get; set; } = "modrinth";    // "modrinth" or "manual"
}
