namespace McSH.Models;

public class SkinEntry
{
    public string Name       { get; set; } = "";
    public string FileName   { get; set; } = "";
    /// <summary>"classic" (wide arms) or "slim" (narrow arms)</summary>
    public string Model      { get; set; } = "classic";
    public DateTime ImportedAt { get; set; } = DateTime.UtcNow;
}
