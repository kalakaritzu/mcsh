using System.Text.Json;
using System.Text.Json.Serialization;
using McSH.Models;

namespace McSH.Services;

public class InstanceStore
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    // ── Read ──────────────────────────────────────────────────────────────────

    public List<Instance> GetAll()
    {
        var list = new List<Instance>();
        var dir = PathService.InstancesDir;
        if (!Directory.Exists(dir)) return list;

        foreach (var instanceDir in Directory.EnumerateDirectories(dir))
        {
            var manifest = Path.Combine(instanceDir, "instance.json");
            if (!File.Exists(manifest)) continue;
            try
            {
                var inst = JsonSerializer.Deserialize<Instance>(File.ReadAllText(manifest), Opts);
                if (inst is not null) list.Add(inst);
            }
            catch { /* malformed manifest — skip silently */ }
        }

        return [.. list.OrderBy(i => i.CreatedAt)];
    }

    public Instance? Get(string name)
    {
        var path = PathService.InstanceManifest(name);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<Instance>(File.ReadAllText(path), Opts); }
        catch { return null; }
    }

    public bool Exists(string name) => File.Exists(PathService.InstanceManifest(name));

    // ── Write ─────────────────────────────────────────────────────────────────

    public void Save(Instance instance)
    {
        Directory.CreateDirectory(PathService.InstanceDir(instance.Name));
        Directory.CreateDirectory(PathService.ModsDir(instance.Name));
        File.WriteAllText(
            PathService.InstanceManifest(instance.Name),
            JsonSerializer.Serialize(instance, Opts));
    }

    public void Rename(string oldName, string newName)
    {
        var instance = Get(oldName);
        if (instance is null) throw new InvalidOperationException("Instance manifest could not be read.");
        if (Exists(newName)) throw new InvalidOperationException("An instance with the new name already exists.");

        var oldDir = PathService.InstanceDir(oldName);
        var newDir = PathService.InstanceDir(newName);

        Directory.Move(oldDir, newDir);

        instance.Name = newName;
        Save(instance);
    }

    public void Delete(string name)
    {
        var dir = PathService.InstanceDir(name);
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }

    // ── Launch cache ──────────────────────────────────────────────────────────

    public LaunchCache? GetLaunchCache(string name)
    {
        var path = PathService.LaunchCachePath(name);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<LaunchCache>(File.ReadAllText(path), Opts); }
        catch { return null; }
    }

    public void SaveLaunchCache(string name, LaunchCache cache) =>
        File.WriteAllText(PathService.LaunchCachePath(name), JsonSerializer.Serialize(cache, Opts));
}

