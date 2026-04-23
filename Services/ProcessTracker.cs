using System.Diagnostics;

namespace McSH.Services;

/// <summary>
/// Tracks live Java processes spawned by 'instance run'.
/// Keyed by instance name (case-insensitive).
/// </summary>
public class ProcessTracker
{
    private readonly Dictionary<string, Process> _running =
        new(StringComparer.OrdinalIgnoreCase);

    public void Register(string name, Process process) =>
        _running[name] = process;

    public Process? Get(string name) =>
        _running.TryGetValue(name, out var p) ? p : null;

    public bool IsRunning(string name) =>
        _running.TryGetValue(name, out var p) && !p.HasExited;

    /// <summary>
    /// Kill (if still alive) and remove the tracked process for <paramref name="name"/>.
    /// </summary>
    public void Remove(string name)
    {
        if (!_running.TryGetValue(name, out var p)) return;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch { /* process may have already exited between the check and the kill */ }
        p.Dispose();
        _running.Remove(name);
    }

    /// <summary>Dispose and remove any processes that have already exited on their own.</summary>
    public void Purge()
    {
        var dead = _running
            .Where(kv => kv.Value.HasExited)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in dead)
        {
            _running[key].Dispose();
            _running.Remove(key);
        }
    }
}

