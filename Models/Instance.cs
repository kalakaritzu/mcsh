using System.Text.Json.Serialization;

namespace McSH.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ModLoader { Vanilla, Fabric, Forge, NeoForge, Quilt }

public class Instance
{
    public string Name { get; set; } = string.Empty;
    public string MinecraftVersion { get; set; } = string.Empty;
    public ModLoader Loader { get; set; } = ModLoader.Vanilla;
    public int RamMb { get; set; } = 2048;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Set when the instance was installed from a Modrinth modpack — used by 'modpack update'.
    public string? ModrinthProjectId      { get; set; }
    public string? ModrinthVersionId      { get; set; }
    public string? ModrinthVersionNumber  { get; set; }

    // ── Window ────────────────────────────────────────────────────────────────

    /// <summary>Launch Minecraft in fullscreen mode (overwrites options.txt).</summary>
    public bool Fullscreen { get; set; } = false;

    /// <summary>Width of the game window in pixels.</summary>
    public int WindowWidth { get; set; } = 854;

    /// <summary>Height of the game window in pixels.</summary>
    public int WindowHeight { get; set; } = 480;

    // ── JVM / environment ─────────────────────────────────────────────────────

    /// <summary>Extra JVM arguments appended after the standard flags. Space-separated.</summary>
    public string? ExtraJvmArgs { get; set; }

    /// <summary>Environment variables injected before launch. One KEY=VALUE per line.</summary>
    public string? EnvVars { get; set; }

    // ── Hooks ─────────────────────────────────────────────────────────────────

    /// <summary>Command to run before Minecraft launches.</summary>
    public string? PreLaunchCommand { get; set; }

    /// <summary>Wrapper command that prefixes the Java executable (e.g. mangohud).</summary>
    public string? WrapperCommand { get; set; }

    /// <summary>Command to run after Minecraft exits.</summary>
    public string? PostExitCommand { get; set; }

    // ── Server ────────────────────────────────────────────────────────────────

    /// <summary>True when this instance is a dedicated server rather than a client.</summary>
    public bool IsServer { get; set; } = false;
}
