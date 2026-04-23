using System.Text.Json;
using McSH.Models;
using McSH.Services;

namespace McSH.State;

/// <summary>
/// Shared mutable state passed through the lifetime of the REPL session.
/// The active instance is persisted to disk so it survives across launches.
/// </summary>
public class AppState
{
    private string? _activeInstance;

    /// <summary>
    /// The currently selected instance name, or null if none is selected.
    /// Setting this clears any stale mod-search context and saves the session.
    /// </summary>
    public string? ActiveInstance
    {
        get => _activeInstance;
        set
        {
            if (_activeInstance != value)
                LastSearchHits = [];   // stale search results from a different instance
            _activeInstance = value;
            SaveSession();
        }
    }

    /// <summary>Results from the last 'mod search' — used for install-by-number.</summary>
    public ModSearchHit[] LastSearchHits { get; set; } = [];

    /// <summary>Results from the last 'modpack search' — used for install-by-number.</summary>
    public ModSearchHit[] LastModpackHits { get; set; } = [];

    // ── Session persistence ───────────────────────────────────────────────────

    /// <summary>
    /// Restores the active instance from the last saved session.
    /// Validates the instance still exists on disk before restoring.
    /// </summary>
    public void LoadSession()
    {
        try
        {
            var path = PathService.SessionPath;
            if (!File.Exists(path)) return;
            var data = JsonSerializer.Deserialize<SessionData>(File.ReadAllText(path));
            if (data?.ActiveInstance is string name &&
                File.Exists(PathService.InstanceManifest(name)))
            {
                // Bypass the property setter to avoid redundant file I/O on startup.
                _activeInstance = name;
            }
        }
        catch { }
    }

    private void SaveSession()
    {
        try
        {
            Directory.CreateDirectory(PathService.RootDir);
            File.WriteAllText(
                PathService.SessionPath,
                JsonSerializer.Serialize(new SessionData(_activeInstance)));
        }
        catch { }
    }

    private record SessionData(string? ActiveInstance);
}
