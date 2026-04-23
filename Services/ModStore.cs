using System.Text.Json;
using McSH.Models;

namespace McSH.Services;

/// <summary>
/// Reads and writes the per-instance mods.json file.
/// </summary>
public class ModStore
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public List<ModEntry> GetAll(string instanceName)
    {
        var path = PathService.ModsJsonPath(instanceName);
        if (!File.Exists(path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<ModEntry>>(
                File.ReadAllText(path), Opts) ?? [];
        }
        catch { return []; }
    }

    public void Save(string instanceName, IEnumerable<ModEntry> mods)
    {
        Directory.CreateDirectory(PathService.InstanceDir(instanceName));
        File.WriteAllText(
            PathService.ModsJsonPath(instanceName),
            JsonSerializer.Serialize(mods.ToList(), Opts));
    }

    public void Add(string instanceName, ModEntry entry)
    {
        var mods = GetAll(instanceName);
        // Replace existing entry for the same project, otherwise append
        var idx = mods.FindIndex(m =>
            !string.IsNullOrEmpty(m.ProjectId) &&
            m.ProjectId.Equals(entry.ProjectId, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) mods[idx] = entry;
        else mods.Add(entry);
        Save(instanceName, mods);
    }

    public void Remove(string instanceName, string projectId)
    {
        var mods = GetAll(instanceName);
        mods.RemoveAll(m => m.ProjectId.Equals(projectId, StringComparison.OrdinalIgnoreCase));
        Save(instanceName, mods);
    }
}

