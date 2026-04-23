namespace McSH.Models;

public class ModProfile
{
    public string Name { get; set; } = "";
    /// <summary>Names of mods that should be enabled when this profile is loaded.</summary>
    public List<string> EnabledMods { get; set; } = [];
}
