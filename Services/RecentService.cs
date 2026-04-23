using fNbt;
using McSH.Models;

namespace McSH.Services;

public record RecentEntry(
    string DisplayName,     // World display name or server name
    string FolderOrAddress, // World folder name (for quickPlay arg) or "host:port"
    string InstanceName,
    string MinecraftVersion,
    DateTime LastPlayed,    // DateTime.MinValue for servers (no timestamp in servers.dat)
    bool IsServer
);

public class RecentService
{
    public List<RecentEntry> GetRecent()
    {
        var entries = new List<RecentEntry>();
        var instances = new InstanceStore().GetAll();

        foreach (var instance in instances)
        {
            var instDir = PathService.InstanceDir(instance.Name);

            // ── Singleplayer worlds ───────────────────────────────────────────
            var savesDir = Path.Combine(instDir, "saves");
            if (Directory.Exists(savesDir))
            {
                foreach (var worldDir in Directory.GetDirectories(savesDir))
                {
                    var levelDat = Path.Combine(worldDir, "level.dat");
                    if (!File.Exists(levelDat)) continue;

                    try
                    {
                        var nbt  = new NbtFile(levelDat); // auto-detects GZip
                        var data = (NbtCompound)nbt.RootTag["Data"]!;

                        var displayName = data.TryGet("LevelName", out NbtTag? lvlTag)
                            ? (lvlTag!.StringValue ?? Path.GetFileName(worldDir))
                            : Path.GetFileName(worldDir);

                        var lastPlayedMs = data.TryGet("LastPlayed", out NbtTag? lpTag)
                            ? ((NbtLong)lpTag!).Value
                            : 0L;

                        var lastPlayed = lastPlayedMs > 0
                            ? DateTimeOffset.FromUnixTimeMilliseconds(lastPlayedMs).UtcDateTime
                            : File.GetLastWriteTimeUtc(levelDat);

                        entries.Add(new RecentEntry(
                            displayName,
                            Path.GetFileName(worldDir), // folder name for --quickPlaySingleplayer
                            instance.Name,
                            instance.MinecraftVersion,
                            lastPlayed,
                            IsServer: false
                        ));
                    }
                    catch { /* corrupted or empty level.dat — skip */ }
                }
            }

            // ── Servers ───────────────────────────────────────────────────────
            var serversDat = Path.Combine(instDir, "servers.dat");
            if (!File.Exists(serversDat)) continue;

            try
            {
                var nbt = new NbtFile();
                nbt.LoadFromFile(serversDat, NbtCompression.None, null);

                if (!nbt.RootTag.TryGet("servers", out NbtTag? svTag)) continue;
                var servers = (NbtList)svTag!;

                foreach (NbtCompound server in servers)
                {
                    var name = server.TryGet("name", out NbtTag? nTag) ? (nTag!.StringValue ?? "Unknown") : "Unknown";
                    var ip   = server.TryGet("ip",   out NbtTag? iTag) ? (iTag!.StringValue ?? "")        : "";
                    if (string.IsNullOrWhiteSpace(ip)) continue;

                    // Ensure port is present
                    var address = ip.Contains(':') ? ip : $"{ip}:25565";

                    entries.Add(new RecentEntry(
                        name,
                        address,
                        instance.Name,
                        instance.MinecraftVersion,
                        DateTime.MinValue, // servers.dat has no last-played timestamp
                        IsServer: true
                    ));
                }
            }
            catch { }
        }

        // Worlds sorted by last-played desc, servers appended after (order from servers.dat)
        return entries
            .Where(e => !e.IsServer)
            .OrderByDescending(e => e.LastPlayed)
            .Concat(entries.Where(e => e.IsServer))
            .ToList();
    }

    /// <summary>
    /// Returns a human-readable relative time string (e.g. "2 hours ago", "yesterday").
    /// </summary>
    public static string RelativeTime(DateTime utc)
    {
        var diff = DateTime.UtcNow - utc;
        if (diff.TotalMinutes < 2)  return "just now";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes} minutes ago";
        if (diff.TotalHours   < 2)  return "1 hour ago";
        if (diff.TotalHours   < 24) return $"{(int)diff.TotalHours} hours ago";
        if (diff.TotalDays    < 2)  return "yesterday";
        if (diff.TotalDays    < 7)  return $"{(int)diff.TotalDays} days ago";
        if (diff.TotalDays    < 14) return "last week";
        if (diff.TotalDays    < 31) return $"{(int)(diff.TotalDays / 7)} weeks ago";
        return utc.ToLocalTime().ToString("MMM d, yyyy");
    }
}
